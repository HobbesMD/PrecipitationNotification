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
    IFallbackForecastSource fallback,
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
        var started = clock.GetTimestamp();
        var firstFailure = ExitCodes.Ok;
        var deadlinePassed = false;
        foreach (var location in locations)
        {
            // Checked before starting a location, never in the middle of one: a location that has
            // begun gets to finish (its own HTTP timeouts bound it), but a slow night must not
            // start work the job's replica timeout will kill, since a killed replica loses the
            // exit code and the log of what was skipped.
            if (deadlinePassed || clock.GetElapsedTime(started) >= options.Deadline)
            {
                deadlinePassed = true;
                log.LogError(
                    "Location {LocationId} ({LocationName}): not evaluated (pass deadline of {Deadline} reached).",
                    location.Id, location.Name, options.Deadline);
                continue;
            }

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

        if (deadlinePassed && firstFailure == ExitCodes.Ok)
            return ExitCodes.PassDeadline;

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

        // ---- NWS first ----
        // Any failure on this path except our own cancellation hands the location to the fallback
        // provider (D33). The error is kept, not thrown, so the fallback decision sits in one place.
        NwsPoint point;
        TimeZoneInfo? zone = null;
        NightWindow? resolved = null;
        string? checkedZoneId = null;
        ProviderForecast? forecast = null;
        Exception? nwsError = null;
        var nwsStage = "";

        try
        {
            point = await cache.GetAsync(location, ct);
            // The zone comes from the location (NWS), not from config; an unknown id is a bad NWS response.
            zone = TimeZoneInfo.FindSystemTimeZoneById(point.TimeZoneId);
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            zone = null;
            nwsError = ex;
            nwsStage = "NWS point lookup failed";
            point = default!;
        }

        if (zone is not null)
        {
            resolved = NightWindow.Resolve(now, zone, rule.WindowStart, rule.WindowEnd);
            if (await AlreadySentAsync(location, resolved, ct) is { } alreadyDone)
                return alreadyDone;
            checkedZoneId = zone.Id;

            // Never log the coordinates: they pin a home, and stdout lands in Log Analytics in Azure.
            log.LogInformation(
                "Location {LocationId} ({LocationName}): fetching NWS forecast ...", location.Id, location.Name);
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
                nwsError = ex;
                nwsStage = "NWS fetch failed";
            }
        }

        // ---- Fallback ----
        if (forecast is null)
        {
            log.LogWarning(
                "Location {LocationId} ({LocationName}): {Stage}: {Error}; trying the fallback provider.",
                location.Id, location.Name, nwsStage, Describe(nwsError!));

            // The cached zone is what keeps the night key stable across providers. When it is known
            // the already-sent check can run before spending a fallback call.
            var cachedZone = FindZone(location.TimeZoneId);
            if (cachedZone is not null && cachedZone.Id != checkedZoneId)
            {
                resolved = NightWindow.Resolve(now, cachedZone, rule.WindowStart, rule.WindowEnd);
                if (await AlreadySentAsync(location, resolved, ct) is { } alreadyDone)
                    return alreadyDone;
                checkedZoneId = cachedZone.Id;
            }

            log.LogInformation(
                "Location {LocationId} ({LocationName}): fetching fallback forecast ...", location.Id, location.Name);
            try
            {
                forecast = await fallback.GetForecastAsync(location, ct);
            }
            catch (Exception ex) when (Failed(ex, ct))
            {
                log.LogError(
                    "Location {LocationId} ({LocationName}): both providers failed. {Stage}: {NwsError}; fallback fetch failed: {FallbackError}",
                    location.Id, location.Name, nwsStage, Describe(nwsError!), Describe(ex));
                return ExitCodes.Nws;
            }

            var forecastZone = FindZone(forecast.TimeZoneId);
            if (cachedZone is not null && forecastZone is not null
                && !string.Equals(cachedZone.Id, forecastZone.Id, StringComparison.Ordinal))
            {
                log.LogWarning(
                    "Location {LocationId} ({LocationName}): fallback time zone {FallbackZone} differs from the cached {CachedZone}; using the cached one.",
                    location.Id, location.Name, forecastZone.Id, cachedZone.Id);
            }

            zone = cachedZone ?? forecastZone;
            if (zone is null)
            {
                log.LogError(
                    "Location {LocationId} ({LocationName}): both providers failed. {Stage}: {NwsError}; fallback returned a time zone this system does not know.",
                    location.Id, location.Name, nwsStage, Describe(nwsError!));
                return ExitCodes.Nws;
            }

            if (zone.Id != checkedZoneId)
            {
                resolved = NightWindow.Resolve(now, zone, rule.WindowStart, rule.WindowEnd);
                if (await AlreadySentAsync(location, resolved, ct) is { } alreadyDone)
                    return alreadyDone;
                checkedZoneId = zone.Id;
            }

            log.LogWarning(
                "Location {LocationId} ({LocationName}): using {Provider} forecast.",
                location.Id, location.Name, forecast.Provider);
        }

        // Every path that reaches here has resolved a zone and checked its window.
        var window = resolved!;
        var localZone = zone!;

        log.LogInformation("Got {Count} hourly periods.", forecast.Hours.Count);
        if (forecast.Amounts is null)
            log.LogWarning(
                "Amounts unavailable ({Reason}); alerting on probability only.", forecast.AmountsUnavailableReason);

        NightAssessment assessment;
        try
        {
            assessment = ForecastEvaluator.Evaluate(
                window, forecast.Hours, forecast.Amounts, new Forecast.AlertRule(rule.ProbabilityThreshold), now)
                with { Provider = forecast.Provider };
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
        log.LogInformation("Time zone: {TimeZone}", localZone.Id);
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

        var msg = NotificationComposer.Compose(assessment, localZone);

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

    /// <summary>
    /// Null when the night is not yet sent (or this is a dry run, which never asks); otherwise the
    /// exit code to return: Ok for an already-sent night, Database when the ledger can't be read.
    /// </summary>
    private async Task<int?> AlreadySentAsync(Location location, NightWindow window, CancellationToken ct)
    {
        if (options.DryRun)
            return null;

        try
        {
            if (!await ledger.IsSentAsync(location.Id, window.NightDate, ct))
                return null;

            log.LogInformation(
                "Location {LocationId} ({LocationName}): already sent for night {NightDate:yyyy-MM-dd}.",
                location.Id, location.Name, window.NightDate);
            return ExitCodes.Ok;
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            log.LogError(
                "Location {LocationId} ({LocationName}): alert ledger unavailable: {Error}",
                location.Id, location.Name, Describe(ex));
            return ExitCodes.Database;
        }
    }

    /// <summary>The zone for an IANA id, or null when the id is missing or this system doesn't know it.</summary>
    private static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
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
