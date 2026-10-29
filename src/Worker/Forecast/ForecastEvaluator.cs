namespace Worker.Forecast;

/// <summary>The decision engine: a pure function of forecast, window, rule and clock.</summary>
internal static class ForecastEvaluator
{
    private const double MmPerInch = 25.4;

    // Kind: any ice wins (it is the hazard), then a measurable snowfall, else rain.
    private const double SnowKindInches = 0.1;

    // Priority: the "go outside tonight" line — a shovel-worthy snowfall, any ice, a soaking rain.
    private const double HighPrioritySnowInches = 1.0;
    private const double HighPriorityLiquidInches = 0.5;

    /// <summary>
    /// Whether to alert is decided by probability alone: <c>ShouldAlert</c> is "any hit". The
    /// accumulation amounts never gate it — NWS amounts are noisy and often 0 when PoP is high —
    /// they only shape the message (kind and priority).
    /// </summary>
    /// <remarks>
    /// Both hits and totals look at the same effective range, <c>[max(window start, now), window end)</c>,
    /// so a run at 02:00 reports only what is still ahead. An hour counts if it overlaps that range at
    /// all, so the hour containing <paramref name="now"/> still counts. A null probability is "no data",
    /// treated as 0. All comparisons are on instants, never wall-clock time, so the repeated fall-back
    /// hour and the missing spring-forward hour need no special case.
    /// </remarks>
    public static NightAssessment Evaluate(
        NightWindow window,
        IReadOnlyList<HourlyForecast> hours,
        PrecipAmounts? amounts,
        AlertRule rule,
        DateTimeOffset now)
    {
        var from = window.StartUtc > now ? window.StartUtc : now;
        var to = window.EndUtc;

        var hits = hours
            .Where(h => h.Start < to && h.End > from)
            .Where(h => (h.PrecipProbability ?? 0) >= rule.ProbabilityThreshold)
            .OrderBy(h => h.Start)
            .ToList();

        var peak = hits.Count == 0 ? 0 : hits.Max(h => h.PrecipProbability ?? 0);

        amounts ??= PrecipAmounts.Empty; // Wave 0 placeholder; T3.1 implements the unavailable path
        var liquid = Inches(amounts.Liquid, from, to);
        var snow = Inches(amounts.Snow, from, to);
        var ice = Inches(amounts.Ice, from, to);

        var kind = ice > 0 ? PrecipKind.Ice
            : snow >= SnowKindInches ? PrecipKind.Snow
            : PrecipKind.Rain;

        var priority = snow >= HighPrioritySnowInches || ice > 0 || liquid >= HighPriorityLiquidInches
            ? AlertPriority.High
            : AlertPriority.Default;

        return new NightAssessment(window, hits, peak, kind, liquid, snow, ice, priority);
    }

    private static double Inches(IReadOnlyList<IntervalAmount> series, DateTimeOffset from, DateTimeOffset to) =>
        PrecipAmounts.Apportion(series, from, to) / MmPerInch;
}
