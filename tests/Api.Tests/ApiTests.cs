using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Persistence;
using TestSupport;

namespace Api.Tests;

/// <summary>
/// The admin API end to end: the real host (WebApplicationFactory) against real Postgres
/// (a fresh migrated database per test). Coordinates used here are made-up values, never the
/// owner's (D21).
/// </summary>
public sealed class ApiTests(PostgresFixture postgres)
{
    private const string ValidLocation = """
        {"name":"test-home","latitude":40.7128,"longitude":-74.0060,
         "rule":{"probabilityThreshold":50,"windowStart":"20:00","windowEnd":"07:00"}}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<(ApiFactory Factory, HttpClient Client, string ConnectionString)> StartAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var factory = new ApiFactory(ApiFactory.ValidKey, connectionString);
        return (factory, factory.CreateApiClient(), connectionString);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    private static async Task<int> CreateAsync(HttpClient client, string json = ValidLocation)
    {
        var response = await client.PostAsync("/locations", Json(json), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("id").GetInt32();
    }

    // ---- auth ----

    [Fact]
    public async Task Missing_key_is_401_with_no_body()
    {
        var (factory, _, _) = await StartAsync();
        await using var _f = factory;
        var client = factory.CreateApiClient(key: null);

        var response = await client.GetAsync("/locations", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("test-api-key-0123456789-abcdefghij-XYZ ")] // trailing space
    [InlineData("TEST-API-KEY-0123456789-ABCDEFGHIJ-XYZ")] // case matters
    public async Task Wrong_key_is_401_with_no_body(string key)
    {
        var (factory, _, _) = await StartAsync();
        await using var _f = factory;
        var client = factory.CreateApiClient(key);

        var response = await client.GetAsync("/locations", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Writes_without_a_key_are_401_even_when_the_body_is_garbage()
    {
        var (factory, _, _) = await StartAsync();
        await using var _f = factory;
        var client = factory.CreateApiClient(key: null);

        var response = await client.PostAsync("/locations", Json("{not json"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_open()
    {
        var (factory, _, _) = await StartAsync();
        await using var _f = factory;
        var client = factory.CreateApiClient(key: null);

        var response = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- create / read ----

    [Fact]
    public async Task Create_then_get_round_trips_coordinates_exactly()
    {
        var (factory, client, _) = await StartAsync();
        await using var _f = factory;

        var created = await client.PostAsync("/locations", Json(ValidLocation), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt32();
        Assert.Equal($"/locations/{id}", created.Headers.Location?.OriginalString);

        var location = await ReadJsonAsync(await client.GetAsync($"/locations/{id}", Ct));
        Assert.Equal(40.7128m, location.GetProperty("latitude").GetDecimal());
        Assert.Equal(-74.0060m, location.GetProperty("longitude").GetDecimal());
        Assert.Equal("test-home", location.GetProperty("name").GetString());
        Assert.True(location.GetProperty("isActive").GetBoolean());
        var rule = location.GetProperty("rule");
        Assert.Equal(50, rule.GetProperty("probabilityThreshold").GetInt32());
        Assert.Equal("20:00", rule.GetProperty("windowStart").GetString());
        Assert.Equal("07:00", rule.GetProperty("windowEnd").GetString());
    }

    [Fact]
    public async Task List_returns_every_location_with_its_rule()
    {
        var (factory, client, _) = await StartAsync();
        await using var _f = factory;
        await CreateAsync(client);
        await CreateAsync(client, ValidLocation.Replace("test-home", "second"));

        var list = await ReadJsonAsync(await client.GetAsync("/locations", Ct));

        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("test-home", list[0].GetProperty("name").GetString());
        Assert.Equal(50, list[1].GetProperty("rule").GetProperty("probabilityThreshold").GetInt32());
    }

    [Fact]
    public async Task Unknown_location_is_404()
    {
        var (factory, client, _) = await StartAsync();
        await using var _f = factory;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/locations/999", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/locations/999", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/locations/999/alerts", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsync("/locations/999/rule", Json("""{"probabilityThreshold":10,"windowStart":"20:00","windowEnd":"07:00"}"""), Ct)).StatusCode);
    }

    // ---- validation ----

    [Fact]
    public async Task Five_decimal_latitude_is_400_and_the_response_never_echoes_it()
    {
        var (factory, client, _) = await StartAsync();
        await using var _f = factory;

        var response = await client.PostAsync("/locations", Json(ValidLocation.Replace("40.7128", "40.71284")), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("latitude", body);
        Assert.DoesNotContain("71284", body);
        // And it was rejected, not rounded to a different location.
        Assert.Equal(0, (await ReadJsonAsync(await client.GetAsync("/locations", Ct))).GetArrayLength());
    }

    [Theory]
    [InlineData("""{"name":"a","latitude":91,"longitude":-74.0060,"rule":{"probabilityThreshold":50,"windowStart":"20:00","windowEnd":"07:00"}}""", "latitude", "91")]
    [InlineData("""{"name":"a","latitude":40.7128,"longitude":-181,"rule":{"probabilityThreshold":50,"windowStart":"20:00","windowEnd":"07:00"}}""", "longitude", "181")]
    [InlineData("""{"name":"a","latitude":40.7128,"longitude":-74.0060,"rule":{"probabilityThreshold":101,"windowStart":"20:00","windowEnd":"07:00"}}""", "rule.probabilityThreshold", null)]
    [InlineData("""{"name":"a","latitude":40.7128,"longitude":-74.0060,"rule":{"probabilityThreshold":50,"windowStart":"07:00","windowEnd":"20:00"}}""", "rule.windowStart", null)]
    [InlineData("""{"name":"a","latitude":40.7128,"longitude":-74.0060,"rule":{"probabilityThreshold":50,"windowStart":"8pm","windowEnd":"07:00"}}""", "rule.windowStart", null)]
    [InlineData("""{"name":"","latitude":40.7128,"longitude":-74.0060,"rule":{"probabilityThreshold":50,"windowStart":"20:00","windowEnd":"07:00"}}""", "name", null)]
    [InlineData("""{"name":"a","longitude":-74.0060,"rule":{"probabilityThreshold":50,"windowStart":"20:00","windowEnd":"07:00"}}""", "latitude", null)]
    [InlineData("""{"name":"a","latitude":40.7128,"longitude":-74.0060}""", "rule", null)]
    public async Task Invalid_create_is_400_naming_the_field(string json, string field, string? mustNotEcho)
    {
        var (factory, client, _) = await StartAsync();
        await using var _f = factory;

        var response = await client.PostAsync("/locations", Json(json), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var errors = JsonDocument.Parse(body).RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out var messages), $"expected an error for '{field}' in: {body}");
        // Checked against the field's message, not the whole body: "91" also occurs in the RFC link.
        if (mustNotEcho is not null) Assert.DoesNotContain(mustNotEcho, messages.ToString());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"name":"a","latitude":"north","longitude":1,"rule":{}}""")]
    [InlineData("null")]
    [InlineData("")]
    public async Task Malformed_body_is_400_and_never_echoed(string json)
    {
        var (factory, client, _) = await StartAsync();
        await using var _f = factory;

        var response = await client.PostAsync("/locations", Json(json), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (json.Length > 0) Assert.DoesNotContain(json, body);
    }

    [Fact]
    public async Task Non_json_content_type_is_415()
    {
        var (factory, client, _) = await StartAsync();
        await using var _f = factory;

        var response = await client.PostAsync("/locations", new StringContent(ValidLocation, Encoding.UTF8, "text/plain"), Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_name_is_409()
    {
        var (factory, client, _) = await StartAsync();
        await using var _f = factory;
        await CreateAsync(client);

        var response = await client.PostAsync("/locations", Json(ValidLocation), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public void Postgres_check_violation_maps_to_400_and_unique_violation_to_409()
    {
        // The backstop for a constraint the code-side validation missed. Message text is a
        // secret-looking value on purpose: the mapped response must not carry it.
        static Exception Pg(string state) => new DbUpdateException("save failed",
            new PostgresException("value 12.34567 rejected", "ERROR", "ERROR", state));

        Assert.Equal(400, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)ApiErrors.FromDatabase(Pg("23514"))!).StatusCode);
        Assert.Equal(409, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)ApiErrors.FromDatabase(Pg("23505"))!).StatusCode);
        Assert.Null(ApiErrors.FromDatabase(Pg("42P01")));
        Assert.Null(ApiErrors.FromDatabase(new InvalidOperationException()));
    }

    // ---- rule update ----

    [Fact]
    public async Task Put_rule_replaces_the_rule_and_validates()
    {
        var (factory, client, _) = await StartAsync();
        await using var _f = factory;
        var id = await CreateAsync(client);

        var ok = await client.PutAsync($"/locations/{id}/rule",
            Json("""{"probabilityThreshold":30,"windowStart":"21:30","windowEnd":"06:15"}"""), Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var rule = (await ReadJsonAsync(await client.GetAsync($"/locations/{id}", Ct))).GetProperty("rule");
        Assert.Equal(30, rule.GetProperty("probabilityThreshold").GetInt32());
        Assert.Equal("21:30", rule.GetProperty("windowStart").GetString());
        Assert.Equal("06:15", rule.GetProperty("windowEnd").GetString());

        var bad = await client.PutAsync($"/locations/{id}/rule",
            Json("""{"probabilityThreshold":30,"windowStart":"05:00","windowEnd":"06:00"}"""), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.True((await ReadJsonAsync(bad)).GetProperty("errors").TryGetProperty("windowStart", out _));
    }

    [Fact]
    public async Task No_endpoint_changes_coordinates()
    {
        var (factory, client, _) = await StartAsync();
        await using var _f = factory;
        var id = await CreateAsync(client);

        // Coordinates smuggled into the rule body are ignored, and there is no other write route.
        await client.PutAsync($"/locations/{id}/rule",
            Json("""{"probabilityThreshold":30,"windowStart":"21:30","windowEnd":"06:15","latitude":1.0,"longitude":2.0}"""), Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PutAsync($"/locations/{id}", Json(ValidLocation), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PatchAsync($"/locations/{id}", Json(ValidLocation), Ct)).StatusCode);

        var location = await ReadJsonAsync(await client.GetAsync($"/locations/{id}", Ct));
        Assert.Equal(40.7128m, location.GetProperty("latitude").GetDecimal());
        Assert.Equal(-74.0060m, location.GetProperty("longitude").GetDecimal());
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_deactivates_and_keeps_the_row_and_its_history()
    {
        var (factory, client, connectionString) = await StartAsync();
        await using var _f = factory;
        var id = await CreateAsync(client);
        await SeedHistoryAsync(connectionString, id, 3);

        var response = await client.DeleteAsync($"/locations/{id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/locations/{id}", Ct)).StatusCode); // idempotent

        var location = await ReadJsonAsync(await client.GetAsync($"/locations/{id}", Ct));
        Assert.False(location.GetProperty("isActive").GetBoolean());
        Assert.Equal(40.7128m, location.GetProperty("latitude").GetDecimal());
        Assert.Equal(3, (await ReadJsonAsync(await client.GetAsync($"/locations/{id}/alerts", Ct))).GetArrayLength());
    }

    // ---- alerts ----

    [Fact]
    public async Task Alerts_are_newest_night_first_and_honor_limit()
    {
        var (factory, client, connectionString) = await StartAsync();
        await using var _f = factory;
        var id = await CreateAsync(client);
        await SeedHistoryAsync(connectionString, id, 10);

        var all = await ReadJsonAsync(await client.GetAsync($"/locations/{id}/alerts", Ct));
        Assert.Equal(10, all.GetArrayLength());
        var nights = all.EnumerateArray().Select(a => a.GetProperty("nightDate").GetString()).ToList();
        Assert.Equal(nights.OrderByDescending(n => n, StringComparer.Ordinal), nights);
        Assert.Equal(SeedStart.AddDays(9).ToString("yyyy-MM-dd"), nights[0]);

        var limited = await ReadJsonAsync(await client.GetAsync($"/locations/{id}/alerts?limit=3", Ct));
        Assert.Equal(3, limited.GetArrayLength());
        Assert.Equal(nights[0], limited[0].GetProperty("nightDate").GetString());
        Assert.Equal("Sent", limited[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Alerts_default_to_50_and_cap_at_500()
    {
        var (factory, client, connectionString) = await StartAsync();
        await using var _f = factory;
        var id = await CreateAsync(client);
        await SeedHistoryAsync(connectionString, id, 600);

        Assert.Equal(50, (await ReadJsonAsync(await client.GetAsync($"/locations/{id}/alerts", Ct))).GetArrayLength());
        Assert.Equal(500, (await ReadJsonAsync(await client.GetAsync($"/locations/{id}/alerts?limit=500", Ct))).GetArrayLength());
        var capped = await ReadJsonAsync(await client.GetAsync($"/locations/{id}/alerts?limit=100000", Ct));
        Assert.Equal(500, capped.GetArrayLength());
        Assert.Equal(SeedStart.AddDays(599).ToString("yyyy-MM-dd"), capped[0].GetProperty("nightDate").GetString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task Bad_limit_is_400(string limit)
    {
        var (factory, client, _) = await StartAsync();
        await using var _f = factory;
        var id = await CreateAsync(client);

        var response = await client.GetAsync($"/locations/{id}/alerts?limit={limit}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Alerts_of_one_location_do_not_include_anothers()
    {
        var (factory, client, connectionString) = await StartAsync();
        await using var _f = factory;
        var first = await CreateAsync(client);
        var second = await CreateAsync(client, ValidLocation.Replace("test-home", "other"));
        await SeedHistoryAsync(connectionString, first, 4);
        await SeedHistoryAsync(connectionString, second, 2);

        Assert.Equal(4, (await ReadJsonAsync(await client.GetAsync($"/locations/{first}/alerts", Ct))).GetArrayLength());
        Assert.Equal(2, (await ReadJsonAsync(await client.GetAsync($"/locations/{second}/alerts", Ct))).GetArrayLength());
    }

    // ---- startup ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234")] // 31 chars
    public void Host_refuses_to_start_without_a_usable_key(string? key)
    {
        using var factory = new ApiFactory(key, "Host=localhost;Database=unused");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var text = AllMessages(ex);
        Assert.Contains("PRECIP_API_KEY", text);
        if (key is { Length: > 0 }) Assert.DoesNotContain(key, text);
    }

    [Fact]
    public async Task Host_accepts_a_32_char_key()
    {
        using var factory = new ApiFactory(new string('k', 32), "Host=localhost;Database=unused");

        using var client = factory.CreateClient(); // no database touched: /health only
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
    }

    [Fact]
    public void Host_refuses_to_start_without_a_connection_string()
    {
        using var factory = new ApiFactory(ApiFactory.ValidKey, null);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var text = AllMessages(ex);
        Assert.Contains("ConnectionStrings__Precip", text);
        Assert.DoesNotContain(ApiFactory.ValidKey, text);
    }

    // ---- helpers ----

    private static readonly DateOnly SeedStart = new(2025, 1, 1);

    /// <summary>Inserts <paramref name="count"/> consecutive nights directly, bypassing the API.</summary>
    private static async Task SeedHistoryAsync(string connectionString, int locationId, int count)
    {
        var now = new DateTimeOffset(2026, 10, 29, 23, 0, 0, TimeSpan.Zero);
        await using var db = new PrecipDbContext(PostgresFixture.Options(connectionString));
        db.AlertHistory.AddRange(Enumerable.Range(0, count).Select(i => new AlertHistory
        {
            LocationId = locationId,
            NightDate = SeedStart.AddDays(i),
            Status = AlertStatus.Sent,
            Attempts = 1,
            CreatedAt = now,
            LastAttemptAt = now,
            SentAt = now,
            PeakProbability = 80,
            Kind = "Rain",
            Title = "Rain tonight",
        }));
        await db.SaveChangesAsync(Ct);
    }

    private static string AllMessages(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException) sb.AppendLine(e.Message);
        return sb.ToString();
    }
}
