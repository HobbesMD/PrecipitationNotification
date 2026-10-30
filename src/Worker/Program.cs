using System.Globalization;
using Worker;
using Worker.Forecast;
using Worker.Nws;
using Worker.Notifications;

// Block 2 flow: fetch NWS forecast -> resolve the overnight window in the
// location's own time zone -> evaluate -> compose -> ntfy. Pass --dry-run to
// print the message instead of sending it. Still missing: persistence and
// idempotency (Block 4), retry and backoff (Block 5).
//
// Exit codes: 0 ok (alerted or genuinely dry), 1 config, 2 NWS fetch,
// 3 ntfy publish, 4 incomplete forecast (gaps in the hourly data and no hits
// in the hours we do have; with hits, the alert goes out flagged as partial),
// 5 runtime self-check failed (--self-check only).

// Before .env and config on purpose: the check needs neither, so CI can run it
// against the built image with nothing set.
if (args.Contains("--self-check")) return RuntimeSelfCheck.Run(Console.Out);

var dryRun = args.Contains("--dry-run");
var inv = CultureInfo.InvariantCulture;

var envFile = Persistence.DotEnv.Load();
Console.WriteLine(envFile is null
    ? "No .env file found; relying on process environment."
    : $"Loaded config from {envFile}");

// Block 4 interim: AppConfig now requires the database, which this flow doesn't use
// yet, so the pre-database settings are read here until the nightly pass replaces it.
LegacyConfig config;
try
{
    config = LegacyConfig.FromEnvironment();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Config error: {ex.Message}");
    return 1;
}

using var nwsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
// NWS rejects requests without a User-Agent that carries contact info.
nwsHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", config.UserAgent);
nwsHttp.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json");

var nws = new NwsClient(nwsHttp);
var now = TimeProvider.System.GetUtcNow();

// Never log the coordinates: they pin a home, and stdout lands in Log Analytics in Azure.
Console.WriteLine("Fetching NWS forecast ...");
NwsForecast forecast;
TimeZoneInfo zone;
try
{
    forecast = await nws.GetForecastAsync(config.LatLon, CancellationToken.None);
    // The zone comes from the location (NWS), not from config; an unknown id is a bad NWS response.
    zone = TimeZoneInfo.FindSystemTimeZoneById(forecast.TimeZoneId);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"NWS fetch failed: {ex.Message}");
    return 2;
}

Console.WriteLine($"Got {forecast.Hours.Count} hourly periods.");
if (forecast.Amounts is null)
    Console.Error.WriteLine($"Warning: amounts unavailable ({forecast.AmountsUnavailableReason}); alerting on probability only.");

var window = NightWindow.Resolve(now, zone, config.WindowStart, config.WindowEnd);
NightAssessment assessment;
try
{
    assessment = ForecastEvaluator.Evaluate(
        window, forecast.Hours, forecast.Amounts, new AlertRule(config.ProbabilityThreshold), now);
}
catch (IncompleteForecastException ex)
{
    // Not a dry night: we don't know. Fail the run so it can't be mistaken for "no precipitation".
    Console.Error.WriteLine($"Incomplete forecast: {ex.Message}");
    return 4;
}

foreach (var gap in assessment.Gaps)
    Console.Error.WriteLine($"Warning: no hourly data {gap.StartUtc:o} -> {gap.EndUtc:o}; alerting on the hours we have.");

var hoursInWindow = forecast.Hours.Count(h => h.Start < window.EndUtc && h.End > window.StartUtc);
Console.WriteLine($"Time zone: {zone.Id}");
Console.WriteLine(
    $"Night {window.NightDate:yyyy-MM-dd}: {window.StartUtc:o} -> {window.EndUtc:o} (length {window.Length})");
Console.WriteLine(
    $"Hours in window: {hoursInWindow}; hits at/above {config.ProbabilityThreshold}%: {assessment.Hits.Count}");

if (!assessment.ShouldAlert)
{
    Console.WriteLine(
        $"No hour at/above {config.ProbabilityThreshold}% precipitation. No notification sent.");
    return 0;
}

Console.WriteLine(
    $"Peak {assessment.PeakProbability}%, kind {assessment.Kind}, " +
    $"liquid {assessment.LiquidInches.ToString("0.00", inv)} in, " +
    $"snow {assessment.SnowInches.ToString("0.00", inv)} in, " +
    $"ice {assessment.IceInches.ToString("0.00", inv)} in");

var msg = NotificationComposer.Compose(assessment, zone);

if (dryRun)
{
    Console.WriteLine("----- notification -----");
    Console.WriteLine($"Title: {msg.Title}");
    Console.WriteLine($"Priority: {msg.Priority}");
    Console.WriteLine($"Tags: {msg.Tags}");
    Console.WriteLine("Body:");
    Console.WriteLine(msg.Body);
    Console.WriteLine("------------------------");
    Console.WriteLine("(dry run: not published)");
    return 0;
}

using var ntfyHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var ntfy = new NtfyPublisher(ntfyHttp, config.NtfyBaseUrl, config.NtfyTopic);
try
{
    await ntfy.PublishAsync(msg.Title, msg.Body, msg.Priority, msg.Tags, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ntfy publish failed: {ex.Message}");
    return 3;
}

Console.WriteLine(
    $"Sent notification (peak {assessment.PeakProbability}%, {assessment.Hits.Count} hour(s) over threshold).");
return 0;


/// <summary>
/// Block 4 interim: the pre-database configuration for the single hardcoded
/// location, read from the environment exactly as before Block 4 (same variables,
/// same defaults), now validated by <see cref="Persistence.AlertRuleValidation"/>. Goes away
/// when the nightly pass reads locations from the database.
/// </summary>
file sealed record LegacyConfig(
    decimal Lat,
    decimal Lon,
    string NtfyBaseUrl,
    string NtfyTopic,
    string UserAgent,
    TimeOnly WindowStart,
    TimeOnly WindowEnd,
    int ProbabilityThreshold)
{
    public static LegacyConfig FromEnvironment()
    {
        var windowStart = ParseTime("PRECIP_WINDOW_START", "20:00");
        var windowEnd = ParseTime("PRECIP_WINDOW_END", "07:00");
        Persistence.AlertRuleValidation.ValidateWindow(windowStart, windowEnd);

        return new(
            Lat: ParseCoordinate("PRECIP_LAT", Persistence.AlertRuleValidation.LatitudeLimit),
            Lon: ParseCoordinate("PRECIP_LON", Persistence.AlertRuleValidation.LongitudeLimit),
            NtfyBaseUrl: Optional("PRECIP_NTFY_BASE_URL", "https://ntfy.sh"),
            NtfyTopic: Require("PRECIP_NTFY_TOPIC"),
            UserAgent: Require("PRECIP_USER_AGENT"),
            WindowStart: windowStart,
            WindowEnd: windowEnd,
            ProbabilityThreshold: ParseThreshold());
    }

    /// <summary>NWS rejects coordinates with more than 4 decimal places.</summary>
    public string LatLon => FormattableString.Invariant($"{Lat:0.####},{Lon:0.####}");

    /// <summary>Never prints the coordinates or the topic (D22).</summary>
    public override string ToString() => "LegacyConfig { (redacted) }";

    private const string ThresholdKey = "PRECIP_PROBABILITY_THRESHOLD";

    /// <summary>Unset or empty means 50; anything else must be an integer 0–100 (D14).</summary>
    private static int ParseThreshold()
    {
        var raw = Environment.GetEnvironmentVariable(ThresholdKey);
        if (string.IsNullOrEmpty(raw))
            return 50;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(
                $"{ThresholdKey} must be an integer from 0 to 100, but was '{raw}'.");

        Persistence.AlertRuleValidation.ValidateThreshold(value);
        return value;
    }

    /// <summary>Names the variable, never the value: coordinates are secret (D22).</summary>
    private static decimal ParseCoordinate(string key, decimal limit)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(raw))
            throw new InvalidOperationException($"Missing required environment variable {key}.");

        // Float, not Number: no thousands separators, so 12,3456 can't become 123456.
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException(
                $"{key} must be decimal degrees, e.g. 40.7128 (value not shown: coordinates are secret).");

        Persistence.AlertRuleValidation.ValidateCoordinate(key, value, limit);
        return value;
    }

    private static string Require(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"Missing required environment variable {key}.");

    private static string Optional(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    private static TimeOnly ParseTime(string key, string fallback) =>
        TimeOnly.Parse(Optional(key, fallback), CultureInfo.InvariantCulture);
}
