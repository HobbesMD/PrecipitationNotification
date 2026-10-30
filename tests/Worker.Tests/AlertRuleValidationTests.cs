using System.Globalization;
using Persistence;

namespace Worker.Tests;

/// <summary>
/// The per-location rules that used to be checked as environment variables in
/// <c>AppConfig</c> (D5, D14, D22), now checked on typed values before they reach
/// the database. Parse-level cases (<c>60%</c>, <c>12,3456</c>) are kept as the typed
/// value the old bug would have produced, so the rule that caught them is still proved.
/// </summary>
public class AlertRuleValidationTests
{
    [Theory]
    [InlineData("20:00", "07:00")]
    [InlineData("23:59", "00:00")]
    [InlineData("00:01", "00:00")]
    public void Window_crossing_midnight_is_accepted(string start, string end) =>
        AlertRuleValidation.ValidateWindow(Time(start), Time(end));

    [Theory]
    [InlineData("01:00", "05:00")] // same-day window: Resolve would make it 28 h
    [InlineData("07:00", "20:00")] // edges swapped by mistake
    [InlineData("20:00", "20:00")] // zero-length, or 24 h — ambiguous either way
    public void Window_not_crossing_midnight_is_rejected(string start, string end)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => AlertRuleValidation.ValidateWindow(Time(start), Time(end)));
        Assert.Contains("must cross midnight", ex.Message);
        Assert.Contains("WindowStart", ex.Message);
        Assert.Contains("WindowEnd", ex.Message);
        Assert.Equal("WindowStart", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(60)]
    [InlineData(50)] // the old "unset" default must itself be valid
    public void Threshold_in_range_is_accepted(int value) => AlertRuleValidation.ValidateThreshold(value);

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(150)]
    public void Threshold_out_of_range_is_rejected_naming_field_and_value(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => AlertRuleValidation.ValidateThreshold(value));
        Assert.Contains("ProbabilityThreshold", ex.Message);
        Assert.Equal("ProbabilityThreshold", ex.ParamName);
        // Not secret (D14), so the message helps by echoing it.
        Assert.Contains(value.ToString(CultureInfo.InvariantCulture), ex.Message);
    }

    [Theory]
    [InlineData("Latitude", "40.7128", 90)]
    [InlineData("Longitude", "-74.006", 180)]
    [InlineData("Latitude", "90", 90)] // edges are inclusive, matching the CHECK's BETWEEN
    [InlineData("Latitude", "-90", 90)]
    [InlineData("Longitude", "180", 180)]
    [InlineData("Longitude", "-180", 180)]
    [InlineData("Latitude", "40.71280", 90)] // trailing zero: still 4 places of precision
    public void Coordinate_in_range_with_at_most_four_places_is_accepted(string field, string value, int limit) =>
        AlertRuleValidation.ValidateCoordinate(field, Dec(value), limit);

    [Theory]
    // 12,3456 used to parse as 123456 (thousands separator, D22): now out of range.
    [InlineData("Latitude", "123456", 90)]
    [InlineData("Latitude", "90.3456", 90)]
    [InlineData("Latitude", "-90.3456", 90)]
    [InlineData("Longitude", "180.3456", 180)]
    [InlineData("Longitude", "-180.3456", 180)]
    // 12.3456x was malformed; the typed analogue is precision NWS won't accept.
    [InlineData("Latitude", "12.34567", 90)]
    [InlineData("Longitude", "-74.00345", 180)]
    public void Coordinate_invalid_is_rejected_without_echoing_the_value(string field, string value, int limit)
    {
        // Errors are logged, and coordinates pin a home: name the field, never the value.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => AlertRuleValidation.ValidateCoordinate(field, Dec(value), limit));
        Assert.Contains(field, ex.Message);
        Assert.Equal(field, ex.ParamName);
        Assert.DoesNotContain("345", ex.Message);
        Assert.DoesNotContain(value.TrimStart('-'), ex.Message);
        Assert.Null(ex.ActualValue);
    }

    private static TimeOnly Time(string s) => TimeOnly.Parse(s, CultureInfo.InvariantCulture);

    // xunit can't put a decimal in an attribute; strings keep the cases exact.
    private static decimal Dec(string s) => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
