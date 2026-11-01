using Worker.Forecast;

namespace Worker.Alerts;

/// <summary>What <see cref="IAlertLedger.SendOnceAsync"/> did.</summary>
internal enum SendOutcome
{
    /// <summary>This call published the alert.</summary>
    Sent,

    /// <summary>The alert for this location and night had already gone out; nothing was published.</summary>
    AlreadySent,
}

/// <summary>
/// Exactly one alert per location per night (CLAUDE.md problem 2), keyed on
/// <c>(locationId, nightDate)</c> and enforced by the database's unique index
/// (<see cref="Persistence.PrecipDbContext.AlertHistoryUniqueIndex"/>), not by memory,
/// so it holds across retries (D20), restarts and overlapping runs.
/// </summary>
internal interface IAlertLedger
{
    /// <summary>True if the alert for this location and night has already been sent.</summary>
    Task<bool> IsSentAsync(int locationId, DateOnly nightDate, CancellationToken ct);

    /// <summary>
    /// Publishes <paramref name="msg"/> unless this location and night was already sent,
    /// and records the outcome. A publish failure throws; it never reports <see cref="SendOutcome.Sent"/>.
    /// </summary>
    Task<SendOutcome> SendOnceAsync(
        int locationId, DateOnly nightDate, NightAssessment assessment, ComposedMessage msg, CancellationToken ct);
}
