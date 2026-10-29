namespace Worker.Forecast;

internal sealed record AlertRule(int ProbabilityThreshold);

internal enum PrecipKind { Rain, Snow, Ice }

internal enum AlertPriority { Default, High }

/// <summary>
/// Everything the decision engine concluded about one night. Inch totals are raw
/// (unrounded); every threshold and every displayed number goes through
/// <see cref="PrecipRounding.Tenths"/> so the decision and the text can't disagree.
/// When <see cref="AmountsAvailable"/> is false the totals are 0 because the
/// gridpoint data couldn't be used, not because none is forecast.
/// </summary>
internal sealed record NightAssessment(
    NightWindow Window,
    IReadOnlyList<HourlyForecast> Hits,
    int PeakProbability,
    PrecipKind Kind,
    double LiquidInches,
    double SnowInches,
    double IceInches,
    AlertPriority Priority,
    bool AmountsAvailable = true)
{
    public bool ShouldAlert => Hits.Count > 0;

    /// <summary>
    /// Parts of the evaluated range the hourly forecast had no data for. Only ever
    /// non-empty alongside hits: gaps with no hits throw <see cref="IncompleteForecastException"/>.
    /// </summary>
    public IReadOnlyList<ForecastGap> Gaps { get; init; } = [];
}

/// <summary>A stretch of the evaluated range with no hourly forecast data.</summary>
internal sealed record ForecastGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>The one rounding rule shared by the evaluator's thresholds and the notification text.</summary>
internal static class PrecipRounding
{
    /// <summary>Inches rounded to the displayed precision (0.1"), midpoints away from zero.</summary>
    public static double Tenths(double inches) => Math.Round(inches, 1, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The hourly forecast doesn't cover the whole range being evaluated. Treated as a
/// failed run rather than a dry night: silence must never mean "we had no data".
/// </summary>
internal sealed class IncompleteForecastException(DateTimeOffset gapStartUtc, DateTimeOffset gapEndUtc)
    : Exception($"Hourly forecast has no data for {gapStartUtc:o} -> {gapEndUtc:o}.")
{
    public DateTimeOffset GapStartUtc { get; } = gapStartUtc;
    public DateTimeOffset GapEndUtc { get; } = gapEndUtc;
}
