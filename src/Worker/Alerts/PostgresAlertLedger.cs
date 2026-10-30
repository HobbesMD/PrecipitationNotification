using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Persistence;
using Worker.Forecast;
using Worker.Notifications;

namespace Worker.Alerts;

/// <summary>
/// <see cref="IAlertLedger"/> over the <c>alert_history</c> table. Timestamps come
/// from <see cref="TimeProvider"/> (UTC), never the host clock, so tests control them.
///
/// <para>
/// <b>How "exactly one alert per location per night" is enforced.</b> The row for
/// <c>(location_id, night_date)</c> is the lock. Every send claims it (an
/// <c>INSERT … ON CONFLICT DO NOTHING</c> against the unique index
/// <see cref="PrecipDbContext.AlertHistoryUniqueIndex"/>), locks it
/// (<c>SELECT … FOR UPDATE</c>), and publishes <em>while holding that lock</em>,
/// committing <c>Sent</c> in the same transaction. A concurrent sender for the same
/// key therefore waits until the first one commits or rolls back, then sees the
/// outcome: <c>Sent</c> means it publishes nothing; still <c>Pending</c> (the first
/// one failed) means it is now the one to try.
/// </para>
///
/// <para>
/// <b>The one gap, accepted on purpose (at-least-once).</b> If ntfy accepted the
/// push but the response was lost, the publish throws, the transaction rolls back,
/// and the row stays <c>Pending</c>. The next attempt sends again and the phone gets
/// a duplicate. No database can close this gap, because the side effect lives in
/// ntfy, outside the transaction. The owner chose a possible duplicate over a
/// possible missed alert.
/// </para>
///
/// <para>
/// Every statement is raw, parameterized SQL (EF interpolation turns each hole into a
/// parameter). Nothing is tracked, so a rolled-back attempt leaves no stale entity in
/// the context for the next call to trip over.
/// </para>
/// </summary>
internal sealed partial class PostgresAlertLedger(PrecipDbContext db, IAlertPublisher publisher, TimeProvider clock)
    : IAlertLedger
{
    /// <summary>
    /// A plain read, and only an optimization: it lets the caller skip the NWS fetch
    /// for a night that already went out. It is <em>not</em> the enforcement. Between
    /// this read and the send, another run can send, so the caller must still go
    /// through <see cref="SendOnceAsync"/>, which re-checks under a row lock.
    /// </summary>
    public Task<bool> IsSentAsync(int locationId, DateOnly nightDate, CancellationToken ct) =>
        db.AlertHistory
            .AsNoTracking()
            .AnyAsync(h => h.LocationId == locationId && h.NightDate == nightDate && h.Status == AlertStatus.Sent, ct);

    public async Task<SendOutcome> SendOnceAsync(
        int locationId, DateOnly nightDate, NightAssessment assessment, ComposedMessage msg, CancellationToken ct)
    {
        // Cancellation of our token is never caught below: disposing the transaction rolls it
        // back, so a cancelled attempt leaves the row exactly as it found it (or absent).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var createdAt = clock.GetUtcNow();

        // Claim the key. If the row exists (committed), this does nothing and returns at once.
        // If another transaction has inserted the same key but not yet committed, this
        // statement WAITS for it: that is the serialization point for a brand-new night.
        // It then does nothing (the other committed) or inserts (the other rolled back).
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO alert_history (location_id, night_date, status, attempts, created_at, last_attempt_at)
            VALUES ({locationId}, {nightDate}, 'Pending', 0, {createdAt}, {createdAt})
            ON CONFLICT (location_id, night_date) DO NOTHING
            """,
            ct);

        // Lock the row for the rest of the transaction. ON CONFLICT DO NOTHING takes no lock
        // on an existing row, so without FOR UPDATE two senders could both read an earlier
        // attempt's Pending row and both publish. Under READ COMMITTED a waiter re-reads the
        // row once the lock is released, so it sees the winner's committed Sent.
        var rows = await db.AlertHistory
            .FromSql(
                $"""
                SELECT * FROM alert_history
                WHERE location_id = {locationId} AND night_date = {nightDate}
                FOR UPDATE
                """)
            .AsNoTracking()
            .ToListAsync(ct);
        var row = rows.Single();

        if (row.Status == AlertStatus.Sent)
        {
            await tx.CommitAsync(ct);
            return SendOutcome.AlreadySent;
        }

        try
        {
            // Published while the lock is held: no other sender for this key can get past
            // the FOR UPDATE above until this transaction ends.
            await publisher.PublishAsync(msg, ct);
        }
        catch (Exception ex) when (!IsOurCancellation(ex, ct))
        {
            // Ends the lock. The failure is then recorded outside it, in its own transaction,
            // because this one is about to be thrown away.
            await RollBackQuietlyAsync(tx);
            await RecordFailureQuietlyAsync(locationId, nightDate, ex);
            throw;
        }

        // The push has gone out. From here on, ignore cancellation: abandoning the bookkeeping now
        // would only leave the row Pending and turn a stop request into a duplicate alert.
        var sentAt = clock.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE alert_history
            SET status = 'Sent',
                sent_at = {sentAt},
                attempts = attempts + 1,
                last_attempt_at = {sentAt},
                peak_probability = {assessment.PeakProbability},
                kind = {assessment.Kind.ToString()},
                title = {msg.Title}
            WHERE id = {row.Id}
            """,
            CancellationToken.None);

        // If this commit fails, the push went out but the row stays Pending and the next run
        // re-sends: the same at-least-once case as a lost ntfy response, and accepted with it.
        await tx.CommitAsync(CancellationToken.None);
        return SendOutcome.Sent;
    }

    /// <summary>
    /// Cancellation of <em>our</em> token is the caller stopping us: no bookkeeping. Any other
    /// <see cref="OperationCanceledException"/> is a failure to record, not a cancellation:
    /// <see cref="HttpClient"/> reports its own timeout as a <see cref="TaskCanceledException"/>,
    /// and a timed-out POST is exactly the lost-response case that must leave a trace.
    /// </summary>
    private static bool IsOurCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    private static async Task RollBackQuietlyAsync(IDbContextTransaction tx)
    {
        try
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // A broken connection has already rolled back on the server. The publish failure is
            // the error that matters; it is rethrown by the caller either way.
        }
    }

    /// <summary>
    /// Upserts the row as Pending with one more attempt and the scrubbed error, in a new short
    /// transaction. Best effort: this is a diagnostic trail, not a correctness mechanism. Whether
    /// it lands or not, the row is absent or Pending, so the next attempt sends. If it fails, the
    /// publish exception (the actionable one) still propagates unchanged.
    ///
    /// The DO UPDATE is guarded on Pending: between the rollback and this statement another sender
    /// may have taken the lock and sent, and a Sent row must never be marked back to Pending.
    /// </summary>
    private async Task RecordFailureQuietlyAsync(int locationId, DateOnly nightDate, Exception ex)
    {
        var now = clock.GetUtcNow();
        var error = DescribeFailure(ex);

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO alert_history (location_id, night_date, status, attempts, created_at, last_attempt_at, last_error)
                VALUES ({locationId}, {nightDate}, 'Pending', 1, {now}, {now}, {error})
                ON CONFLICT (location_id, night_date) DO UPDATE
                SET attempts = alert_history.attempts + 1,
                    last_attempt_at = EXCLUDED.last_attempt_at,
                    last_error = EXCLUDED.last_error
                WHERE alert_history.status = 'Pending'
                """,
                CancellationToken.None);
            await tx.CommitAsync(CancellationToken.None);
        }
        catch
        {
            // See the summary: losing the trail never changes what the next attempt does.
        }
    }

    /// <summary>
    /// Exception type and message, with anything URL-shaped removed. The ntfy topic is the
    /// last path segment of the publish URL and is a secret (anyone holding it can read and
    /// publish), so a message that quotes the request URI must not reach the table, which
    /// the API may show.
    /// </summary>
    internal static string DescribeFailure(Exception ex) =>
        $"{ex.GetType().FullName}: {ScrubUrls(ex.Message)}";

    internal static string ScrubUrls(string text) => UrlShaped().Replace(text, "[url removed]");

    /// <summary>
    /// Either a scheme URL (<c>https://host/topic</c>), or a bare host with a path
    /// (<c>ntfy.sh/topic</c>, <c>ntfy.sh:443/topic</c>). A bare host with no path is left
    /// alone: it carries no topic and is useful when diagnosing DNS failures.
    /// </summary>
    [GeneratedRegex(
        @"\b[a-z][a-z0-9+.\-]*://\S*|\b(?:[a-z0-9\-]+\.)+[a-z]{2,}(?::\d+)?/\S*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlShaped();
}
