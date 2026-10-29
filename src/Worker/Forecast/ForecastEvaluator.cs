namespace Worker.Forecast;

/// <summary>The decision engine: a pure function of forecast, window, rule and clock.</summary>
internal static class ForecastEvaluator
{
    public static NightAssessment Evaluate(
        NightWindow window,
        IReadOnlyList<HourlyForecast> hours,
        PrecipAmounts amounts,
        AlertRule rule,
        DateTimeOffset now) =>
        throw new NotImplementedException();
}
