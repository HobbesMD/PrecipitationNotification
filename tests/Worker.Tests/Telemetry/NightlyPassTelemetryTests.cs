using System.Diagnostics;
using System.Diagnostics.Metrics;
using TestSupport;
using Worker.Pass;
using Worker.Tests.Pass;
using Worker.Telemetry;

namespace Worker.Tests.Telemetry;

/// <summary>
/// The spans and metrics <see cref="NightlyPass"/> emits, captured with a plain ActivityListener
/// and MeterListener (no OpenTelemetry SDK), over the same harness as the pass tests.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class NightlyPassTelemetryTests(PostgresFixture postgres)
{
    /// <summary>One expected location: its outcome, its provider tag (null = none), and its child spans.</summary>
    private sealed record Loc(string Outcome, string? Provider, params string[] Children);

    private sealed record Expect(
        int ExitCode, Loc[] Locations, int Fallbacks = 0, string? AlertProvider = null, int Alerts = 0);

    // Child span notation: name, then ":" provider for fetches, then "!" when the span ended in Error.
    private static readonly Dictionary<string, Expect> Table = new()
    {
        ["dry"] = new(0, [new("dry", "NWS", "fetch:NWS", "evaluate")]),
        ["alert"] = new(0, [new("alerted", "NWS", "fetch:NWS", "evaluate", "notify")], AlertProvider: "NWS", Alerts: 1),
        ["already_sent"] = new(0, [new("already_sent", null, "fetch:NWS")]),
        ["fallback_ok"] = new(
            0,
            [new("alerted", "Open-Meteo", "fetch:NWS!", "fetch:Open-Meteo", "evaluate", "notify")],
            Fallbacks: 1, AlertProvider: "Open-Meteo", Alerts: 1),
        ["both_fail"] = new(2, [new("failed", null, "fetch:NWS!", "fetch:Open-Meteo!")], Fallbacks: 1),
        ["deadline"] = new(
            7,
            [new("alerted", "NWS", "fetch:NWS", "evaluate", "notify"), new("skipped_deadline", null)],
            AlertProvider: "NWS", Alerts: 1),
        ["db_down"] = new(6, []),
        ["dry_run"] = new(0, [new("alerted", "NWS", "fetch:NWS", "evaluate")]),
    };

    public static TheoryData<string> Scenarios => [.. Table.Keys];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Pass_emits_the_expected_spans_and_metrics(string scenario)
    {
        var expect = Table[scenario];
        using var capture = new Capture();

        var code = await RunScenarioAsync(scenario);

        Assert.Equal(expect.ExitCode, code);

        // ---- span tree ----
        var pass = Assert.Single(capture.Spans, s => s.OperationName == Instrumentation.Spans.Pass);
        Assert.Equal(ActivityKind.Internal, pass.Kind);
        Assert.Null(pass.ParentId);
        Assert.Equal(expect.ExitCode, pass.GetTagItem(Instrumentation.Attributes.ExitCode));
        Assert.Equal(expect.ExitCode != 0 ? ActivityStatusCode.Error : ActivityStatusCode.Unset, pass.Status);
        if (scenario != "db_down")
            Assert.Equal(scenario == "deadline" ? 2 : 1, pass.GetTagItem(Instrumentation.Attributes.LocationsCount));

        var locationSpans = capture.Spans.Where(s => s.OperationName == Instrumentation.Spans.Location).ToList();
        Assert.Equal(expect.Locations.Length, locationSpans.Count);
        for (var i = 0; i < locationSpans.Count; i++)
        {
            var span = locationSpans[i];
            var loc = expect.Locations[i];
            Assert.Equal(pass.SpanId, span.ParentSpanId);
            Assert.Equal(loc.Outcome, span.GetTagItem(Instrumentation.Attributes.Outcome));
            Assert.Equal(loc.Provider, span.GetTagItem(Instrumentation.Attributes.Provider));
            Assert.NotNull(span.GetTagItem(Instrumentation.Attributes.LocationId));
            Assert.NotNull(span.GetTagItem(Instrumentation.Attributes.LocationName));

            if (loc.Outcome == "skipped_deadline")
            {
                Assert.Null(span.GetTagItem(Instrumentation.Attributes.ExitCode));
                Assert.Null(span.GetTagItem(Instrumentation.Attributes.NightDate));
            }
            else
            {
                var locCode = loc.Outcome == "failed" ? expect.ExitCode : 0;
                Assert.Equal(locCode, span.GetTagItem(Instrumentation.Attributes.ExitCode));
                Assert.Equal(locCode != 0 ? ActivityStatusCode.Error : ActivityStatusCode.Unset, span.Status);
                Assert.Equal("2026-10-29", span.GetTagItem(Instrumentation.Attributes.NightDate));
            }

            var children = capture.Spans.Where(s => s.ParentSpanId == span.SpanId).Select(Describe).ToArray();
            Assert.Equal(loc.Children, children);
        }

        // Every non-pass, non-location span hangs off a location span.
        var locationIds = locationSpans.Select(s => s.SpanId).ToHashSet();
        foreach (var child in capture.Spans.Where(s => s.OperationName is not (Instrumentation.Spans.Pass or Instrumentation.Spans.Location)))
            Assert.Contains(child.ParentSpanId, locationIds);

        // ---- metrics ----
        var passes = Assert.Single(capture.Measurements, m => m.Name == "precip.passes");
        Assert.Equal(1, passes.Value);
        Assert.Equal(expect.ExitCode, passes.Tags[Instrumentation.Attributes.ExitCode]);

        Assert.Single(capture.Measurements, m => m.Name == "precip.pass.duration");

        var locationMetrics = capture.Measurements
            .Where(m => m.Name == "precip.locations")
            .Select(m => (Outcome: (string)m.Tags[Instrumentation.Attributes.Outcome]!, Provider: (string)m.Tags[Instrumentation.Attributes.Provider]!))
            .ToArray();
        Assert.Equal(
            expect.Locations.Select(l => (l.Outcome, l.Provider ?? "none")).ToArray(),
            locationMetrics);

        Assert.Equal(expect.Fallbacks, capture.Measurements.Where(m => m.Name == "precip.provider.fallbacks").Sum(m => m.Value));
        var alerts = capture.Measurements.Where(m => m.Name == "precip.alerts.sent").ToList();
        Assert.Equal(expect.Alerts, alerts.Sum(m => m.Value));
        foreach (var a in alerts)
            Assert.Equal(expect.AlertProvider, a.Tags[Instrumentation.Attributes.Provider]);

        // ---- privacy: no location in metric tags; no coordinate, URL or secret on any span ----
        foreach (var m in capture.Measurements)
            Assert.DoesNotContain(m.Tags.Keys, k => k.Contains("location", StringComparison.OrdinalIgnoreCase));

        foreach (var span in capture.Spans)
        {
            var texts = span.TagObjects.Select(t => t.Value?.ToString() ?? "")
                .Append(span.StatusDescription ?? "")
                .Append(span.DisplayName);
            foreach (var text in texts)
            {
                Assert.DoesNotContain("40.71", text);
                Assert.DoesNotContain("74.00", text);
                Assert.DoesNotContain("://", text);
                Assert.DoesNotContain(DeadDatabase.Password, text);
            }

            Assert.Empty(span.Events);
        }
    }

    [Fact]
    public async Task Cancellation_marks_the_pass_span_cancelled_and_rethrows()
    {
        using var capture = new Capture();
        FreeIdleConnections();
        await using var h = await PassHarness.CreateAsync(postgres);
        await h.AddLocationAsync("alpha");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync(ct: cts.Token));

        var pass = Assert.Single(capture.Spans, s => s.OperationName == Instrumentation.Spans.Pass);
        Assert.Equal(ActivityStatusCode.Error, pass.Status);
        Assert.Equal("cancelled", pass.StatusDescription);
        Assert.DoesNotContain(capture.Measurements, m => m.Name == "precip.passes");
    }

    /// <summary>
    /// This collection runs last, alone, after hundreds of tests each left an idle pool of connections
    /// to its own database; the shared server would answer "too many clients". Drop the idle ones.
    /// </summary>
    private static void FreeIdleConnections() => Npgsql.NpgsqlConnection.ClearAllPools();

    private async Task<int> RunScenarioAsync(string scenario)
    {
        if (scenario == "db_down")
        {
            await using var dead = PassHarness.Over(DeadDatabase.CreateContext());
            return await dead.RunAsync();
        }

        FreeIdleConnections();
        await using var h = await PassHarness.CreateAsync(postgres);
        switch (scenario)
        {
            case "dry":
                await h.AddLocationAsync("alpha", threshold: 50);
                break;
            case "alert":
            case "dry_run":
                await h.AddLocationAsync("alpha", threshold: 0);
                break;
            case "already_sent":
                h.Ledger.Sent.Add((await h.AddLocationAsync("alpha", threshold: 0), PassHarness.Night));
                break;
            case "fallback_ok":
                await h.AddLocationAsync("alpha", threshold: 0);
                h.Forecasts.Handler = _ => throw new HttpRequestException("nws down");
                h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo();
                break;
            case "both_fail":
                await h.AddLocationAsync("alpha", threshold: 0);
                h.Forecasts.Handler = _ => throw new HttpRequestException("nws down");
                break;
            case "deadline":
                await h.AddLocationAsync("first", threshold: 0);
                await h.AddLocationAsync("second", threshold: 0);
                h.Forecasts.Handler = _ =>
                {
                    h.Clock.Advance(TimeSpan.FromSeconds(241));
                    return FixtureForecast.Full();
                };
                break;
            default:
                throw new ArgumentException(scenario);
        }

        return await h.RunAsync(dryRun: scenario == "dry_run");
    }

    private static string Describe(Activity span)
    {
        var name = span.OperationName["precip.".Length..];
        if (span.OperationName == Instrumentation.Spans.Fetch)
            name += ":" + span.GetTagItem(Instrumentation.Attributes.Provider);
        return span.Status == ActivityStatusCode.Error ? name + "!" : name;
    }

    private sealed record Measurement(string Name, double Value, Dictionary<string, object?> Tags);

    /// <summary>Listens to source and meter "Precip.Worker" until disposed.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters = new();
        private readonly object _gate = new();
        private readonly List<Activity> _started = [];
        private readonly List<Measurement> _measurements = [];

        public Capture()
        {
            _activities = new ActivityListener
            {
                ShouldListenTo = s => s.Name == Instrumentation.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = a =>
                {
                    lock (_gate)
                        _started.Add(a);
                },
            };
            ActivitySource.AddActivityListener(_activities);

            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Instrumentation.Name)
                    listener.EnableMeasurementEvents(instrument);
            };
            _meters.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i.Name, v, tags));
            _meters.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i.Name, v, tags));
            _meters.Start();
        }

        /// <summary>Spans in the order they started (a parent before its children).</summary>
        public IReadOnlyList<Activity> Spans
        {
            get
            {
                lock (_gate)
                    return [.. _started];
            }
        }

        public IReadOnlyList<Measurement> Measurements
        {
            get
            {
                lock (_gate)
                    return [.. _measurements];
            }
        }

        private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var t in tags)
                dict[t.Key] = t.Value;
            lock (_gate)
                _measurements.Add(new Measurement(name, value, dict));
        }

        public void Dispose()
        {
            _meters.Dispose();
            _activities.Dispose();
        }
    }
}
