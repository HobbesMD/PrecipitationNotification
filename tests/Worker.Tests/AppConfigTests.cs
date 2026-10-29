namespace Worker.Tests;

public class AppConfigTests
{
    [Theory]
    [InlineData("20:00", "07:00")]
    [InlineData("23:59", "00:00")]
    [InlineData("00:01", "00:00")]
    public void Window_crossing_midnight_is_accepted(string start, string end) =>
        AppConfig.ValidateWindow(TimeOnly.Parse(start), TimeOnly.Parse(end));

    [Theory]
    [InlineData("01:00", "05:00")] // same-day window: Resolve would make it 28 h
    [InlineData("07:00", "20:00")] // edges swapped by mistake
    [InlineData("20:00", "20:00")] // zero-length, or 24 h — ambiguous either way
    public void Window_not_crossing_midnight_is_rejected(string start, string end)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AppConfig.ValidateWindow(TimeOnly.Parse(start), TimeOnly.Parse(end)));
        Assert.Contains("must cross midnight", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Threshold_unset_defaults_to_50(string? raw) => Assert.Equal(50, AppConfig.ParseThreshold(raw));

    [Theory]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    [InlineData("60", 60)]
    public void Threshold_in_range_is_accepted(string raw, int expected) =>
        Assert.Equal(expected, AppConfig.ParseThreshold(raw));

    [Theory]
    [InlineData("60%")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("150")]
    public void Threshold_invalid_is_rejected_naming_variable(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AppConfig.ParseThreshold(raw));
        Assert.Contains("PRECIP_PROBABILITY_THRESHOLD", ex.Message);
        Assert.Contains(raw, ex.Message);
    }

    [Theory]
    [InlineData("40.7128", 40.7128)]
    [InlineData("-74.006", -74.006)]
    public void Coordinate_parses_invariant_decimal_degrees(string raw, double expected) =>
        Assert.Equal(expected, AppConfig.ParseCoordinate("PRECIP_LAT", raw));

    [Theory]
    [InlineData("12.3456x")]
    [InlineData("12,3456")]
    public void Coordinate_invalid_is_rejected_without_echoing_the_value(string raw)
    {
        // Config errors are logged, and coordinates pin a home: name the variable, never the value.
        var ex = Assert.Throws<InvalidOperationException>(() => AppConfig.ParseCoordinate("PRECIP_LAT", raw));
        Assert.Contains("PRECIP_LAT", ex.Message);
        Assert.DoesNotContain("3456", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Coordinate_missing_is_rejected_naming_variable(string? raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AppConfig.ParseCoordinate("PRECIP_LON", raw));
        Assert.Contains("PRECIP_LON", ex.Message);
    }
}
