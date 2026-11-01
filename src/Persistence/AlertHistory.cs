namespace Persistence;

/// <summary>
/// Stored as text, not an integer, so the table reads on its own and a reordered
/// enum can't silently change what old rows mean. A CHECK limits it to these names.
/// </summary>
public enum AlertStatus
{
    Pending,
    Sent,
}

/// <summary>
/// One row per location per night: the idempotency record. The unique index on
/// <c>(location_id, night_date)</c>, <see cref="PrecipDbContext.AlertHistoryUniqueIndex"/>,
/// is what guarantees "exactly one alert per location per night". It lives in the
/// database, not in memory, so it holds across retries, restarts and concurrent runs.
///
/// The location FK is RESTRICT, not CASCADE: deleting a location must not quietly
/// erase the record of what was sent for it.
///
/// Every <see cref="DateTimeOffset"/> is UTC; Npgsql refuses to write a
/// <c>timestamptz</c> with any other offset.
/// </summary>
public sealed class AlertHistory
{
    public long Id { get; set; }

    public int LocationId { get; set; }

    public Location Location { get; set; } = null!;

    /// <summary>The local date the overnight window starts on (D2), not a UTC date.</summary>
    public DateOnly NightDate { get; set; }

    public AlertStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastAttemptAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public int? PeakProbability { get; set; }

    public string? Kind { get; set; }

    public string? Title { get; set; }

    /// <summary>
    /// Why the last attempt failed. Must never contain the ntfy topic, a coordinate
    /// or a connection string: it is stored and may be shown by the API.
    /// </summary>
    public string? LastError { get; set; }
}
