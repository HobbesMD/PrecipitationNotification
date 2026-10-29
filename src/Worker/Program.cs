using System.Globalization;
using Worker;
using Worker.Forecast;
using Worker.Nws;
using Worker.Notifications;

// Block 2 flow: fetch NWS forecast -> resolve the overnight window in the
// location's own time zone -> evaluate -> compose -> ntfy. Pass --dry-run to
// print the message instead of sending it. Still missing: persistence and
// idempotency (Block 4), retry and backoff (Block 5).

var dryRun = args.Contains("--dry-run");
var inv = CultureInfo.InvariantCulture;

var envFile = DotEnv.Load();
Console.WriteLine(envFile is null
    ? "No .env file found; relying on process environment."
    : $"Loaded config from {envFile}");

AppConfig config;
try
{
    config = AppConfig.FromEnvironment();
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

Console.WriteLine($"Fetching NWS forecast for {config.LatLon} ...");
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

var window = NightWindow.Resolve(now, zone, config.WindowStart, config.WindowEnd);
var assessment = ForecastEvaluator.Evaluate(
    window, forecast.Hours, forecast.Amounts, new AlertRule(config.ProbabilityThreshold), now);

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
