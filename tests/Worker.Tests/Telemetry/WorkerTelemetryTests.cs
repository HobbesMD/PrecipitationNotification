using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Worker.Telemetry;

namespace Worker.Tests.Telemetry;

/// <summary>
/// The wiring around <see cref="TelemetryRedactor"/>: it sits in front of every exporter, even
/// one added after <c>AddWorkerTelemetry</c>; with no endpoint nothing is exported; with one, the
/// spans buffered in a run are sent when the provider is disposed (the <c>--once</c> exit path).
/// Everything is loopback: WireMock plays NWS and the OTLP collector.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class WorkerTelemetryTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public WorkerTelemetryTests() =>
        _server.Given(Request.Create().UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Exporter_added_later_only_sees_the_redacted_http_span()
    {
        var exported = new List<Activity>();
        var services = new ServiceCollection();
        services.AddWorkerTelemetry(Config());
        services.ConfigureOpenTelemetryTracerProvider(b => b.AddInMemoryExporter(exported));

        using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<TracerProvider>();
            using var http = new HttpClient();
            using var response = await http.GetAsync(_server.Url + "/points/40.7128,-74.006", TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode);
        }

        var span = Assert.Single(exported, a => a.Source.Name == TelemetryRedactor.HttpSource);
        Assert.Equal(_server.Url + "/points/{redacted}", span.GetTagItem("url.full"));
        Assert.Equal("GET", span.DisplayName);
        foreach (var (key, value) in span.TagObjects)
        {
            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            Assert.DoesNotContain("40.7128", text);
            Assert.DoesNotContain("74.006", text);
        }
        Assert.DoesNotContain("40.7128", span.DisplayName);
    }

    [Fact]
    public void No_endpoint_means_no_export_even_if_the_sdk_could_find_one()
    {
        var services = new ServiceCollection();
        // The SDK reads OTEL_* itself from a registered IConfiguration; the Worker's own reading
        // (null here, as some tests pass) is what decides whether an exporter exists at all.
        services.AddSingleton<IConfiguration>(Config(("OTEL_EXPORTER_OTLP_ENDPOINT", _server.Url!), ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")));
        services.AddWorkerTelemetry(configuration: null);

        RunOnce(services);

        Assert.Empty(_server.LogEntries);
    }

    [Fact]
    public void Endpoint_set_flushes_spans_to_v1_traces_when_the_provider_is_disposed()
    {
        var services = new ServiceCollection();
        services.AddWorkerTelemetry(Config(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", _server.Url!),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")));

        RunOnce(services);

        Assert.Contains(_server.LogEntries, e =>
            e.RequestMessage?.Method == "POST" && e.RequestMessage.Path == "/v1/traces");
    }

    [Theory]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "not a url")]
    [InlineData("OTEL_EXPORTER_OTLP_PROTOCOL", "http/json")]
    [InlineData("OTEL_EXPORTER_OTLP_TIMEOUT", "soon")]
    public void Malformed_otlp_setting_fails_startup_naming_the_key(string key, string value)
    {
        var settings = new List<(string, string)> { ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4318") };
        settings.RemoveAll(s => s.Item1 == key);
        settings.Add((key, value));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddWorkerTelemetry(Config([.. settings])));

        Assert.Contains(key, ex.Message);
        Assert.DoesNotContain(value, ex.Message);
    }

    /// <summary>
    /// Starts the providers, records one span, one log and one HTTP call, then disposes them: the
    /// same order of events as a <c>--once</c> run.
    /// </summary>
    private void RunOnce(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<TracerProvider>();
        provider.GetRequiredService<OpenTelemetry.Metrics.MeterProvider>();
        provider.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogInformation("pass done");
        using (var span = Instrumentation.Source.StartActivity(Instrumentation.Spans.Pass))
            span?.SetTag(Instrumentation.Attributes.Outcome, Instrumentation.Outcomes.Dry);
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
}
