using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Worker.Hosting;
using Worker.Nws;
using Worker.Resilience;

namespace Worker.Tests.Resilience;

/// <summary>
/// The gridpoint fetch has its own client and so its own breaker. Gridpoint failures cost only
/// the amounts (D11) and are not a fallback trigger (D36), so they must never open the breaker
/// that guards the hourly forecast. Real pipelines against WireMock: hourly always answers 200,
/// gridpoint always answers 500.
/// </summary>
public sealed class NwsBreakerIsolationTests : IDisposable
{
    /// <summary>Fast retries; a breaker that, once open, stays open for the rest of the test.</summary>
    private static readonly ResilienceSettings Settings = new()
    {
        AttemptTimeout = TimeSpan.FromSeconds(5),
        TotalTimeout = TimeSpan.FromSeconds(10),
        MaxAttempts = 3,
        BackoffBaseDelay = TimeSpan.FromMilliseconds(10),
        BreakerMinimumThroughput = 4,
        BreakerFailureRatio = 0.5,
        BreakerSamplingDuration = TimeSpan.FromSeconds(30),
        BreakDuration = TimeSpan.FromSeconds(30),
    };

    /// <summary>Each call makes 3 gridpoint attempts, so 3 calls are 9 samples: well past a minimum of 4.</summary>
    private const int Calls = 4;

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly List<ServiceProvider> _owned = [];

    public NwsBreakerIsolationTests()
    {
        _server.Given(Request.Create().WithPath("/hourly").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/geo+json")
                .WithBody(FixtureFiles.Read("nws/2026-10-29T1900Z_pre-dst-fallback/forecast-hourly.json")));
        _server.Given(Request.Create().WithPath("/grid").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));
    }

    private NwsPoint Point => new("America/New_York", _server.Url + "/hourly", _server.Url + "/grid");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Gridpoint_500s_open_only_the_grid_breaker_and_hourly_keeps_succeeding()
    {
        var nws = Client(separateGridClient: true);

        for (var i = 1; i <= Calls; i++)
        {
            // Never BrokenCircuitException: the hourly breaker never sees the gridpoint failures.
            var forecast = await nws.GetForecastAsync(Point, Ct);
            Assert.NotEmpty(forecast.Hours);
            Assert.Null(forecast.Amounts);
        }

        // By the last call the grid breaker was open: the gridpoint failed fast, without a request.
        var last = await nws.GetForecastAsync(Point, Ct);
        Assert.NotEmpty(last.Hours);
        Assert.Contains(nameof(BrokenCircuitException), last.AmountsUnavailableReason);
        Assert.Equal(Calls + 1, Requests("/hourly"));
        Assert.True(Requests("/grid") < (Calls + 1) * Settings.MaxAttempts, "the grid breaker never opened");
    }

    /// <summary>
    /// The contrast, kept so the reason for the second client stays visible: with one client (and so
    /// one breaker) for both, the same gridpoint failures open it and the healthy hourly call fails
    /// fast, which in the pass would mean a needless fallback to Open-Meteo.
    /// </summary>
    [Fact]
    public async Task With_one_shared_breaker_the_same_gridpoint_failures_would_break_hourly()
    {
        var nws = Client(separateGridClient: false);

        var broken = false;
        for (var i = 1; i <= Calls + 1 && !broken; i++)
        {
            try
            {
                await nws.GetForecastAsync(Point, Ct);
            }
            catch (BrokenCircuitException)
            {
                broken = true;
            }
        }

        Assert.True(broken, "expected the shared breaker to reject the hourly call");
    }

    /// <summary>The named clients exactly as <c>WorkerServices</c> registers them, or one shared client.</summary>
    private NwsClient Client(bool separateGridClient)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(WorkerServices.NwsClientName).AddGetResilience(Settings);
        services.AddHttpClient(WorkerServices.NwsGridClientName).AddGetResilience(Settings);
        var provider = services.BuildServiceProvider();
        _owned.Add(provider);
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        return new NwsClient(
            factory.CreateClient(WorkerServices.NwsClientName),
            new NwsOptions(_server.Url + "/"),
            separateGridClient ? factory.CreateClient(WorkerServices.NwsGridClientName) : null);
    }

    private int Requests(string path) =>
        _server.LogEntries.Count(e => e.RequestMessage?.Path == path);

    public void Dispose()
    {
        foreach (var provider in _owned)
            provider.Dispose();
        _server.Dispose();
    }
}
