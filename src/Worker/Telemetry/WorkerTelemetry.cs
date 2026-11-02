using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Worker.Telemetry;

/// <summary>OpenTelemetry registration for the Worker: traces, metrics and logs over OTLP.</summary>
internal static class WorkerTelemetry
{
    public const string DefaultServiceName = "precip-worker";

    /// <summary>
    /// Short on purpose. In a <c>--once</c> run the providers flush when the host is disposed,
    /// after the pass and the finish ping, and each export is bounded by this timeout. That alone
    /// is not the budget: three providers flushing against a hung collector took about 10 s. The
    /// guarantee is <see cref="Hosting.BoundedShutdown"/>, which abandons disposal after 5 s, so
    /// the run fits Azure's 300 s: start ping (at most 4 s) + hard deadline (270 s, at most 280 s)
    /// + finish ping (at most 4 s) + flush (at most 5 s). A slow collector costs lost telemetry,
    /// never the exit code.
    /// </summary>
    public const int DefaultExportTimeoutMilliseconds = 5000;

    /// <summary>Npgsql's Meter name (Npgsql 10.0.3, MetricsReporter).</summary>
    public const string NpgsqlMeter = "Npgsql";

    public const string PollyMeter = "Polly";

    /// <summary>
    /// Registers OpenTelemetry. Exports nothing unless <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set in
    /// <paramref name="configuration"/> (null counts as unset): with no endpoint the providers
    /// still exist (so spans and the redactor run, and tests can add an exporter) but no exporter
    /// is registered, so there are no connection attempts, retries or console noise in tests, CI
    /// or a bare local run.
    ///
    /// <para>
    /// A malformed OTEL_* value turns export off for the run instead of failing it (D45): a
    /// warning names the key, never the value, and the pass runs as normal. Telemetry must never
    /// cost the night's alert, and a typo in the headers secret would otherwise throw inside the
    /// SDK as the host starts, before the pass and before either heartbeat ping.
    /// </para>
    /// </summary>
    public static IServiceCollection AddWorkerTelemetry(this IServiceCollection services, IConfiguration? configuration)
    {
        OtlpSettings? otlp;
        try
        {
            otlp = ReadOtlp(configuration);
        }
        catch (InvalidOperationException ex)
        {
            otlp = null;
            services.AddHostedService(sp => new TelemetryConfigWarning(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(), ex.Message));
        }
        var serviceName = configuration?["OTEL_SERVICE_NAME"] is { Length: > 0 } name ? name : DefaultServiceName;

        services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName)
                // OTEL_RESOURCE_ATTRIBUTES (deployment.environment and the like).
                .AddEnvironmentVariableDetector())
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(Instrumentation.Name, TelemetryRedactor.HttpSource)
                    .AddNpgsql()
                    // FIRST, before any exporter. OpenTelemetry runs processors in the order they are
                    // registered, and an exporter is itself a processor: whatever is registered
                    // later (the OTLP exporter below, or an exporter a test adds afterwards through
                    // ConfigureOpenTelemetryTracerProvider) only ever sees the redacted span.
                    .AddProcessor(new TelemetryRedactor());
                if (otlp is not null)
                    tracing.AddOtlpExporter(o => otlp.Apply(o, "v1/traces"));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(Instrumentation.Name, TelemetryRedactor.HttpSource, PollyMeter, NpgsqlMeter)
                    // Metrics bypass the span processor, so their tags are allowlisted by views.
                    .AddView(MetricTagAllowlist);
                if (otlp is not null)
                    metrics.AddOtlpExporter(o => otlp.Apply(o, "v1/metrics"));
            })
            // Logs bypass the span processor entirely. They have the D22 log hygiene (HttpClient's
            // URL-bearing logs are off, see AddWorkerServices; our own error logs go through
            // Redact.Describe and never pass an exception object), and, because framework code
            // does pass exception objects, their own redactor: FIRST, for the same reason as the
            // span one. The same filters apply to this provider as to the console one, which stays.
            .WithLogging(
                logging =>
                {
                    logging.AddProcessor(new TelemetryLogRedactor());
                    if (otlp is not null)
                        logging.AddOtlpExporter(o => otlp.Apply(o, "v1/logs"));
                },
                options =>
                {
                    options.IncludeFormattedMessage = true;
                    options.IncludeScopes = false;
                });

        return services;
    }

    /// <summary>
    /// Tag allowlists for the meters whose tags we don't write ourselves. Npgsql tags almost every
    /// instrument with <c>db.client.connection.pool.name</c>, which is the data source name: by
    /// default the connection string (password removed, but still user, host and database).
    /// HttpClient's <c>http.client.open_connections</c> carries <c>network.peer.address</c>.
    /// Our own meter keeps only <see cref="PrecipMetricTags"/>.
    /// </summary>
    private static MetricStreamConfiguration? MetricTagAllowlist(Instrument instrument) =>
        instrument.Meter.Name switch
        {
            // Npgsql 10.0.3 metric tags: db.client.connection.pool.name (dropped), db.system.name,
            // server.address, server.port (operation duration only), db.client.connection.state.
            NpgsqlMeter => new MetricStreamConfiguration
            {
                TagKeys = ["db.system.name", "server.address", "server.port", "db.client.connection.state", "error.type"],
            },
            TelemetryRedactor.HttpSource => new MetricStreamConfiguration
            {
                TagKeys =
                [
                    "http.request.method", "http.response.status_code", "server.address", "server.port",
                    "url.scheme", "error.type", "network.protocol.version", "http.connection.state",
                ],
            },
            PollyMeter => new MetricStreamConfiguration
            {
                TagKeys =
                [
                    "pipeline.name", "pipeline.instance", "strategy.name", "operation.key", "event.name",
                    "event.severity", "attempt.number", "attempt.handled", "exception.type", "error.type",
                    "http.request.method", "http.response.status_code", "server.address", "server.port",
                    "url.scheme", "request.name", "request.dependency.name",
                ],
            },
            Instrumentation.Name => new MetricStreamConfiguration { TagKeys = PrecipMetricTags },
            _ => null,
        };

    /// <summary>
    /// Listed, not derived from <see cref="Instrumentation.Attributes"/>: every key here is a new
    /// time series per value, so each one is a decision. Exit code, outcome and provider have a
    /// handful of values each. The night date (one series a night), the location id and name,
    /// and the per-evaluation numbers are span attributes only (D43).
    /// </summary>
    internal static readonly string[] PrecipMetricTags =
    [
        Instrumentation.Attributes.ExitCode,
        Instrumentation.Attributes.Outcome,
        Instrumentation.Attributes.Provider,
    ];

    /// <summary>
    /// The OTLP settings, read from the standard variable names in <paramref name="configuration"/>
    /// (the host's configuration includes the environment). Null when no endpoint is set.
    /// Set explicitly rather than left to the exporter's own environment reading so that the one
    /// configuration passed in decides, in production and in tests alike.
    /// </summary>
    private static OtlpSettings? ReadOtlp(IConfiguration? configuration)
    {
        var endpoint = configuration?["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute http(s) URL.");

        OtlpExportProtocol? protocol = configuration!["OTEL_EXPORTER_OTLP_PROTOCOL"]?.Trim() switch
        {
            null or "" => null,
            "grpc" => OtlpExportProtocol.Grpc,
            "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
            _ => throw new InvalidOperationException("OTEL_EXPORTER_OTLP_PROTOCOL must be grpc or http/protobuf."),
        };

        var timeout = DefaultExportTimeoutMilliseconds;
        var rawTimeout = configuration["OTEL_EXPORTER_OTLP_TIMEOUT"];
        if (!string.IsNullOrWhiteSpace(rawTimeout)
            && (!int.TryParse(rawTimeout, NumberStyles.None, CultureInfo.InvariantCulture, out timeout) || timeout <= 0))
            throw new InvalidOperationException("OTEL_EXPORTER_OTLP_TIMEOUT must be a positive number of milliseconds.");

        // The headers carry the collector's credentials (a Grafana Cloud token): passed through,
        // never logged or echoed in an error.
        var headers = configuration["OTEL_EXPORTER_OTLP_HEADERS"];
        if (!string.IsNullOrWhiteSpace(headers) && !HeadersAreWellFormed(headers))
            throw new InvalidOperationException(
                "OTEL_EXPORTER_OTLP_HEADERS must be comma-separated key=value pairs (spaces in a value written as %20).");
        return new OtlpSettings(uri, protocol, timeout, string.IsNullOrWhiteSpace(headers) ? null : headers);
    }

    /// <summary>
    /// The shape the OTLP exporter accepts (OpenTelemetry 1.20: split on ',', each pair split at
    /// the first '=', the value URL-decoded, then added as an HTTP header). Anything else throws
    /// inside the SDK when the providers are built, so it is caught here, where it can be reported
    /// without the value.
    /// </summary>
    internal static bool HeadersAreWellFormed(string headers)
    {
        foreach (var pair in headers.Split(','))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                return false;
            var key = pair[..eq].Trim();
            if (key.Length == 0 || !key.All(IsTokenChar))
                return false;
            string value;
            try
            {
                value = Uri.UnescapeDataString(pair[(eq + 1)..].Trim());
            }
            catch (UriFormatException)
            {
                return false;
            }
            if (value.Any(char.IsControl))
                return false;
        }
        return true;
    }

    // RFC 9110 token characters: what an HTTP header name may contain.
    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

    private sealed record OtlpSettings(Uri Endpoint, OtlpExportProtocol? Protocol, int TimeoutMilliseconds, string? Headers)
    {
        public void Apply(OtlpExporterOptions o, string signalPath)
        {
            if (Protocol is { } protocol)
                o.Protocol = protocol;
            // Over http/protobuf, OTEL_EXPORTER_OTLP_ENDPOINT is a base URL and each signal posts to
            // its own path; the exporter appends it only when it reads the variable itself, not when
            // the endpoint is set in code. Over gRPC the endpoint is used as is.
            o.Endpoint = o.Protocol == OtlpExportProtocol.HttpProtobuf
                ? new Uri(Endpoint.AbsoluteUri.TrimEnd('/') + "/" + signalPath)
                : Endpoint;
            o.TimeoutMilliseconds = TimeoutMilliseconds;
            if (Headers is not null)
                o.Headers = Headers;
        }
    }
}

/// <summary>
/// Reports, once at startup and through the normal logs, that a malformed OTEL_* setting turned
/// export off. The message names the key and never the value.
/// </summary>
internal sealed class TelemetryConfigWarning(Microsoft.Extensions.Logging.ILoggerFactory loggers, string problem)
    : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
            loggers.CreateLogger("Worker.Telemetry"),
            "Telemetry: {Problem} OTLP export is off for this run; the pass is unaffected.", problem);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
