using Worker.Nws;

namespace Worker.Forecast;

/// <summary>
/// Block 1 decision logic — deliberately naive throwaway code. Block 2 replaces
/// this entire type with a tested pure function.
///
/// Known shortcuts, all intentional for now:
///  * "Tonight" is always <c>today WindowStart</c> → <c>tomorrow WindowEnd</c> in
///    the configured zone, no matter what time the job actually runs. Run it at
///    6am and it still looks at the window that already ended.
///  * Window edges use wall-clock comparison. On the fall-back night the
///    repeated 1am–2am hour matches twice; on the spring-forward night the
///    skipped hour just isn't there.
///  * No "forecast night date", no dedupe, no idempotency — every run that finds
///    precipitation sends a notification.
/// </summary>
internal static class PrecipDecision
{
    internal sealed record Hour(
        DateTimeOffset LocalStart,
        int ProbabilityPercent,
        string ShortForecast,
        int Temperature,
        string TemperatureUnit);

    internal sealed record Outcome(
        DateTime WindowStartLocal,
        DateTime WindowEndLocal,
        IReadOnlyList<Hour> Hits)
    {
        public bool ShouldAlert => Hits.Count > 0;

        public int PeakProbability => Hits.Count == 0 ? 0 : Hits.Max(h => h.ProbabilityPercent);

        public bool LooksLikeSnow => Hits.Any(h =>
            h.ShortForecast.Contains("snow", StringComparison.OrdinalIgnoreCase) ||
            h.ShortForecast.Contains("sleet", StringComparison.OrdinalIgnoreCase) ||
            h.ShortForecast.Contains("flurr", StringComparison.OrdinalIgnoreCase) ||
            h.ShortForecast.Contains("wintry", StringComparison.OrdinalIgnoreCase));
    }

    public static Outcome Evaluate(
        IReadOnlyList<ForecastPeriod> periods,
        DateTimeOffset now,
        TimeZoneInfo zone,
        TimeOnly windowStart,
        TimeOnly windowEnd,
        int thresholdPercent)
    {
        var todayLocal = TimeZoneInfo.ConvertTime(now, zone).Date;
        var startLocal = todayLocal + windowStart.ToTimeSpan();
        var endLocal = todayLocal.AddDays(1) + windowEnd.ToTimeSpan();

        var hits = new List<Hour>();
        foreach (var period in periods)
        {
            var localStart = TimeZoneInfo.ConvertTime(period.StartTime, zone);
            if (localStart.DateTime < startLocal || localStart.DateTime >= endLocal)
                continue;

            var probability = (int)Math.Round(period.ProbabilityOfPrecipitation?.Value ?? 0);
            if (probability < thresholdPercent)
                continue;

            hits.Add(new Hour(
                localStart,
                probability,
                period.ShortForecast ?? "",
                period.Temperature,
                period.TemperatureUnit ?? "F"));
        }

        return new Outcome(startLocal, endLocal, hits);
    }
}
