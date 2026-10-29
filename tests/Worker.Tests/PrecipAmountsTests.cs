using System.Text.Json;
using Worker.Forecast;

namespace Worker.Tests;

public class PrecipAmountsTests
{
    private const double Tolerance = 1e-9;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-01T00:00:00+00:00");

    private static IntervalAmount Hours(double startHour, double hours, double mm) =>
        new(T0.AddHours(startHour), TimeSpan.FromHours(hours), mm);

    // ---- ParseValidTime ----

    [Theory]
    [InlineData("2026-10-29T12:00:00+00:00/PT1H", 1)]
    [InlineData("2026-10-29T12:00:00+00:00/PT6H", 6)]
    [InlineData("2026-10-29T12:00:00+00:00/P1DT6H", 30)]
    public void ParseValidTime_reads_iso_durations(string validTime, double expectedHours)
    {
        var result = PrecipAmounts.ParseValidTime(validTime, 1.5);

        Assert.Equal(DateTimeOffset.Parse("2026-10-29T12:00:00+00:00"), result.Start);
        Assert.Equal(TimeSpan.FromHours(expectedHours), result.Duration);
        Assert.Equal(1.5, result.Mm);
    }

    [Fact]
    public void ParseValidTime_non_utc_offset_keeps_the_same_instant()
    {
        var result = PrecipAmounts.ParseValidTime("2026-10-31T20:00:00-04:00/PT1H", 0);

        Assert.Equal(DateTimeOffset.Parse("2026-11-01T00:00:00+00:00"), result.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T01:00:00+00:00"), result.End);
    }

    [Fact]
    public void ParseValidTime_null_amount_becomes_zero() =>
        Assert.Equal(0, PrecipAmounts.ParseValidTime("2026-10-29T12:00:00+00:00/PT1H", null).Mm);

    [Theory]
    [InlineData("2026-10-29T12:00:00+00:00")]           // no slash
    [InlineData("2026-10-29T12:00:00+00:00/PT1H/PT1H")] // too many parts
    [InlineData("not-a-date/PT1H")]                     // bad date
    [InlineData("2026-10-29T12:00:00+00:00/banana")]    // bad duration
    [InlineData("2026-10-29T12:00:00+00:00/")]          // empty duration
    [InlineData("2026-10-29T12:00:00/PT1H")]            // no UTC offset
    [InlineData("2026-10-29T12:00:00+00:00/PT0H")]      // zero duration
    [InlineData("2026-10-29T12:00:00+00:00/-PT1H")]     // negative duration
    public void ParseValidTime_malformed_input_throws_FormatException(string validTime) =>
        Assert.Throws<FormatException>(() => PrecipAmounts.ParseValidTime(validTime, 1));

    [Theory]
    [InlineData("2026-10-29T12:00:00Z", "2026-10-29T12:00:00+00:00")]
    [InlineData("2026-10-29T12:00:00+00:00", "2026-10-29T12:00:00+00:00")]
    [InlineData("2026-10-29T12:00:00-04:00", "2026-10-29T16:00:00+00:00")]
    [InlineData("2026-10-29T12:00:00-0400", "2026-10-29T16:00:00+00:00")]
    public void ParseValidTime_offset_forms_parse_to_the_right_instant(string start, string expectedUtc)
    {
        var result = PrecipAmounts.ParseValidTime(start + "/PT1H", 0);
        Assert.Equal(DateTimeOffset.Parse(expectedUtc), result.Start);
    }

    // ---- Apportion (window [fromHour, toHour) measured from T0) ----

    private static double Over(double fromHour, double toHour, params IntervalAmount[] series) =>
        PrecipAmounts.Apportion(series, T0.AddHours(fromHour), T0.AddHours(toHour));

    [Fact]
    public void Interval_fully_inside_window_counts_in_full() =>
        Assert.Equal(3.0, Over(0, 10, Hours(2, 6, 3.0)), Tolerance);

    [Fact]
    public void Interval_fully_outside_window_counts_zero()
    {
        Assert.Equal(0, Over(0, 10, Hours(12, 6, 5.0)), Tolerance); // after
        Assert.Equal(0, Over(0, 10, Hours(-6, 6, 5.0)), Tolerance); // before, ends exactly at window start
        Assert.Equal(0, Over(0, 10, Hours(10, 6, 5.0)), Tolerance); // starts exactly at the exclusive end
    }

    [Fact]
    public void Interval_straddling_window_start_is_pro_rated() =>
        // [-2h, +4h) with 6 mm; 4 of 6 hours inside -> 4 mm
        Assert.Equal(4.0, Over(0, 10, Hours(-2, 6, 6.0)), Tolerance);

    [Fact]
    public void Interval_straddling_window_end_is_pro_rated() =>
        // [8h, 14h) with 6 mm; 2 of 6 hours inside -> 2 mm
        Assert.Equal(2.0, Over(0, 10, Hours(8, 6, 6.0)), Tolerance);

    [Fact]
    public void Window_strictly_inside_one_interval_gets_a_fraction() =>
        // 12 mm over 6h, window [2h, 4h) is 2h long -> 12 * 2/6 = 4 mm
        Assert.Equal(4.0, Over(2, 4, Hours(0, 6, 12.0)), Tolerance);

    [Fact]
    public void Zero_length_window_is_zero() =>
        Assert.Equal(0, Over(3, 3, Hours(0, 6, 12.0)), Tolerance);

    [Fact]
    public void Inverted_window_is_zero() =>
        Assert.Equal(0, Over(5, 1, Hours(0, 6, 12.0)), Tolerance);

    [Fact]
    public void Empty_series_is_zero() =>
        Assert.Equal(0, Over(0, 10), Tolerance);

    [Fact]
    public void Multiple_intervals_are_summed()
    {
        // [-3h,+3h) 6mm   -> 3h of 6h inside = 3.0
        // [3h,9h)   9mm   -> fully inside    = 9.0
        // [9h,15h)  12mm  -> 1h of 6h inside = 2.0
        // [20h,26h) 99mm  -> outside         = 0
        var total = Over(0, 10, Hours(-3, 6, 6), Hours(3, 6, 9), Hours(9, 6, 12), Hours(20, 6, 99));

        Assert.Equal(14.0, total, Tolerance);
    }

    [Fact]
    public void Offsets_do_not_matter_only_instants()
    {
        // 20:00-04:00 == 00:00Z. Window ends 22:00-04:00 == 02:00Z, so 2h of the 6h interval -> 2 mm.
        var series = new[] { PrecipAmounts.ParseValidTime("2026-10-31T20:00:00-04:00/PT6H", 6) };

        var total = PrecipAmounts.Apportion(
            series,
            DateTimeOffset.Parse("2026-11-01T00:00:00+00:00"),
            DateTimeOffset.Parse("2026-10-31T22:00:00-04:00"));

        Assert.Equal(2.0, total, Tolerance);
    }

    // ---- Real captured payload ----

    private const string Fixture = "nws/2026-10-29T1900Z_pre-dst-fallback/gridpoint.json";

    // Overnight window for the night of 2026-10-31 -> 2026-11-01, which contains the DST fall-back
    // (01:00-04:00 EDT repeats as 01:00-05:00 EST): 20:00-04:00 -> 07:00-05:00, i.e. 12 real hours (11 on the wall clock, plus the repeated hour).
    private static readonly DateTimeOffset WindowFrom = DateTimeOffset.Parse("2026-10-31T20:00:00-04:00");
    private static readonly DateTimeOffset WindowTo = DateTimeOffset.Parse("2026-11-01T07:00:00-05:00");

    private static List<(string ValidTime, double? Value)> FixtureValues()
    {
        using var doc = JsonDocument.Parse(FixtureFiles.Read(Fixture));
        return doc.RootElement.GetProperty("properties").GetProperty("quantitativePrecipitation").GetProperty("values")
            .EnumerateArray()
            .Select(v => (
                v.GetProperty("validTime").GetString()!,
                v.GetProperty("value").ValueKind == JsonValueKind.Null ? (double?)null : v.GetProperty("value").GetDouble()))
            .ToList();
    }

    [Fact]
    public void Fixture_window_total_matches_hand_computation()
    {
        // Hand-read from gridpoint.json (quantitativePrecipitation.values). The two entries that
        // overlap the window [2026-11-01T00:00Z, 2026-11-01T12:00Z) are:
        //   2026-11-01T00:00:00+00:00/PT6H  value 0   -> [00:00Z, 06:00Z), fully inside
        //   2026-11-01T06:00:00+00:00/PT6H  value 0   -> [06:00Z, 12:00Z), fully inside
        // Every other entry ends at or before 2026-11-01T00:00Z (the last of them,
        // 2026-10-31T18:00Z/PT6H, ends exactly at 00:00Z) so contributes 0.
        // Total = 0 * 6/6 + 0 * 6/6 = 0 mm. The forecast that night is dry: the fixture
        // covers the whole window, it just holds no precipitation.
        var series = FixtureValues().Select(v => PrecipAmounts.ParseValidTime(v.ValidTime, v.Value)).ToList();

        Assert.Equal(0.0, PrecipAmounts.Apportion(series, WindowFrom, WindowTo), Tolerance);
    }

    [Fact]
    public void Fixture_intervals_tile_the_whole_window()
    {
        // Guards the zero above against being vacuous (data missing -> also 0): replace every value
        // with 6 mm. Window is 12h -> hand arithmetic: two 6h intervals fully inside, 6 + 6 = 12 mm.
        var series = FixtureValues().Select(v => PrecipAmounts.ParseValidTime(v.ValidTime, 6)).ToList();

        Assert.Equal(12.0, PrecipAmounts.Apportion(series, WindowFrom, WindowTo), Tolerance);
    }
}
