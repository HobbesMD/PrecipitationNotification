using System.Net;
using Worker.Nws;

namespace Worker.Tests;

public class NwsClientTests
{
    private const string Dir = "nws/2026-10-29T1900Z_pre-dst-fallback/";
    private const string PointsUrl = "https://api.weather.gov/points/40.7128,-74.0060";
    private const string HourlyUrl = "https://api.weather.gov/gridpoints/OKX/33,42/forecast/hourly";
    private const string GridUrl = "https://api.weather.gov/gridpoints/OKX/33,42";

    /// <summary>Routes by exact URL to canned responses; anything unrouted is a 404.</summary>
    private sealed class StubHandler(Dictionary<string, Func<HttpResponseMessage>> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var url = request.RequestUri!.ToString();
            return Task.FromResult(routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static Func<HttpResponseMessage> Json(string body) =>
        () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private static Func<HttpResponseMessage> Status(HttpStatusCode status) =>
        () => new HttpResponseMessage(status);

    private static NwsClient Client(
        Func<HttpResponseMessage>? points = null,
        Func<HttpResponseMessage>? hourly = null,
        Func<HttpResponseMessage>? grid = null) =>
        new(new HttpClient(new StubHandler(new()
        {
            [PointsUrl] = points ?? Json(FixtureFiles.Read(Dir + "points.json")),
            [HourlyUrl] = hourly ?? Json(FixtureFiles.Read(Dir + "forecast-hourly.json")),
            [GridUrl] = grid ?? Json(FixtureFiles.Read(Dir + "gridpoint.json")),
        })));

    private static Task<NwsForecast> Get(NwsClient client, CancellationToken? ct = null) =>
        client.GetForecastAsync("40.7128,-74.0060", ct ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task All_ok_returns_hours_and_amounts()
    {
        var forecast = await Get(Client());

        Assert.Equal(156, forecast.Hours.Count);
        Assert.NotNull(forecast.Amounts);
        Assert.Null(forecast.AmountsUnavailableReason);
    }

    [Fact]
    public async Task Gridpoint_500_degrades_to_no_amounts_but_keeps_hours()
    {
        var forecast = await Get(Client(grid: Status(HttpStatusCode.InternalServerError)));

        Assert.Equal(156, forecast.Hours.Count);
        Assert.Null(forecast.Amounts);
        Assert.Contains("500", forecast.AmountsUnavailableReason);
    }

    [Fact]
    public async Task Gridpoint_invalid_json_degrades_to_no_amounts()
    {
        var forecast = await Get(Client(grid: Json("{ not json")));

        Assert.Equal(156, forecast.Hours.Count);
        Assert.Null(forecast.Amounts);
        Assert.NotNull(forecast.AmountsUnavailableReason);
    }

    [Fact]
    public async Task Gridpoint_json_null_body_degrades_to_no_amounts()
    {
        var forecast = await Get(Client(grid: Json("null")));

        Assert.Null(forecast.Amounts);
        Assert.Contains("empty body", forecast.AmountsUnavailableReason);
    }

    [Fact]
    public async Task Points_without_grid_url_degrades_to_no_amounts()
    {
        var points = FixtureFiles.Read(Dir + "points.json")
            .Replace("\"forecastGridData\"", "\"forecastGridDataRenamed\"");

        var forecast = await Get(Client(points: Json(points)));

        Assert.Equal(156, forecast.Hours.Count);
        Assert.Null(forecast.Amounts);
        Assert.Contains("forecastGridData", forecast.AmountsUnavailableReason);
    }

    [Fact]
    public async Task Hourly_500_throws() =>
        await Assert.ThrowsAsync<HttpRequestException>(() => Get(Client(hourly: Status(HttpStatusCode.InternalServerError))));

    [Fact]
    public async Task Points_500_throws() =>
        await Assert.ThrowsAsync<HttpRequestException>(() => Get(Client(points: Status(HttpStatusCode.InternalServerError))));

    [Fact]
    public async Task Cancelled_token_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(Client(), cts.Token));
    }
}
