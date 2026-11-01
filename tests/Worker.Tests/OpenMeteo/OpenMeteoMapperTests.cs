using Worker.Forecast;
using Worker.OpenMeteo;

namespace Worker.Tests.OpenMeteo;

public class OpenMeteoMapperTests
{
    private const string Fixture = "open-meteo/2026-10-30T1417Z_pre-dst-fallback/forecast.json";

    // 2026-11-01T03:00:00Z. Its hour is [02:00Z, 03:00Z) = 22:00 EDT on Oct 31, inside that night's window.
    private const long NightStamp = 1793502000;

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset FallBackNow = DateTimeOffset.Parse("2026-10-31T23:00:00Z");

    private static OpenMeteoResponse Fixtureful() => FixtureFiles.Load<OpenMeteoResponse>(Fixture);

    private static Dictionary<string, string> Units(string precipitation = "mm", string snowfall = "cm") => new()
    {
        ["time"] = "unixtime",
        ["precipitation_probability"] = "%",
        ["precipitation"] = precipitation,
        ["snowfall"] = snowfall,
        ["weather_code"] = "wmo code",
    };

    private static OpenMeteoResponse Build(
        long[] time,
        int?[]? probability = null,
        double?[]? precipitation = null,
        double?[]? snowfall = null,
        int?[]? code = null,
        Dictionary<string, string>? units = null,
        string? timezone = "America/New_York") =>
        new(timezone, -14400, units ?? Units(),
            new OpenMeteoHourly(
                time,
                probability ?? new int?[time.Length],
                precipitation ?? new double?[time.Length],
                snowfall ?? new double?[time.Length],
                code ?? new int?[time.Length]));

    private static OpenMeteoResponse One(int? code, int? probability = 100, double? precipitation = 1, double? snowfall = 0) =>
        Build([NightStamp], [probability], [precipitation], [snowfall], [code]);

    // --- temperature: display-only, never blocks the forecast (D16) ---

    [Theory]
    [InlineData("°F", 41.5, 42, "F")]   // rounded half away from zero, labelled like NWS
    [InlineData("°C", 5.3, 0, "")]      // wrong unit: dropped, not mislabelled
    public void Temperature_maps_only_when_fahrenheit(string unit, double value, int degrees, string label)
    {
        var units = Units();
        units["temperature_2m"] = unit;
        var response = One(code: 61) with { HourlyUnits = units };
        response = response with { Hourly = response.Hourly! with { Temperature2m = [value] } };

        var hour = Assert.Single(OpenMeteoMapper.ToForecast(response).Hours);

        Assert.Equal(degrees, hour.Temperature);
        Assert.Equal(label, hour.TemperatureUnit);
    }

    [Fact]
    public void Missing_temperature_series_leaves_the_hour_without_a_temperature()
    {
        var hour = Assert.Single(OpenMeteoMapper.ToForecast(One(code: 61)).Hours);

        Assert.Equal("", hour.TemperatureUnit);
    }

    /// <summary>
    /// The unit-test cases above build the <c>°F</c> label by hand. This one proves the real API
    /// answers with exactly that string for the query <see cref="OpenMeteoClient"/> sends, so a
    /// change on Open-Meteo's side (say <c>"F"</c> or <c>"degF"</c>) can't silently drop every
    /// temperature from the message (D35).
    /// </summary>
    [Fact]
    public void Captured_fahrenheit_payload_labels_every_hour_F()
    {
        var response = FixtureFiles.Load<OpenMeteoResponse>(
            "open-meteo/2026-10-30T1508Z_with-temperature/forecast.json");

        var forecast = OpenMeteoMapper.ToForecast(response);

        Assert.Equal("°F", response.HourlyUnits!["temperature_2m"]);
        Assert.NotEmpty(forecast.Hours);
        Assert.All(forecast.Hours, h => Assert.Equal("F", h.TemperatureUnit));
    }

    // --- the captured payload ---

    [Fact]
    public void Fixture_maps_every_entry_to_one_hour()
    {
        var response = Fixtureful();
        var forecast = OpenMeteoMapper.ToForecast(response);

        Assert.Equal(response.Hourly!.Time!.Length, forecast.Hours.Count);
        Assert.Equal(Providers.OpenMeteo, forecast.Provider);
        Assert.NotNull(forecast.Amounts);
        Assert.Null(forecast.AmountsUnavailableReason);
    }

    [Fact]
    public void Fixture_first_hour_starts_one_hour_before_its_timestamp()
    {
        var response = Fixtureful();
        var forecast = OpenMeteoMapper.ToForecast(response);

        var first = DateTimeOffset.FromUnixTimeSeconds(response.Hourly!.Time![0]);
        Assert.Equal(first - TimeSpan.FromHours(1), forecast.Hours[0].Start);
        Assert.All(forecast.Hours, h => Assert.Equal(TimeSpan.FromHours(1), h.Duration));
    }

    [Fact]
    public void Fixture_hours_are_contiguous()
    {
        var forecast = OpenMeteoMapper.ToForecast(Fixtureful());

        for (var i = 1; i < forecast.Hours.Count; i++)
            Assert.Equal(forecast.Hours[i - 1].End, forecast.Hours[i].Start);
    }

    [Fact]
    public void Fixture_zone_is_new_york()
    {
        Assert.Equal("America/New_York", OpenMeteoMapper.ToForecast(Fixtureful()).TimeZoneId);
    }

    [Fact]
    public void Fall_back_night_has_twelve_in_range_hours_and_no_gaps()
    {
        var forecast = OpenMeteoMapper.ToForecast(Fixtureful());

        var window = NightWindow.Resolve(FallBackNow, NewYork, new TimeOnly(20, 0), new TimeOnly(7, 0));
        var assessment = ForecastEvaluator.Evaluate(window, forecast.Hours, forecast.Amounts, new AlertRule(50), FallBackNow);

        Assert.Equal(new DateOnly(2026, 10, 31), window.NightDate);
        Assert.Equal(TimeSpan.FromHours(12), window.Length); // the 25-hour day makes this night 12 h, not 11
        Assert.Equal(12, forecast.Hours.Count(h => h.Start < window.EndUtc && h.End > window.StartUtc));
        Assert.Empty(assessment.Gaps);
        Assert.False(assessment.ShouldAlert); // the capture is entirely dry
    }

    // --- fail-loud ---

    [Theory]
    [InlineData("inch", "cm", "precipitation")]
    [InlineData("mm", "mm", "snowfall")]
    [InlineData("mm", "inch", "snowfall")]
    public void Wrong_units_throw_and_name_the_unit(string precipitation, string snowfall, string series)
    {
        var response = Build([NightStamp], units: Units(precipitation, snowfall));

        var ex = Assert.Throws<InvalidOperationException>(() => OpenMeteoMapper.ToForecast(response));

        Assert.Contains(series, ex.Message);
        Assert.Contains(series == "precipitation" ? precipitation : snowfall, ex.Message);
    }

    [Fact]
    public void Missing_unit_throws()
    {
        var units = Units();
        units.Remove("snowfall");

        Assert.Throws<InvalidOperationException>(() => OpenMeteoMapper.ToForecast(Build([NightStamp], units: units)));
    }

    [Fact]
    public void Unequal_array_lengths_throw()
    {
        var response = Build([NightStamp, NightStamp + 3600], snowfall: [0.0]);

        var ex = Assert.Throws<InvalidOperationException>(() => OpenMeteoMapper.ToForecast(response));

        Assert.Contains("unequal", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_timezone_throws(string? timezone)
    {
        Assert.Throws<InvalidOperationException>(() => OpenMeteoMapper.ToForecast(Build([NightStamp], timezone: timezone)));
    }

    [Fact]
    public void Missing_hourly_throws()
    {
        var response = new OpenMeteoResponse("America/New_York", 0, Units(), null);

        Assert.Throws<InvalidOperationException>(() => OpenMeteoMapper.ToForecast(response));
    }

    // --- preceding-hour semantics ---

    [Fact]
    public void Value_at_T_describes_the_hour_ending_at_T()
    {
        const long t0 = 1793502000; // 03:00Z
        var response = Build(
            [t0, t0 + 3600, t0 + 7200],
            probability: [10, 20, 30],
            precipitation: [1.0, 2.0, 3.0],
            snowfall: [0.1, 0.2, 0.3],
            code: [61, 63, 65]);

        var forecast = OpenMeteoMapper.ToForecast(response);

        var starts = forecast.Hours.Select(h => h.Start.UtcDateTime).ToArray();
        Assert.Equal(
            [new DateTime(2026, 11, 1, 2, 0, 0), new DateTime(2026, 11, 1, 3, 0, 0), new DateTime(2026, 11, 1, 4, 0, 0)],
            starts);
        Assert.Equal([10, 20, 30], forecast.Hours.Select(h => h.PrecipProbability));
        Assert.Equal([1.0, 2.0, 3.0], forecast.Amounts!.Liquid.Select(a => a.Mm));
        Assert.Equal(starts, forecast.Amounts.Liquid.Select(a => a.Start.UtcDateTime));
        Assert.All(forecast.Amounts.Liquid, a => Assert.Equal(TimeSpan.FromHours(1), a.Duration));
    }

    // --- conversions ---

    [Fact]
    public void Snowfall_centimetres_become_millimetres_of_depth()
    {
        var forecast = OpenMeteoMapper.ToForecast(One(code: 73, precipitation: 1, snowfall: 1.5));

        Assert.Equal(15.0, forecast.Amounts!.Snow.Single().Mm, 6);
        Assert.Equal(1.0, forecast.Amounts.Liquid.Single().Mm, 6);
    }

    [Theory]
    [InlineData(56)]
    [InlineData(57)]
    [InlineData(66)]
    [InlineData(67)]
    public void Freezing_rain_or_drizzle_hour_lands_in_ice(int code)
    {
        var forecast = OpenMeteoMapper.ToForecast(One(code, precipitation: 2.5));

        Assert.Equal(2.5, forecast.Amounts!.Ice.Single().Mm, 6);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(71)]
    [InlineData(45)]
    [InlineData(null)]
    public void Other_hours_have_no_ice(int? code)
    {
        var forecast = OpenMeteoMapper.ToForecast(One(code, precipitation: 2.5));

        Assert.Equal(0, forecast.Amounts!.Ice.Single().Mm);
    }

    [Fact]
    public void Null_amounts_count_as_zero_and_null_probability_stays_null()
    {
        var forecast = OpenMeteoMapper.ToForecast(One(code: 61, probability: null, precipitation: null, snowfall: null));

        Assert.Equal(0, forecast.Amounts!.Liquid.Single().Mm);
        Assert.Equal(0, forecast.Amounts.Snow.Single().Mm);
        Assert.Equal(0, forecast.Amounts.Ice.Single().Mm);
        Assert.Null(forecast.Hours.Single().PrecipProbability);
    }

    // --- weather codes ---

    private static NightAssessment EvaluateWithoutAmounts(ProviderForecast forecast)
    {
        var window = NightWindow.Resolve(FallBackNow, NewYork, new TimeOnly(20, 0), new TimeOnly(7, 0));
        // Hours around the single hit so the one-hour fixture has no gaps to throw on.
        var hours = Enumerable.Range(0, 12)
            .Select(i => new HourlyForecast(window.StartUtc.AddHours(i), TimeSpan.FromHours(1), 0, "", 0, ""))
            .Where(h => h.Start != forecast.Hours[0].Start)
            .Concat(forecast.Hours)
            .ToList();
        return ForecastEvaluator.Evaluate(window, hours, null, new AlertRule(50), FallBackNow);
    }

    [Theory]
    [InlineData(0, "Clear sky", "Rain")]
    [InlineData(1, "Mainly clear", "Rain")]
    [InlineData(2, "Partly cloudy", "Rain")]
    [InlineData(3, "Overcast", "Rain")]
    [InlineData(45, "Fog", "Rain")]
    [InlineData(48, "Rime fog", "Rain")]
    [InlineData(51, "Light drizzle", "Rain")]
    [InlineData(53, "Moderate drizzle", "Rain")]
    [InlineData(55, "Dense drizzle", "Rain")]
    [InlineData(56, "Light freezing drizzle", "Ice")]
    [InlineData(57, "Dense freezing drizzle", "Ice")]
    [InlineData(61, "Light rain", "Rain")]
    [InlineData(63, "Moderate rain", "Rain")]
    [InlineData(65, "Heavy rain", "Rain")]
    [InlineData(66, "Light freezing rain", "Ice")]
    [InlineData(67, "Heavy freezing rain", "Ice")]
    [InlineData(71, "Light snow", "Snow")]
    [InlineData(73, "Moderate snow", "Snow")]
    [InlineData(75, "Heavy snow", "Snow")]
    [InlineData(77, "Snow grains", "Snow")]
    [InlineData(80, "Light rain showers", "Rain")]
    [InlineData(81, "Moderate rain showers", "Rain")]
    [InlineData(82, "Violent rain showers", "Rain")]
    [InlineData(85, "Light snow showers", "Snow")]
    [InlineData(86, "Heavy snow showers", "Snow")]
    [InlineData(95, "Thunderstorm", "Rain")]
    [InlineData(96, "Thunderstorm with hail", "Rain")]
    [InlineData(97, "Heavy thunderstorm", "Rain")]
    [InlineData(99, "Thunderstorm with heavy hail", "Rain")]
    public void Weather_code_text_and_resulting_kind(int code, string text, string kind)
    {
        var forecast = OpenMeteoMapper.ToForecast(One(code));

        Assert.Equal(text, forecast.Hours.Single().ShortForecast);

        var assessment = EvaluateWithoutAmounts(forecast);
        Assert.True(assessment.ShouldAlert);
        Assert.False(assessment.AmountsAvailable);
        Assert.Equal(kind, assessment.Kind.ToString());
    }

    [Fact]
    public void Unknown_or_missing_weather_code_gives_empty_text()
    {
        Assert.Equal("", OpenMeteoMapper.ToForecast(One(code: 42)).Hours.Single().ShortForecast);
        Assert.Equal("", OpenMeteoMapper.ToForecast(One(code: null)).Hours.Single().ShortForecast);
    }
}
