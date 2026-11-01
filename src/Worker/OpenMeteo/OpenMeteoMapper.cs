using Worker.Forecast;

namespace Worker.OpenMeteo;

/// <summary>Pure translation from Open-Meteo DTOs to provider-neutral forecast types.</summary>
internal static class OpenMeteoMapper
{
    private const string PrecipitationUnit = "mm";
    private const string SnowfallUnit = "cm";
    private const string FahrenheitUnit = "°F";
    private const double MmPerCm = 10;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    // WMO weather interpretation codes (the list Open-Meteo publishes). The wording is chosen
    // for ForecastEvaluator's whole-word keyword fallback: "freezing rain"/"freezing drizzle"
    // read as ice, "snow" as snow ("snowfall" would NOT match \bsnow\b), and fog never as ice.
    private static readonly Dictionary<int, string> WeatherText = new()
    {
        [0] = "Clear sky",
        [1] = "Mainly clear",
        [2] = "Partly cloudy",
        [3] = "Overcast",
        [45] = "Fog",
        [48] = "Rime fog",
        [51] = "Light drizzle",
        [53] = "Moderate drizzle",
        [55] = "Dense drizzle",
        [56] = "Light freezing drizzle",
        [57] = "Dense freezing drizzle",
        [61] = "Light rain",
        [63] = "Moderate rain",
        [65] = "Heavy rain",
        [66] = "Light freezing rain",
        [67] = "Heavy freezing rain",
        [71] = "Light snow",
        [73] = "Moderate snow",
        [75] = "Heavy snow",
        [77] = "Snow grains",
        [80] = "Light rain showers",
        [81] = "Moderate rain showers",
        [82] = "Violent rain showers",
        [85] = "Light snow showers",
        [86] = "Heavy snow showers",
        [95] = "Thunderstorm",
        [96] = "Thunderstorm with hail",
        [97] = "Heavy thunderstorm",
        [99] = "Thunderstorm with heavy hail",
    };

    // Codes whose precipitation falls as freezing rain/drizzle: that hour's liquid is ice.
    private static readonly HashSet<int> FreezingCodes = [56, 57, 66, 67];

    /// <summary>
    /// Maps a <c>/v1/forecast</c> response to a <see cref="ProviderForecast"/> whose provider is
    /// <see cref="Providers.OpenMeteo"/>.
    /// </summary>
    /// <remarks>
    /// <b>Preceding-hour semantics.</b> Open-Meteo documents <c>precipitation</c>,
    /// <c>precipitation_probability</c> and <c>snowfall</c> as covering the <em>preceding hour</em>
    /// ("Valid time: Preceding hour sum / probability", https://open-meteo.com/en/docs, Hourly
    /// Parameter Definition). So the value stamped T describes <c>[T - 1h, T)</c>, and each entry
    /// becomes an hour starting at T - 1h. Taking T as the start would shift every hour, and
    /// the overnight window with it, one hour late. <c>weather_code</c> is documented as an
    /// instant value; it is attached to the same hour, the closest hourly period available.
    /// <para>
    /// Units fail loudly (D7): the docs list precipitation in mm and snowfall in cm by default,
    /// but a different unit would silently scale every amount, so anything else throws. Snowfall
    /// cm are converted to mm of snow depth (x10), the unit NWS <c>snowfallAmount</c> uses.
    /// A null probability stays null; a null amount counts as 0 (D6).
    /// </para>
    /// <para>
    /// <b>Temperature is display-only</b> (D16: an input that doesn't decide the alert must never
    /// block it). <c>temperature_2m</c> is requested in Fahrenheit to match NWS. If the series is
    /// missing, a value is null, or the unit isn't <c>°F</c>, that hour carries an empty unit and
    /// the composer omits the temperature, rather than failing the forecast or printing a wrong number.
    /// </para>
    /// </remarks>
    public static ProviderForecast ToForecast(OpenMeteoResponse response)
    {
        var zone = string.IsNullOrWhiteSpace(response.Timezone)
            ? throw new InvalidOperationException("Open-Meteo response had no timezone.")
            : response.Timezone;

        RequireUnit(response.HourlyUnits, "precipitation", PrecipitationUnit);
        RequireUnit(response.HourlyUnits, "snowfall", SnowfallUnit);

        var hourly = response.Hourly
            ?? throw new InvalidOperationException("Open-Meteo response had no hourly data.");
        var time = hourly.Time ?? throw new InvalidOperationException("Open-Meteo hourly data had no time series.");
        var probability = hourly.PrecipitationProbability
            ?? throw new InvalidOperationException("Open-Meteo hourly data had no precipitation_probability series.");
        var precipitation = hourly.Precipitation
            ?? throw new InvalidOperationException("Open-Meteo hourly data had no precipitation series.");
        var snowfall = hourly.Snowfall
            ?? throw new InvalidOperationException("Open-Meteo hourly data had no snowfall series.");
        var code = hourly.WeatherCode
            ?? throw new InvalidOperationException("Open-Meteo hourly data had no weather_code series.");
        var temperature = hourly.Temperature2m is { } t && t.Length == time.Length
            && response.HourlyUnits?.GetValueOrDefault("temperature_2m") == FahrenheitUnit ? t : null;

        var n = time.Length;
        if (probability.Length != n || precipitation.Length != n || snowfall.Length != n || code.Length != n)
        {
            throw new InvalidOperationException(
                $"Open-Meteo hourly series have unequal lengths: time {n}, precipitation_probability {probability.Length}, " +
                $"precipitation {precipitation.Length}, snowfall {snowfall.Length}, weather_code {code.Length}.");
        }

        var hours = new List<HourlyForecast>(n);
        var liquid = new List<IntervalAmount>(n);
        var snow = new List<IntervalAmount>(n);
        var ice = new List<IntervalAmount>(n);

        for (var i = 0; i < n; i++)
        {
            var start = DateTimeOffset.FromUnixTimeSeconds(time[i]) - Hour;
            var mm = precipitation[i] ?? 0;
            var freezing = code[i] is { } c && FreezingCodes.Contains(c);

            // The temperature is an instant value at T, not a preceding-hour one; it labels the same hour.
            var (degrees, unit) = temperature?[i] is { } f ? ((int)Math.Round(f, MidpointRounding.AwayFromZero), "F") : (0, "");
            hours.Add(new HourlyForecast(start, Hour, probability[i], TextFor(code[i]), degrees, unit));
            liquid.Add(new IntervalAmount(start, Hour, mm));
            snow.Add(new IntervalAmount(start, Hour, (snowfall[i] ?? 0) * MmPerCm));
            ice.Add(new IntervalAmount(start, Hour, freezing ? mm : 0));
        }

        return new ProviderForecast(Providers.OpenMeteo, zone, hours, new PrecipAmounts(liquid, snow, ice));
    }

    private static string TextFor(int? code) =>
        code is { } c && WeatherText.TryGetValue(c, out var text) ? text : "";

    private static void RequireUnit(Dictionary<string, string>? units, string series, string expected)
    {
        if (units is null || !units.TryGetValue(series, out var actual))
            throw new InvalidOperationException($"Open-Meteo hourly_units had no '{series}' unit, expected '{expected}'.");
        if (actual != expected)
            throw new InvalidOperationException($"Open-Meteo {series} uses unit '{actual}', expected '{expected}'.");
    }
}
