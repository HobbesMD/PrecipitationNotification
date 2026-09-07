using System.Globalization;

namespace Worker;

/// <summary>
/// Block 1 configuration, read straight from environment variables (see
/// <c>.env.example</c>). No validation beyond "is it present and parseable".
/// </summary>
internal sealed record AppConfig(
    double Lat,
    double Lon,
    string NtfyBaseUrl,
    string NtfyTopic,
    string UserAgent,
    TimeZoneInfo TimeZone,
    TimeOnly WindowStart,
    TimeOnly WindowEnd,
    int ProbabilityThreshold)
{
    public static AppConfig FromEnvironment() => new(
        Lat: ParseDouble("PRECIP_LAT"),
        Lon: ParseDouble("PRECIP_LON"),
        NtfyBaseUrl: Optional("PRECIP_NTFY_BASE_URL", "https://ntfy.sh"),
        NtfyTopic: Require("PRECIP_NTFY_TOPIC"),
        UserAgent: Require("PRECIP_USER_AGENT"),
        TimeZone: TimeZoneInfo.FindSystemTimeZoneById(Optional("PRECIP_TIMEZONE", "America/New_York")),
        WindowStart: ParseTime("PRECIP_WINDOW_START", "20:00"),
        WindowEnd: ParseTime("PRECIP_WINDOW_END", "07:00"),
        ProbabilityThreshold: ParseInt("PRECIP_PROBABILITY_THRESHOLD", 50));

    /// <summary>NWS rejects coordinates with more than 4 decimal places.</summary>
    public string LatLon => FormattableString.Invariant($"{Lat:0.####},{Lon:0.####}");

    private static string Require(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"Missing required environment variable {key}.");

    private static string Optional(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    private static double ParseDouble(string key) =>
        double.Parse(Require(key), CultureInfo.InvariantCulture);

    private static int ParseInt(string key, int fallback) =>
        int.TryParse(Optional(key, fallback.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    private static TimeOnly ParseTime(string key, string fallback) =>
        TimeOnly.Parse(Optional(key, fallback), CultureInfo.InvariantCulture);
}
