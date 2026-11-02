using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Worker.Hosting;
using Worker.Pass;
using Worker.Telemetry;

namespace Worker.Tests.Telemetry;

/// <summary>
/// D47. <c>precip.passes</c> is counted in <see cref="PassBackstop"/>, where every pass ends, so a
/// night the hard deadline or a shutdown cut short is in the metric with the code the process
/// exits with, not missing from it. Exactly one count per pass, on every path.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class PassBackstopMetricsTests
{
    private sealed class ScriptedPass(Func<CancellationToken, Task<int>> script) : INightlyPass
    {
        public Task<int> RunAsync(CancellationToken ct) => script(ct);
    }

    private static async Task<(List<long> ExitCodes, Exception? Thrown, int Returned)> RunAsync(
        Func<CancellationToken, Task<int>> script, FakeTimeProvider? clock = null, CancellationToken stopping = default,
        Action? whenStarted = null)
    {
        var codes = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Instrumentation.Name && instrument.Name == "precip.passes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == Instrumentation.Attributes.ExitCode)
                    lock (codes)
                        codes.Add(Convert.ToInt64(tag.Value));
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddScoped<INightlyPass>(_ => new ScriptedPass(script));
        await using var provider = services.BuildServiceProvider();

        var run = PassBackstop.RunAsync(
            provider.GetRequiredService<IServiceScopeFactory>(), new PassOptions(), clock ?? new FakeTimeProvider(),
            NullLogger.Instance, stopping);
        whenStarted?.Invoke();
        try
        {
            var returned = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return (codes, null, returned);
        }
        catch (Exception ex)
        {
            return (codes, ex, -1);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    public async Task A_finished_pass_is_counted_once_with_its_code(int code)
    {
        var (codes, thrown, returned) = await RunAsync(_ => Task.FromResult(code));

        Assert.Null(thrown);
        Assert.Equal(code, returned);
        Assert.Equal([(long)code], codes);
    }

    [Fact]
    public async Task A_pass_the_hard_deadline_cancels_is_counted_as_7()
    {
        var clock = new FakeTimeProvider();
        var started = new TaskCompletionSource();
        var (codes, thrown, returned) = await RunAsync(
            async ct =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            },
            clock,
            whenStarted: () =>
            {
                started.Task.Wait(TimeSpan.FromSeconds(10));
                clock.Advance(PassOptions.DefaultHardDeadline);
            });

        Assert.Null(thrown);
        Assert.Equal(ExitCodes.PassDeadline, returned);
        Assert.Equal([(long)ExitCodes.PassDeadline], codes);
    }

    [Fact]
    public async Task A_pass_stopped_by_shutdown_is_counted_as_1_and_still_propagates()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var (codes, thrown, _) = await RunAsync(
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            },
            stopping: stopping.Token);

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Equal([(long)ExitCodes.Failure], codes);
    }

    [Fact]
    public async Task A_crashing_pass_is_counted_as_1_and_still_propagates()
    {
        var (codes, thrown, _) = await RunAsync(_ => throw new InvalidOperationException("boom"));

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal([(long)ExitCodes.Failure], codes);
    }
}
