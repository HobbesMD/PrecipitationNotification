using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Worker.Hosting;
using Worker.Telemetry;
using Worker.Tests.Telemetry;

namespace Worker.Tests.Hosting;

/// <summary>
/// Disposing the host flushes telemetry; <see cref="BoundedShutdown"/> caps how long that may take,
/// so a dead collector can never push a <c>--once</c> run past Azure's 300 s replica timeout.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class BoundedShutdownTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

    private sealed class BlocksOnDispose(TimeSpan block) : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Thread.Sleep(block);
            Disposed = true;
        }
    }

    // Factory registrations: the container only disposes what it created itself.
    private static IHost BuildHost(Action<IServiceCollection> configure)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        configure(builder.Services);
        return builder.Build();
    }

    [Fact]
    public async Task A_disposal_that_blocks_is_abandoned_at_the_budget_and_says_so()
    {
        var host = BuildHost(s => s.AddSingleton(_ => new BlocksOnDispose(TimeSpan.FromSeconds(30))));
        host.Services.GetRequiredService<BlocksOnDispose>();
        var error = new StringWriter();

        var sw = Stopwatch.StartNew();
        await BoundedShutdown.DisposeWithinAsync(host, Budget, error, TimeProvider.System);
        sw.Stop();

        Assert.InRange(sw.Elapsed, Budget - TimeSpan.FromMilliseconds(50), Budget + TimeSpan.FromSeconds(2));
        Assert.Contains("did not finish within 1 s", error.ToString());
    }

    [Fact]
    public async Task A_quick_disposal_completes_and_says_nothing()
    {
        var blocker = new BlocksOnDispose(TimeSpan.Zero);
        var host = BuildHost(s => s.AddSingleton(_ => blocker));
        host.Services.GetRequiredService<BlocksOnDispose>();
        var error = new StringWriter();

        await BoundedShutdown.DisposeWithinAsync(host, Budget, error, TimeProvider.System);

        Assert.True(blocker.Disposed);
        Assert.Equal("", error.ToString());
    }

    private sealed class FailsToStart : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            throw new ArgumentException("Headers provided in an invalid format.");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_host_that_fails_to_start_returns_false_with_type_and_message_only()
    {
        // Program turns false into exit 1. Before, the exception escaped Program unhandled: a crash
        // with a stack trace instead of a config-error exit code.
        var host = BuildHost(s => s.AddHostedService<FailsToStart>());
        var error = new StringWriter();

        var started = await BoundedShutdown.RunAsync(host, Budget, error, TimeProvider.System);

        Assert.False(started);
        Assert.Equal(
            "Startup error: the host failed to start: ArgumentException: Headers provided in an invalid format.",
            error.ToString().TrimEnd());
    }

    [Fact]
    public async Task A_collector_that_never_answers_cannot_hold_the_exit_past_the_budget()
    {
        // The case that was measured: real providers, OTLP pointed at a server that accepts the
        // request and never responds. Unbounded, disposing them took about 10 s.
        using var collector = WireMockServer.Start();
        collector.Given(Request.Create().UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(60)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = collector.Url,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        }).Build();

        var host = BuildHost(s => s.AddWorkerTelemetry(configuration));
        var error = new StringWriter();

        var sw = Stopwatch.StartNew();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using (Instrumentation.Source.StartActivity("shutdown-probe")) { }
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("probe").LogWarning("probe");
        await host.StopAsync(TestContext.Current.CancellationToken);
        await BoundedShutdown.DisposeWithinAsync(host, Budget, error, TimeProvider.System);
        sw.Stop();

        Assert.True(sw.Elapsed < Budget + TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.Contains("did not finish", error.ToString());
    }
}
