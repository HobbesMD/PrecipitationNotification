using System.Text;
using Worker;
using Worker.Forecast;
using Worker.Nws;
using Worker.Notifications;

// Block 1 walking skeleton: NWS hourly forecast -> naive overnight threshold
// -> ntfy notification. Everything hardcoded or read from .env. No tests, no
// retry, no persistence, no scheduling. See CLAUDE.md for what Block 2 fixes.

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

Console.WriteLine($"Fetching NWS hourly forecast for {config.LatLon} ...");
IReadOnlyList<ForecastPeriod> periods;
try
{
    periods = await nws.GetHourlyForecastAsync(config.LatLon, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"NWS fetch failed: {ex.Message}");
    return 2;
}

Console.WriteLine($"Got {periods.Count} hourly periods.");

var outcome = PrecipDecision.Evaluate(
    periods,
    DateTimeOffset.UtcNow,
    config.TimeZone,
    config.WindowStart,
    config.WindowEnd,
    config.ProbabilityThreshold);

Console.WriteLine(
    $"Overnight window (local): {outcome.WindowStartLocal:ddd MMM d h:mm tt} -> " +
    $"{outcome.WindowEndLocal:ddd MMM d h:mm tt}");

if (!outcome.ShouldAlert)
{
    Console.WriteLine(
        $"No hour at/above {config.ProbabilityThreshold}% precipitation. No notification sent.");
    return 0;
}

var kind = outcome.LooksLikeSnow ? "snow" : "rain";
var title = $"Overnight {kind}: up to {outcome.PeakProbability}% precip";

var body = new StringBuilder();
body.AppendLine(
    $"Tonight {outcome.WindowStartLocal:h:mm tt}-{outcome.WindowEndLocal:h:mm tt}, " +
    $"{kind} likely (peak {outcome.PeakProbability}%).");
foreach (var hour in outcome.Hits)
{
    body.AppendLine(
        $"{hour.LocalStart,8:h tt}  {hour.ProbabilityPercent,3}%  " +
        $"{hour.ShortForecast}  {hour.Temperature} {hour.TemperatureUnit}");
}

var tags = outcome.LooksLikeSnow ? "snowflake" : "rain_cloud";
var priority = outcome.PeakProbability >= 80 ? "high" : "default";

using var ntfyHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var ntfy = new NtfyPublisher(ntfyHttp, config.NtfyBaseUrl, config.NtfyTopic);
try
{
    await ntfy.PublishAsync(title, body.ToString().TrimEnd(), priority, tags, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ntfy publish failed: {ex.Message}");
    return 3;
}

Console.WriteLine(
    $"Sent notification (peak {outcome.PeakProbability}%, {outcome.Hits.Count} hour(s) over threshold).");
return 0;
