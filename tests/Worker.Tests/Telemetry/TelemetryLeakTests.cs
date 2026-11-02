using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Logs;
using TestSupport;
using WireMock.Server;

namespace Worker.Tests.Telemetry;

/// <summary>
/// The test behind D22 for telemetry: the real <c>--once</c> host runs end to end against fakes,
/// every span, metric and log record it would export is captured in memory (after the redactor),
/// and none of it may contain a secret. Scenarios cover the alert path, the fallback and publish
/// failure path, and a dead database (whose exceptions carry the connection string). A negative
/// control proves the scanner reports a leak when there is one, and the trace-shape assertions in
/// the alert scenario prove the run actually produced something to scan.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class TelemetryLeakTests(PostgresFixture postgres)
{
    /// <summary>
    /// Retries on, but tiny: Polly needs at least two attempts, and its backoff waits on the injected
    /// TimeProvider, which LeakTestHost keeps ticking in real time.
    /// </summary>
    private static readonly Dictionary<string, string?> FastResilience = new()
    {
        ["Resilience:Nws:MaxAttempts"] = "2",
        ["Resilience:Nws:BackoffBaseDelay"] = "00:00:00.010",
        ["Resilience:OpenMeteo:MaxAttempts"] = "2",
        ["Resilience:OpenMeteo:BackoffBaseDelay"] = "00:00:00.010",
        ["Resilience:Ntfy:MaxAttempts"] = "2",
        ["Resilience:Ntfy:BackoffBaseDelay"] = "00:00:00.010",
    };

    [Fact]
    public async Task Alert_path_has_the_intended_trace_shape_and_leaks_nothing()
    {
        var (admin, role) = await LeakTestHost.CreateDatabaseAsync(postgres);
        await LeakTestHost.SeedLocationAsync(admin);
        using var server = WireMockServer.Start();
        LeakTestHost.ConfigureServer(server, new LeakStubs());

        var run = await LeakTestHost.RunAsync(server, role, FastResilience);

        Assert.Equal(0, run.ExitCode);

        // Shape: this is what stops the leak scan passing vacuously.
        var pass = Assert.Single(run.Spans, s => s.OperationName == "precip.pass");
        Assert.Equal(default, pass.ParentSpanId);
        var location = Assert.Single(run.Spans, s => s.OperationName == "precip.location");
        Assert.Equal(pass.SpanId, location.ParentSpanId);
        Assert.Equal("alerted", location.GetTagItem("precip.outcome"));
        var fetch = Assert.Single(run.Spans, s => s.OperationName == "precip.fetch");
        Assert.Equal("NWS", fetch.GetTagItem("precip.provider"));
        Assert.Equal(location.SpanId, fetch.ParentSpanId);
        var evaluate = Assert.Single(run.Spans, s => s.OperationName == "precip.evaluate");
        Assert.Equal(location.SpanId, evaluate.ParentSpanId);
        var notify = Assert.Single(run.Spans, s => s.OperationName == "precip.notify");
        Assert.Equal(location.SpanId, notify.ParentSpanId);

        var http = run.Spans.Where(s => s.Source.Name == "System.Net.Http").ToList();
        var underFetch = http.Where(s => s.ParentSpanId == fetch.SpanId).ToList();
        var underNotify = http.Where(s => s.ParentSpanId == notify.SpanId).ToList();
        Assert.NotEmpty(underFetch);
        Assert.NotEmpty(underNotify);
        Assert.All(underFetch.Concat(underNotify), s => Assert.Contains("{redacted}", Url(s)));

        Assert.Contains(run.Spans, s => s.Source.Name == "Npgsql");

        var starts = http.Where(s => Url(s).EndsWith("/start", StringComparison.Ordinal)).ToList();
        var finishes = http.Where(s => Url(s).EndsWith("/0", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(starts);
        Assert.NotEmpty(finishes);
        Assert.All(starts.Concat(finishes), s => Assert.DoesNotContain(LeakTestHost.HeartbeatUuid, Url(s)));

        Assert.Contains(run.Metrics, m => m.Name == "precip.passes");
        Assert.Contains(run.Metrics, m => m.Name == "precip.alerts.sent");

        var pings = run.Requests.Where(r => r.Path.StartsWith("/ping/", StringComparison.Ordinal)).Select(r => r.Path).ToList();
        Assert.Equal([$"/ping/{LeakTestHost.HeartbeatUuid}/start", $"/ping/{LeakTestHost.HeartbeatUuid}/0"], pings);
        Assert.Single(run.Requests, r => r.Method == "POST" && r.Path == "/" + LeakTestHost.Topic);

        LeakScanner.AssertNoLeaks(run);

        TestContext.Current.SendDiagnosticMessage(
            "trace: " + string.Join(" | ", run.Spans.Select(s => $"{s.OperationName}<-{ParentName(run, s)}")));
    }

    [Fact]
    public async Task Fallback_and_publish_failure_leak_nothing()
    {
        var (admin, role) = await LeakTestHost.CreateDatabaseAsync(postgres);
        await LeakTestHost.SeedLocationAsync(admin);
        using var server = WireMockServer.Start();
        LeakTestHost.ConfigureServer(server, new LeakStubs(NwsStatus: 500, NtfyStatus: 500));

        var run = await LeakTestHost.RunAsync(server, role, FastResilience);

        Assert.Equal(3, run.ExitCode);
        Assert.Contains(run.Requests, r => r.Path == "/v1/forecast");
        Assert.Contains(run.Requests, r => r.Path == $"/ping/{LeakTestHost.HeartbeatUuid}/3");
        Assert.Contains(run.Spans, s => s.OperationName == "precip.fetch" && Equals(s.GetTagItem("precip.provider"), "Open-Meteo"));
        LeakScanner.AssertNoLeaks(run);
    }

    [Fact]
    public async Task Database_down_leaks_nothing()
    {
        NpgsqlConnection.ClearAllPools();
        var dead = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = "precip",
            Username = "u",
            Password = LeakTestHost.DbRolePassword,
            Timeout = 2,
            Pooling = false,
        }.ConnectionString;
        using var server = WireMockServer.Start();
        LeakTestHost.ConfigureServer(server, new LeakStubs());

        var run = await LeakTestHost.RunAsync(server, dead, FastResilience);

        Assert.Equal(6, run.ExitCode);
        Assert.Contains(run.Requests, r => r.Path == $"/ping/{LeakTestHost.HeartbeatUuid}/6");
        LeakScanner.AssertNoLeaks(run);
    }

    [Fact]
    public void Negative_control_scanner_reports_a_secret_on_a_span_and_in_a_log_message()
    {
        var secrets = new List<Secret> { new("ntfy topic", LeakTestHost.Topic), new("lat,lon pair", "40.7128,-74.006") };

        var spans = new List<Activity>();
        using (var source = new ActivitySource("leak.negative.control"))
        using (var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "leak.negative.control",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        })
        {
            ActivitySource.AddActivityListener(listener);
            using var activity = source.StartActivity("handmade");
            Assert.NotNull(activity);
            activity.SetTag("note", $"posted to /{LeakTestHost.Topic}");
        }

        var logs = new List<LogRecord>();
        using (var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.AddInMemoryExporter(logs);
        })))
        {
            factory.CreateLogger("handmade").LogInformation("looked up 40.7128,-74.006");
        }

        var run = new LeakRun { Spans = spans, Logs = logs, Secrets = secrets };
        var findings = LeakScanner.Findings(run);

        Assert.Contains(findings, f => f.Contains("ntfy topic") && f.Contains("tag note"));
        Assert.Contains(findings, f => f.Contains("lat,lon pair") && f.Contains("log"));
        Assert.Throws<Xunit.Sdk.TrueException>(() => LeakScanner.AssertNoLeaks(run));
    }

    private static string Url(Activity a) => Convert.ToString(a.GetTagItem("url.full"), CultureInfo.InvariantCulture) ?? "";

    private static string ParentName(LeakRun run, Activity a) =>
        run.Spans.FirstOrDefault(p => p.SpanId == a.ParentSpanId)?.OperationName ?? "(root)";
}

/// <summary>Flattens every exported signal into (where, text) pairs and searches them for secrets.</summary>
internal static class LeakScanner
{
    public static void AssertNoLeaks(LeakRun run)
    {
        var findings = Findings(run);
        Assert.True(findings.Count == 0, "Secret(s) found in exported telemetry:" + Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    public static List<string> Findings(LeakRun run)
    {
        var findings = new List<string>();
        foreach (var (where, text) in Flatten(run))
        {
            foreach (var secret in run.Secrets)
            {
                if (text.Contains(secret.Value, StringComparison.OrdinalIgnoreCase))
                    findings.Add($"[{secret.Label}] in {where}");
            }
        }

        return findings;
    }

    public static IEnumerable<(string Where, string Text)> Flatten(LeakRun run)
    {
        for (var i = 0; i < run.Spans.Count; i++)
        {
            var s = run.Spans[i];
            var p = $"span #{i} ({s.OperationName})";
            yield return ($"{p} DisplayName", s.DisplayName);
            yield return ($"{p} OperationName", s.OperationName);
            yield return ($"{p} source", s.Source.Name);
            yield return ($"{p} StatusDescription", s.StatusDescription ?? "");
            yield return ($"{p} TraceStateString", s.TraceStateString ?? "");
            foreach (var t in s.TagObjects)
            {
                yield return ($"{p} tag key", t.Key);
                yield return ($"{p} tag {t.Key}", Text(t.Value));
            }

            foreach (var b in s.Baggage)
            {
                yield return ($"{p} baggage key", b.Key);
                yield return ($"{p} baggage {b.Key}", b.Value ?? "");
            }

            foreach (var e in s.Events)
            {
                yield return ($"{p} event name", e.Name);
                foreach (var t in e.Tags)
                {
                    yield return ($"{p} event {e.Name} tag key", t.Key);
                    yield return ($"{p} event {e.Name} tag {t.Key}", Text(t.Value));
                }
            }

            foreach (var l in s.Links)
            {
                yield return ($"{p} link context", l.Context.TraceId + "/" + l.Context.SpanId + "/" + l.Context.TraceState);
                foreach (var t in l.Tags ?? [])
                {
                    yield return ($"{p} link tag key", t.Key);
                    yield return ($"{p} link tag {t.Key}", Text(t.Value));
                }
            }
        }

        foreach (var m in run.Metrics)
        {
            var p = $"metric {m.Name}";
            yield return ($"{p} name", m.Name);
            yield return ($"{p} description", m.Description ?? "");
            yield return ($"{p} unit", m.Unit ?? "");
            yield return ($"{p} meter", m.MeterName);
            foreach (var point in m.MetricPoints)
            {
                foreach (var t in point.Tags)
                {
                    yield return ($"{p} point tag key", t.Key);
                    yield return ($"{p} point tag {t.Key}", Text(t.Value));
                }
            }
        }

        for (var i = 0; i < run.Logs.Count; i++)
        {
            var l = run.Logs[i];
            var p = $"log #{i} ({l.CategoryName})";
            yield return ($"{p} FormattedMessage", l.FormattedMessage ?? "");
            yield return ($"{p} Body", l.Body ?? "");
            yield return ($"{p} CategoryName", l.CategoryName ?? "");
            yield return ($"{p} EventId", l.EventId.Name ?? "");
            foreach (var a in l.Attributes ?? [])
            {
                yield return ($"{p} attribute key", a.Key);
                yield return ($"{p} attribute {a.Key}", Text(a.Value));
            }

            if (l.Exception is { } ex)
            {
                yield return ($"{p} exception type", ex.GetType().FullName ?? "");
                yield return ($"{p} exception message", ex.Message);
                yield return ($"{p} exception stack", ex.ToString());
            }

            var scopes = new List<(string, string)>();
            l.ForEachScope((scope, state) =>
            {
                foreach (var kv in scope)
                    state.Add(($"{p} scope {kv.Key}", kv.Key + "=" + Text(kv.Value)));
            }, scopes);
            foreach (var s in scopes)
                yield return s;
        }

        foreach (var (signal, key, value) in run.ResourceAttributes)
        {
            yield return ($"{signal} resource attribute key", key);
            yield return ($"{signal} resource attribute {key}", value);
        }

        // Not exported anywhere, but the worker's own stderr is what a failed run leaves behind.
        yield return ("stderr", run.Stderr);
    }

    private static string Text(object? value) => value switch
    {
        null => "",
        string s => s,
        IEnumerable e => string.Join(",", e.Cast<object?>().Select(Text)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}
