using Microsoft.EntityFrameworkCore;
using Npgsql;
using Persistence;
using TestSupport;
using Worker.Alerts;
using Worker.Forecast;
using Worker.Notifications;

namespace Worker.Tests.Alerts;

/// <summary>
/// Stands in for ntfy. Counts every call (the number the idempotency tests are about)
/// and can be told to wait, block until released, or throw.
/// </summary>
internal sealed class FakeAlertPublisher : IAlertPublisher
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>How long each publish takes. Long enough, it holds the row lock while others queue.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>If set, called with the 1-based call number; a non-null result is thrown.</summary>
    public Func<int, Exception?>? Fail { get; set; }

    /// <summary>If set, each publish signals <see cref="Entered"/> and then waits for this (or cancellation).</summary>
    public TaskCompletionSource? Block { get; init; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task PublishAsync(ComposedMessage msg, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        Entered.TrySetResult();

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, ct);

        if (Block is not null)
            await Block.Task.WaitAsync(ct);

        if (Fail?.Invoke(call) is { } ex)
            throw ex;
    }
}

/// <summary>A clock the test sets. Always UTC: Npgsql refuses any other offset for timestamptz.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>One migrated database per test, plus the helpers every ledger test needs.</summary>
internal sealed class LedgerDb(string connectionString)
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 29, 23, 0, 0, TimeSpan.Zero);

    public string ConnectionString { get; } = connectionString;

    public static async Task<LedgerDb> CreateAsync(PostgresFixture postgres) =>
        new(await postgres.CreateDatabaseAsync());

    public PrecipDbContext NewContext() => new(PostgresFixture.Options(ConnectionString));

    /// <summary>A made-up location; real coordinates never appear in source (D21).</summary>
    public async Task<int> AddLocationAsync(string name = "ledger-test")
    {
        await using var db = NewContext();
        var location = new Location { Name = name, Latitude = 10.0000m, Longitude = 20.0000m, CreatedAt = T0 };
        db.Locations.Add(location);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return location.Id;
    }

    public async Task<AlertHistory?> RowAsync(int locationId, DateOnly night)
    {
        await using var db = NewContext();
        return await db.AlertHistory.AsNoTracking()
            .SingleOrDefaultAsync(h => h.LocationId == locationId && h.NightDate == night,
                TestContext.Current.CancellationToken);
    }

    public async Task<int> RowCountAsync()
    {
        await using var db = NewContext();
        return await db.AlertHistory.CountAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Leaves the trace of an earlier failed attempt, as raw SQL beneath the ledger.</summary>
    public async Task SeedPendingAsync(int locationId, DateOnly night, int attempts = 1)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO alert_history (location_id, night_date, status, attempts, created_at, last_attempt_at, last_error)
            VALUES (@location_id, @night, 'Pending', @attempts, @at, @at, 'System.Net.Http.HttpRequestException: earlier failure')
            """, conn);
        cmd.Parameters.AddWithValue("location_id", locationId);
        cmd.Parameters.AddWithValue("night", night);
        cmd.Parameters.AddWithValue("attempts", attempts);
        cmd.Parameters.AddWithValue("at", T0.AddHours(-1));
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

internal static class Alert
{
    public static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static readonly TimeOnly WindowStart = new(20, 0);
    public static readonly TimeOnly WindowEnd = new(7, 0);

    /// <summary>The night a run at <paramref name="runAt"/> evaluates in New York, via the real D2 resolver.</summary>
    public static NightWindow NightFor(DateTimeOffset runAt) =>
        NightWindow.Resolve(runAt, NewYork, WindowStart, WindowEnd);

    public static NightAssessment Assessment(NightWindow window, int peak = 80, PrecipKind kind = PrecipKind.Rain) =>
        new(window, [], peak, kind, LiquidInches: 0.3, SnowInches: 0, IceInches: 0, AlertPriority.Default);

    public static ComposedMessage Message(string title = "Overnight rain: 0.3\" expected, up to 80%") =>
        new(title, "Thu Oct 29 8:00 PM -> Fri Oct 30 7:00 AM", "umbrella", "default");

    public static Task<SendOutcome> SendAsync(
        this PostgresAlertLedger ledger, int locationId, NightWindow window, CancellationToken ct = default) =>
        ledger.SendOnceAsync(
            locationId, window.NightDate, Assessment(window), Message(),
            ct == default ? TestContext.Current.CancellationToken : ct);
}
