using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Persistence;
using TestSupport;
using Worker.Alerts;
using Worker.Forecast;

namespace Worker.Tests.Alerts;

/// <summary>
/// Exactly one alert per location per night, against real Postgres. Every count here is
/// a count of publishes, the thing the phone would see. Keys come from the real
/// <c>NightWindow.Resolve</c>, so these also prove D2's key is the one the ledger uses.
/// </summary>
public sealed class PostgresAlertLedgerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private LedgerDb _db = null!;
    private int _locationId;
    private readonly ManualClock _clock = new(LedgerDb.T0);

    public async ValueTask InitializeAsync()
    {
        _db = await LedgerDb.CreateAsync(postgres);
        _locationId = await _db.AddLocationAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly NightWindow Tonight = Alert.NightFor(LedgerDb.T0);

    [Fact]
    public async Task First_send_publishes_once_and_records_Sent()
    {
        var publisher = new FakeAlertPublisher();
        await using var ctx = _db.NewContext();
        var ledger = new PostgresAlertLedger(ctx, publisher, _clock);

        var outcome = await ledger.SendAsync(_locationId, Tonight);

        Assert.Equal(SendOutcome.Sent, outcome);
        Assert.Equal(1, publisher.Calls);

        var row = await _db.RowAsync(_locationId, Tonight.NightDate);
        Assert.NotNull(row);
        Assert.Equal(AlertStatus.Sent, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(LedgerDb.T0, row.CreatedAt);
        Assert.Equal(LedgerDb.T0, row.SentAt);
        Assert.Equal(LedgerDb.T0, row.LastAttemptAt);
        Assert.Equal(80, row.PeakProbability);
        Assert.Equal("Rain", row.Kind);
        Assert.Equal(Alert.Message().Title, row.Title);
        Assert.Null(row.LastError);
        Assert.True(await ledger.IsSentAsync(_locationId, Tonight.NightDate, Ct));
    }

    [Fact]
    public async Task Second_send_for_the_same_key_publishes_nothing()
    {
        var publisher = new FakeAlertPublisher();
        await using var ctx = _db.NewContext();
        var ledger = new PostgresAlertLedger(ctx, publisher, _clock);

        await ledger.SendAsync(_locationId, Tonight);
        _clock.Now = LedgerDb.T0.AddHours(2);
        var second = await ledger.SendAsync(_locationId, Tonight);

        Assert.Equal(SendOutcome.AlreadySent, second);
        Assert.Equal(1, publisher.Calls);

        var row = await _db.RowAsync(_locationId, Tonight.NightDate);
        Assert.Equal(1, row!.Attempts);
        Assert.Equal(LedgerDb.T0, row.SentAt); // untouched by the no-op
    }

    /// <summary>
    /// D2 tie-in: the Azure retry (D20) after the 23:00 UTC run lands after local midnight.
    /// It must still resolve to the night that started on the 29th, or the unique key
    /// can't stop the second alert.
    /// </summary>
    [Fact]
    public async Task Retry_after_local_midnight_resolves_to_the_same_night_and_publishes_nothing()
    {
        var run = Alert.NightFor(new DateTimeOffset(2026, 10, 29, 23, 0, 0, TimeSpan.Zero));   // 7 PM EDT
        var retry = Alert.NightFor(new DateTimeOffset(2026, 10, 30, 5, 0, 0, TimeSpan.Zero));  // 1 AM EDT, next local day
        Assert.Equal(new DateOnly(2026, 10, 29), run.NightDate);
        Assert.Equal(run.NightDate, retry.NightDate);

        var publisher = new FakeAlertPublisher();
        await using (var ctx = _db.NewContext())
            Assert.Equal(SendOutcome.Sent, await new PostgresAlertLedger(ctx, publisher, _clock).SendAsync(_locationId, run));

        // A separate process in real life: a new context, a later clock.
        _clock.Now = new DateTimeOffset(2026, 10, 30, 5, 0, 0, TimeSpan.Zero);
        await using (var ctx = _db.NewContext())
        {
            var ledger = new PostgresAlertLedger(ctx, publisher, _clock);
            Assert.True(await ledger.IsSentAsync(_locationId, retry.NightDate, Ct));
            Assert.Equal(SendOutcome.AlreadySent, await ledger.SendAsync(_locationId, retry));
        }

        Assert.Equal(1, publisher.Calls);
    }

    /// <summary>
    /// The one duplicate the design accepts (at-least-once). ntfy may have delivered, but
    /// the response never arrived, so the ledger can't know. It records the attempt, lets
    /// the exception go, and the next attempt sends again: a duplicate beats a missed alert.
    /// </summary>
    [Fact]
    public async Task Lost_response_is_resent_this_is_the_accepted_duplicate()
    {
        const string secretTopic = "s3cr3t-topic-do-not-store";
        var publisher = new FakeAlertPublisher
        {
            Fail = call => call == 1
                ? new HttpRequestException($"Error while copying content to a stream (https://ntfy.sh/{secretTopic}).")
                : null,
        };

        await using (var ctx = _db.NewContext())
        {
            var ledger = new PostgresAlertLedger(ctx, publisher, _clock);
            await Assert.ThrowsAsync<HttpRequestException>(() => ledger.SendAsync(_locationId, Tonight));
        }

        var afterFailure = await _db.RowAsync(_locationId, Tonight.NightDate);
        Assert.NotNull(afterFailure);
        Assert.Equal(AlertStatus.Pending, afterFailure.Status);
        Assert.Equal(1, afterFailure.Attempts);
        Assert.Null(afterFailure.SentAt);
        Assert.StartsWith("System.Net.Http.HttpRequestException: ", afterFailure.LastError);
        Assert.DoesNotContain(secretTopic, afterFailure.LastError);
        Assert.DoesNotContain("://", afterFailure.LastError);

        _clock.Now = LedgerDb.T0.AddMinutes(5);
        await using (var ctx = _db.NewContext())
        {
            var ledger = new PostgresAlertLedger(ctx, publisher, _clock);
            Assert.False(await ledger.IsSentAsync(_locationId, Tonight.NightDate, Ct));
            Assert.Equal(SendOutcome.Sent, await ledger.SendAsync(_locationId, Tonight));
        }

        Assert.Equal(2, publisher.Calls);
        var sent = await _db.RowAsync(_locationId, Tonight.NightDate);
        Assert.Equal(AlertStatus.Sent, sent!.Status);
        Assert.Equal(2, sent.Attempts);
        Assert.Equal(LedgerDb.T0.AddMinutes(5), sent.SentAt);
        Assert.Equal(LedgerDb.T0, sent.CreatedAt);
    }

    /// <summary>
    /// <see cref="HttpClient"/> reports its own timeout as a <see cref="TaskCanceledException"/>.
    /// That isn't the caller cancelling; it's the lost-response case, and it must be recorded.
    /// </summary>
    [Fact]
    public async Task Http_timeout_is_a_recorded_failure_not_a_cancellation()
    {
        var publisher = new FakeAlertPublisher
        {
            Fail = _ => new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
                new TimeoutException()),
        };
        await using var ctx = _db.NewContext();
        var ledger = new PostgresAlertLedger(ctx, publisher, _clock);

        await Assert.ThrowsAsync<TaskCanceledException>(() => ledger.SendAsync(_locationId, Tonight));

        var row = await _db.RowAsync(_locationId, Tonight.NightDate);
        Assert.Equal(AlertStatus.Pending, row!.Status);
        Assert.Equal(1, row.Attempts);
        Assert.StartsWith("System.Threading.Tasks.TaskCanceledException: ", row.LastError);
    }

    public enum Crash { DisposeContextAndConnection, TerminateBackend }

    /// <summary>
    /// The process dies after claiming the row and while publishing, before anything commits.
    /// Postgres rolls the transaction back, so nothing records a send that may not have
    /// happened, and the next run publishes.
    /// </summary>
    [Theory]
    [InlineData(Crash.DisposeContextAndConnection)]
    [InlineData(Crash.TerminateBackend)]
    public async Task Crash_mid_send_leaves_no_Sent_row_and_the_next_run_publishes(Crash crash)
    {
        var stuck = new FakeAlertPublisher { Block = new TaskCompletionSource() };

        // Own the physical connection (no pool) so "the process died" really closes the socket.
        var conn = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(_db.ConnectionString) { Pooling = false }.ConnectionString);
        await conn.OpenAsync(Ct);
        var ctx = new PrecipDbContext(new DbContextOptionsBuilder<PrecipDbContext>().UseNpgsql(conn).Options);

        using var abandon = new CancellationTokenSource();
        var doomed = new PostgresAlertLedger(ctx, stuck, _clock).SendAsync(_locationId, Tonight, abandon.Token);
        await stuck.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // Mid-send, the row is claimed but uncommitted: invisible to everyone else.
        Assert.Null(await _db.RowAsync(_locationId, Tonight.NightDate));

        if (crash == Crash.TerminateBackend)
        {
            await using var admin = new NpgsqlConnection(_db.ConnectionString);
            await admin.OpenAsync(Ct);
            await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin);
            kill.Parameters.AddWithValue("pid", conn.ProcessID);
            await kill.ExecuteScalarAsync(Ct);
        }
        else
        {
            await ctx.DisposeAsync();
            await conn.DisposeAsync();
        }

        // The next run. If the dead backend still holds the claim for a moment, the INSERT waits for it.
        var publisher = new FakeAlertPublisher();
        await using (var next = _db.NewContext())
            Assert.Equal(SendOutcome.Sent, await new PostgresAlertLedger(next, publisher, _clock).SendAsync(_locationId, Tonight));
        Assert.Equal(1, publisher.Calls);

        var row = await _db.RowAsync(_locationId, Tonight.NightDate);
        Assert.Equal(AlertStatus.Sent, row!.Status);

        // Let the abandoned send unwind; whatever it throws, it has nothing left to write to.
        abandon.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => doomed);
        await ctx.DisposeAsync();
        await conn.DisposeAsync();
        Assert.Equal(AlertStatus.Sent, (await _db.RowAsync(_locationId, Tonight.NightDate))!.Status);
    }

    [Fact]
    public async Task Different_night_or_different_location_sends_independently()
    {
        var otherLocation = await _db.AddLocationAsync("ledger-test-2");
        var tomorrow = Alert.NightFor(LedgerDb.T0.AddDays(1));
        Assert.Equal(Tonight.NightDate.AddDays(1), tomorrow.NightDate);

        var publisher = new FakeAlertPublisher();
        await using var ctx = _db.NewContext();
        var ledger = new PostgresAlertLedger(ctx, publisher, _clock);

        Assert.Equal(SendOutcome.Sent, await ledger.SendAsync(_locationId, Tonight));
        Assert.Equal(SendOutcome.Sent, await ledger.SendAsync(_locationId, tomorrow));
        Assert.Equal(SendOutcome.Sent, await ledger.SendAsync(otherLocation, Tonight));

        Assert.Equal(3, publisher.Calls);
        Assert.Equal(3, await _db.RowCountAsync());
    }

    [Fact]
    public async Task Earlier_Pending_row_does_not_block_a_send()
    {
        await _db.SeedPendingAsync(_locationId, Tonight.NightDate, attempts: 1);

        var publisher = new FakeAlertPublisher();
        await using var ctx = _db.NewContext();
        var ledger = new PostgresAlertLedger(ctx, publisher, _clock);

        Assert.False(await ledger.IsSentAsync(_locationId, Tonight.NightDate, Ct));
        Assert.Equal(SendOutcome.Sent, await ledger.SendAsync(_locationId, Tonight));

        Assert.Equal(1, publisher.Calls);
        var row = await _db.RowAsync(_locationId, Tonight.NightDate);
        Assert.Equal(AlertStatus.Sent, row!.Status);
        Assert.Equal(2, row.Attempts);
        Assert.Equal(LedgerDb.T0.AddHours(-1), row.CreatedAt); // the same row, not a new one
        Assert.Equal(1, await _db.RowCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_throws_and_leaves_the_row_unchanged(bool earlierAttempt)
    {
        if (earlierAttempt)
            await _db.SeedPendingAsync(_locationId, Tonight.NightDate, attempts: 1);
        var before = await _db.RowAsync(_locationId, Tonight.NightDate);

        var publisher = new FakeAlertPublisher { Block = new TaskCompletionSource() };
        await using var ctx = _db.NewContext();
        var ledger = new PostgresAlertLedger(ctx, publisher, _clock);
        using var cts = new CancellationTokenSource();
        _clock.Now = LedgerDb.T0.AddMinutes(30);

        var send = ledger.SendAsync(_locationId, Tonight, cts.Token);
        await publisher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        var after = await _db.RowAsync(_locationId, Tonight.NightDate);
        if (before is null)
        {
            Assert.Null(after);
        }
        else
        {
            Assert.NotNull(after);
            Assert.Equivalent(before, after, strict: true);
        }
    }

    [Theory]
    [InlineData("Response status code does not indicate success: 500 (Internal Server Error).",
        "Response status code does not indicate success: 500 (Internal Server Error).")]
    [InlineData("POST https://ntfy.sh/my-secret-topic failed", "POST [url removed] failed")]
    [InlineData("connect to ntfy.sh:443/my-secret-topic refused", "connect to [url removed] refused")]
    [InlineData("No such host is known. (ntfy.sh:443)", "No such host is known. (ntfy.sh:443)")]
    public void Failure_text_never_carries_a_url(string message, string stored)
    {
        Assert.Equal(stored, PostgresAlertLedger.ScrubUrls(message));
        Assert.DoesNotContain("my-secret-topic", PostgresAlertLedger.DescribeFailure(new HttpRequestException(message)));
    }
}
