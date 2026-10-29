namespace Worker.Forecast;

internal sealed record AlertRule(int ProbabilityThreshold);

internal enum PrecipKind { Rain, Snow, Ice }

internal enum AlertPriority { Default, High }

/// <summary>Everything the decision engine concluded about one night.</summary>
internal sealed record NightAssessment(
    NightWindow Window,
    IReadOnlyList<HourlyForecast> Hits,
    int PeakProbability,
    PrecipKind Kind,
    double LiquidInches,
    double SnowInches,
    double IceInches,
    AlertPriority Priority)
{
    public bool ShouldAlert => Hits.Count > 0;
}
