using System.Globalization;
using Worker.Forecast;
using Worker.Nws;

namespace Worker.Tests;

public class ForecastEvaluatorTests
{
    private const double Tolerance = 1e-9;
    private const double MmPerInch = 25.4;

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly PointProperties Point = new(null, "America/New_York", null);

    // Ordinary summer night, EDT: 2026-07-15 20:00 -04:00 -> 2026-07-16 07:00 -04:00 = 00:00Z -> 11:00Z.
    private static readonly NightWindow Summer = Window("2026-07-15T17:00:00-04:00");
    private static readonly DateTimeOffset BeforeSummer = Instant("2026-07-15T17:00:00-04:00");

    private static DateTimeOffset Instant(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    private static NightWindow Window(string now) =>
        NightWindow.Resolve(Instant(now), NewYork, new TimeOnly(20, 0), new TimeOnly(7, 0));

    private static HourlyForecast Hour(string start, int? pop, double hours = 1) =>
        new(Instant(start), TimeSpan.FromHours(hours), pop, "", 50, "F");

    /// <summary>One hour per local hour from 17:00 to 08:00 (-04:00), all at <paramref name="pop"/>.</summary>
    private static List<HourlyForecast> SummerDay(int? pop) =>
        Enumerable.Range(0, 16)
            .Select(i => new HourlyForecast(Instant("2026-07-15T17:00:00-04:00").AddHours(i), TimeSpan.FromHours(1), pop, "", 50, "F"))
            .ToList();

    private static NightAssessment Evaluate(
        NightWindow window, IReadOnlyList<HourlyForecast> hours, int threshold, DateTimeOffset now, PrecipAmounts? amounts = null) =>
        ForecastEvaluator.Evaluate(window, hours, amounts ?? PrecipAmounts.Empty, new AlertRule(threshold), now);

    private static IntervalAmount Interval(string startUtc, double hours, double mm) =>
        new(Instant(startUtc), TimeSpan.FromHours(hours), mm);

    // ---- Captured / synthetic fixtures ----

    private static IReadOnlyList<HourlyForecast> LoadHours(string dir, string file)
    {
        var periods = FixtureFiles.Load<HourlyForecastResponse>($"nws/{dir}/{file}").Properties!.Periods!;
        return NwsMapper.ToForecast(Point, periods, null).Hours;
    }

    [Fact]
    public void Real_fall_back_fixture_has_exactly_twelve_hours_in_range_including_both_1am_hours()
    {
        var hours = LoadHours("2026-10-29T1900Z_pre-dst-fallback", "forecast-hourly.json");
        var now = Instant("2026-10-31T17:00:00-04:00");
        var window = Window("2026-10-31T17:00:00-04:00");

        // Threshold 0 makes every in-range hour a hit, so the count is independent of the real PoPs.
        var result = Evaluate(window, hours, threshold: 0, now);

        Assert.Equal(new DateOnly(2026, 10, 31), window.NightDate);
        Assert.Equal(12, result.Hits.Count);
        Assert.Equal(Instant("2026-10-31T20:00:00-04:00"), result.Hits[0].Start);
        Assert.Equal(Instant("2026-11-01T06:00:00-05:00"), result.Hits[^1].Start);

        var oneAm = result.Hits.Where(h => h.Start.DateTime == new DateTime(2026, 11, 1, 1, 0, 0)).ToList();
        Assert.Equal([TimeSpan.FromHours(-4), TimeSpan.FromHours(-5)], oneAm.Select(h => h.Start.Offset));
    }

    [Fact]
    public void Synthetic_spring_forward_has_ten_hours_in_range_and_hits_match_planted_pops()
    {
        var hours = LoadHours("synthetic-dst-springforward", "forecast-hourly.json");
        var now = Instant("2027-03-13T17:00:00-05:00");
        var window = Window("2027-03-13T17:00:00-05:00");

        Assert.Equal(new DateOnly(2027, 3, 13), window.NightDate);

        // 18:00 (80) and 19:00 (60) end by 20:00 and 07:00 (95) starts at the window end: all excluded.
        var all = Evaluate(window, hours, threshold: 0, now);
        Assert.Equal(10, all.Hits.Count);
        Assert.DoesNotContain(all.Hits, h => h.PrecipProbability is 80 or 60 or 95 or 100);

        // The skipped 02:00 hour doesn't exist: 01:00 -05:00 is followed directly by 03:00 -04:00.
        Assert.Equal(all.Hits[5].End, all.Hits[6].Start);

        // 22:00 (50), 01:00 (70), 03:00 (55), 05:00 (90). 21:00 (49) and 06:00 (40) miss.
        var result = Evaluate(window, hours, threshold: 50, now);
        Assert.Equal(
            [
                Instant("2027-03-13T22:00:00-05:00"),
                Instant("2027-03-14T01:00:00-05:00"),
                Instant("2027-03-14T03:00:00-04:00"),
                Instant("2027-03-14T05:00:00-04:00"),
            ],
            result.Hits.Select(h => h.Start));
        Assert.Equal(90, result.PeakProbability);
        Assert.True(result.ShouldAlert);
    }

    // ---- Hit rule ----

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(51, true)]
    public void Threshold_boundary_is_inclusive(int pop, bool isHit)
    {
        var result = Evaluate(Summer, [Hour("2026-07-15T23:00:00-04:00", pop)], threshold: 50, BeforeSummer);

        Assert.Equal(isHit, result.ShouldAlert);
    }

    [Theory]
    [InlineData(50, false)] // no data is not a hit
    [InlineData(0, true)]   // ...but it counts as 0, so a threshold of 0 still matches it
    public void Null_probability_counts_as_zero(int threshold, bool isHit)
    {
        var result = Evaluate(Summer, [Hour("2026-07-15T23:00:00-04:00", null)], threshold, BeforeSummer);

        Assert.Equal(isHit, result.ShouldAlert);
        Assert.Equal(0, result.PeakProbability);
    }

    [Fact]
    public void Hits_are_ordered_by_start_and_peak_is_the_max()
    {
        var hours = new[]
        {
            Hour("2026-07-16T03:00:00-04:00", 60),
            Hour("2026-07-15T21:00:00-04:00", 75),
            Hour("2026-07-16T01:00:00-04:00", 55),
        };

        var result = Evaluate(Summer, hours, threshold: 50, BeforeSummer);

        Assert.Equal(
            [Instant("2026-07-15T21:00:00-04:00"), Instant("2026-07-16T01:00:00-04:00"), Instant("2026-07-16T03:00:00-04:00")],
            result.Hits.Select(h => h.Start));
        Assert.Equal(75, result.PeakProbability);
        Assert.Same(Summer, result.Window);
    }

    [Fact]
    public void No_hits_means_no_alert_and_zero_peak()
    {
        var result = Evaluate(Summer, SummerDay(pop: 10), threshold: 50, BeforeSummer);

        Assert.False(result.ShouldAlert);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.PeakProbability);
    }

    // ---- Window edges ----

    [Fact]
    public void Hour_ending_exactly_at_window_start_and_hour_starting_exactly_at_window_end_are_excluded()
    {
        var hours = new[]
        {
            Hour("2026-07-15T19:00:00-04:00", 100), // [19:00, 20:00)
            Hour("2026-07-16T07:00:00-04:00", 100), // [07:00, 08:00)
        };

        Assert.False(Evaluate(Summer, hours, threshold: 50, BeforeSummer).ShouldAlert);
    }

    [Fact]
    public void Hour_that_only_partly_overlaps_the_window_counts()
    {
        var hours = new[]
        {
            Hour("2026-07-15T19:30:00-04:00", 100), // [19:30, 20:30) overlaps the start
            Hour("2026-07-16T06:30:00-04:00", 100), // [06:30, 07:30) overlaps the end
        };

        Assert.Equal(2, Evaluate(Summer, hours, threshold: 50, BeforeSummer).Hits.Count);
    }

    [Theory]
    // now,                          first in-range hour (local),       count of hits
    [InlineData("2026-07-15T17:00:00-04:00", "2026-07-15T20:00:00-04:00", 11)] // before the window: whole night
    [InlineData("2026-07-15T20:00:00-04:00", "2026-07-15T20:00:00-04:00", 11)] // exactly at the start
    [InlineData("2026-07-16T01:59:59-04:00", "2026-07-16T01:00:00-04:00", 6)]  // 1 s before 02:00: the 01:00 hour still contains now
    [InlineData("2026-07-16T02:00:00-04:00", "2026-07-16T02:00:00-04:00", 5)]  // on the boundary: 01:00 hour ended, 02:00 begins
    [InlineData("2026-07-16T02:30:00-04:00", "2026-07-16T02:00:00-04:00", 5)]  // mid-hour: the 02:00 hour straddles now and counts
    [InlineData("2026-07-16T06:30:00-04:00", "2026-07-16T06:00:00-04:00", 1)]  // only the last hour left
    public void Run_time_trims_hours_already_over_but_keeps_the_hour_containing_now(string now, string firstHour, int count)
    {
        var result = Evaluate(Summer, SummerDay(pop: 100), threshold: 50, Instant(now));

        Assert.Equal(count, result.Hits.Count);
        Assert.Equal(Instant(firstHour), result.Hits[0].Start);
        Assert.Equal(Instant("2026-07-16T06:00:00-04:00"), result.Hits[^1].Start);
    }

    [Fact]
    public void Run_at_or_after_window_end_sees_nothing()
    {
        var result = Evaluate(Summer, SummerDay(pop: 100), threshold: 50, Instant("2026-07-16T07:00:00-04:00"));

        Assert.False(result.ShouldAlert);
    }

    // ---- Apportioned totals ----

    // Window is 00:00Z -> 11:00Z. Amounts are in mm; expected inches are hand arithmetic (mm / 25.4).
    private static readonly PrecipAmounts Amounts = new(
        Liquid: [Interval("2026-07-16T08:00:00Z", hours: 6, mm: 15.24)], // 08:00Z-14:00Z, straddles the 11:00Z end
        Snow: [Interval("2026-07-16T02:00:00Z", hours: 6, mm: 12.7)],    // 02:00Z-08:00Z, wholly inside
        Ice: [Interval("2026-07-16T04:00:00Z", hours: 3, mm: 2.54)]);    // 04:00Z-07:00Z, wholly inside

    [Fact]
    public void Totals_before_the_window_clip_to_the_window_end()
    {
        var result = Evaluate(Summer, [], threshold: 50, BeforeSummer, Amounts);

        // Liquid: 3 of 6 h inside (08:00Z-11:00Z) -> 15.24 mm x 3/6 = 7.62 mm = 0.3 in.
        Assert.Equal(0.3, result.LiquidInches, Tolerance);
        // Snow: all of it -> 12.7 mm = 0.5 in.
        Assert.Equal(0.5, result.SnowInches, Tolerance);
        // Ice: all of it -> 2.54 mm = 0.1 in.
        Assert.Equal(0.1, result.IceInches, Tolerance);
    }

    [Fact]
    public void Totals_shrink_when_run_mid_window()
    {
        // now = 09:30Z (05:30 local).
        var now = Instant("2026-07-16T09:30:00Z");

        var result = Evaluate(Summer, [], threshold: 50, now, Amounts);

        // Liquid: 09:30Z-11:00Z = 1.5 of 6 h -> 15.24 x 1.5/6 = 3.81 mm = 0.15 in.
        Assert.Equal(0.15, result.LiquidInches, Tolerance);
        // Snow: its interval ended at 08:00Z, before now -> 0.
        Assert.Equal(0, result.SnowInches, Tolerance);
        Assert.Equal(0, result.IceInches, Tolerance);
    }

    [Fact]
    public void Totals_split_an_interval_that_straddles_now()
    {
        // now = 05:00Z: snow 05:00Z-08:00Z = 3 of 6 h -> 12.7 x 3/6 = 6.35 mm = 0.25 in.
        // ice 05:00Z-07:00Z = 2 of 3 h -> 2.54 x 2/3 mm = 0.0666... in.
        var result = Evaluate(Summer, [], threshold: 50, Instant("2026-07-16T05:00:00Z"), Amounts);

        Assert.Equal(0.25, result.SnowInches, Tolerance);
        Assert.Equal(0.1 * 2 / 3, result.IceInches, Tolerance);
    }

    [Fact]
    public void Amounts_never_decide_whether_to_alert()
    {
        // Real accumulation forecast, but no hour reaches the threshold: still silent.
        var result = Evaluate(Summer, SummerDay(pop: 10), threshold: 50, BeforeSummer, Amounts);

        Assert.False(result.ShouldAlert);
        Assert.Equal(0.3, result.LiquidInches, Tolerance);
    }

    // ---- Kind and priority ----

    /// <summary>One in-window hour of the given inch amounts.</summary>
    private static NightAssessment WithAmounts(double liquidIn = 0, double snowIn = 0, double iceIn = 0)
    {
        IReadOnlyList<IntervalAmount> Series(double inches) =>
            inches == 0 ? [] : [Interval("2026-07-16T02:00:00Z", hours: 1, mm: inches * MmPerInch)];

        var amounts = new PrecipAmounts(Series(liquidIn), Series(snowIn), Series(iceIn));
        return Evaluate(Summer, [], threshold: 50, BeforeSummer, amounts);
    }

    // The enums are internal, so theory rows carry their names (xUnit needs public parameter types).
    [Theory]
    [InlineData(0.0, 0.0, "Rain")]
    [InlineData(0.09, 0.0, "Rain")]   // just under the snow floor
    [InlineData(0.1, 0.0, "Snow")]    // exactly at it
    [InlineData(2.0, 0.0, "Snow")]
    [InlineData(0.0, 0.001, "Ice")]   // any ice at all
    [InlineData(5.0, 0.001, "Ice")]   // ice beats snow
    public void Kind_follows_ice_then_snow_then_rain(double snowIn, double iceIn, string expected) =>
        Assert.Equal(Enum.Parse<PrecipKind>(expected), WithAmounts(snowIn: snowIn, iceIn: iceIn).Kind);

    [Theory]
    [InlineData(0.0, 0.0, 0.0, "Default")]
    [InlineData(0.49, 0.0, 0.0, "Default")] // liquid just under
    [InlineData(0.5, 0.0, 0.0, "High")]     // liquid exactly at
    [InlineData(0.0, 0.99, 0.0, "Default")] // snow just under
    [InlineData(0.0, 1.0, 0.0, "High")]     // snow exactly at
    [InlineData(0.0, 0.0, 0.001, "High")]   // any ice
    public void Priority_is_high_for_heavy_snow_heavy_liquid_or_any_ice(
        double liquidIn, double snowIn, double iceIn, string expected) =>
        Assert.Equal(Enum.Parse<AlertPriority>(expected), WithAmounts(liquidIn, snowIn, iceIn).Priority);

    [Fact]
    public void Totals_are_reported_in_inches()
    {
        var result = WithAmounts(liquidIn: 0.4, snowIn: 0.2, iceIn: 0.01);

        Assert.Equal(0.4, result.LiquidInches, Tolerance);
        Assert.Equal(0.2, result.SnowInches, Tolerance);
        Assert.Equal(0.01, result.IceInches, Tolerance);
    }
}
