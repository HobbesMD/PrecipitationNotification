using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Worker.Hosting;
using Worker.Notifications;
using Worker.Nws;
using Worker.OpenMeteo;
using Worker.Pass;
using Worker.Resilience;
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

    private static ServiceCollection NewServices(LogCapture logs, bool dryRun = false, IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        if (configuration is not null)
            services.AddSingleton(configuration); // as the generic host does
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
        services.AddHttpClient(WorkerServices.NwsClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
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
        services.AddHttpClient(WorkerServices.NwsClientName).ConfigurePrimaryHttpMessageHandler(() => headers);
        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAnyAsync<Exception>(
            () => provider.GetRequiredService<NwsClient>().ResolvePointAsync("1.0,2.0", TestContext.Current.CancellationToken));

        Assert.Equal(UserAgent, headers.UserAgent);
        Assert.Contains("application/geo+json", headers.Accept);
    }

    [Fact]
    public async Task Hourly_and_gridpoint_requests_go_through_their_own_clients_with_the_same_headers()
    {
        // 404 so nothing is retried: hourly throws stale, gridpoint degrades to "no amounts".
        var hourly = new CapturingHandler(HttpStatusCode.NotFound);
        var grid = new CapturingHandler(HttpStatusCode.NotFound);
        var services = NewServices(new LogCapture());
        services.AddHttpClient(WorkerServices.NwsClientName).ConfigurePrimaryHttpMessageHandler(() => hourly);
        services.AddHttpClient(WorkerServices.NwsGridClientName).ConfigurePrimaryHttpMessageHandler(() => grid);
        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<NwsPointStaleException>(() => provider.GetRequiredService<NwsClient>().GetForecastAsync(
            new NwsPoint("America/New_York", "https://hourly.invalid/h", "https://grid.invalid/g"),
            TestContext.Current.CancellationToken));

        Assert.Equal("https://hourly.invalid/h", hourly.LastUrl);
        Assert.Equal("https://grid.invalid/g", grid.LastUrl);
        foreach (var h in new[] { hourly, grid })
        {
            Assert.Equal(UserAgent, h.UserAgent);
            Assert.Contains("application/geo+json", h.Accept);
        }
    }

    [Fact]
    public async Task Every_client_timeout_is_its_pipelines_total_timeout()
    {
        var services = NewServices(new LogCapture(), configuration: ConfigOf(
            ("Resilience:Nws:TotalTimeout", "00:00:21"),
            ("Resilience:OpenMeteo:TotalTimeout", "00:00:22"),
            ("Resilience:Ntfy:TotalTimeout", "00:00:23")));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // One cap per call that also covers the body, which the pipeline can't see.
        Assert.Equal(TimeSpan.FromSeconds(21), factory.CreateClient(WorkerServices.NwsClientName).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(21), factory.CreateClient(WorkerServices.NwsGridClientName).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(22), factory.CreateClient(typeof(OpenMeteoClient).Name).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(23), factory.CreateClient(WorkerServices.NtfyClientName).Timeout);
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

    private static IConfiguration ConfigOf(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public async Task Clients_and_the_fallback_source_resolve()
    {
        await using var provider = NewServices(new LogCapture()).BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<NwsClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<OpenMeteoClient>());
        Assert.IsType<OpenMeteoForecastSource>(scope.ServiceProvider.GetRequiredService<IFallbackForecastSource>());
        Assert.IsType<NtfyPublisher>(scope.ServiceProvider.GetRequiredService<IAlertPublisher>());
        Assert.Equal("https://api.open-meteo.com/", provider.GetRequiredService<OpenMeteoOptions>().BaseUrl);
    }

    [Fact]
    public async Task Resilience_settings_default_when_nothing_is_configured()
    {
        await using var provider = NewServices(new LogCapture()).BuildServiceProvider();

        foreach (var key in new[] { "nws", "open-meteo", "ntfy" })
            Assert.Equal(new ResilienceSettings(), provider.GetRequiredKeyedService<ResilienceSettings>(key));
        Assert.Equal(PassOptions.DefaultDeadline, provider.GetRequiredService<PassOptions>().Deadline);
    }

    [Fact]
    public async Task Resilience_section_overrides_only_the_provider_it_names_and_only_the_keys_present()
    {
        var services = NewServices(new LogCapture(), configuration: ConfigOf(
            ("Resilience:Nws:MaxAttempts", "5"),
            ("Resilience:Ntfy:AttemptTimeout", "00:00:02")));
        await using var provider = services.BuildServiceProvider();

        var nws = provider.GetRequiredKeyedService<ResilienceSettings>("nws");
        Assert.Equal(5, nws.MaxAttempts);
        Assert.Equal(new ResilienceSettings { MaxAttempts = 5 }, nws); // everything else is the record default
        Assert.Equal(new ResilienceSettings(), provider.GetRequiredKeyedService<ResilienceSettings>("open-meteo"));
        Assert.Equal(
            new ResilienceSettings { AttemptTimeout = TimeSpan.FromSeconds(2) },
            provider.GetRequiredKeyedService<ResilienceSettings>("ntfy"));
    }

    [Fact]
    public async Task Provider_base_urls_come_from_config_with_a_trailing_slash_and_default_to_the_real_ones()
    {
        await using (var provider = NewServices(new LogCapture(), configuration: ConfigOf(
            ("Nws:BaseUrl", "http://127.0.0.1:9"))).BuildServiceProvider())
        {
            Assert.Equal("http://127.0.0.1:9/", provider.GetRequiredService<NwsOptions>().BaseUrl);
            Assert.Equal(new OpenMeteoOptions().BaseUrl, provider.GetRequiredService<OpenMeteoOptions>().BaseUrl);
        }

        Assert.Throws<InvalidOperationException>(() =>
            NewServices(new LogCapture(), configuration: ConfigOf(("OpenMeteo:BaseUrl", "not a url"))));
    }

    [Fact]
    public void The_generic_hosts_own_configuration_is_found_without_being_passed_in()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resilience:OpenMeteo:MaxAttempts"] = "7",
            ["Worker:PassDeadline"] = "00:01:30",
        });
        builder.Services.AddWorkerServices(Config, dryRun: false);

        using var host = builder.Build();

        Assert.Equal(7, host.Services.GetRequiredKeyedService<ResilienceSettings>("open-meteo").MaxAttempts);
        Assert.Equal(3, host.Services.GetRequiredKeyedService<ResilienceSettings>("nws").MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(90), host.Services.GetRequiredService<PassOptions>().Deadline);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("00:00:00")]
    [InlineData("-00:01:00")]
    public void A_bad_pass_deadline_fails_at_startup_without_echoing_it(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => NewServices(new LogCapture(), configuration: ConfigOf(("Worker:PassDeadline", value))));

        Assert.Contains("Worker:PassDeadline", ex.Message);
        Assert.Contains("00:01:40", ex.Message); // the example is the real default, not 00:04:00
        Assert.DoesNotContain(value, ex.Message);
    }

    [Theory]
    [InlineData("Resilience:Ntfy:MaxAttempts", "0")]
    [InlineData("Resilience:Nws:MaxAttempts", "-7")]
    [InlineData("Resilience:OpenMeteo:AttemptTimeout", "00:00:00")]
    [InlineData("Resilience:Nws:TotalTimeout", "-00:00:05")]
    [InlineData("Resilience:Ntfy:BackoffBaseDelay", "00:00:00")]
    [InlineData("Resilience:Nws:BreakerSamplingDuration", "00:00:00")]
    [InlineData("Resilience:OpenMeteo:BreakDuration", "-00:00:30")]
    [InlineData("Resilience:Nws:BreakerFailureRatio", "2.5")]
    [InlineData("Resilience:Nws:BreakerFailureRatio", "0.0")]
    [InlineData("Resilience:OpenMeteo:BreakerFailureRatio", "-0.25")]
    [InlineData("Resilience:Nws:BreakerFailureRatio", "NaN")]
    [InlineData("Resilience:Nws:BreakerMinimumThroughput", "1")]
    [InlineData("Resilience:OpenMeteo:BreakerMinimumThroughput", "-3")]
    [InlineData("Resilience:Ntfy:AttemptTimeout", "00:00:45")] // longer than the 30 s total
    public void A_resilience_value_that_parses_but_is_invalid_fails_at_startup_naming_the_key(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => NewServices(new LogCapture(), configuration: ConfigOf((key, value))));

        Assert.StartsWith(key + " ", ex.Message);
        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void Resilience_boundaries_that_polly_accepts_pass_validation()
    {
        var services = NewServices(new LogCapture(), configuration: ConfigOf(
            ("Resilience:Nws:MaxAttempts", "1"),
            ("Resilience:Nws:BreakerFailureRatio", "1"),
            ("Resilience:Nws:BreakerMinimumThroughput", "2"),
            ("Resilience:Nws:AttemptTimeout", "00:00:30"))); // equal to the total is allowed

        using var provider = services.BuildServiceProvider();
        Assert.Equal(1, provider.GetRequiredKeyedService<ResilienceSettings>("nws").MaxAttempts);
    }

    /// <summary>Returns <c>status</c> (500 by default) and keeps the URL and headers of the last request.</summary>
    private sealed class CapturingHandler(HttpStatusCode status = HttpStatusCode.InternalServerError) : HttpMessageHandler
    {
        public string? UserAgent { get; private set; }

        public string Accept { get; private set; } = "";

        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            UserAgent = string.Join(" ", request.Headers.UserAgent);
            Accept = string.Join(",", request.Headers.Accept);
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
