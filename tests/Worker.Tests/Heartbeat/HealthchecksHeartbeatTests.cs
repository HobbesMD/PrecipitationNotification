using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Worker.Heartbeat;
using Worker.Hosting;
using Worker.Tests.Pass;

namespace Worker.Tests.Heartbeat;

/// <summary>The healthchecks.io heartbeat against a local WireMock server; nothing leaves loopback.</summary>
public sealed class HealthchecksHeartbeatTests : IDisposable
{
    private const string Uuid = "4f8c1d2e-9a3b-4c5d-8e7f-0a1b2c3d4e5f";

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly LogCapture _logs = new();
    private readonly ServiceProvider _provider;

    public HealthchecksHeartbeatTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(_logs)
            // As AddWorkerServices does: the framework's own HttpClient logging prints full URLs.
            .AddFilter("System.Net.Http.HttpClient", LogLevel.None));
        services.AddHttpClient("t");
        _provider = services.BuildServiceProvider();

        // First request to a fresh WireMock server is slow (JIT); keep that out of the 2 s attempt timeout.
        using var plain = new HttpClient();
        using var _ = plain.GetAsync(_server.Url + "/warmup").GetAwaiter().GetResult();
        _server.ResetLogEntries();
    }

    private string Url => $"{_server.Url}/ping/{Uuid}";

    private HealthchecksHeartbeat Create(string? url = null) => new(
        _provider.GetRequiredService<IHttpClientFactory>().CreateClient("t"),
        url ?? Url,
        TimeProvider.System,
        _provider.GetRequiredService<ILogger<HealthchecksHeartbeat>>());

    private void Respond(params int[] statuses)
    {
        // One scenario step per status; the last repeats.
        var name = Guid.NewGuid().ToString();
        for (var i = 0; i < statuses.Length; i++)
        {
            _server.Given(Request.Create().UsingPost())
                .InScenario(name)
                .WhenStateIs(i == 0 ? null! : $"s{i}")
                .WillSetStateTo(i == statuses.Length - 1 ? $"s{i}" : $"s{i + 1}")
                .RespondWith(Response.Create().WithStatusCode(statuses[i]));
        }
    }

    private int Requests => _server.LogEntries.Count();

    private static string Body(WireMock.Logging.ILogEntry e) => e.RequestMessage?.Body ?? "";

    [Fact]
    public async Task Start_posts_to_slash_start()
    {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        await Create().StartAsync(TestContext.Current.CancellationToken);

        var e = Assert.Single(_server.LogEntries);
        Assert.Equal($"/ping/{Uuid}/start", e.RequestMessage!.Path);
        Assert.Equal("POST", e.RequestMessage.Method);
    }

    [Fact]
    public async Task Finish_zero_posts_to_slash_0_with_an_ok_body()
    {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        await Create().FinishAsync(0, TestContext.Current.CancellationToken);

        var e = Assert.Single(_server.LogEntries);
        Assert.Equal($"/ping/{Uuid}/0", e.RequestMessage!.Path);
        Assert.Equal("exit 0: ok", Body(e));
    }

    [Fact]
    public async Task Finish_two_posts_to_slash_2_and_the_body_names_the_failure()
    {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        await Create().FinishAsync(2, TestContext.Current.CancellationToken);

        var e = Assert.Single(_server.LogEntries);
        Assert.Equal($"/ping/{Uuid}/2", e.RequestMessage!.Path);
        Assert.Equal("exit 2: both forecast providers failed", Body(e));
    }

    [Fact]
    public async Task An_unknown_exit_code_gets_a_bare_body()
    {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        await Create().FinishAsync(42, TestContext.Current.CancellationToken);

        Assert.Equal("exit 42", Body(Assert.Single(_server.LogEntries)));
    }

    [Fact]
    public async Task A_trailing_slash_on_the_url_is_trimmed()
    {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        await Create(Url + "/").StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal($"/ping/{Uuid}/start", Assert.Single(_server.LogEntries).RequestMessage!.Path);
    }

    [Fact]
    public async Task A_500_then_a_200_is_two_requests_and_no_warning()
    {
        Respond(500, 200);

        await Create().FinishAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(2, Requests);
        Assert.DoesNotContain(_logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Always_500_returns_after_two_attempts_with_one_warning()
    {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(500));

        await Create().FinishAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(2, Requests);
        var warning = Assert.Single(_logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));
        Assert.Contains("Heartbeat finish ping failed: HTTP 500", warning);
    }

    [Fact]
    public async Task A_404_is_not_retried()
    {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(404));

        await Create().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Requests);
        Assert.Contains("Heartbeat start ping failed: HTTP 404", _logs.AllText);
    }

    [Fact]
    public async Task A_hanging_server_is_given_up_on_within_the_budget()
    {
        _server.Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(30)));

        var sw = Stopwatch.StartNew();
        await Create().FinishAsync(0, TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4.5), $"took {sw.Elapsed}");
        Assert.Contains("ping failed: timed out", _logs.AllText);
    }

    [Fact]
    public async Task A_cancelled_caller_token_returns_without_throwing()
    {
        _server.Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(30)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Create().StartAsync(cts.Token);
        await Create().FinishAsync(0, cts.Token);

        // Also a token cancelled mid-flight.
        using var late = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        await Create().FinishAsync(0, late.Token);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task A_failure_never_logs_the_secret_uuid_or_the_url()
    {
        // 5xx path
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(500));
        await Create().FinishAsync(2, TestContext.Current.CancellationToken);

        // network-error path: a server that has been stopped
        var dead = WireMockServer.Start();
        var deadUrl = $"{dead.Url}/ping/{Uuid}";
        dead.Stop();
        await Create(deadUrl).StartAsync(TestContext.Current.CancellationToken);

        Assert.Contains(_logs.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));
        Assert.DoesNotContain(Uuid, _logs.AllText);
        Assert.DoesNotContain("/ping/", _logs.AllText);
        Assert.DoesNotContain("http://", _logs.AllText);
    }

    // ---- AddHeartbeat ----

    private static readonly AppConfig Config = new(
        NtfyBaseUrl: "https://ntfy.invalid",
        NtfyTopic: "topic",
        UserAgent: "precip-tests (test@example.invalid)",
        ConnectionString: "Host=127.0.0.1;Port=1;Database=precip;Username=u;Password=pw;Pooling=false",
        HeartbeatUrl: "http://hc.invalid/ping/" + Uuid);

    private static IHeartbeat Resolve(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        configure(services);
        return services.BuildServiceProvider().GetRequiredService<IHeartbeat>();
    }

    [Fact]
    public void No_url_gives_the_no_op()
    {
        Assert.IsType<NoOpHeartbeat>(Resolve(s => s.AddHeartbeat(Config with { HeartbeatUrl = null }, dryRun: false)));
    }

    [Fact]
    public void Dry_run_gives_the_no_op_even_with_a_url()
    {
        Assert.IsType<NoOpHeartbeat>(Resolve(s => s.AddHeartbeat(Config, dryRun: true)));
    }

    [Fact]
    public void A_url_gives_the_healthchecks_heartbeat()
    {
        Assert.IsType<HealthchecksHeartbeat>(Resolve(s => s.AddHeartbeat(Config, dryRun: false)));
    }

    [Fact]
    public void The_real_heartbeat_wins_over_the_pass_defaults()
    {
        var hb = Resolve(s => s.AddWorkerServices(Config, dryRun: false).AddOncePass());
        Assert.IsType<HealthchecksHeartbeat>(hb);
    }

    [Fact]
    public void Which_heartbeat_is_active_is_logged_once_on_resolution()
    {
        var logs = new LogCapture();
        Resolve(s =>
        {
            s.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            s.AddHeartbeat(Config with { HeartbeatUrl = null }, dryRun: false);
        });
        Assert.Contains("Heartbeat: disabled (PRECIP_HEARTBEAT_URL not set)", logs.AllText);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _server.Dispose();
    }
}
