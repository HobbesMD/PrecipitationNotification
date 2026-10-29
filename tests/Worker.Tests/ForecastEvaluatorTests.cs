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

    private static HourlyForecast Hour(string start, int? pop, double hours = 1, string text = "") =>
        new(Instant(start), TimeSpan.FromHours(hours), pop, text, 50, "F");

    /// <summary>
    /// <paramref name="given"/> plus contiguous PoP-0 hours filling every gap in the window, so a test
    /// that plants a few interesting hours still has the gap-free forecast the evaluator now demands.
    /// </summary>
    private static List<HourlyForecast> Filled(NightWindow window, params HourlyForecast[] given)
    {
        static HourlyForecast Dry(DateTimeOffset start, DateTimeOffset end) =>
            new(start, end - start, 0, "", 50, "F");

        var result = given.ToList();
        var cursor = window.StartUtc;
        foreach (var h in given.OrderBy(h => h.Start))
        {
            if (cursor >= window.EndUtc) break;
            if (h.End <= cursor) continue;
            if (h.Start > cursor)
                result.Add(Dry(cursor, h.Start < window.EndUtc ? h.Start : window.EndUtc));
            cursor = h.End;
        }

        if (cursor < window.EndUtc)
            result.Add(Dry(cursor, window.EndUtc));
        return result;
    }

    /// <summary>Contiguous one-hour periods covering <c>[start, end)</c>, all at <paramref name="pop"/>.</summary>
    private static List<HourlyForecast> Span(string start, string end, int? pop = 0)
    {
        var s = Instant(start);
        var e = Instant(end);
        var list = new List<HourlyForecast>();
        for (var t = s; t < e; t = t.AddHours(1))
            list.Add(new HourlyForecast(t, TimeSpan.FromHours(1), pop, "", 50, "F"));
        return list;
    }

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
        var result = Evaluate(Summer, Filled(Summer, Hour("2026-07-15T23:00:00-04:00", pop)), threshold: 50, BeforeSummer);

        Assert.Equal(isHit, result.ShouldAlert);
    }

    [Theory]
    [InlineData(50, false)] // no data is not a hit
    [InlineData(0, true)]   // ...but it counts as 0, so a threshold of 0 still matches it
    public void Null_probability_counts_as_zero(int threshold, bool isHit)
    {
        var result = Evaluate(Summer, Filled(Summer, Hour("2026-07-15T23:00:00-04:00", null)), threshold, BeforeSummer);

        Assert.Equal(isHit, result.ShouldAlert);
        Assert.Equal(0, result.PeakProbability);
    }

    [Fact]
    public void Hits_are_ordered_by_start_and_peak_is_the_max()
    {
        var hours = Filled(Summer,
            Hour("2026-07-16T03:00:00-04:00", 60),
            Hour("2026-07-15T21:00:00-04:00", 75),
            Hour("2026-07-16T01:00:00-04:00", 55));

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
        var hours = Filled(Summer,
            Hour("2026-07-15T19:00:00-04:00", 100), // [19:00, 20:00)
            Hour("2026-07-16T07:00:00-04:00", 100)); // [07:00, 08:00)

        Assert.False(Evaluate(Summer, hours, threshold: 50, BeforeSummer).ShouldAlert);
    }

    [Fact]
    public void Hour_that_only_partly_overlaps_the_window_counts()
    {
        var hours = Filled(Summer,
            Hour("2026-07-15T19:30:00-04:00", 100), // [19:30, 20:30) overlaps the start
            Hour("2026-07-16T06:30:00-04:00", 100)); // [06:30, 07:30) overlaps the end

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
        var result = Evaluate(Summer, Filled(Summer), threshold: 50, BeforeSummer, Amounts);

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

        var result = Evaluate(Summer, Filled(Summer), threshold: 50, now, Amounts);

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
        var result = Evaluate(Summer, Filled(Summer), threshold: 50, Instant("2026-07-16T05:00:00Z"), Amounts);

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
        return Evaluate(Summer, Filled(Summer), threshold: 50, BeforeSummer, amounts);
    }

    // The enums are internal, so theory rows carry their names (xUnit needs public parameter types).
    [Theory]
    [InlineData(0.0, 0.0, "Rain")]
    [InlineData(0.049, 0.0, "Rain")]  // rounds to 0.0: under the snow floor
    [InlineData(0.06, 0.0, "Snow")]   // rounds to 0.1: at it
    [InlineData(0.1, 0.0, "Snow")]
    [InlineData(2.0, 0.0, "Snow")]
    [InlineData(0.0, 0.001, "Rain")]  // a trace of ice rounds to 0.0, so it is not Ice
    [InlineData(0.0, 0.06, "Ice")]    // rounds to 0.1: any displayable ice
    [InlineData(5.0, 0.06, "Ice")]    // ice beats snow
    public void Kind_follows_ice_then_snow_then_rain(double snowIn, double iceIn, string expected) =>
        Assert.Equal(Enum.Parse<PrecipKind>(expected), WithAmounts(snowIn: snowIn, iceIn: iceIn).Kind);

    [Theory]
    [InlineData(0.0, 0.0, 0.0, "Default")]
    [InlineData(0.44, 0.0, 0.0, "Default")] // liquid rounds to 0.4
    [InlineData(0.46, 0.0, 0.0, "High")]    // rounds to 0.5
    [InlineData(0.5, 0.0, 0.0, "High")]
    [InlineData(0.0, 0.94, 0.0, "Default")] // snow rounds to 0.9
    [InlineData(0.0, 0.96, 0.0, "High")]    // rounds to 1.0
    [InlineData(0.0, 1.0, 0.0, "High")]
    [InlineData(0.0, 0.0, 0.001, "Default")] // a trace of ice is not worth High
    [InlineData(0.0, 0.0, 0.06, "High")]    // any displayable ice
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

    // ---- Coverage: no data must never look like a dry night ----
    // Summer's effective range from BeforeSummer is [20:00, 07:00) local = [00:00Z, 11:00Z).

    private static IncompleteForecastException Gap(NightWindow window, IReadOnlyList<HourlyForecast> hours, DateTimeOffset now) =>
        Assert.Throws<IncompleteForecastException>(() => Evaluate(window, hours, threshold: 50, now));

    [Fact]
    public void Mid_window_gap_throws_with_the_exact_gap()
    {
        var hours = Span("2026-07-15T20:00:00-04:00", "2026-07-15T23:00:00-04:00")
            .Concat(Span("2026-07-16T01:00:00-04:00", "2026-07-16T07:00:00-04:00"))
            .ToList();

        var ex = Gap(Summer, hours, BeforeSummer);

        Assert.Equal(Instant("2026-07-15T23:00:00-04:00"), ex.GapStartUtc);
        Assert.Equal(Instant("2026-07-16T01:00:00-04:00"), ex.GapEndUtc);
    }

    [Fact]
    public void Missing_tail_throws_up_to_the_window_end()
    {
        var ex = Gap(Summer, Span("2026-07-15T20:00:00-04:00", "2026-07-16T05:00:00-04:00"), BeforeSummer);

        Assert.Equal(Instant("2026-07-16T05:00:00-04:00"), ex.GapStartUtc);
        Assert.Equal(Summer.EndUtc, ex.GapEndUtc);
    }

    [Fact]
    public void Empty_hours_throw_for_the_whole_range()
    {
        var ex = Gap(Summer, [], BeforeSummer);

        Assert.Equal(Summer.StartUtc, ex.GapStartUtc);
        Assert.Equal(Summer.EndUtc, ex.GapEndUtc);
    }

    [Fact]
    public void Hours_starting_after_the_range_start_throw_for_the_leading_gap()
    {
        var ex = Gap(Summer, Span("2026-07-15T21:00:00-04:00", "2026-07-16T07:00:00-04:00"), BeforeSummer);

        Assert.Equal(Summer.StartUtc, ex.GapStartUtc);
        Assert.Equal(Instant("2026-07-15T21:00:00-04:00"), ex.GapEndUtc);
    }

    [Fact]
    public void Hours_past_the_window_end_do_not_fill_a_gap_and_the_gap_is_clipped_to_the_end()
    {
        var hours = Span("2026-07-15T20:00:00-04:00", "2026-07-15T22:00:00-04:00")
            .Append(Hour("2026-07-16T08:00:00-04:00", 0))
            .ToList();

        var ex = Gap(Summer, hours, BeforeSummer);

        Assert.Equal(Instant("2026-07-15T22:00:00-04:00"), ex.GapStartUtc);
        Assert.Equal(Summer.EndUtc, ex.GapEndUtc);
    }

    [Fact]
    public void Contiguous_range_passes_exactly_or_with_margin()
    {
        Evaluate(Summer, Span("2026-07-15T20:00:00-04:00", "2026-07-16T07:00:00-04:00"), threshold: 50, BeforeSummer);
        Evaluate(Summer, SummerDay(pop: 0), threshold: 50, BeforeSummer);
    }

    [Fact]
    public void Unsorted_input_is_still_checked_in_start_order()
    {
        var hours = Span("2026-07-15T20:00:00-04:00", "2026-07-16T07:00:00-04:00");
        hours.Reverse();

        Evaluate(Summer, hours, threshold: 50, BeforeSummer);
    }

    [Fact]
    public void Run_at_0200_needs_only_0200_to_0700_covered()
    {
        var now = Instant("2026-07-16T02:00:00-04:00");

        // Nothing before 02:00 exists, and none is needed.
        Evaluate(Summer, Span("2026-07-16T02:00:00-04:00", "2026-07-16T07:00:00-04:00"), threshold: 50, now);

        // ...but 02:00 itself is needed: starting at 03:00 leaves [02:00, 03:00) open.
        var ex = Gap(Summer, Span("2026-07-16T03:00:00-04:00", "2026-07-16T07:00:00-04:00"), now);
        Assert.Equal(now, ex.GapStartUtc);
        Assert.Equal(Instant("2026-07-16T03:00:00-04:00"), ex.GapEndUtc);
    }

    [Fact]
    public void Run_mid_hour_is_covered_by_the_hour_containing_now()
    {
        Evaluate(Summer, Span("2026-07-16T02:00:00-04:00", "2026-07-16T07:00:00-04:00"), threshold: 50,
            Instant("2026-07-16T02:30:00-04:00"));
    }

    [Fact]
    public void Overlapping_and_duplicate_hours_pass()
    {
        var hours = Span("2026-07-15T20:00:00-04:00", "2026-07-16T07:00:00-04:00");
        hours.Add(Hour("2026-07-15T23:00:00-04:00", 0));              // exact duplicate
        hours.Add(Hour("2026-07-16T01:30:00-04:00", 0, hours: 2));    // straddles two hours
        hours.Add(Hour("2026-07-15T18:00:00-04:00", 0, hours: 4));    // starts before, long

        Evaluate(Summer, hours, threshold: 50, BeforeSummer);
    }

    [Fact]
    public void Null_probability_inside_an_existing_hour_is_still_covered()
    {
        var result = Evaluate(Summer, Span("2026-07-15T20:00:00-04:00", "2026-07-16T07:00:00-04:00", pop: null),
            threshold: 50, BeforeSummer);

        Assert.False(result.ShouldAlert);
    }

    [Fact]
    public void Run_after_the_window_ends_has_nothing_to_cover()
    {
        var result = Evaluate(Summer, [], threshold: 50, Instant("2026-07-16T07:00:00-04:00"));

        Assert.False(result.ShouldAlert);
    }

    [Fact]
    public void Real_fall_back_and_synthetic_spring_forward_fixtures_have_full_coverage()
    {
        // No IncompleteForecastException: the repeated 1 AM hour and the skipped 2 AM hour are
        // both gap-free on the instant timeline.
        var fall = LoadHours("2026-10-29T1900Z_pre-dst-fallback", "forecast-hourly.json");
        Evaluate(Window("2026-10-31T17:00:00-04:00"), fall, threshold: 50, Instant("2026-10-31T17:00:00-04:00"));

        var spring = LoadHours("synthetic-dst-springforward", "forecast-hourly.json");
        Evaluate(Window("2027-03-13T17:00:00-05:00"), spring, threshold: 50, Instant("2027-03-13T17:00:00-05:00"));
    }

    // ---- One rounding rule for decisions and text ----

    private static (NightAssessment Assessment, ComposedMessage Message) Decide(
        double mmLiquid = 0, double mmSnow = 0, double mmIce = 0)
    {
        IReadOnlyList<IntervalAmount> Series(double mm) =>
            mm == 0 ? [] : [Interval("2026-07-16T02:00:00Z", hours: 1, mm: mm)];

        var hours = Filled(Summer, Hour("2026-07-16T00:00:00-04:00", 80));
        var assessment = Evaluate(Summer, hours, threshold: 50, BeforeSummer,
            new PrecipAmounts(Series(mmLiquid), Series(mmSnow), Series(mmIce)));
        return (assessment, NotificationComposer.Compose(assessment, NewYork));
    }

    [Fact]
    public void Snow_2_3_mm_rounds_to_a_tenth_so_it_is_Snow_and_the_text_says_so()
    {
        var (a, m) = Decide(mmSnow: 2.3); // 0.0906"

        Assert.Equal(PrecipKind.Snow, a.Kind);
        Assert.Equal(0.0906, a.SnowInches, 1e-4); // raw is preserved
        Assert.StartsWith("Overnight snow: 0.1\" expected", m.Title);
        Assert.Contains("0.1\" snow", m.Body);
    }

    [Fact]
    public void Snow_24_5_mm_rounds_to_an_inch_so_priority_is_High_and_the_text_says_1_0()
    {
        var (a, m) = Decide(mmSnow: 24.5); // 0.965"

        Assert.Equal(AlertPriority.High, a.Priority);
        Assert.Equal(0.965, a.SnowInches, 1e-3);
        Assert.Contains("1.0\" snow", m.Body);
        Assert.Equal("high", m.Priority);
    }

    [Fact]
    public void Liquid_12_5_mm_rounds_to_half_an_inch_so_priority_is_High_and_the_text_says_0_5()
    {
        var (a, m) = Decide(mmLiquid: 12.5); // 0.492"

        Assert.Equal(PrecipKind.Rain, a.Kind);
        Assert.Equal(AlertPriority.High, a.Priority);
        Assert.Contains("0.5\" liquid", m.Body);
        Assert.StartsWith("Overnight rain: 0.5\" expected", m.Title);
    }

    [Fact]
    public void Ice_0_001_inch_is_a_trace_so_not_Ice_not_High_and_the_body_says_trace()
    {
        var (a, m) = Decide(mmIce: 0.001 * MmPerInch);

        Assert.Equal(PrecipKind.Rain, a.Kind);
        Assert.Equal(AlertPriority.Default, a.Priority);
        Assert.Equal(0.001, a.IceInches, Tolerance);
        Assert.Contains("trace ice", m.Body);
        Assert.Equal("Overnight rain: up to 80%", m.Title);
    }

    [Fact]
    public void Ice_1_27_mm_rounds_to_a_tenth_so_it_is_Ice_and_High()
    {
        var (a, m) = Decide(mmIce: 1.27); // 0.05"

        Assert.Equal(PrecipKind.Ice, a.Kind);
        Assert.Equal(AlertPriority.High, a.Priority);
        Assert.Contains("0.1\" ice", m.Body);
        Assert.Equal("Overnight ice: 0.1\" expected, up to 80%", m.Title);
    }

    [Fact]
    public void Snowstorm_with_displayable_ice_names_both_in_the_title()
    {
        var (a, m) = Decide(mmSnow: 6 * MmPerInch, mmIce: 0.2 * MmPerInch);

        Assert.Equal(PrecipKind.Ice, a.Kind);
        Assert.Equal("Overnight ice (0.2\") + 6.0\" snow, up to 80%", m.Title);
    }

    // ---- Amounts unavailable (gridpoint data missing) ----

    private static NightAssessment Unavailable(string text, int pop = 80, int threshold = 50) =>
        ForecastEvaluator.Evaluate(
            Summer,
            Filled(Summer, Hour("2026-07-16T00:00:00-04:00", pop, text: text)),
            amounts: null,
            new AlertRule(threshold),
            BeforeSummer);

    [Theory]
    [InlineData("Rain Showers", "Rain")]
    [InlineData("Chance Light Rain", "Rain")]
    [InlineData("Freezing Rain", "Ice")]
    [InlineData("Light Freezing Drizzle", "Ice")]
    [InlineData("Ice Pellets", "Snow")]             // = sleet; grouped with sleet (D11)
    [InlineData("Patchy Freezing Fog", "Rain")]     // fog is not precipitation
    [InlineData("Rain And Patchy Freezing Fog", "Rain")]
    [InlineData("Ice Fog", "Rain")]                 // real Alaska wording, not precipitation
    [InlineData("Ice Crystals", "Rain")]
    [InlineData("Rain Showers Nice", "Rain")]       // whole words only: "Nice" contains "ice"
    [InlineData("Snow Showers", "Snow")]
    [InlineData("Sleet", "Snow")]
    [InlineData("Chance Flurries", "Snow")]
    [InlineData("Wintry Mix", "Snow")]
    [InlineData("HEAVY SNOW", "Snow")]              // case-insensitive
    [InlineData("Rain And Snow", "Snow")]
    [InlineData("Snow Then Freezing Rain", "Ice")]  // ice outranks snow
    public void Unavailable_amounts_guess_the_kind_from_the_forecast_text(string text, string expected)
    {
        var a = Unavailable(text);

        Assert.False(a.AmountsAvailable);
        Assert.Equal(Enum.Parse<PrecipKind>(expected), a.Kind);
        Assert.Equal(0, a.LiquidInches);
        Assert.Equal(0, a.SnowInches);
        Assert.Equal(0, a.IceInches);
    }

    [Theory]
    [InlineData("Rain Showers", "Default")]
    [InlineData("Snow Showers", "Default")] // no amount, so no way to call a snowfall shovel-worthy
    [InlineData("Freezing Rain", "High")]
    public void Unavailable_amounts_are_High_priority_for_ice_only(string text, string expected) =>
        Assert.Equal(Enum.Parse<AlertPriority>(expected), Unavailable(text).Priority);

    [Fact]
    public void Unavailable_amounts_only_read_text_from_hits()
    {
        var hours = Filled(Summer,
            Hour("2026-07-15T22:00:00-04:00", 10, text: "Freezing Rain"), // below threshold: not a hit
            Hour("2026-07-16T00:00:00-04:00", 80, text: "Rain Showers"));

        var a = ForecastEvaluator.Evaluate(Summer, hours, null, new AlertRule(50), BeforeSummer);

        Assert.Equal(PrecipKind.Rain, a.Kind);
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    public void Unavailable_amounts_still_alert_on_probability_alone(int pop, bool shouldAlert)
    {
        var a = Unavailable("Freezing Rain", pop);

        Assert.Equal(shouldAlert, a.ShouldAlert);
        Assert.False(a.AmountsAvailable);
    }

    [Fact]
    public void Unavailable_amounts_compose_a_title_without_amount_and_a_body_that_says_so()
    {
        var m = NotificationComposer.Compose(Unavailable("Snow Showers"), NewYork);

        Assert.Equal("Overnight snow: up to 80%", m.Title);
        Assert.Contains("Amounts unavailable from NWS", m.Body);
    }
}
