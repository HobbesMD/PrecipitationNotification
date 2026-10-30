using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Polly.Timeout;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Worker.Notifications;
using Worker.Resilience;

namespace Worker.Tests.Resilience;

/// <summary>
/// The ntfy pipeline retries only what provably never reached ntfy, or what ntfy provably
/// didn't process. Each test pins one class of failure and asserts how many requests went out.
/// Every client here runs through the real <c>SocketsHttpHandler</c>, so the exceptions are the
/// ones production would see. Network is limited to localhost and the reserved <c>.invalid</c> TLD.
/// </summary>
public sealed class NtfyResilienceTests : IAsyncLifetime
{
    private const string Topic = "/resilience-test-topic";

    /// <summary>The agreed test settings: small enough to run fast, same shape as production.</summary>
    private static readonly ResilienceSettings Fast = new()
    {
        AttemptTimeout = TimeSpan.FromMilliseconds(300),
        MaxAttempts = 3,
        TotalTimeout = TimeSpan.FromSeconds(3),
        BackoffBaseDelay = TimeSpan.FromMilliseconds(10),
    };

    /// <summary>
    /// For the connect-failure tests only. Windows answers a refused loopback connect after about
    /// 2 s (it retransmits the SYN after each RST), which would trip a 300 ms attempt timeout and
    /// turn "refused" into "timed out". A <c>.invalid</c> lookup that reaches an upstream resolver
    /// (as on a CI runner) can be slow for the same reason. The pipeline's shape is unchanged;
    /// only the clock is.
    /// </summary>
    private static readonly ResilienceSettings ConnectFailures = Fast with
    {
        AttemptTimeout = TimeSpan.FromSeconds(10),
        TotalTimeout = TimeSpan.FromSeconds(40),
    };

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly AttemptCounter _attempts = new();
    private readonly List<ServiceProvider> _owned = [];

    /// <summary>
    /// One throwaway request first, so WireMock's and the HTTP stack's first-call JIT doesn't
    /// count against a 300 ms attempt timeout. Its log entry is cleared before the test runs.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        using var warmUp = new HttpClient();
        using var _ = await warmUp.GetAsync(_server.Url + "/warm-up", Ct);
        _server.ResetLogEntries();
    }

    public ValueTask DisposeAsync()
    {
        foreach (var provider in _owned)
        {
            provider.Dispose();
        }
        _server.Stop();
        _server.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string TopicUrl => _server.Url + Topic;

    // ---------------------------------------------------------------- not processed: retried

    [Fact]
    public async Task Two_503s_then_200_retries_and_succeeds_in_3_requests()
    {
        RespondInOrder(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var http = Client(Fast);

        using var response = await http.SendAsync(Post(TopicUrl), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, ServerRequestCount());
    }

    [Fact]
    public async Task A_429_then_200_retries_and_succeeds_in_2_requests()
    {
        RespondInOrder(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var http = Client(Fast);

        using var response = await http.SendAsync(Post(TopicUrl), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, ServerRequestCount());
    }

    // ---------------------------------------------------- may have been processed: never retried

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_status_the_origin_may_have_processed_is_sent_exactly_once(HttpStatusCode status)
    {
        // The follow-up 200 is there so a wrongful retry would *succeed* and be visible as a
        // second request, rather than hiding behind a repeated failure.
        RespondInOrder(status, HttpStatusCode.OK);
        using var http = Client(Fast);

        using var response = await http.SendAsync(Post(TopicUrl), Ct);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, ServerRequestCount());
    }

    [Fact]
    public async Task A_response_slower_than_the_attempt_timeout_is_sent_exactly_once_and_throws()
    {
        // ntfy may well have published this one; only its answer was late. That's D25's
        // lost-response case: the ledger records it, the next run decides. No in-process retry.
        var delay = TimeSpan.FromSeconds(1);
        _server.Given(Request.Create().WithPath(Topic).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(delay));
        using var http = Client(Fast);

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => http.SendAsync(Post(TopicUrl), Ct));

        Assert.Equal(1, _attempts.Count);
        // Give a wrongful retry every chance to arrive before counting what the server saw.
        await Task.Delay(delay + TimeSpan.FromMilliseconds(500), Ct);
        Assert.Equal(1, ServerRequestCount());
    }

    [Fact]
    public async Task A_connection_dropped_after_the_request_was_read_is_sent_exactly_once()
    {
        // The server reads the whole request, then closes the connection without a byte of
        // response: the request is out, no answer came back, and ntfy could have published it.
        //
        // Not a WireMock fault: WireMock.Net 2.21's faults never drop the connection.
        // EMPTY_RESPONSE sends the status with an empty body and MALFORMED_RESPONSE_CHUNK sends
        // a well-framed garbage body (OwinResponseMapper.MapAsync), so the client sees a valid
        // response either way. A raw socket is the only honest way to stage this one.
        await using var server = DroppingServer.Start();
        using var http = Client(Fast);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.SendAsync(Post(server.Url + Topic), Ct));

        Assert.Equal(HttpRequestError.ResponseEnded, ex.HttpRequestError);
        Assert.False(NtfyResilience.IsNeverSent(ex));
        Assert.Equal(1, _attempts.Count);
        Assert.Equal(1, server.RequestsRead);
    }

    // ------------------------------------------------------------- never sent: retried, then thrown

    [Fact]
    public async Task A_refused_connection_is_retried_up_to_MaxAttempts_then_throws()
    {
        var closedPort = ReserveThenReleasePort();
        using var http = Client(ConnectFailures);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.SendAsync(Post($"http://127.0.0.1:{closedPort}{Topic}"), Ct));

        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
        Assert.Equal(3, _attempts.Count);
    }

    [Fact]
    public async Task An_unresolvable_host_is_retried_up_to_MaxAttempts_then_throws()
    {
        // RFC 6761 reserves .invalid: it never resolves, so this never leaves the machine's resolver.
        using var http = Client(ConnectFailures);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.SendAsync(Post($"http://ntfy-resilience-test.invalid{Topic}"), Ct));

        Assert.Equal(HttpRequestError.NameResolutionError, ex.HttpRequestError);
        Assert.Equal(3, _attempts.Count);
    }

    // ---------------------------------------------------------- the classifier, case by case

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, true)]
    [InlineData(HttpRequestError.ConnectionError, true)]
    [InlineData(HttpRequestError.SecureConnectionError, true)]
    [InlineData(HttpRequestError.ResponseEnded, false)]
    [InlineData(HttpRequestError.InvalidResponse, false)]
    [InlineData(HttpRequestError.HttpProtocolError, false)]
    [InlineData(HttpRequestError.Unknown, false)]
    [InlineData(HttpRequestError.ProxyTunnelError, false)]
    [InlineData(HttpRequestError.ConfigurationLimitExceeded, false)]
    public void Only_connect_phase_errors_are_never_sent(HttpRequestError error, bool expected) =>
        Assert.Equal(expected, NtfyResilience.IsNeverSent(new HttpRequestException(error, "x")));

    [Fact]
    public void Timeouts_and_cancellations_are_never_classified_as_never_sent()
    {
        Assert.False(NtfyResilience.IsNeverSent(new TimeoutRejectedException()));
        Assert.False(NtfyResilience.IsNeverSent(new OperationCanceledException()));
        Assert.False(NtfyResilience.IsNeverSent(new TaskCanceledException()));
        Assert.False(NtfyResilience.IsNeverSent(new IOException()));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    [InlineData(HttpStatusCode.GatewayTimeout, false)]
    public void Only_429_and_503_are_not_processed(HttpStatusCode status, bool expected)
    {
        using var response = new HttpResponseMessage(status);
        Assert.Equal(expected, NtfyResilience.IsNotProcessed(response));
    }

    // ---------------------------------------------------------- through the real publisher

    [Fact]
    public async Task Publisher_retries_a_503_and_sends_the_same_body_and_headers_twice()
    {
        RespondInOrder(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var http = Client(Fast);
        var publisher = new NtfyPublisher(http, _server.Url!, Topic.TrimStart('/'));

        await publisher.PublishAsync("Rain tonight", "Up to 0.4 in after 11 PM", "high", "umbrella", Ct);

        var posts = _server.LogEntries.Select(e => e.RequestMessage!).ToList();
        Assert.Equal(2, posts.Count);
        Assert.All(posts, p => Assert.Equal("POST", p.Method));
        Assert.Equal("Up to 0.4 in after 11 PM", posts[0].Body);
        Assert.Equal(posts[0].Body, posts[1].Body);
        foreach (var header in new[] { "Title", "Priority", "Tags", "Content-Type", "Content-Length" })
        {
            Assert.Equal(posts[0].Headers![header], posts[1].Headers![header]);
        }
        Assert.Equal("Rain tonight", posts[1].Headers!["Title"].Single());
    }

    // ---------------------------------------------------------------------------------- helpers

    /// <summary>
    /// A client built the way production builds it: through <see cref="IHttpClientFactory"/> with
    /// the real primary handler, plus a counter inside the pipeline that sees every attempt.
    /// </summary>
    private HttpClient Client(ResilienceSettings settings)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("ntfy", http => http.Timeout = TimeSpan.FromSeconds(1))
            .AddNtfyResilience(settings)
            .AddHttpMessageHandler(() => new CountingHandler(_attempts));
        var provider = services.BuildServiceProvider();
        _owned.Add(provider);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("ntfy");
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        return client;
    }

    private static HttpRequestMessage Post(string url) =>
        new(HttpMethod.Post, url) { Content = new StringContent("body") };

    /// <summary>Answers the Nth POST with the Nth status; the last one repeats.</summary>
    private void RespondInOrder(params HttpStatusCode[] statuses)
    {
        var n = -1;
        _server.Given(Request.Create().WithPath(Topic).UsingPost())
            .RespondWith(Response.Create().WithCallback(_ =>
            {
                var i = Math.Min(Interlocked.Increment(ref n), statuses.Length - 1);
                return new WireMock.ResponseMessage { StatusCode = (int)statuses[i] };
            }));
    }

    private int ServerRequestCount() => _server.LogEntries.Count();

    /// <summary>Binds port 0 to get a free port from the OS, then releases it so nothing listens.</summary>
    private static int ReserveThenReleasePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// A loopback HTTP "server" that reads each request in full (headers, then
    /// <c>Content-Length</c> bytes of body), counts it, and closes the connection without
    /// answering. Every connection it accepts is handled, so a wrongful retry would be counted.
    /// </summary>
    private sealed class DroppingServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;
        private int _requestsRead;

        private DroppingServer()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _acceptLoop = AcceptLoopAsync();
        }

        public static DroppingServer Start() => new();

        public string Url { get; }

        public int RequestsRead => Volatile.Read(ref _requestsRead);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var socket = await _listener.AcceptSocketAsync(_stop.Token);
                    _ = ReadThenDropAsync(socket);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ReadThenDropAsync(Socket socket)
        {
            using (socket)
            {
                var buffer = new List<byte>();
                var chunk = new byte[4096];
                int headerEnd;
                while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
                {
                    var n = await socket.ReceiveAsync(chunk, SocketFlags.None, _stop.Token);
                    if (n == 0)
                    {
                        return;
                    }
                    buffer.AddRange(chunk.AsSpan(0, n));
                }

                var headers = System.Text.Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray());
                var contentLength = headers.Split("\r\n")
                    .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    .Select(l => int.Parse(l["Content-Length:".Length..].Trim()))
                    .SingleOrDefault();
                while (buffer.Count - (headerEnd + 4) < contentLength)
                {
                    var n = await socket.ReceiveAsync(chunk, SocketFlags.None, _stop.Token);
                    if (n == 0)
                    {
                        return;
                    }
                    buffer.AddRange(chunk.AsSpan(0, n));
                }

                Interlocked.Increment(ref _requestsRead);
                socket.Shutdown(SocketShutdown.Both);
            }
        }

        private static int IndexOfHeaderEnd(List<byte> buffer)
        {
            for (var i = 0; i + 3 < buffer.Count; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _acceptLoop;
            _stop.Dispose();
        }
    }

    private sealed class AttemptCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class CountingHandler(AttemptCounter counter) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            counter.Increment();
            return base.SendAsync(request, ct);
        }
    }
}
