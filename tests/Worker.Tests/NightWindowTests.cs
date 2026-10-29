using System.Globalization;
using Worker.Forecast;

namespace Worker.Tests;

/// <summary>
/// Every case pins all four outputs. Instants are written as offset literals so a
/// failing row reads as "this wall-clock time, in this offset" without arithmetic.
/// </summary>
public class NightWindowTests
{
    private static readonly TimeOnly Start = new(20, 0);
    private static readonly TimeOnly End = new(7, 0);

    [Theory]
    // Ordinary summer date (EDT, -04:00): window is 2026-07-16 00:00Z → 11:00Z.
    [InlineData("2026-07-15T17:00:00-04:00", "2026-07-15", "2026-07-16T00:00:00Z", "2026-07-16T11:00:00Z", 11)] // before tonight
    [InlineData("2026-07-15T23:00:00-04:00", "2026-07-15", "2026-07-16T00:00:00Z", "2026-07-16T11:00:00Z", 11)] // tonight in progress
    [InlineData("2026-07-16T02:00:00-04:00", "2026-07-15", "2026-07-16T00:00:00Z", "2026-07-16T11:00:00Z", 11)] // after midnight: yesterday's night
    [InlineData("2026-07-16T06:59:59-04:00", "2026-07-15", "2026-07-16T00:00:00Z", "2026-07-16T11:00:00Z", 11)] // last second of the window
    [InlineData("2026-07-16T07:00:00-04:00", "2026-07-16", "2026-07-17T00:00:00Z", "2026-07-17T11:00:00Z", 11)] // end is exclusive: next night
    [InlineData("2026-07-15T20:00:00-04:00", "2026-07-15", "2026-07-16T00:00:00Z", "2026-07-16T11:00:00Z", 11)] // exactly at start
    // Ordinary winter date (EST, -05:00): same wall clock, UTC shifts by an hour.
    [InlineData("2026-01-15T17:00:00-05:00", "2026-01-15", "2026-01-16T01:00:00Z", "2026-01-16T12:00:00Z", 11)]
    public void Ordinary_nights_in_New_York(string now, string night, string startUtc, string endUtc, int hours) =>
        AssertWindow("America/New_York", now, night, startUtc, endUtc, hours);

    [Theory]
    // Fall-back night: 2026-11-01 02:00 EDT → 01:00 EST. Starts at -04:00, ends at -05:00.
    [InlineData("2026-10-31T17:00:00-04:00", "2026-10-31", "2026-11-01T00:00:00Z", "2026-11-01T12:00:00Z", 12)]
    [InlineData("2026-11-01T01:30:00-04:00", "2026-10-31", "2026-11-01T00:00:00Z", "2026-11-01T12:00:00Z", 12)] // first 01:30
    [InlineData("2026-11-01T01:30:00-05:00", "2026-10-31", "2026-11-01T00:00:00Z", "2026-11-01T12:00:00Z", 12)] // second 01:30
    [InlineData("2026-11-01T06:59:59-05:00", "2026-10-31", "2026-11-01T00:00:00Z", "2026-11-01T12:00:00Z", 12)]
    // Spring-forward night: 2027-03-14 02:00 EST → 03:00 EDT. Starts at -05:00, ends at -04:00.
    [InlineData("2027-03-13T17:00:00-05:00", "2027-03-13", "2027-03-14T01:00:00Z", "2027-03-14T11:00:00Z", 10)]
    [InlineData("2027-03-14T03:30:00-04:00", "2027-03-13", "2027-03-14T01:00:00Z", "2027-03-14T11:00:00Z", 10)] // just after the gap
    public void Dst_nights_in_New_York(string now, string night, string startUtc, string endUtc, int hours) =>
        AssertWindow("America/New_York", now, night, startUtc, endUtc, hours);

    [Theory]
    // Southern hemisphere, positive offset: AEST +10:00, AEDT +11:00.
    [InlineData("2026-07-15T17:00:00+10:00", "2026-07-15", "2026-07-15T10:00:00Z", "2026-07-15T21:00:00Z", 11)]
    // Local 02:00 on the 16th is still the 15th in UTC; night is the 15th either way.
    [InlineData("2026-07-16T02:00:00+10:00", "2026-07-15", "2026-07-15T10:00:00Z", "2026-07-15T21:00:00Z", 11)]
    // Local 08:00 on the 16th is the 15th in UTC; a UTC-date shortcut would pick the wrong night.
    [InlineData("2026-07-16T08:00:00+10:00", "2026-07-16", "2026-07-16T10:00:00Z", "2026-07-16T21:00:00Z", 11)]
    // Spring forward 2026-10-04 02:00 → 03:00 (October, not March).
    [InlineData("2026-10-03T17:00:00+10:00", "2026-10-03", "2026-10-03T10:00:00Z", "2026-10-03T20:00:00Z", 10)]
    // Fall back 2027-04-04 03:00 → 02:00 (April, not November).
    [InlineData("2027-04-03T17:00:00+11:00", "2027-04-03", "2027-04-03T09:00:00Z", "2027-04-03T21:00:00Z", 12)]
    public void Positive_offset_zone(string now, string night, string startUtc, string endUtc, int hours) =>
        AssertWindow("Australia/Sydney", now, night, startUtc, endUtc, hours);

    [Theory]
    // Custom zones (-05:00 standard, +1h DST) whose transitions land exactly on a window edge.
    // (a) Start 20:00 is skipped (20:00 → 21:00): moves forward to 21:00 -04:00.
    [InlineData(GapAtStart, "2027-03-14T12:00:00-05:00", "2027-03-14", "2027-03-15T01:00:00Z", "2027-03-15T11:00:00Z", 10)]
    // End 07:00 is skipped (07:00 → 08:00): moves forward to 08:00 -04:00.
    [InlineData(GapAtEnd, "2027-03-13T12:00:00-05:00", "2027-03-13", "2027-03-14T01:00:00Z", "2027-03-14T12:00:00Z", 11)]
    // (b) Start 20:00 is repeated (21:00 → 20:00): first occurrence, 20:00 -04:00. Second would give 01:00Z / 11h.
    [InlineData(AmbiguousAtStart, "2026-11-07T12:00:00-04:00", "2026-11-07", "2026-11-08T00:00:00Z", "2026-11-08T12:00:00Z", 12)]
    // (c) End 07:00 is repeated (08:00 → 07:00): second occurrence, 07:00 -05:00. First would give 11:00Z / 11h.
    [InlineData(AmbiguousAtEnd, "2026-11-06T12:00:00-04:00", "2026-11-06", "2026-11-07T00:00:00Z", "2026-11-07T12:00:00Z", 12)]
    public void Dst_transition_on_window_edge(string zone, string now, string night, string startUtc, string endUtc, int hours) =>
        AssertWindow(zone, now, night, startUtc, endUtc, hours);

    private const string GapAtStart = "Test/GapAtStart";
    private const string GapAtEnd = "Test/GapAtEnd";
    private const string AmbiguousAtStart = "Test/AmbiguousAtStart";
    private const string AmbiguousAtEnd = "Test/AmbiguousAtEnd";

    private static void AssertWindow(string zoneId, string now, string night, string startUtc, string endUtc, int hours)
    {
        var window = NightWindow.Resolve(Instant(now), Zone(zoneId), Start, End);

        Assert.Equal(DateOnly.Parse(night, CultureInfo.InvariantCulture), window.NightDate);
        Assert.Equal(Instant(startUtc), window.StartUtc);
        Assert.Equal(Instant(endUtc), window.EndUtc);
        Assert.Equal(TimeSpan.FromHours(hours), window.Length);
        // DateTimeOffset equality ignores offset; the property names promise UTC.
        Assert.Equal(TimeSpan.Zero, window.StartUtc.Offset);
        Assert.Equal(TimeSpan.Zero, window.EndUtc.Offset);
    }

    private static DateTimeOffset Instant(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static TimeZoneInfo Zone(string id) => id switch
    {
        GapAtStart => Custom(id, springForwardAt: new(20, 0), fallBackAt: new(2, 0)),
        GapAtEnd => Custom(id, springForwardAt: new(7, 0), fallBackAt: new(2, 0)),
        AmbiguousAtStart => Custom(id, springForwardAt: new(2, 0), fallBackAt: new(21, 0)),
        AmbiguousAtEnd => Custom(id, springForwardAt: new(2, 0), fallBackAt: new(8, 0)),
        _ => TimeZoneInfo.FindSystemTimeZoneById(id),
    };

    /// <summary>
    /// -05:00 standard, +1h DST from 14 March to 7 November every year. Transition
    /// times follow .NET's convention: spring-forward in standard time, fall-back in
    /// daylight time, so fall-back at 21:00 repeats 20:00–21:00.
    /// </summary>
    private static TimeZoneInfo Custom(string id, TimeOnly springForwardAt, TimeOnly fallBackAt)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(TimeOfDay(springForwardAt), month: 3, day: 14),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(TimeOfDay(fallBackAt), month: 11, day: 7));

        return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(-5), id, id + " Standard", id + " Daylight", [rule]);
    }

    private static DateTime TimeOfDay(TimeOnly time) => new DateOnly(1, 1, 1).ToDateTime(time, DateTimeKind.Unspecified);
}
