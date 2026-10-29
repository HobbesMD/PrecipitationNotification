namespace Worker.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Captured_fixture_is_copied_to_output() =>
        Assert.Contains("forecastHourly", FixtureFiles.Read("nws/2026-10-29T1900Z_pre-dst-fallback/points.json"));
}
