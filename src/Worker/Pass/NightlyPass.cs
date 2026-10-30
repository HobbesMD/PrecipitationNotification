using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Persistence;
using Worker.Alerts;
using Worker.Forecast;
using Worker.Nws;

namespace Worker.Pass;

/// <summary>
/// One run over every active location: resolve the point, fetch, evaluate, send once.
/// Shared by <c>--once</c> (the scheduled job) and the long-running
/// <c>BackgroundService</c> mode.
///
/// Each location is isolated: one failing location never stops the next, and the pass
/// returns the first failure's code in id order. Locations are identified in logs by id and
/// name only. The coordinates, the topic and the connection string never reach a log line (D22).
/// </summary>
internal sealed class NightlyPass(
    PrecipDbContext db,
    IGridpointCache cache,
    IForecastSource forecasts,
    IAlertLedger ledger,
    TimeProvider clock,
    PassOptions options,
    ILogger<NightlyPass> log) : INightlyPass
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        IReadOnlyList<Location> locations;
        try
        {
            // Unreachable, or migrations not applied: running against the wrong schema could
            // mean a missing idempotency index, so stop before touching anything. Migrations are
            // applied by --migrate, never automatically.
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0)
            {
                log.LogError(
                    "Database has {Count} pending migration(s); run with --migrate first.", pending.Count);
                return ExitCodes.Database;
            }

            locations = await db.Locations
                .Include(l => l.AlertRule)
                .Where(l => l.IsActive)
                .OrderBy(l => l.Id)
                .ToListAsync(ct);
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            log.LogError("Database unavailable: {Error}", Describe(ex));
            return ExitCodes.Database;
        }

        if (locations.Count == 0)
        {
            log.LogError("No active locations; nothing to check.");
            return ExitCodes.Failure;
        }

        var now = clock.GetUtcNow();
        var firstFailure = ExitCodes.Ok;
        foreach (var location in locations)
        {
            int code;
            try
            {
                code = await RunLocationAsync(location, now, ct);
            }
            catch (Exception ex) when (Failed(ex, ct))
            {
                // A bug, not an expected failure mode; still must not stop the other locations.
                log.LogError(
                    "Location {LocationId} ({LocationName}): unexpected failure: {Error}",
                    location.Id, location.Name, Describe(ex));
                code = ExitCodes.Failure;
            }

            if (code != ExitCodes.Ok && firstFailure == ExitCodes.Ok)
                firstFailure = code;
        }

        return firstFailure;
    }

    private async Task<int> RunLocationAsync(Location location, DateTimeOffset now, CancellationToken ct)
    {
        var rule = location.AlertRule;
        if (rule is null)
        {
            log.LogError(
                "Location {LocationId} ({LocationName}) has no alert rule; skipped.", location.Id, location.Name);
            return ExitCodes.Failure;
        }

        NwsPoint point;
        TimeZoneInfo zone;
        try
        {
            point = await cache.GetAsync(location, ct);
            // The zone comes from the location (NWS), not from config; an unknown id is a bad NWS response.
            zone = TimeZoneInfo.FindSystemTimeZoneById(point.TimeZoneId);
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            log.LogError(
                "Location {LocationId} ({LocationName}): NWS point lookup failed: {Error}",
                location.Id, location.Name, Describe(ex));
            return ExitCodes.Nws;
        }

        var window = NightWindow.Resolve(now, zone, rule.WindowStart, rule.WindowEnd);

        if (!options.DryRun)
        {
            try
            {
                if (await ledger.IsSentAsync(location.Id, window.NightDate, ct))
                {
                    log.LogInformation(
                        "Location {LocationId} ({LocationName}): already sent for night {NightDate:yyyy-MM-dd}.",
                        location.Id, location.Name, window.NightDate);
                    return ExitCodes.Ok;
                }
            }
            catch (Exception ex) when (Failed(ex, ct))
            {
                log.LogError(
                    "Location {LocationId} ({LocationName}): alert ledger unavailable: {Error}",
                    location.Id, location.Name, Describe(ex));
                return ExitCodes.Database;
            }
        }

        // Never log the coordinates: they pin a home, and stdout lands in Log Analytics in Azure.
        log.LogInformation(
            "Location {LocationId} ({LocationName}): fetching NWS forecast ...", location.Id, location.Name);
        NwsForecast forecast;
        try
        {
            try
            {
                forecast = await forecasts.GetForecastAsync(point, ct);
            }
            catch (NwsPointStaleException)
            {
                log.LogWarning(
                    "Location {LocationId} ({LocationName}): cached NWS point is stale; refreshing once.",
                    location.Id, location.Name);
                point = await cache.RefreshAsync(location, ct);
                forecast = await forecasts.GetForecastAsync(point, ct);
            }
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            log.LogError(
                "Location {LocationId} ({LocationName}): NWS fetch failed: {Error}",
                location.Id, location.Name, Describe(ex));
            return ExitCodes.Nws;
        }

        log.LogInformation("Got {Count} hourly periods.", forecast.Hours.Count);
        if (forecast.Amounts is null)
            log.LogWarning(
                "Amounts unavailable ({Reason}); alerting on probability only.", forecast.AmountsUnavailableReason);

        NightAssessment assessment;
        try
        {
            assessment = ForecastEvaluator.Evaluate(
                window, forecast.Hours, forecast.Amounts, new Forecast.AlertRule(rule.ProbabilityThreshold), now);
        }
        catch (IncompleteForecastException ex)
        {
            // Not a dry night: we don't know. Fail the run so it can't be mistaken for "no precipitation".
            log.LogError(
                "Location {LocationId} ({LocationName}): incomplete forecast: {Error}",
                location.Id, location.Name, ex.Message);
            return ExitCodes.IncompleteForecast;
        }

        foreach (var gap in assessment.Gaps)
            log.LogWarning(
                "No hourly data {GapStart:o} -> {GapEnd:o}; alerting on the hours we have.", gap.StartUtc, gap.EndUtc);

        var hoursInWindow = forecast.Hours.Count(h => h.Start < window.EndUtc && h.End > window.StartUtc);
        log.LogInformation("Time zone: {TimeZone}", zone.Id);
        log.LogInformation(
            "Night {NightDate:yyyy-MM-dd}: {WindowStart:o} -> {WindowEnd:o} (length {Length})",
            window.NightDate, window.StartUtc, window.EndUtc, window.Length);
        log.LogInformation(
            "Hours in window: {Hours}; hits at/above {Threshold}%: {Hits}",
            hoursInWindow, rule.ProbabilityThreshold, assessment.Hits.Count);

        if (!assessment.ShouldAlert)
        {
            log.LogInformation(
                "No hour at/above {Threshold}% precipitation. No notification sent.", rule.ProbabilityThreshold);
            return ExitCodes.Ok;
        }

        log.LogInformation(
            "Peak {Peak}%, kind {Kind}, liquid {Liquid} in, snow {Snow} in, ice {Ice} in",
            assessment.PeakProbability,
            assessment.Kind,
            assessment.LiquidInches.ToString("0.00", Inv),
            assessment.SnowInches.ToString("0.00", Inv),
            assessment.IceInches.ToString("0.00", Inv));

        var msg = NotificationComposer.Compose(assessment, zone);

        if (options.DryRun)
        {
            log.LogInformation(
                "----- notification -----\nTitle: {Title}\nPriority: {Priority}\nTags: {Tags}\nBody:\n{Body}\n------------------------\n(dry run: not published)",
                msg.Title, msg.Priority, msg.Tags, msg.Body);
            return ExitCodes.Ok;
        }

        try
        {
            var outcome = await ledger.SendOnceAsync(location.Id, window.NightDate, assessment, msg, ct);
            if (outcome == SendOutcome.Sent)
                log.LogInformation(
                    "Location {LocationId} ({LocationName}): Sent notification (peak {Peak}%, {Hits} hour(s) over threshold).",
                    location.Id, location.Name, assessment.PeakProbability, assessment.Hits.Count);
            else
                log.LogInformation(
                    "Location {LocationId} ({LocationName}): AlreadySent for night {NightDate:yyyy-MM-dd}; nothing published.",
                    location.Id, location.Name, window.NightDate);
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            // The ledger's own statements failed (claim, lock or commit), not ntfy. If the commit
            // failed after a 2xx the push did go out; either way the row isn't Sent, so the next
            // run sends (D25's at-least-once case). Exit 6 says where to look, not "ntfy down".
            log.LogError(
                "Location {LocationId} ({LocationName}): alert ledger database error during send: {Error}",
                location.Id, location.Name, Describe(ex));
            return ExitCodes.Database;
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            log.LogError(
                "Location {LocationId} ({LocationName}): publish failed: {Error}",
                location.Id, location.Name, Describe(ex));
            return ExitCodes.Publish;
        }

        return ExitCodes.Ok;
    }

    /// <summary>True unless the failure is just the host shutting down, which must propagate.</summary>
    private static bool Failed(Exception ex, CancellationToken ct) =>
        ex is not OperationCanceledException || !ct.IsCancellationRequested;

    /// <summary>
    /// Type and message only, never the exception object (its inner exceptions and stack are
    /// not vetted for secrets). Our own exceptions keep coordinates and URLs out of messages (D22).
    /// </summary>
    private static string Describe(Exception ex) => Redact.Describe(ex);

    /// <summary>
    /// Npgsql errors derive from <see cref="System.Data.Common.DbException"/>; EF wraps some in
    /// <see cref="DbUpdateException"/> or an <see cref="InvalidOperationException"/>, so look one
    /// level in. A publish failure (HttpRequestException, timeout) never matches.
    /// </summary>
    private static bool IsDatabaseFailure(Exception ex) =>
        ex is System.Data.Common.DbException or DbUpdateException
        || ex.InnerException is System.Data.Common.DbException;
}
