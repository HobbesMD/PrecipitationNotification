using System.Globalization;

namespace Worker;

/// <summary>
/// Configuration read straight from environment variables (see
/// <c>.env.example</c>). Block 2 validates the overnight window; everything else
/// is only checked for "is it present and parseable".
/// </summary>
internal sealed record AppConfig(
    double Lat,
    double Lon,
    string NtfyBaseUrl,
    string NtfyTopic,
    string UserAgent,
    TimeOnly WindowStart,
    TimeOnly WindowEnd,
    int ProbabilityThreshold)
{
    public static AppConfig FromEnvironment()
    {
        var windowStart = ParseTime("PRECIP_WINDOW_START", "20:00");
        var windowEnd = ParseTime("PRECIP_WINDOW_END", "07:00");
        ValidateWindow(windowStart, windowEnd);

        return new(
            Lat: ParseCoordinate("PRECIP_LAT", Environment.GetEnvironmentVariable("PRECIP_LAT")),
            Lon: ParseCoordinate("PRECIP_LON", Environment.GetEnvironmentVariable("PRECIP_LON")),
            NtfyBaseUrl: Optional("PRECIP_NTFY_BASE_URL", "https://ntfy.sh"),
            NtfyTopic: Require("PRECIP_NTFY_TOPIC"),
            UserAgent: Require("PRECIP_USER_AGENT"),
            WindowStart: windowStart,
            WindowEnd: windowEnd,
            ProbabilityThreshold: ParseThreshold(Environment.GetEnvironmentVariable(ThresholdKey)));
    }

    /// <summary>
    /// <see cref="Forecast.NightWindow.Resolve"/> always puts the end on the day after
    /// the start, so a window that doesn't cross midnight (01:00 → 05:00) would
    /// silently become 28 hours. Reject it here, before any run, rather than per-run.
    /// </summary>
    internal static void ValidateWindow(TimeOnly start, TimeOnly end)
    {
        if (start <= end)
            throw new InvalidOperationException(
                $"Overnight window must cross midnight: PRECIP_WINDOW_START ({start:HH:mm}) " +
                $"must be later than PRECIP_WINDOW_END ({end:HH:mm}).");
    }

    private const string ThresholdKey = "PRECIP_PROBABILITY_THRESHOLD";

    /// <summary>
    /// Unset or empty means 50. Anything set must be an integer in 0–100: a typo like <c>60%</c>
    /// used to fall back to 50 silently, the worst failure for an alerter. 0 is valid (every hour
    /// hits), which is handy for testing.
    /// </summary>
    internal static int ParseThreshold(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return 50;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 100)
            throw new InvalidOperationException(
                $"{ThresholdKey} must be an integer from 0 to 100, but was '{raw}'.");

        return value;
    }

    /// <summary>NWS rejects coordinates with more than 4 decimal places.</summary>
    public string LatLon => FormattableString.Invariant($"{Lat:0.####},{Lon:0.####}");

    private static string Require(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"Missing required environment variable {key}.");

    private static string Optional(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    /// <summary>
    /// Coordinates are secrets (they pin a home), so unlike the threshold the error
    /// names the variable but never echoes the value: <c>double.Parse</c>'s own
    /// message would, and config errors go to the logs.
    /// </summary>
    internal static double ParseCoordinate(string key, string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            throw new InvalidOperationException($"Missing required environment variable {key}.");

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(
                $"{key} must be decimal degrees, e.g. 40.7128 (value not shown: coordinates are secret).");

        return value;
    }

    private static TimeOnly ParseTime(string key, string fallback) =>
        TimeOnly.Parse(Optional(key, fallback), CultureInfo.InvariantCulture);
}
