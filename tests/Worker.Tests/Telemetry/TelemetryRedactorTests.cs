using System.Diagnostics;
using Worker.Telemetry;

namespace Worker.Tests.Telemetry;

/// <summary>
/// D22 at the telemetry boundary: what survives <see cref="TelemetryRedactor"/> is exactly the
/// allowlist, URLs keep their shape but lose every secret segment, and exception events keep only
/// type and a redacted message. The spans here come from private ActivitySources that borrow the
/// real source names, sampled by a listener that listens to those instances only.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class TelemetryRedactorTests : IDisposable
{
    private const string Uuid = "0b7c1f3e-5d2a-4c8e-9f61-2a3b4c5d6e7f";

    private readonly ActivitySource _http = new(TelemetryRedactor.HttpSource);
    private readonly ActivitySource _npgsql = new(TelemetryRedactor.NpgsqlSource);
    private readonly ActivitySource _precip = new(Instrumentation.Name);
    private readonly ActivitySource _other = new("Some.Other.Library");
    private readonly ActivityListener _listener;
    private readonly TelemetryRedactor _redactor = new();

    public TelemetryRedactorTests()
    {
        ActivitySource[] ours = [_http, _npgsql, _precip, _other];
        _listener = new ActivityListener
        {
            ShouldListenTo = s => ours.Contains(s),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _redactor.Dispose();
        _listener.Dispose();
        _http.Dispose();
        _npgsql.Dispose();
        _precip.Dispose();
        _other.Dispose();
    }

    public static TheoryData<string, string> Urls => new()
    {
        // NWS /points, both signs of coordinate.
        { "https://api.weather.gov/points/40.7128,-74.0060", "https://api.weather.gov/points/{redacted}" },
        { "https://api.weather.gov/points/-33.8688,151.2093", "https://api.weather.gov/points/{redacted}" },
        // The grid cell pins a location to ~2.5 km: office and x,y both go.
        { "https://api.weather.gov/gridpoints/OKX/33,35/forecast/hourly", "https://api.weather.gov/gridpoints/{redacted}/{redacted}/forecast/hourly" },
        { "https://api.weather.gov/gridpoints/OKX/33,35", "https://api.weather.gov/gridpoints/{redacted}/{redacted}" },
        // Open-Meteo: the coordinates are in the query, which is dropped whole.
        {
            "https://api.open-meteo.com/v1/forecast?latitude=40.7128&longitude=-74.006&hourly=precipitation_probability&timezone=auto",
            "https://api.open-meteo.com/v1/forecast"
        },
        // ntfy publish: the topic is the path.
        { "https://ntfy.sh/precip-a8f3k2j9x7q1w4", "https://ntfy.sh/{redacted}" },
        // healthchecks: the UUID goes; "start" and the exit code stay.
        { $"https://hc-ping.com/{Uuid}/start", "https://hc-ping.com/{redacted}/start" },
        { $"https://hc-ping.com/{Uuid}/0", "https://hc-ping.com/{redacted}/0" },
        { $"https://hc-ping.com/{Uuid}/7", "https://hc-ping.com/{redacted}/7" },
        { $"https://hc-ping.com/{Uuid}", "https://hc-ping.com/{redacted}" },
        // A four-digit segment isn't an exit code.
        { "https://hc-ping.com/1234", "https://hc-ping.com/{redacted}" },
        // A non-default port stays, the default one isn't spelled out.
        { "http://localhost:8080/precip-topic", "http://localhost:8080/{redacted}" },
        { "https://ntfy.sh:443/precip-topic", "https://ntfy.sh/{redacted}" },
        // Trailing slash and bare root keep their shape.
        { "https://api.weather.gov/points/40.7128,-74.006/", "https://api.weather.gov/points/{redacted}/" },
        { "https://ntfy.sh/", "https://ntfy.sh/" },
        // User info and fragment go.
        { "https://user:secret@ntfy.sh/topic#frag", "https://ntfy.sh/{redacted}" },
        // Anything that doesn't parse is replaced whole.
        { "not a url", "{redacted}" },
        { "/points/40.7128,-74.006", "{redacted}" },
    };

    [Theory]
    [MemberData(nameof(Urls))]
    public void RedactUrl_keeps_the_shape_and_drops_every_secret(string url, string expected) =>
        Assert.Equal(expected, TelemetryRedactor.RedactUrl(url));

    [Theory]
    [InlineData(
        "Response status code does not indicate success: 404 (Not Found) for https://ntfy.sh/precip-a8f3k2j9x7q1w4.",
        "Response status code does not indicate success: 404 (Not Found) for https://ntfy.sh/{redacted}.")]
    [InlineData(
        "GET https://api.weather.gov/points/40.7128,-74.006 then https://api.weather.gov/gridpoints/OKX/33,35/forecast/hourly failed",
        "GET https://api.weather.gov/points/{redacted} then https://api.weather.gov/gridpoints/{redacted}/{redacted}/forecast/hourly failed")]
    [InlineData(
        "Request to 'https://api.open-meteo.com/v1/forecast?latitude=40.7128&longitude=-74.006' timed out",
        "Request to 'https://api.open-meteo.com/v1/forecast' timed out")]
    [InlineData("(see https://ntfy.sh/precip-topic)", "(see https://ntfy.sh/{redacted})")]
    // A bare host with a path is removed, as Redact.Urls does.
    [InlineData("POST to ntfy.sh/precip-topic failed", "POST to [url removed] failed")]
    // Nothing URL-shaped: unchanged, including a host and port with no path.
    [InlineData("Connection refused (ntfy.sh:443)", "Connection refused (ntfy.sh:443)")]
    [InlineData("23505", "23505")]
    public void RedactUrlsIn_rewrites_every_url_in_a_longer_message(string text, string expected) =>
        Assert.Equal(expected, TelemetryRedactor.RedactUrlsIn(text));

    [Fact]
    public void RedactUrlsIn_is_idempotent()
    {
        var once = TelemetryRedactor.RedactUrlsIn($"failed: https://hc-ping.com/{Uuid}/start and https://ntfy.sh/t");
        Assert.Equal(once, TelemetryRedactor.RedactUrlsIn(once));
    }

    [Fact]
    public void Http_span_keeps_only_the_allowlist_and_a_redacted_url()
    {
        var activity = Start(_http, "GET api.weather.gov/points/40.7128,-74.006");
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.response.status_code", 200);
        activity.SetTag("server.address", "api.weather.gov");
        activity.SetTag("server.port", 443);
        activity.SetTag("url.scheme", "https");
        activity.SetTag("network.protocol.version", "1.1");
        activity.SetTag("http.request.resend_count", 1);
        activity.SetTag("error.type", "500");
        activity.SetTag("url.full", "https://api.weather.gov/points/40.7128,-74.006");
        activity.SetTag("url.path", "/points/40.7128,-74.006");
        activity.SetTag("url.query", "latitude=40.7128");
        activity.SetTag("http.url", "https://api.weather.gov/points/40.7128,-74.006");
        activity.SetTag("network.peer.address", "203.0.113.7");
        activity.SetTag("http.request.method_original", "get");

        End(activity);

        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["http.request.method"] = "GET",
                ["http.response.status_code"] = 200,
                ["server.address"] = "api.weather.gov",
                ["server.port"] = 443,
                ["url.scheme"] = "https",
                ["network.protocol.version"] = "1.1",
                ["http.request.resend_count"] = 1,
                ["error.type"] = "500",
                ["url.full"] = "https://api.weather.gov/points/{redacted}",
            },
            Tags(activity));
        Assert.Equal("GET", activity.DisplayName);
        Assert.True(activity.Recorded);
    }

    [Fact]
    public void Http_span_without_a_method_is_named_HTTP()
    {
        var activity = Start(_http, "https://ntfy.sh/topic");
        End(activity);
        Assert.Equal("HTTP", activity.DisplayName);
    }

    [Fact]
    public void Npgsql_span_drops_the_data_source_and_connection_details()
    {
        var activity = Start(_npgsql, "postgresql");
        activity.SetTag("db.system.name", "postgresql");
        activity.SetTag("db.namespace", "precip");
        activity.SetTag("db.operation.name", "SELECT");
        activity.SetTag("db.query.text", "SELECT l.id FROM locations AS l WHERE l.id = @p0");
        activity.SetTag("server.address", "ep-example.neon.tech");
        activity.SetTag("server.port", 5433);
        activity.SetTag("error.type", "23505");
        activity.SetTag("db.response.status_code", "23505");
        // Emitted by Npgsql 10.0.3 but not allowed:
        activity.SetTag("db.npgsql.data_source", "Host=ep-example.neon.tech;Database=precip;Username=precip_owner");
        activity.SetTag("db.npgsql.connection_id", 42);
        activity.SetTag("db.npgsql.prepared", false);
        activity.SetTag("db.npgsql.rows", 3);
        activity.SetTag("db.collection.name", "locations");
        // Never emitted by this version, listed by the brief, must still go:
        activity.SetTag("db.user", "precip_owner");
        activity.SetTag("db.connection_string", "Host=x;Password=hunter2");
        activity.SetTag("network.peer.address", "203.0.113.7");
        activity.SetTag("network.peer.port", 5432);

        End(activity);

        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["db.system.name"] = "postgresql",
                ["db.namespace"] = "precip",
                ["db.operation.name"] = "SELECT",
                ["db.query.text"] = "SELECT l.id FROM locations AS l WHERE l.id = @p0",
                ["server.address"] = "ep-example.neon.tech",
                ["server.port"] = 5433,
                ["error.type"] = "23505",
                ["db.response.status_code"] = "23505",
            },
            Tags(activity));
        Assert.Equal("postgresql", activity.DisplayName);
    }

    [Fact]
    public void Precip_span_keeps_precip_keys_only()
    {
        var activity = Start(_precip, Instrumentation.Spans.Location);
        activity.SetTag(Instrumentation.Attributes.LocationId, 1);
        activity.SetTag(Instrumentation.Attributes.Outcome, Instrumentation.Outcomes.Dry);
        activity.SetTag("latitude", 40.7128);
        activity.SetTag("url.full", "https://ntfy.sh/topic");
        activity.SetTag("precipitation", "not a precip.* key");

        End(activity);

        Assert.Equal(
            new Dictionary<string, object?>
            {
                [Instrumentation.Attributes.LocationId] = 1,
                [Instrumentation.Attributes.Outcome] = Instrumentation.Outcomes.Dry,
            },
            Tags(activity));
    }

    [Fact]
    public void Unknown_source_loses_every_tag()
    {
        var activity = Start(_other, "whatever");
        activity.SetTag("precip.outcome", "dry");
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("server.address", "example.com");

        End(activity);

        Assert.Empty(Tags(activity));
    }

    [Fact]
    public void Status_description_urls_are_redacted()
    {
        var activity = Start(_http, "GET");
        activity.SetStatus(ActivityStatusCode.Error, $"Ping to https://hc-ping.com/{Uuid}/start failed");

        End(activity);

        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("Ping to https://hc-ping.com/{redacted}/start failed", activity.StatusDescription);
    }

    [Fact]
    public void Exception_event_keeps_type_and_redacted_message_and_no_stack_trace()
    {
        var activity = Start(_npgsql, "postgresql");
        Exception thrown;
        try
        {
            throw new HttpRequestException(
                "Request to https://ntfy.sh/precip-a8f3k2j9x7q1w4 failed",
                new InvalidOperationException("inner mentions https://api.weather.gov/points/40.7128,-74.006"));
        }
        catch (HttpRequestException ex)
        {
            thrown = ex;
        }
        activity.AddException(thrown, new TagList { { "exception.escaped", true }, { "extra", "x" } });

        End(activity);

        var e = Assert.Single(activity.Events);
        Assert.Equal("exception", e.Name);
        var tags = e.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["exception.type"] = typeof(HttpRequestException).FullName,
                ["exception.message"] = "Request to https://ntfy.sh/{redacted} failed",
                // AddException fills in a stack trace only when the key is absent, so the key is
                // pre-empted with no value: the closest the public API allows to removing it.
                ["exception.stacktrace"] = null,
            },
            tags);
        Assert.True(activity.Recorded);
    }

    [Fact]
    public void Exception_event_added_by_hand_with_a_stack_trace_drops_the_span()
    {
        var activity = Start(_http, "GET");
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = "System.Exception",
            ["exception.message"] = "boom",
            ["exception.stacktrace"] = "at Somewhere() in https://ntfy.sh/topic",
        }));

        End(activity);

        Assert.False(activity.Recorded);
    }

    [Fact]
    public void Exception_event_added_by_hand_with_an_unredacted_message_drops_the_span()
    {
        var activity = Start(_http, "GET");
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = "System.Exception",
            ["exception.message"] = "failed for https://ntfy.sh/topic",
        }));

        End(activity);

        Assert.False(activity.Recorded);
    }

    [Fact]
    public void Event_with_a_tag_off_the_allowlist_drops_the_span()
    {
        var activity = Start(_precip, Instrumentation.Spans.Fetch);
        activity.AddEvent(new ActivityEvent("retry", tags: new ActivityTagsCollection { ["url.full"] = "https://ntfy.sh/topic" }));

        End(activity);

        Assert.False(activity.Recorded);
    }

    [Fact]
    public void Events_with_allowlisted_or_no_tags_are_kept()
    {
        var activity = Start(_precip, Instrumentation.Spans.Fetch);
        activity.AddEvent(new ActivityEvent("received-first-response"));
        activity.AddEvent(new ActivityEvent("fallback", tags: new ActivityTagsCollection { [Instrumentation.Attributes.Provider] = "Open-Meteo" }));

        End(activity);

        Assert.True(activity.Recorded);
        Assert.Equal(2, activity.Events.Count());
    }

    [Fact]
    public void Link_with_tags_drops_the_span()
    {
        var activity = _precip.StartActivity(
            Instrumentation.Spans.Pass,
            ActivityKind.Internal,
            parentContext: default,
            links: [new ActivityLink(default, new ActivityTagsCollection { ["coords"] = "40.7128,-74.006" })])!;

        End(activity);

        Assert.False(activity.Recorded);
    }

    [Fact]
    public void Own_baggage_is_cleared()
    {
        var activity = Start(_precip, Instrumentation.Spans.Pass);
        activity.SetBaggage("topic", "precip-a8f3k2j9x7q1w4");

        End(activity);

        Assert.Empty(activity.Baggage);
    }

    private static Activity Start(ActivitySource source, string name) =>
        source.StartActivity(name, ActivityKind.Client) ?? throw new InvalidOperationException("Not sampled.");

    private void End(Activity activity)
    {
        activity.Stop();
        _redactor.OnEnd(activity);
    }

    private static Dictionary<string, object?> Tags(Activity activity) =>
        activity.TagObjects.ToDictionary(t => t.Key, t => t.Value);
}
