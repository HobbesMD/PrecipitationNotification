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
}
