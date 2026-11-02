using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OpenTelemetry;

namespace Worker.Telemetry;

/// <summary>
/// The last thing that touches a span before any exporter sees it (D22): the ntfy topic, the
/// coordinates (and the NWS grid cell, which pins a location to ~2.5 km), the healthchecks ping
/// UUID and the connection string must never leave the process. Works as an allowlist per
/// <see cref="ActivitySource"/> name: a tag that isn't listed is removed, so a new tag added by a
/// library upgrade is dropped until someone decides it is safe (fail closed).
///
/// What it does, in <see cref="OnEnd"/>:
/// <list type="bullet">
/// <item>Tags: kept only if on the source's allowlist; <c>url.full</c> is rewritten by <see cref="RedactUrl"/>.</item>
/// <item>Status description: every URL in it rewritten (<see cref="RedactUrlsIn"/>).</item>
/// <item>HTTP span names: reduced to the request method.</item>
/// <item>Baggage: the span's own baggage cleared.</item>
/// <item>Events and links: these cannot be edited after they are added (there is no public API to
///   remove or replace an <see cref="ActivityEvent"/>). Exception events are scrubbed as they are
///   recorded instead, by an <see cref="ActivityListener.ExceptionRecorder"/> this processor owns
///   (see <see cref="ScrubException"/>). Any event or link that still carries a tag this processor
///   can't vouch for causes the whole span to be dropped: the Recorded flag is cleared, and
///   OpenTelemetry's export processors skip spans that are not recorded.</item>
/// </list>
///
/// Must be registered before every exporter: OpenTelemetry runs processors in registration order.
/// </summary>
internal sealed partial class TelemetryRedactor : BaseProcessor<Activity>
{
    public const string HttpSource = "System.Net.Http";
    public const string NpgsqlSource = "Npgsql";

    /// <summary>What a path segment that isn't known to be safe becomes.</summary>
    public const string RedactedSegment = "{redacted}";

    private const string ExceptionEvent = "exception";
    private const string ExceptionType = "exception.type";
    private const string ExceptionMessage = "exception.message";
    private const string ExceptionStackTrace = "exception.stacktrace";
    private const string UrlFull = "url.full";

    /// <summary>
    /// .NET's own HttpClient spans. <c>url.full</c> is allowed only because it is rewritten; it is
    /// never allowed on an event (which can't be rewritten). <c>url.path</c>, <c>url.query</c> and
    /// the old <c>http.url</c> are not listed, so they go.
    /// </summary>
    private static readonly HashSet<string> HttpTags = new(StringComparer.Ordinal)
    {
        "http.request.method", "http.response.status_code", "server.address", "server.port",
        "url.scheme", "error.type", "network.protocol.version", "http.request.resend_count",
    };

    /// <summary>
    /// Npgsql 10.0.3 (NpgsqlActivitySource) sets these span tags: db.query.text, db.npgsql.prepared,
    /// db.operation.name, db.stored_procedure.name, db.collection.name, db.system.name,
    /// db.npgsql.data_source, db.namespace, server.address, server.port, db.npgsql.connection_id,
    /// db.response.status_code, error.type, db.npgsql.rows. Kept: the ones below (the older
    /// db.system / db.name / db.statement names too, in case a version emits them). Dropped among
    /// them: db.npgsql.data_source, which is the data source's name and so by default the
    /// connection string (password removed, but still user, host and database), plus the
    /// connection id, row count, prepared flag and the procedure/collection names. db.user,
    /// db.connection_string and network.peer.* are not emitted by this version and are not listed.
    /// db.query.text is EF Core's parameterised SQL: values are never in it.
    /// </summary>
    private static readonly HashSet<string> NpgsqlTags = new(StringComparer.Ordinal)
    {
        "db.system", "db.system.name", "db.namespace", "db.name", "db.operation.name",
        "db.query.text", "db.statement", "server.address", "server.port", "error.type",
        "db.response.status_code",
    };

    /// <summary>Path segments every URL we call is built from that carry nothing secret.</summary>
    private static readonly HashSet<string> SafeSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "points", "gridpoints", "forecast", "hourly", "v1", "start",
    };

    private readonly ActivityListener _exceptionScrubber;

    public TelemetryRedactor()
    {
        // Samples nothing itself: it exists only to see AddException calls on every source and
        // rewrite the event's tags before the event is created (the event can't be changed after).
        _exceptionScrubber = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.None,
            ExceptionRecorder = ScrubException,
        };
        ActivitySource.AddActivityListener(_exceptionScrubber);
    }

    /// <summary>
    /// Runs inside <see cref="Activity.AddException"/>, before the event exists. Sets the type
    /// and a URL-redacted message (matching <c>Redact.Describe</c>: type and message only), and
    /// pre-empts the stack trace with a null value: <c>AddException</c> only fills in the keys
    /// that are absent, and <c>exception.ToString()</c> would bring the inner exceptions'
    /// messages along. The key therefore survives with an empty value; that is the closest the
    /// public API allows. Any other caller-supplied tag is removed.
    /// </summary>
    internal static void ScrubException(Activity activity, Exception exception, ref TagList tags)
    {
        tags = new TagList
        {
            { ExceptionType, exception.GetType().FullName },
            { ExceptionMessage, RedactUrlsIn(exception.Message) },
            { ExceptionStackTrace, null },
        };
    }

    public override void OnEnd(Activity data)
    {
        var source = data.Source.Name;
        Func<string, bool> allowed = source switch
        {
            HttpSource => HttpTags.Contains,
            NpgsqlSource => NpgsqlTags.Contains,
            Instrumentation.Name => key => key.StartsWith("precip.", StringComparison.Ordinal),
            _ => _ => false,
        };

        // Snapshot first: SetTag mutates the collection being enumerated.
        foreach (var (key, value) in data.TagObjects.ToList())
        {
            if (source == HttpSource && key == UrlFull && value is string url)
                data.SetTag(key, RedactUrl(url));
            else if (!allowed(key))
                data.SetTag(key, null);
        }

        if (source == HttpSource)
        {
            var method = data.GetTagItem("http.request.method") as string ?? "HTTP";
            if (data.DisplayName != method)
                data.DisplayName = method;
        }

        if (!string.IsNullOrEmpty(data.StatusDescription))
            data.SetStatus(data.Status, RedactUrlsIn(data.StatusDescription));

        foreach (var (key, _) in data.Baggage.ToList())
            data.SetBaggage(key, null);

        // Fail closed on what can't be edited: drop the whole span rather than export it.
        var clean = data.Events.All(e => EventIsClean(e, allowed))
            && data.Links.All(l => l.Tags is null || !l.Tags.Any());
        if (!clean)
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }

    private static bool EventIsClean(ActivityEvent e, Func<string, bool> allowed)
    {
        foreach (var (key, value) in e.EnumerateTagObjects())
        {
            var ok = e.Name == ExceptionEvent
                ? key switch
                {
                    ExceptionType => true,
                    ExceptionMessage => value is null || (value is string m && m == RedactUrlsIn(m)),
                    ExceptionStackTrace => value is null,
                    _ => false,
                }
                : key != UrlFull && allowed(key);
            if (!ok)
                return false;
        }
        return true;
    }

    /// <summary>
    /// <c>scheme://host[:port]/path</c> with every path segment that isn't a known-safe literal
    /// (or a 1-3 digit heartbeat exit code) replaced by <see cref="RedactedSegment"/>. The query,
    /// fragment and any user info are dropped. Anything that doesn't parse as an absolute URL is
    /// replaced whole.
    /// </summary>
    internal static string RedactUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return RedactedSegment;

        // SchemeAndServer leaves out user info; the port appears only when it isn't the default.
        var sb = new StringBuilder(uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
        var segments = uri.AbsolutePath.Split('/');
        // AbsolutePath starts with '/', so segments[0] is empty; an empty last segment is a trailing slash.
        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i];
            sb.Append('/');
            if (segment.Length == 0 || SafeSegments.Contains(segment) || ExitCode().IsMatch(segment))
                sb.Append(segment);
            else
                sb.Append(RedactedSegment);
        }
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> with every scheme URL rewritten by <see cref="RedactUrl"/>, and,
    /// like <c>Redact.Urls</c>, every bare host with a path (<c>ntfy.sh/topic</c>) removed.
    /// </summary>
    internal static string RedactUrlsIn(string text) =>
        UrlShaped().Replace(text, m =>
        {
            if (m.Groups["bare"].Success)
                return "[url removed]";
            // Sentence punctuation after a URL is far likelier than a path ending in it.
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']');
            return RedactUrl(url) + m.Value[url.Length..];
        });

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _exceptionScrubber.Dispose();
        base.Dispose(disposing);
    }

    [GeneratedRegex(@"^\d{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExitCode();

    /// <summary>
    /// A scheme URL up to whitespace, a quote or an angle bracket, or (group "bare") the same bare host+path
    /// form <c>Redact</c> removes. Alternatives are tried left to right at each position, so a
    /// scheme URL is consumed whole before its host could match as a bare one.
    /// </summary>
    [GeneratedRegex(
        @"\b[a-z][a-z0-9+.\-]*://[^\s'""<>]+|(?<bare>\b(?:localhost|\d{1,3}(?:\.\d{1,3}){3}|(?:[a-z0-9\-]+\.)+[a-z]{2,})(?::\d+)?/[^\s'""<>]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlShaped();
}
