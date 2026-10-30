using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Worker.Forecast;
using Worker.OpenMeteo;

namespace Worker.Tests.OpenMeteo;

public sealed class OpenMeteoClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Dispose();

    private OpenMeteoClient Client() => new(new HttpClient(), new OpenMeteoOptions(_server.Url + "/"));

    private void Serve(int status, string body) =>
        _server.Given(Request.Create().WithPath("/v1/forecast").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    [Fact]
    public async Task Serves_fixture_and_requests_unixtime_and_auto_timezone()
    {
        Serve(200, FixtureFiles.Read("open-meteo/2026-10-30T1417Z_pre-dst-fallback/forecast.json"));

        var forecast = await Client().GetForecastAsync(40.7128m, -74.0060m, TestContext.Current.CancellationToken);

        Assert.Equal(96, forecast.Hours.Count);
        Assert.Equal(Providers.OpenMeteo, forecast.Provider);
        Assert.Equal("America/New_York", forecast.TimeZoneId);

        var request = Assert.Single(_server.LogEntries).RequestMessage;
        var query = request!.AbsoluteUrl;
        Assert.Contains("timeformat=unixtime", query);
        Assert.Contains("timezone=auto", query);
        Assert.Contains("forecast_days=4", query);
        Assert.Contains("hourly=precipitation_probability,precipitation,snowfall,weather_code,temperature_2m", query);
        Assert.Contains("temperature_unit=fahrenheit", query);
        Assert.Contains("latitude=40.7128", query);
        Assert.Contains("longitude=-74.006&", query); // at most 4 decimals, invariant, no trailing zeros
    }

    [Fact]
    public async Task Coordinates_are_rounded_to_four_decimals()
    {
        Serve(200, FixtureFiles.Read("open-meteo/2026-10-30T1417Z_pre-dst-fallback/forecast.json"));

        await Client().GetForecastAsync(40.712849m, -74.006051m, TestContext.Current.CancellationToken);

        var url = Assert.Single(_server.LogEntries).RequestMessage!.AbsoluteUrl;
        Assert.Contains("latitude=40.7128&", url);
        Assert.Contains("longitude=-74.0061&", url);
    }

    [Fact]
    public async Task Server_error_throws_HttpRequestException_without_url_or_coordinates()
    {
        Serve(500, "boom");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Client().GetForecastAsync(40.7128m, -74.0060m, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("40.7128", ex.Message);
        Assert.DoesNotContain("74.006", ex.Message);
        Assert.DoesNotContain("latitude", ex.Message);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    public async Task Bad_or_empty_body_throws_InvalidOperationException_without_coordinates(string body)
    {
        Serve(200, body);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client().GetForecastAsync(40.7128m, -74.0060m, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("40.7128", ex.Message);
        Assert.DoesNotContain("latitude", ex.Message);
    }
}
