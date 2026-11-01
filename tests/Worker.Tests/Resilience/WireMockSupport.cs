using Microsoft.Extensions.DependencyInjection;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;
using Worker.Resilience;

namespace Worker.Tests.Resilience;

/// <summary>A scripted reply: a status code, optionally delayed.</summary>
internal sealed record Step(int Status, TimeSpan? Delay = null);

/// <summary>
/// A local WireMock server plus an <see cref="HttpClient"/> wired through the real
/// <see cref="GetResilience"/> pipeline. Nothing here touches the network beyond loopback.
/// </summary>
internal sealed class WireMockSupport : IDisposable
{
    /// <summary>Millisecond-scale settings so the retry and breaker paths run quickly.</summary>
    public static readonly ResilienceSettings Fast = new()
    {
        AttemptTimeout = TimeSpan.FromMilliseconds(300),
        MaxAttempts = 3,
        TotalTimeout = TimeSpan.FromSeconds(3),
        BackoffBaseDelay = TimeSpan.FromMilliseconds(10),
        BreakerSamplingDuration = TimeSpan.FromSeconds(2),
        BreakerMinimumThroughput = 4,
        BreakerFailureRatio = 0.5,
        BreakDuration = TimeSpan.FromSeconds(1),
    };

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly ServiceProvider _services;
    private int _arrived;

    public WireMockSupport(ResilienceSettings? settings = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("t").AddGetResilience(settings ?? Fast);
        _services = services.BuildServiceProvider();
        Client = _services.GetRequiredService<IHttpClientFactory>().CreateClient("t");
        WarmUp();
    }

    /// <summary>
    /// The first request to a fresh WireMock server takes several hundred milliseconds (JIT and
    /// socket setup), longer than the 300 ms attempt timeout. Send one unmatched request on a
    /// plain client, outside the resilience pipeline, so tests measure the pipeline and not startup.
    /// The request is cleared from the log afterwards.
    /// </summary>
    private void WarmUp()
    {
        using var plain = new HttpClient();
        using var _ = plain.GetAsync(_server.Url + "/warmup").GetAwaiter().GetResult();
        _server.ResetLogEntries();
    }

    public HttpClient Client { get; }

    public string Url => _server.Url + "/x";

    /// <summary>How many requests have actually reached the server.</summary>
    public int RequestCount => _server.LogEntries.Count();

    /// <summary>
    /// Requests that have reached the server's handler, counted on arrival. WireMock writes its log
    /// entry only after the response (including any delay) completes, so for delayed replies the log
    /// lags; assert on this for those.
    /// </summary>
    public int Arrived => Math.Max(Volatile.Read(ref _arrived) + 1, 0);

    /// <summary>Replies with <paramref name="steps"/> in order; the last one repeats forever.</summary>
    public void Sequence(params Step[] steps)
    {
        // A request counter picks the step when the request arrives. WireMock scenarios advance
        // their state only after a delayed response has finished, so a retry that arrives while an
        // earlier delayed reply is still pending would be served the same step again.
        Interlocked.Exchange(ref _arrived, -1);
        _server.Given(Request.Create().WithPath("/x").UsingGet())
            .RespondWith(Response.Create().WithCallback(async _ =>
            {
                var n = Interlocked.Increment(ref _arrived);
                var step = steps[Math.Min(n, steps.Length - 1)];
                if (step.Delay is { } delay)
                    await Task.Delay(delay);
                return new ResponseMessage
                {
                    StatusCode = step.Status,
                    BodyData = new BodyData { DetectedBodyType = BodyType.String, BodyAsString = "{}" },
                };
            }));
    }

    /// <summary>Drops all mappings and the request log, for a test that changes the script midway.</summary>
    public void Reset() => _server.Reset();

    public void Dispose()
    {
        Client.Dispose();
        _services.Dispose();
        _server.Dispose();
    }
}
