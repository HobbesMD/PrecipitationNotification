using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Worker.Hosting;
using Worker.Notifications;
using Worker.Nws;
using Worker.Pass;
using Worker.Tests.Pass;

namespace Worker.Tests.Hosting;

/// <summary>The real container: what <c>Program</c> registers resolves, is configured, and does not leak URLs into logs.</summary>
public sealed class WorkerServicesTests
{
    private const string Topic = "topic-secret-xyz";
    private const string UserAgent = "precip-tests (test@example.invalid)";

    private static readonly AppConfig Config = new(
        NtfyBaseUrl: "https://ntfy.invalid",
        NtfyTopic: Topic,
        UserAgent: UserAgent,
        ConnectionString: "Host=127.0.0.1;Port=1;Database=precip;Username=u;Password=pw;Pooling=false");

    private static ServiceCollection NewServices(LogCapture logs, bool dryRun = false)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddWorkerServices(Config, dryRun);
        return services;
    }

    [Fact]
    public async Task The_nightly_pass_and_its_dependencies_resolve_per_scope()
    {
        await using var provider = NewServices(new LogCapture(), dryRun: true).BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();

        Assert.IsType<NightlyPass>(scope.ServiceProvider.GetRequiredService<INightlyPass>());
        Assert.IsType<NtfyPublisher>(scope.ServiceProvider.GetRequiredService<IAlertPublisher>());
        Assert.IsType<NwsForecastSource>(scope.ServiceProvider.GetRequiredService<IForecastSource>());
        Assert.IsType<DbGridpointCache>(scope.ServiceProvider.GetRequiredService<IGridpointCache>());
        Assert.IsType<global::Worker.Alerts.PostgresAlertLedger>(scope.ServiceProvider.GetRequiredService<global::Worker.Alerts.IAlertLedger>());
        Assert.True(provider.GetRequiredService<PassOptions>().DryRun);
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task Nws_requests_carry_the_user_agent_and_accept_header_and_the_url_is_never_logged()
    {
        var logs = new LogCapture();
        var handler = new StubHandler(HttpStatusCode.InternalServerError);
        var services = NewServices(logs);
        services.AddHttpClient<NwsClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        var nws = provider.GetRequiredService<NwsClient>();
        await Assert.ThrowsAsync<HttpRequestException>(
            () => nws.ResolvePointAsync("40.7128,-74.006", TestContext.Current.CancellationToken));

        // The request really went to the coordinates URL ...
        Assert.Contains(handler.Requests, r => r.Contains("40.7128"));
        // ... and the framework's own request logging (Information, full URL) is switched off.
        Assert.DoesNotContain("40.7128", logs.AllText);
        Assert.DoesNotContain("74.006", logs.AllText);
    }

    [Fact]
    public async Task Nws_client_sends_the_configured_headers()
    {
        var headers = new CapturingHandler();
        var services = NewServices(new LogCapture());
        services.AddHttpClient<NwsClient>().ConfigurePrimaryHttpMessageHandler(() => headers);
        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAnyAsync<Exception>(
            () => provider.GetRequiredService<NwsClient>().ResolvePointAsync("1.0,2.0", TestContext.Current.CancellationToken));

        Assert.Equal(UserAgent, headers.UserAgent);
        Assert.Contains("application/geo+json", headers.Accept);
    }

    [Fact]
    public async Task Ntfy_publisher_posts_to_the_configured_topic_and_the_topic_is_never_logged()
    {
        var logs = new LogCapture();
        var handler = new StubHandler(HttpStatusCode.OK);
        var services = NewServices(logs);
        services.AddHttpClient(WorkerServices.NtfyClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAlertPublisher>().PublishAsync(
            new Forecast.ComposedMessage("t", "b", "umbrella", "default"), TestContext.Current.CancellationToken);

        Assert.Equal($"POST https://ntfy.invalid/{Topic}", Assert.Single(handler.Requests));
        Assert.DoesNotContain(Topic, logs.AllText);
    }

    /// <summary>Returns 500 and keeps the headers of the last request.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? UserAgent { get; private set; }

        public string Accept { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            UserAgent = string.Join(" ", request.Headers.UserAgent);
            Accept = string.Join(",", request.Headers.Accept);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
