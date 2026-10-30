using Microsoft.EntityFrameworkCore;
using TestSupport;
using Worker.Nws;
using Worker.Pass;

namespace Worker.Tests.Pass;

/// <summary>
/// <see cref="NightlyPass"/> over a real migrated Postgres (locations and rules are read from it)
/// with the ledger, gridpoint cache and forecast source faked. The forecasts are the captured
/// 2026-10-29 payloads; the clock is fixed at 17:00 EDT that day.
/// </summary>
public sealed class NightlyPassTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset NightStart = new(2026, 10, 29, 20, 0, 0, TimeSpan.FromHours(-4));

    private Task<PassHarness> NewHarness() => PassHarness.CreateAsync(postgres);

    // ---- Exit codes ----

    [Fact]
    public async Task Alerting_night_sends_once_with_the_windows_night_date_and_returns_0()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha", threshold: 0);

        var code = await h.RunAsync();

        Assert.Equal(0, code);
        var sent = Assert.Single(h.Ledger.SendCalls);
        Assert.Equal(id, sent.LocationId);
        Assert.Equal(PassHarness.Night, sent.Night);
        Assert.Contains("Sent notification", h.Logs.AllText);
    }

    [Fact]
    public async Task Run_after_midnight_keys_on_the_night_the_window_started()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0);
        h.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 30, 6, 0, 0, TimeSpan.Zero)); // 02:00 EDT

        Assert.Equal(0, await h.RunAsync());

        Assert.Equal(PassHarness.Night, Assert.Single(h.Ledger.SendCalls).Night);
    }

    [Fact]
    public async Task Dry_night_returns_0_and_sends_nothing()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 50);

        Assert.Equal(0, await h.RunAsync());

        Assert.Empty(h.Ledger.SendCalls);
        Assert.Contains("No notification sent", h.Logs.AllText);
    }

    [Fact]
    public async Task No_active_locations_returns_1()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("inactive", active: false);

        Assert.Equal(1, await h.RunAsync());

        Assert.Contains("No active locations", h.Logs.AllText);
        Assert.Empty(h.Cache.GetCalls);
    }

    [Fact]
    public async Task Location_without_a_rule_fails_the_run_but_not_the_others()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("ruleless", withRule: false);
        await h.AddLocationAsync("fine", threshold: 0);

        Assert.Equal(1, await h.RunAsync());

        Assert.Single(h.Ledger.SendCalls);
        Assert.Contains("has no alert rule", h.Logs.AllText);
    }

    [Fact]
    public async Task Forecast_fetch_failure_returns_2()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha");
        h.Forecasts.Handler = _ => throw new HttpRequestException("nws down");

        Assert.Equal(2, await h.RunAsync());

        Assert.Empty(h.Ledger.SendCalls);
        Assert.Contains("NWS fetch failed", h.Logs.AllText);
    }

    [Fact]
    public async Task Point_lookup_failure_returns_2()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha");
        h.Cache.GetFailures[id] = new HttpRequestException("points down");

        Assert.Equal(2, await h.RunAsync());

        Assert.Empty(h.Forecasts.Calls);
    }

    [Fact]
    public async Task Unknown_time_zone_counts_as_an_nws_failure()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha");
        h.Cache.TimeZoneId = "Mars/Olympus_Mons";

        Assert.Equal(2, await h.RunAsync());

        Assert.Empty(h.Ledger.SendCalls);
    }

    [Fact]
    public async Task Publish_failure_returns_3()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha");
        h.Ledger.Publish = (_, _, _) => throw new HttpRequestException("ntfy down");

        Assert.Equal(3, await h.RunAsync());

        Assert.Empty(h.Ledger.Sent);
        Assert.Contains("publish failed", h.Logs.AllText);
    }

    [Fact]
    public async Task Gap_with_no_hits_returns_4_instead_of_a_dry_verdict()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 50);
        // Forecast ends at 02:00: the rest of the 20:00-07:00 window is unknown, and nothing before it hit.
        h.Forecasts.Handler = _ => FixtureForecast.Truncated(NightStart.AddHours(6));

        Assert.Equal(4, await h.RunAsync());

        Assert.Empty(h.Ledger.SendCalls);
        Assert.Contains("incomplete forecast", h.Logs.AllText);
    }

    [Fact]
    public async Task Gap_with_hits_still_alerts_and_warns_about_the_gap()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0);
        h.Forecasts.Handler = _ => FixtureForecast.Truncated(NightStart.AddHours(6));

        Assert.Equal(0, await h.RunAsync());

        Assert.Single(h.Ledger.SendCalls);
        Assert.Contains("No hourly data", h.Logs.AllText);
    }

    [Fact]
    public async Task Pending_migrations_return_6_before_anything_else_is_touched()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha");
        // Forget that the schema was ever migrated.
        await h.Db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\"");

        Assert.Equal(6, await h.RunAsync());

        Assert.Contains("pending migration", h.Logs.AllText);
        Assert.Empty(h.Cache.GetCalls);
        Assert.Equal(0, h.Ledger.TotalCalls);
    }

    [Fact]
    public async Task Database_with_no_schema_at_all_returns_6()
    {
        var cs = await EmptyDatabase.CreateAsync(postgres);
        await using var db = new Persistence.PrecipDbContext(
            Persistence.PrecipDbContext.Configure(new DbContextOptionsBuilder<Persistence.PrecipDbContext>(), cs).Options);
        await using var h = PassHarness.Over(db);

        Assert.Equal(6, await h.RunAsync());
    }

    [Fact]
    public async Task Unreachable_database_returns_6_without_echoing_the_connection_string()
    {
        await using var h = PassHarness.Over(DeadDatabase.CreateContext());

        Assert.Equal(6, await h.RunAsync());

        Assert.Contains("Database unavailable", h.Logs.AllText);
        Assert.DoesNotContain(DeadDatabase.Password, h.Logs.AllText);
    }

    // ---- Isolation ----

    [Fact]
    public async Task Failing_first_location_does_not_stop_the_second_and_its_code_is_returned()
    {
        await using var h = await NewHarness();
        var first = await h.AddLocationAsync("first");
        var second = await h.AddLocationAsync("second");
        h.Cache.GetFailures[first] = new HttpRequestException("points down");

        Assert.Equal(2, await h.RunAsync());

        var sent = Assert.Single(h.Ledger.SendCalls);
        Assert.Equal(second, sent.LocationId);
    }

    [Fact]
    public async Task First_failing_location_in_id_order_decides_the_code()
    {
        await using var h = await NewHarness();
        var ok = await h.AddLocationAsync("ok");
        var publishFails = await h.AddLocationAsync("publish-fails");
        var nwsFails = await h.AddLocationAsync("nws-fails");
        h.Cache.GetFailures[nwsFails] = new HttpRequestException("points down");
        h.Ledger.Publish = (id, _, _) =>
            id == publishFails ? throw new HttpRequestException("ntfy down") : Task.CompletedTask;

        // Location 2 fails with 3 before location 3 fails with 2.
        Assert.Equal(3, await h.RunAsync());

        Assert.Equal([ok, publishFails, nwsFails], h.Cache.GetCalls);
    }

    [Fact]
    public async Task Inactive_locations_are_skipped_and_the_rest_run_in_id_order()
    {
        await using var h = await NewHarness();
        var a = await h.AddLocationAsync("a");
        await h.AddLocationAsync("inactive", active: false);
        var c = await h.AddLocationAsync("c");

        Assert.Equal(0, await h.RunAsync());

        Assert.Equal([a, c], h.Cache.GetCalls);
    }

    // ---- Idempotency, staleness, dry run ----

    [Fact]
    public async Task Already_sent_night_makes_no_forecast_call_and_counts_as_ok()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha");
        h.Ledger.Sent.Add((id, PassHarness.Night));

        Assert.Equal(0, await h.RunAsync());

        Assert.Empty(h.Forecasts.Calls);
        Assert.Empty(h.Ledger.SendCalls);
        Assert.Contains("already sent for night 2026-10-29", h.Logs.AllText);
    }

    [Fact]
    public async Task Ledger_saying_already_sent_at_send_time_is_logged_and_is_not_a_failure()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha");
        // Lost the race: another run recorded the night between our check and our send.
        h.Forecasts.Handler = call =>
        {
            h.Ledger.Sent.Add((call.LocationId, PassHarness.Night));
            return FixtureForecast.Full();
        };

        Assert.Equal(0, await h.RunAsync());

        Assert.Single(h.Ledger.SendCalls);
        Assert.Contains("AlreadySent", h.Logs.AllText);
        Assert.Equal(id, h.Ledger.SendCalls[0].LocationId);
    }

    [Fact]
    public async Task Stale_point_is_refreshed_exactly_once_and_the_fetch_retried_once()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha");
        h.Forecasts.Handler = call => call.PointVersion == 1
            ? throw new NwsPointStaleException("gridpoint moved")
            : FixtureForecast.Full();

        Assert.Equal(0, await h.RunAsync());

        Assert.Equal([id], h.Cache.RefreshCalls);
        Assert.Equal([new FetchCall(id, 1, 1), new FetchCall(id, 2, 2)], h.Forecasts.Calls);
        Assert.Single(h.Ledger.SendCalls);
    }

    [Fact]
    public async Task Still_stale_after_one_refresh_fails_the_location_with_2_and_does_not_loop()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha");
        h.Forecasts.Handler = _ => throw new NwsPointStaleException("gridpoint moved");

        Assert.Equal(2, await h.RunAsync());

        Assert.Equal([id], h.Cache.RefreshCalls);
        Assert.Equal(2, h.Forecasts.Calls.Count);
        Assert.Empty(h.Ledger.SendCalls);
    }

    [Fact]
    public async Task Dry_run_never_touches_the_ledger_and_prints_the_message()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0);
        // Even a night the ledger says was sent: dry run does not ask.
        h.Ledger.Sent.Add((1, PassHarness.Night));

        Assert.Equal(0, await h.RunAsync(dryRun: true));

        Assert.Equal(0, h.Ledger.TotalCalls);
        Assert.Contains("dry run: not published", h.Logs.AllText);
        Assert.Contains("Title:", h.Logs.AllText);
        Assert.Contains("Body:", h.Logs.AllText);
        Assert.Equal(0, await h.Db.AlertHistory.CountAsync(TestContext.Current.CancellationToken));
    }

    // ---- Logging content ----

    [Fact]
    public async Task Logs_keep_the_hours_window_and_hits_lines()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0);

        await h.RunAsync();

        var text = h.Logs.AllText;
        Assert.Contains("Got 156 hourly periods", text);
        Assert.Contains("Time zone: America/New_York", text);
        Assert.Contains("Night 2026-10-29: 2026-10-30T00:00:00.0000000+00:00 -> 2026-10-30T11:00:00.0000000+00:00 (length 11:00:00)", text);
        Assert.Contains("Hours in window: 11; hits at/above 0%: 11", text);
        Assert.Contains("Peak ", text);
    }

    [Fact]
    public async Task Unavailable_amounts_are_warned_about_and_the_alert_still_goes_out()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0);
        h.Forecasts.Handler = _ => FixtureForecast.Full() with
        {
            Amounts = null,
            AmountsUnavailableReason = "gridpoint fetch failed: timeout",
        };

        Assert.Equal(0, await h.RunAsync());

        Assert.Single(h.Ledger.SendCalls);
        Assert.Contains("Amounts unavailable (gridpoint fetch failed: timeout)", h.Logs.AllText);
    }

    // ---- D22 ----

    [Fact]
    public async Task D22_no_log_line_contains_the_coordinates_or_the_topic()
    {
        const string topic = "topic-secret-xyz";
        await using var h = await NewHarness();
        // Every location sits at the secret coordinates; each takes a different path through the pass.
        var alert = await h.AddLocationAsync("alert", threshold: 0, latitude: 40.7128m, longitude: -74.0060m);
        await h.AddLocationAsync("dry", threshold: 50, latitude: 40.7128m, longitude: -74.0060m);
        var nwsFails = await h.AddLocationAsync("nws-fails", latitude: 40.7128m, longitude: -74.0060m);
        var stale = await h.AddLocationAsync("stale", latitude: 40.7128m, longitude: -74.0060m);
        var incomplete = await h.AddLocationAsync("incomplete", threshold: 50, latitude: 40.7128m, longitude: -74.0060m);
        var publishFails = await h.AddLocationAsync("publish-fails", latitude: 40.7128m, longitude: -74.0060m);

        h.Cache.GetFailures[nwsFails] = new HttpRequestException("points down");
        h.Forecasts.Handler = call =>
        {
            if (call.LocationId == stale && call.PointVersion == 1)
                throw new NwsPointStaleException("gridpoint moved");
            if (call.LocationId == incomplete)
                return FixtureForecast.Truncated(NightStart.AddHours(6));
            return FixtureForecast.Full();
        };

        // Publish through the real NtfyPublisher, so its request (whose URL carries the topic) is real.
        var ok = new StubHandler(System.Net.HttpStatusCode.OK);
        var failing = new StubHandler(System.Net.HttpStatusCode.InternalServerError);
        var okPublisher = new Notifications.NtfyPublisher(new HttpClient(ok), "https://ntfy.invalid", topic);
        var failingPublisher = new Notifications.NtfyPublisher(new HttpClient(failing), "https://ntfy.invalid", topic);
        h.Ledger.Publish = (id, msg, ct) =>
            (id == publishFails ? failingPublisher : okPublisher).PublishAsync(msg, ct);

        // Precondition: the coordinates really are what is stored, so a leak would be possible.
        var stored = await h.Db.Locations.SingleAsync(l => l.Id == alert, TestContext.Current.CancellationToken);
        Assert.Equal(40.7128m, stored.Latitude);
        Assert.Equal(-74.0060m, stored.Longitude);

        var code = await h.RunAsync();
        var dryCode = await h.RunAsync(dryRun: true);

        // 2 is the first failure in id order (nws-fails comes before publish-fails).
        Assert.Equal(2, code);
        Assert.Equal(2, dryCode);
        // The topic was in play (the publisher saw it), and the run logged plenty to search.
        Assert.Contains(ok.Requests.Concat(failing.Requests), r => r.Contains(topic));
        Assert.True(h.Logs.Lines.Count > 20);
        foreach (var name in new[] { "alert", "dry", "nws-fails", "stale", "incomplete", "publish-fails" })
            Assert.Contains($"({name})", h.Logs.AllText);

        var text = h.Logs.AllText;
        Assert.DoesNotContain("40.7128", text);
        Assert.DoesNotContain("74.006", text);
        Assert.DoesNotContain(topic, text);
    }
}
