using System.Text.RegularExpressions;

namespace Worker;

/// <summary>
/// One scrubber for every error text that leaves the process: <c>alert_history.last_error</c>
/// and the logs alike. The ntfy topic is the last path segment of the publish URL and the NWS
/// URL carries the coordinates (D22), so anything URL-shaped is removed before it is stored or
/// logged. Today's .NET exception messages don't quote request URIs, but nothing guarantees
/// that, and the logs are the more widely readable copy (Log Analytics in Azure).
/// </summary>
internal static partial class Redact
{
    /// <summary>Exception type and message, URLs removed.</summary>
    public static string Describe(Exception ex) => $"{ex.GetType().Name}: {Urls(ex.Message)}";

    public static string Urls(string text) => UrlShaped().Replace(text, "[url removed]");

    /// <summary>
    /// Either a scheme URL (<c>https://host/topic</c>), or a bare host with a path: a DNS name
    /// (<c>ntfy.sh/topic</c>), <c>localhost</c> or an IPv4 address, optionally with a port
    /// (<c>10.0.0.5:80/topic</c>). A bare host with no path is left alone: it carries no topic
    /// and is useful when diagnosing DNS failures.
    /// </summary>
    [GeneratedRegex(
        @"\b[a-z][a-z0-9+.\-]*://\S*|\b(?:localhost|\d{1,3}(?:\.\d{1,3}){3}|(?:[a-z0-9\-]+\.)+[a-z]{2,})(?::\d+)?/\S*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlShaped();
}
