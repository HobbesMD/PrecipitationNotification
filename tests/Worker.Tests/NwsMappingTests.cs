using System.Text.Json;
using Worker.Nws;

namespace Worker.Tests;

public class NwsMappingTests
{
    private const string Dir = "nws/2026-10-29T1900Z_pre-dst-fallback/";

    private static readonly PointProperties Point = new(null, "America/New_York", null);

    private static NwsForecast MapFixture()
    {
        var points = FixtureFiles.Load<PointResponse>(Dir + "points.json");
        var hourly = FixtureFiles.Load<HourlyForecastResponse>(Dir + "forecast-hourly.json");
        var grid = FixtureFiles.Load<GridpointResponse>(Dir + "gridpoint.json");
        return NwsMapper.ToForecast(points.Properties!, hourly.Properties!.Periods!, grid.Properties);
    }

    private static ForecastPeriod Period(string pop) =>
        JsonSerializer.Deserialize<ForecastPeriod>($$"""
            {"number":1,"startTime":"2026-10-30T20:00:00-04:00","endTime":"2026-10-30T21:00:00-04:00",
             "isDaytime":false,"temperature":50,"temperatureUnit":null,"shortForecast":null,
             "probabilityOfPrecipitation":{{pop}}}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void Maps_all_hours_with_first_and_last_start()
    {
        var forecast = MapFixture();

        Assert.Equal(156, forecast.Hours.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T14:00:00-04:00"), forecast.Hours[0].Start);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T00:00:00-05:00"), forecast.Hours[^1].Start);
    }

    [Fact]
    public void Repeated_fallback_hour_maps_to_two_distinct_one_hour_instants()
    {
        var repeated = MapFixture().Hours
            .Where(h => h.Start.DateTime == new DateTime(2026, 11, 1, 1, 0, 0))
            .ToList();

        Assert.Equal(2, repeated.Count);
        Assert.Equal(TimeSpan.FromHours(-4), repeated[0].Start.Offset);
        Assert.Equal(TimeSpan.FromHours(-5), repeated[1].Start.Offset);
        Assert.All(repeated, h => Assert.Equal(TimeSpan.FromHours(1), h.Duration));
        Assert.Equal(repeated[0].End, repeated[1].Start);
    }

    [Fact]
    public void Time_zone_comes_from_points()
    {
        Assert.Equal("America/New_York", MapFixture().TimeZoneId);
    }

    [Fact]
    public void Missing_time_zone_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            NwsMapper.ToForecast(new PointProperties(null, null, null), [], null));
    }

    [Fact]
    public void Amount_series_counts_match_fixture()
    {
        var grid = FixtureFiles.Load<GridpointResponse>(Dir + "gridpoint.json").Properties!;
        var amounts = MapFixture().Amounts!;

        Assert.Equal(13, grid.QuantitativePrecipitation!.Values!.Count);
        Assert.Equal(13, grid.SnowfallAmount!.Values!.Count);
        Assert.Equal(13, grid.IceAccumulation!.Values!.Count);
        Assert.Equal(13, amounts.Liquid.Count);
        Assert.Equal(13, amounts.Snow.Count);
        Assert.Equal(13, amounts.Ice.Count);
    }

    [Fact]
    public void Null_probability_maps_to_null_and_absent_text_gets_defaults()
    {
        var hour = Assert.Single(NwsMapper.ToForecast(Point, [Period("""{"value":null}""")], null).Hours);

        Assert.Null(hour.PrecipProbability);
        Assert.Equal("", hour.ShortForecast);
        Assert.Equal("F", hour.TemperatureUnit);
    }

    [Fact]
    public void Missing_probability_object_maps_to_null()
    {
        var hour = Assert.Single(NwsMapper.ToForecast(Point, [Period("null")], null).Hours);
        Assert.Null(hour.PrecipProbability);
    }

    [Fact]
    public void Probability_is_rounded()
    {
        var hour = Assert.Single(NwsMapper.ToForecast(Point, [Period("""{"value":54.6}""")], null).Hours);
        Assert.Equal(55, hour.PrecipProbability);
    }

    [Fact]
    public void Missing_gridpoint_series_become_empty()
    {
        var amounts = NwsMapper.ToForecast(Point, [], new GridpointProperties(null, null, null)).Amounts!;

        Assert.Empty(amounts.Liquid);
        Assert.Empty(amounts.Snow);
        Assert.Empty(amounts.Ice);
    }

    [Theory]
    [InlineData("wmoUnit:m")]
    [InlineData("wmoUnit:degC")]
    [InlineData(null)]
    public void Non_millimetre_unit_throws(string? uom)
    {
        var grid = new GridpointProperties(
            new GridSeries("wmoUnit:mm", []),
            new GridSeries(uom, []),
            null);

        var ex = Assert.Throws<InvalidOperationException>(() => NwsMapper.ToForecast(Point, [], grid));
        Assert.Contains("snowfallAmount", ex.Message);
    }
}
