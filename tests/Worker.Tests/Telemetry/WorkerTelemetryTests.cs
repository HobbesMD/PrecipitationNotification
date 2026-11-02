using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
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

    /// <summary>
    /// D45. A malformed OTEL_* value turns export off for the run and warns, naming the key and
    /// never the value; it never fails the run. The headers case is the one the review found:
    /// before validation, the SDK threw on it while the host started, so the pass never ran.
    /// </summary>
    [Theory]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "not a url")]
    [InlineData("OTEL_EXPORTER_OTLP_PROTOCOL", "http/json")]
    [InlineData("OTEL_EXPORTER_OTLP_TIMEOUT", "soon")]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", "Authorization=Basic SECRETTOKEN,bad")]
    public async Task Malformed_otlp_setting_turns_export_off_and_warns_naming_only_the_key(string key, string value)
    {
        var settings = new List<(string, string)>
        {
            ("OTEL_EXPORTER_OTLP_ENDPOINT", _server.Url!),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
        };
        settings.RemoveAll(s => s.Item1 == key);
        settings.Add((key, value));
        var logs = new Worker.Tests.Pass.LogCapture();
        var services = new ServiceCollection();
        services.AddLogging(l => l.AddProvider(logs));

        services.AddWorkerTelemetry(Config([.. settings])); // does not throw

        await using (var provider = services.BuildServiceProvider())
        {
            foreach (var hosted in provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
                await hosted.StartAsync(TestContext.Current.CancellationToken);
            RunOnceIn(provider);
        }

        Assert.Empty(_server.LogEntries); // nothing exported
        var warning = Assert.Single(logs.Lines, l => l.StartsWith("Warning Worker.Telemetry:", StringComparison.Ordinal));
        Assert.Contains(key, warning);
        Assert.DoesNotContain(value, logs.AllText);
        Assert.DoesNotContain("SECRETTOKEN", logs.AllText);
    }

    [Theory]
    [InlineData("Authorization=Basic%20dXNlcjp0b2tlbg==", true)]
    [InlineData("a=b,c=d", true)]
    [InlineData(" x-scope = v ", true)]
    [InlineData("a=", true)]
    [InlineData("Authorization=Basic token,bad", false)] // a pair with no '='
    [InlineData("a=b,", false)] // an empty pair
    [InlineData("=b", false)] // no key
    [InlineData("bad key=b", false)] // a space is not a header-name character
    [InlineData("a=b%0Ac", false)] // decodes to a newline
    public void Headers_are_checked_against_the_shape_the_exporter_accepts(string headers, bool ok) =>
        Assert.Equal(ok, WorkerTelemetry.HeadersAreWellFormed(headers));

    [Theory]
    [InlineData("Authorization=Basic%20dXNlcjp0b2tlbg==")]
    [InlineData("a=b,c=d")]
    [InlineData(" x-scope = v ")]
    public void Headers_that_pass_the_check_are_accepted_by_the_sdk(string headers)
    {
        var services = new ServiceCollection();
        services.AddWorkerTelemetry(Config(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", _server.Url!),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
            ("OTEL_EXPORTER_OTLP_HEADERS", headers)));

        RunOnce(services); // building the providers would throw on headers the SDK rejects

        Assert.Contains(_server.LogEntries, e => e.RequestMessage?.Path == "/v1/traces");
    }

    /// <summary>
    /// D43: on our meter, only the listed low-cardinality keys survive. A tag that is fine on a
    /// span (the night date, the location) is dropped from a metric even if code adds it.
    /// </summary>
    [Fact]
    public void Our_metrics_keep_only_the_listed_tags()
    {
        var exported = new List<MetricSnapshot>();
        var services = new ServiceCollection();
        services.AddWorkerTelemetry(Config());
        services.ConfigureOpenTelemetryMeterProvider(b => b.AddInMemoryExporter(exported));
        var probe = Instrumentation.Meter.CreateCounter<long>("precip.test.tag_probe");

        using (var provider = services.BuildServiceProvider())
        {
            var meters = provider.GetRequiredService<OpenTelemetry.Metrics.MeterProvider>();
            probe.Add(
                1,
                new KeyValuePair<string, object?>(Instrumentation.Attributes.ExitCode, 0),
                new KeyValuePair<string, object?>(Instrumentation.Attributes.Outcome, Instrumentation.Outcomes.Dry),
                new KeyValuePair<string, object?>(Instrumentation.Attributes.Provider, "NWS"),
                new KeyValuePair<string, object?>(Instrumentation.Attributes.NightDate, "2026-10-29"),
                new KeyValuePair<string, object?>(Instrumentation.Attributes.LocationId, 1),
                new KeyValuePair<string, object?>(Instrumentation.Attributes.LocationName, "Home"),
                new KeyValuePair<string, object?>(Instrumentation.Attributes.PeakProbability, 80));
            meters.ForceFlush();
        }

        var snapshots = exported.Where(m => m.Name == "precip.test.tag_probe").ToList();
        Assert.NotEmpty(snapshots);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
            foreach (var point in snapshot.MetricPoints)
                foreach (var tag in point.Tags)
                    keys.Add(tag.Key);
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal)
            {
                Instrumentation.Attributes.ExitCode, Instrumentation.Attributes.Outcome, Instrumentation.Attributes.Provider,
            },
            keys);
    }

    /// <summary>
    /// Starts the providers, records one span, one log and one HTTP call, then disposes them: the
    /// same order of events as a <c>--once</c> run.
    /// </summary>
    private void RunOnce(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        RunOnceIn(provider);
    }

    private static void RunOnceIn(IServiceProvider provider)
    {
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
