using Worker.Forecast;

namespace Worker.Notifications;

/// <summary>
/// Delivers one composed alert. The seam the alert ledger sends through, so the
/// idempotency logic can be tested without ntfy and a second channel can be added
/// without touching it. Implementations throw on failure; they never retry or
/// deduplicate themselves: exactly-once is the ledger's job, enforced in the database.
/// </summary>
internal interface IAlertPublisher
{
    Task PublishAsync(ComposedMessage msg, CancellationToken ct);
}
