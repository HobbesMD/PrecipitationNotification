using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Worker.Hosting;
using Worker.Pass;

namespace Worker.Tests.Hosting;

/// <summary>The two run modes, driven through a real generic host with a fake pass (no database, no network).</summary>
public sealed class PassHostedServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class ScriptedPass(Func<int, int> script) : INightlyPass
    {
        private int _runs;
        private readonly TaskCompletionSource[] _started = [new(), new(), new(), new()];

        public int Runs => Volatile.Read(ref _runs);

        /// <summary>Completes when the n-th (1-based) run has begun.</summary>
        public Task Started(int n) => _started[n - 1].Task.WaitAsync(Timeout);

        public Task<int> RunAsync(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _runs);
            _started[n - 1].TrySetResult();
            return Task.FromResult(script(n));
        }
    }

    private static HostApplicationBuilder NewBuilder(ScriptedPass pass)
    {
        // DisableDefaults: no appsettings, no environment variables, no console logger.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<ExitCodeHolder>();
        builder.Services.AddScoped<INightlyPass>(_ => pass);
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
}
