using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Worker.Hosting;
using Worker.Pass;
using Worker.Tests.Pass;

namespace Worker.Tests.Hosting;

/// <summary>The two run modes, driven through a real generic host with a fake pass (no database, no network).</summary>
public sealed class PassHostedServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class ScriptedPass : INightlyPass
    {
        private readonly Func<int, CancellationToken, Task<int>> _script;
        private int _runs;
        private readonly TaskCompletionSource[] _started = [new(), new(), new(), new()];
        private readonly CancellationToken[] _tokens = new CancellationToken[4];

        public ScriptedPass(Func<int, int> script) : this((n, _) => Task.FromResult(script(n)))
        {
        }

        public ScriptedPass(Func<int, CancellationToken, Task<int>> script) => _script = script;

        public int Runs => Volatile.Read(ref _runs);

        /// <summary>Completes when the n-th (1-based) run has begun.</summary>
        public Task Started(int n) => _started[n - 1].Task.WaitAsync(Timeout);

        /// <summary>The token the n-th run was given, which is the one the ledger would see.</summary>
        public CancellationToken TokenOf(int n) => _tokens[n - 1];

        public Task<int> RunAsync(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _runs);
            _tokens[n - 1] = ct;
            _started[n - 1].TrySetResult();
            return _script(n, ct);
        }
    }

    /// <summary>A pass that never finishes on its own: it waits on its token, like a hung HTTP call.</summary>
    private static async Task<int> HangUntilCancelled(CancellationToken ct)
    {
        await Task.Delay(System.Threading.Timeout.Infinite, ct);
        return ExitCodes.Ok;
    }

    private static HostApplicationBuilder NewBuilder(ScriptedPass pass, LogCapture? logs = null)
    {
        // DisableDefaults: no appsettings, no environment variables, no console logger.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<ExitCodeHolder>();
        builder.Services.AddScoped<INightlyPass>(_ => pass);
        if (logs is not null)
            builder.Logging.AddProvider(logs);
        return builder;
    }

    // ---- --once ----

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public async Task Once_runs_one_pass_and_stops_the_host_with_its_code(int code)
    {
        var pass = new ScriptedPass(_ => code);
        var builder = NewBuilder(pass);
        builder.Services.AddOncePass();
        using var host = builder.Build();
        var exit = host.Services.GetRequiredService<ExitCodeHolder>(); // RunAsync disposes the provider

        await host.RunAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout);

        Assert.Equal(1, pass.Runs);
        Assert.Equal(code, exit.Code);
    }

    [Fact]
    public async Task Once_turns_a_crashing_pass_into_exit_code_1_and_still_stops_the_host()
    {
        var pass = new ScriptedPass(_ => throw new InvalidOperationException("boom"));
        var builder = NewBuilder(pass);
        builder.Services.AddOncePass();
        using var host = builder.Build();
        var exit = host.Services.GetRequiredService<ExitCodeHolder>();

        await host.RunAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout);

        Assert.Equal(ExitCodes.Failure, exit.Code);
    }

    // ---- loop ----

    [Fact]
    public async Task Loop_runs_a_pass_at_start_and_again_after_one_interval()
    {
        var interval = TimeSpan.FromHours(1);
        var clock = new FakeTimeProvider();
        var pass = new ScriptedPass(_ => 0);
        var builder = NewBuilder(pass);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddPassLoop(interval);
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await pass.Started(1);
        Assert.Equal(1, pass.Runs);

        // Less than an interval: nothing yet.
        clock.Advance(interval - TimeSpan.FromSeconds(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, pass.Runs);

        clock.Advance(TimeSpan.FromSeconds(1));
        await pass.Started(2);
        Assert.Equal(2, pass.Runs);

        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, pass.Runs);
    }

    [Fact]
    public async Task Loop_keeps_going_after_a_failing_pass_and_after_a_crashing_one()
    {
        var interval = TimeSpan.FromMinutes(5);
        var clock = new FakeTimeProvider();
        var pass = new ScriptedPass(n => n switch
        {
            1 => 3,
            2 => throw new InvalidOperationException("boom"),
            _ => 0,
        });
        var builder = NewBuilder(pass);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddPassLoop(interval);
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await pass.Started(1);
        clock.Advance(interval);
        await pass.Started(2);
        clock.Advance(interval);
        await pass.Started(3);

        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, pass.Runs);
    }

    [Fact]
    public async Task Loop_exits_cleanly_when_the_host_stops()
    {
        var pass = new ScriptedPass(_ => 0);
        var builder = NewBuilder(pass);
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        builder.Services.AddPassLoop(TimeSpan.FromHours(1));
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await pass.Started(1);

        await host.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout);
        Assert.Equal(0, host.Services.GetRequiredService<ExitCodeHolder>().Code);
    }

    // ---- the hard deadline (backstop) ----

    [Fact]
    public async Task Once_a_pass_still_running_at_the_hard_deadline_is_cancelled_and_the_host_stops_with_7()
    {
        var clock = new FakeTimeProvider();
        var logs = new LogCapture();
        var pass = new ScriptedPass((_, ct) => HangUntilCancelled(ct));
        var builder = NewBuilder(pass, logs);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddOncePass();
        using var host = builder.Build();
        var exit = host.Services.GetRequiredService<ExitCodeHolder>();

        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await pass.Started(1);

        // One second short of the 270 s default: still running.
        clock.Advance(PassOptions.DefaultHardDeadline - TimeSpan.FromSeconds(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        Assert.False(pass.TokenOf(1).IsCancellationRequested);

        clock.Advance(TimeSpan.FromSeconds(1));
        await run.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.PassDeadline, exit.Code);
        // The token the pass hands to the ledger is the backstop's, so a send in flight would be
        // treated as our cancellation: no bookkeeping, rolled back (D25).
        Assert.True(pass.TokenOf(1).IsCancellationRequested);
        var error = Assert.Single(logs.Lines, l => l.StartsWith("Error ", StringComparison.Ordinal));
        Assert.Contains("hard deadline of 00:04:30", error);
        Assert.Contains("in-flight work was cancelled", error);
        Assert.Contains("stays Pending and the next run resends", error);
        Assert.DoesNotContain("Pass cancelled before it finished", logs.AllText);
    }

    [Fact]
    public async Task Once_the_hard_deadline_comes_from_the_pass_options()
    {
        var clock = new FakeTimeProvider();
        var pass = new ScriptedPass((_, ct) => HangUntilCancelled(ct));
        var builder = NewBuilder(pass);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton(new PassOptions { Deadline = TimeSpan.FromSeconds(5), HardDeadline = TimeSpan.FromSeconds(10) });
        builder.Services.AddOncePass();
        using var host = builder.Build();
        var exit = host.Services.GetRequiredService<ExitCodeHolder>();

        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await pass.Started(1);
        clock.Advance(TimeSpan.FromSeconds(10));
        await run.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.PassDeadline, exit.Code);
    }

    [Fact]
    public async Task Loop_the_backstop_cancels_one_pass_and_the_next_interval_still_runs()
    {
        var interval = TimeSpan.FromHours(1);
        var clock = new FakeTimeProvider();
        var logs = new LogCapture();
        var pass = new ScriptedPass((n, ct) => n == 1 ? HangUntilCancelled(ct) : Task.FromResult(ExitCodes.Ok));
        var builder = NewBuilder(pass, logs);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddPassLoop(interval);
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await pass.Started(1);

        clock.Advance(PassOptions.DefaultHardDeadline);
        // The rest of the interval: the timer's tick is kept even if pass 1 is still unwinding.
        clock.Advance(interval - PassOptions.DefaultHardDeadline);
        await pass.Started(2);

        Assert.True(pass.TokenOf(1).IsCancellationRequested);
        Assert.False(pass.TokenOf(2).IsCancellationRequested); // a fresh backstop per pass
        Assert.Contains("hard deadline of 00:04:30", logs.AllText);
        Assert.Contains("Pass finished with exit code 7", logs.AllText);

        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, pass.Runs);
    }

    [Fact]
    public async Task Once_stopping_the_host_mid_pass_is_not_reported_as_a_deadline()
    {
        var clock = new FakeTimeProvider();
        var logs = new LogCapture();
        var pass = new ScriptedPass((_, ct) => HangUntilCancelled(ct));
        var builder = NewBuilder(pass, logs);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddOncePass();
        using var host = builder.Build();
        var exit = host.Services.GetRequiredService<ExitCodeHolder>();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await pass.Started(1);
        // Ctrl+C / SIGTERM, well before the hard deadline.
        await host.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Failure, exit.Code);
        Assert.Contains("Pass cancelled before it finished", logs.AllText);
        Assert.DoesNotContain("hard deadline", logs.AllText);
    }

    [Fact]
    public async Task Loop_stopping_the_host_mid_pass_is_not_reported_as_a_deadline()
    {
        var clock = new FakeTimeProvider();
        var logs = new LogCapture();
        var pass = new ScriptedPass((_, ct) => HangUntilCancelled(ct));
        var builder = NewBuilder(pass, logs);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddPassLoop(TimeSpan.FromHours(1));
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await pass.Started(1);
        await host.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("hard deadline", logs.AllText);
        Assert.DoesNotContain("Pass crashed", logs.AllText);
        Assert.Equal(1, pass.Runs);
    }
}
