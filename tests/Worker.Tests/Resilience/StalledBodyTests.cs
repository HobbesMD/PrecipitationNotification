using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Worker.Notifications;
using Worker.Nws;
using Worker.OpenMeteo;
using Worker.Resilience;

namespace Worker.Tests.Resilience;

/// <summary>
/// A server that sends the response headers and part of the body, then stalls. Polly's timeouts
/// end at the headers, so only <see cref="HttpClient.Timeout"/> (set to the total timeout by the
/// resilience extensions) can end these calls, and only because every client buffers the body
/// inside <c>SendAsync</c>. The total timeout is 1 s and the attempt timeout 5 s, so a call that
/// ends near 1 s was ended by the client timeout, not by an attempt.
///
/// Each client is built the way <c>WorkerServices</c> builds it: through
/// <see cref="IHttpClientFactory"/> with the real pipeline and the real <c>SocketsHttpHandler</c>.
///
/// Every test has a 15 s xUnit timeout: without the fix these calls never return (checked by
/// reverting the client timeout to infinite), so a regression must fail rather than hang the run.
/// </summary>
public sealed class StalledBodyTests : IAsyncDisposable
{
    private static readonly ResilienceSettings Settings = new()
    {
        TotalTimeout = TimeSpan.FromSeconds(1),
        AttemptTimeout = TimeSpan.FromSeconds(5),
        MaxAttempts = 3,
        BackoffBaseDelay = TimeSpan.FromMilliseconds(10),
    };

    /// <summary>The client timeout plus generous slack for a loaded CI box; a hang would be unbounded.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(4);

    private readonly StallingServer _server = StallingServer.Start();
    private readonly List<ServiceProvider> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 15_000)]
    public async Task Nws_points_lookup_with_a_stalled_body_throws_near_the_total_timeout()
    {
        var nws = NwsClient();

        var (ex, elapsed) = await Time(() => nws.ResolvePointAsync("40.7128,-74.006", Ct));

        AssertClientTimeout(ex, elapsed);
        Assert.Equal(1, _server.Requests);
    }

    [Fact(Timeout = 15_000)]
    public async Task Nws_hourly_fetch_with_a_stalled_body_throws_near_the_total_timeout()
    {
        var nws = NwsClient();
        var point = new NwsPoint("America/New_York", _server.Url + "/gridpoints/OKX/33,35/forecast/hourly", null);

        var (ex, elapsed) = await Time(() => nws.GetForecastAsync(point, Ct));

        AssertClientTimeout(ex, elapsed);
    }

    [Fact(Timeout = 15_000)]
    public async Task Open_meteo_fetch_with_a_stalled_body_throws_near_the_total_timeout()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new OpenMeteoOptions(_server.Url + "/"));
        services.AddHttpClient<OpenMeteoClient>().AddGetResilience(Settings);
        var openMeteo = Build(services).GetRequiredService<OpenMeteoClient>();

        var (ex, elapsed) = await Time(() => openMeteo.GetForecastAsync(40.7128m, -74.006m, Ct));

        AssertClientTimeout(ex, elapsed);
    }

    [Fact(Timeout = 15_000)]
    public async Task Ntfy_publish_with_a_stalled_body_throws_near_the_total_timeout_and_is_sent_once()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("ntfy").AddNtfyResilience(Settings);
        var http = Build(services).GetRequiredService<IHttpClientFactory>().CreateClient("ntfy");
        var publisher = new NtfyPublisher(http, _server.Url, "stalled-body-test-topic");

        var (ex, elapsed) = await Time(() => publisher.PublishAsync("t", "b", "default", "umbrella", Ct));

        AssertClientTimeout(ex, elapsed);
        // A timeout may follow a delivered POST, so it is never retried in-process (D34).
        Assert.Equal(1, _server.Requests);
    }

    /// <summary>
    /// The gridpoint fetch stalls too, but it degrades rather than throws (D11): hourly still
    /// decides the alert. Proves the grid client is bounded the same way.
    /// </summary>
    [Fact(Timeout = 15_000)]
    public async Task Nws_gridpoint_fetch_with_a_stalled_body_degrades_to_no_amounts()
    {
        using var hourly = WireMock.Server.WireMockServer.Start();
        hourly.Given(WireMock.RequestBuilders.Request.Create().WithPath("/hourly").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/geo+json")
                .WithBody(FixtureFiles.Read("nws/2026-10-29T1900Z_pre-dst-fallback/forecast-hourly.json")));
        var nws = NwsClient();
        var point = new NwsPoint("America/New_York", hourly.Url + "/hourly", _server.Url + "/gridpoints/OKX/33,35");

        var sw = Stopwatch.StartNew();
        var forecast = await nws.GetForecastAsync(point, Ct);
        sw.Stop();

        Assert.NotEmpty(forecast.Hours);
        Assert.Null(forecast.Amounts);
        Assert.Equal("gridpoint fetch failed: timeout", forecast.AmountsUnavailableReason);
        Assert.True(sw.Elapsed < Bound, $"took {sw.Elapsed}");
    }

    // ---------------------------------------------------------------------------------- helpers

    /// <summary>The two named NWS clients and the factory, exactly as <c>WorkerServices</c> registers them.</summary>
    private NwsClient NwsClient()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(global::Worker.Hosting.WorkerServices.NwsClientName).AddGetResilience(Settings);
        services.AddHttpClient(global::Worker.Hosting.WorkerServices.NwsGridClientName).AddGetResilience(Settings);
        var factory = Build(services).GetRequiredService<IHttpClientFactory>();
        return new NwsClient(
            factory.CreateClient(global::Worker.Hosting.WorkerServices.NwsClientName),
            new NwsOptions(_server.Url + "/"),
            factory.CreateClient(global::Worker.Hosting.WorkerServices.NwsGridClientName));
    }

    private ServiceProvider Build(ServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        _owned.Add(provider);
        return provider;
    }

    private static async Task<(Exception Ex, TimeSpan Elapsed)> Time(Func<Task> call)
    {
        var sw = Stopwatch.StartNew();
        var ex = await Record.ExceptionAsync(call);
        sw.Stop();
        Assert.NotNull(ex);
        return (ex, sw.Elapsed);
    }

    /// <summary>
    /// <c>HttpClient</c>'s own timeout: a <see cref="TaskCanceledException"/> wrapping a
    /// <see cref="TimeoutException"/>, while the caller's token was never cancelled. That is the
    /// shape <c>NightlyPass</c> treats as a provider failure (fallback, D36) and the ledger records
    /// as a failure, not a cancellation (D25).
    /// </summary>
    private static void AssertClientTimeout(Exception ex, TimeSpan elapsed)
    {
        Assert.True(
            ex is TaskCanceledException { InnerException: TimeoutException },
            $"expected HttpClient's timeout, got {ex.GetType().Name}: {ex.Message}");
        Assert.False(Ct.IsCancellationRequested);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(900), $"ended after only {elapsed}");
        Assert.True(elapsed < Bound, $"took {elapsed}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _owned)
            await provider.DisposeAsync();
        await _server.DisposeAsync();
    }

    /// <summary>
    /// A loopback HTTP "server" that reads each request's headers, answers <c>200</c> with a
    /// <c>Content-Length</c> far larger than what it sends, sends the first bytes of a JSON body,
    /// and then says nothing more until disposed. WireMock can't stall mid-body, so a raw socket.
    /// </summary>
    private sealed class StallingServer : IAsyncDisposable
    {
        private const string Head =
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100000\r\n\r\n";

        private const string PartialBody = "{\"properties\":{\"timeZone\":\"America/New_";

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Socket> _sockets = [];
        private readonly Task _acceptLoop;
        private int _requests;

        private StallingServer()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _acceptLoop = AcceptLoopAsync();
        }

        public static StallingServer Start() => new();

        public string Url { get; }

        /// <summary>Requests whose headers were read in full and answered with the stalled response.</summary>
        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var socket = await _listener.AcceptSocketAsync(_stop.Token);
                    lock (_sockets)
                        _sockets.Add(socket);
                    _ = StallAsync(socket);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task StallAsync(Socket socket)
        {
            try
            {
                var received = new StringBuilder();
                var chunk = new byte[4096];
                while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await socket.ReceiveAsync(chunk, SocketFlags.None, _stop.Token);
                    if (n == 0)
                        return;
                    received.Append(Encoding.ASCII.GetString(chunk, 0, n));
                }

                Interlocked.Increment(ref _requests);
                await socket.SendAsync(Encoding.ASCII.GetBytes(Head + PartialBody), SocketFlags.None, _stop.Token);

                // Stall: keep the connection open, send nothing more.
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _acceptLoop;
            lock (_sockets)
            {
                foreach (var socket in _sockets)
                    socket.Dispose();
            }
            _stop.Dispose();
        }
    }
}
