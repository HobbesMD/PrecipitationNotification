using Microsoft.EntityFrameworkCore;
using Persistence;
using TestSupport;
using Worker.Alerts;
using Worker.Forecast;

namespace Worker.Tests.Alerts;

/// <summary>
/// Overlapping runs for the same location and night: the scheduled run and a manual
/// one, or a run and its Azure retry (D20) if the first outlives its timeout. Each
/// contender has its own context and connection, as separate processes would, and they
/// are released together so they genuinely race. The 200 ms publish holds the row lock
/// long enough for every other contender to be queued behind it.
/// </summary>
public sealed class PostgresAlertLedgerConcurrencyTests(PostgresFixture postgres)
{
    private const int Contenders = 8;
    private const int Rounds = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="earlierAttempt">
    /// False: a brand-new night, where contenders serialize on the unique index (the INSERT
    /// waits for the winner's uncommitted row). True: an earlier attempt left the row Pending,
    /// so the INSERT never waits and only the FOR UPDATE row lock keeps them apart.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eight_simultaneous_senders_publish_exactly_once(bool earlierAttempt)
    {
        var db = await LedgerDb.CreateAsync(postgres);
        var locationId = await db.AddLocationAsync();
        var clock = new ManualClock(LedgerDb.T0);

        for (var round = 0; round < Rounds; round++)
        {
            // A fresh night per round, from the real resolver.
            var night = Alert.NightFor(LedgerDb.T0.AddDays(round));
            if (earlierAttempt)
                await db.SeedPendingAsync(locationId, night.NightDate, attempts: 1);

            var publisher = new FakeAlertPublisher { Delay = TimeSpan.FromMilliseconds(200) };
            var outcomes = await RaceAsync(db, Contenders, publisher, clock, locationId, night);

            Assert.Equal(1, publisher.Calls);
            Assert.Equal(1, outcomes.Count(o => o == SendOutcome.Sent));
            Assert.Equal(Contenders - 1, outcomes.Count(o => o == SendOutcome.AlreadySent));

            var row = await db.RowAsync(locationId, night.NightDate);
            Assert.Equal(AlertStatus.Sent, row!.Status);
            Assert.Equal(earlierAttempt ? 2 : 1, row.Attempts);
        }

        Assert.Equal(Rounds, await db.RowCountAsync());
    }

    private static async Task<SendOutcome[]> RaceAsync(
        LedgerDb db, int contenders, FakeAlertPublisher publisher, ManualClock clock, int locationId, NightWindow night)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ready = new CountdownEvent(contenders);

        var tasks = Enumerable.Range(0, contenders).Select(_ => Task.Run(async () =>
        {
            await using var ctx = db.NewContext();
            // Connect before the gate so the race is over the row, not over who connects first.
            await ctx.Database.OpenConnectionAsync(Ct);
            var ledger = new PostgresAlertLedger(ctx, publisher, clock);

            ready.Signal();
            await start.Task;
            return await ledger.SendAsync(locationId, night);
        }, Ct)).ToArray();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(30), Ct), "Contenders never became ready.");
        start.SetResult();

        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), Ct);
    }
}
