using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Persistence;
using TestSupport;
using Worker.Nws;

namespace Worker.Tests.Nws;

/// <summary>
/// The gridpoint cache against a real Postgres (the migrated schema) and a stubbed NWS
/// that counts requests per URL, so "cached" means "zero /points calls", not "same answer".
/// </summary>
public sealed class DbGridpointCacheTests(PostgresFixture postgres)
{
    private const string Dir = "nws/2026-10-29T1900Z_pre-dst-fallback/";
    private const string PointsUrl = "https://api.weather.gov/points/40.7128,-74.006";
    private const string HourlyUrl = "https://api.weather.gov/gridpoints/OKX/33,42/forecast/hourly";
    private const string GridUrl = "https://api.weather.gov/gridpoints/OKX/33,42";

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 3, 15, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Routes by exact URL, counts every request per URL; anything unrouted is a 404.</summary>
    private sealed class CountingHandler(Dictionary<string, Func<HttpResponseMessage>> routes) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _counts = [];

        public int Count(string url) => _counts.GetValueOrDefault(url);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            _counts[url] = Count(url) + 1;
            return Task.FromResult(routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static Func<HttpResponseMessage> Json(string body) =>
        () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private static CountingHandler Handler(Func<HttpResponseMessage>? hourly = null) =>
        new(new()
        {
            [PointsUrl] = Json(FixtureFiles.Read(Dir + "points.json")),
            [HourlyUrl] = hourly ?? Json(FixtureFiles.Read(Dir + "forecast-hourly.json")),
            [GridUrl] = Json(FixtureFiles.Read(Dir + "gridpoint.json")),
        });

    private static NwsClient Client(CountingHandler handler) => new(new HttpClient(handler));

    private static DbGridpointCache Cache(PrecipDbContext db, CountingHandler handler) =>
        new(db, Client(handler), new FixedClock(Now));

    /// <summary>A fresh migrated database holding one never-resolved location; returns its connection string and id.</summary>
    private async Task<(string ConnectionString, int LocationId)> SeedAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = new PrecipDbContext(PostgresFixture.Options(connectionString));
        var location = new Location
        {
            Name = "test",
            Latitude = 40.7128m,
            Longitude = -74.0060m,
            CreatedAt = Now,
        };
        db.Locations.Add(location);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (connectionString, location.Id);
    }

    private static async Task<Location> LoadAsync(PrecipDbContext db, int id) =>
        await db.Locations.SingleAsync(l => l.Id == id, TestContext.Current.CancellationToken);

    [Fact]
    public async Task First_get_calls_points_once_and_persists_the_columns()
    {
        var (cs, id) = await SeedAsync();
        var handler = Handler();

        await using (var db = new PrecipDbContext(PostgresFixture.Options(cs)))
        {
            var point = await Cache(db, handler).GetAsync(await LoadAsync(db, id), TestContext.Current.CancellationToken);

            Assert.Equal(new NwsPoint("America/New_York", HourlyUrl, GridUrl), point);
        }

        Assert.Equal(1, handler.Count(PointsUrl));

        await using var verify = new PrecipDbContext(PostgresFixture.Options(cs));
        var stored = await LoadAsync(verify, id);
        Assert.Equal("America/New_York", stored.TimeZoneId);
        Assert.Equal(HourlyUrl, stored.ForecastHourlyUrl);
        Assert.Equal(GridUrl, stored.ForecastGridDataUrl);
        Assert.Equal(Now, stored.GridpointResolvedAt);
    }

    [Fact]
    public async Task Second_get_with_a_fresh_context_makes_no_points_call()
    {
        var (cs, id) = await SeedAsync();
        var handler = Handler();

        await using (var db = new PrecipDbContext(PostgresFixture.Options(cs)))
            await Cache(db, handler).GetAsync(await LoadAsync(db, id), TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Count(PointsUrl));

        await using var fresh = new PrecipDbContext(PostgresFixture.Options(cs));
        var point = await Cache(fresh, handler).GetAsync(await LoadAsync(fresh, id), TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Count(PointsUrl));
        Assert.Equal(new NwsPoint("America/New_York", HourlyUrl, GridUrl), point);
    }

    [Fact]
    public async Task Refresh_overwrites_a_stale_url()
    {
        var (cs, id) = await SeedAsync();
        var handler = Handler();
        var later = Now.AddDays(30);

        await using (var db = new PrecipDbContext(PostgresFixture.Options(cs)))
        {
            var location = await LoadAsync(db, id);
            location.TimeZoneId = "America/Chicago";
            location.ForecastHourlyUrl = "https://api.weather.gov/gridpoints/OLD/1,1/forecast/hourly";
            location.ForecastGridDataUrl = "https://api.weather.gov/gridpoints/OLD/1,1";
            location.GridpointResolvedAt = Now;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = new PrecipDbContext(PostgresFixture.Options(cs)))
        {
            var cache = new DbGridpointCache(db, Client(handler), new FixedClock(later));
            var point = await cache.RefreshAsync(await LoadAsync(db, id), TestContext.Current.CancellationToken);

            Assert.Equal(HourlyUrl, point.ForecastHourlyUrl);
        }

        Assert.Equal(1, handler.Count(PointsUrl));

        await using var verify = new PrecipDbContext(PostgresFixture.Options(cs));
        var stored = await LoadAsync(verify, id);
        Assert.Equal("America/New_York", stored.TimeZoneId);
        Assert.Equal(HourlyUrl, stored.ForecastHourlyUrl);
        Assert.Equal(GridUrl, stored.ForecastGridDataUrl);
        Assert.Equal(later, stored.GridpointResolvedAt);
    }

    [Fact]
    public async Task Refresh_works_for_a_location_loaded_by_another_context()
    {
        var (cs, id) = await SeedAsync();
        var handler = Handler();

        Location detached;
        await using (var other = new PrecipDbContext(PostgresFixture.Options(cs)))
            detached = await LoadAsync(other, id);

        await using (var db = new PrecipDbContext(PostgresFixture.Options(cs)))
            await Cache(db, handler).RefreshAsync(detached, TestContext.Current.CancellationToken);

        await using var verify = new PrecipDbContext(PostgresFixture.Options(cs));
        Assert.Equal(HourlyUrl, (await LoadAsync(verify, id)).ForecastHourlyUrl);
    }

    [Fact]
    public async Task Hourly_404_on_a_resolved_point_throws_stale()
    {
        var (cs, id) = await SeedAsync();
        var handler = Handler(hourly: () => new HttpResponseMessage(HttpStatusCode.NotFound));

        await using var db = new PrecipDbContext(PostgresFixture.Options(cs));
        var point = await Cache(db, handler).GetAsync(await LoadAsync(db, id), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<NwsPointStaleException>(
            () => Client(handler).GetForecastAsync(point, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hourly_500_on_a_resolved_point_is_an_http_error_not_stale()
    {
        var (cs, id) = await SeedAsync();
        var handler = Handler(hourly: () => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await using var db = new PrecipDbContext(PostgresFixture.Options(cs));
        var point = await Cache(db, handler).GetAsync(await LoadAsync(db, id), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(handler).GetForecastAsync(point, TestContext.Current.CancellationToken));
        Assert.IsNotType<NwsPointStaleException>(ex);
    }
}

public class NwsCoordinatesTests
{
    [Theory]
    [InlineData(40.7128, -74.0060, "40.7128,-74.006")]
    [InlineData(40.5, -74, "40.5,-74")]
    [InlineData(0.1234, 179.9999, "0.1234,179.9999")]
    public void Formats_with_at_most_four_decimals(double lat, double lon, string expected) =>
        Assert.Equal(expected, NwsCoordinates.Format((decimal)lat, (decimal)lon));

    [Fact]
    public void Ignores_the_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("40.7128,-74.006", NwsCoordinates.Format(40.7128m, -74.0060m));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
