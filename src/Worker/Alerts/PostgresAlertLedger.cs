using Persistence;
using Worker.Forecast;
using Worker.Notifications;

namespace Worker.Alerts;

/// <summary>
/// <see cref="IAlertLedger"/> over the <c>alert_history</c> table. Timestamps come
/// from <see cref="TimeProvider"/> (UTC), never the host clock, so tests control them.
/// </summary>
internal sealed class PostgresAlertLedger(PrecipDbContext db, IAlertPublisher publisher, TimeProvider clock)
    : IAlertLedger
{
    public Task<bool> IsSentAsync(int locationId, DateOnly nightDate, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<SendOutcome> SendOnceAsync(
        int locationId, DateOnly nightDate, NightAssessment assessment, ComposedMessage msg, CancellationToken ct) =>
        throw new NotImplementedException();
}
