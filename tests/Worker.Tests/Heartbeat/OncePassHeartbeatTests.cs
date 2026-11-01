using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Worker.Heartbeat;
using Worker.Hosting;
using Worker.Pass;

namespace Worker.Tests.Heartbeat;

/// <summary>How <c>--once</c> brackets the pass with the heartbeat, through a real generic host and a fake pass.</summary>
public sealed class OncePassHeartbeatTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class FakePass(Func<CancellationToken, Task<int>> script, List<string> events) : INightlyPass
    {
        public Task<int> RunAsync(CancellationToken ct)
        {
            lock (events)
                events.Add("pass");
            return script(ct);
        }
    }

    private sealed class RecordingHeartbeat(
        List<string> events, Func<Task>? slow = null, Func<Task>? faultOnFinish = null, Func<Task>? faultOnStart = null) : IHeartbeat
    {
        public async Task StartAsync(CancellationToken ct)
        {
            lock (events)
                events.Add("start");
            if (slow is not null)
                await slow();
            if (faultOnStart is not null)
                await faultOnStart();
        }

        public async Task FinishAsync(int exitCode, CancellationToken ct)
        {
            lock (events)
                events.Add($"finish:{exitCode}");
            if (slow is not null)
                await slow();
            if (faultOnFinish is not null)
                await faultOnFinish();
        }
    }

    private static async Task<(int Exit, List<string> Events)> RunOnce(
        Func<CancellationToken, Task<int>> script, Func<List<string>, IHeartbeat>? heartbeat = null, FakeTimeProvider? clock = null,
        Func<Task>? whenPassStarted = null)
    {
        var events = new List<string>();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<ExitCodeHolder>();
        builder.Services.AddScoped<INightlyPass>(_ => new FakePass(script, events));
        if (clock is not null)
            builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IHeartbeat>(heartbeat?.Invoke(events) ?? new RecordingHeartbeat(events));
        builder.Services.AddOncePass();
        using var host = builder.Build();
        var exit = host.Services.GetRequiredService<ExitCodeHolder>();

        var run = host.RunAsync(TestContext.Current.CancellationToken);
        if (whenPassStarted is not null)
            await whenPassStarted();
        await run.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        return (exit.Code, events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Start_comes_before_the_pass_and_finish_after_it_with_the_passs_code(int code)
    {
        var (exit, events) = await RunOnce(_ => Task.FromResult(code));

        Assert.Equal(["start", "pass", $"finish:{code}"], events);
        Assert.Equal(code, exit);
    }

    [Fact]
    public async Task A_throwing_pass_finishes_with_1()
    {
        var (exit, events) = await RunOnce(_ => throw new InvalidOperationException("boom"));

        Assert.Equal(["start", "pass", "finish:1"], events);
        Assert.Equal(ExitCodes.Failure, exit);
    }

    [Fact]
    public async Task The_hard_deadline_finishes_with_7()
    {
        var clock = new FakeTimeProvider();
        var started = new TaskCompletionSource();
        var (exit, events) = await RunOnce(
            async ct =>
            {
                started.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
                return 0;
            },
            clock: clock,
            whenPassStarted: async () =>
            {
                await started.Task.WaitAsync(Timeout);
                clock.Advance(PassOptions.DefaultHardDeadline);
            });

        Assert.Equal(["start", "pass", "finish:7"], events);
        Assert.Equal(ExitCodes.PassDeadline, exit);
    }

    [Fact]
    public async Task A_slow_heartbeat_does_not_change_the_exit_code()
    {
        var (exit, events) = await RunOnce(
            _ => Task.FromResult(3),
            e => new RecordingHeartbeat(e, () => Task.Delay(300)));

        Assert.Equal(["start", "pass", "finish:3"], events);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task A_faulting_finish_does_not_change_the_exit_code_or_stop_the_host_abnormally()
    {
        // The interface promises never to throw, so this is a broken implementation. OncePassService
        // guards the call anyway: the host stops normally and the pass's code stands.
        var (exit, events) = await RunOnce(
            _ => Task.FromResult(3),
            e => new RecordingHeartbeat(e, faultOnFinish: () => throw new InvalidOperationException("heartbeat broke")));

        Assert.Equal(["start", "pass", "finish:3"], events);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task A_faulting_start_still_runs_the_pass()
    {
        // A broken start ping must never cost the night's alert.
        var (exit, events) = await RunOnce(
            _ => Task.FromResult(0),
            e => new RecordingHeartbeat(e, faultOnStart: () => throw new InvalidOperationException("heartbeat broke")));

        Assert.Equal(["start", "pass", "finish:0"], events);
        Assert.Equal(0, exit);
    }
}
