using Polly.CircuitBreaker;
using Polly.Timeout;
using TestSupport;
using Worker.Forecast;
using Worker.Nws;
using Worker.Pass;
using Worker.Resilience;

namespace Worker.Tests.Pass;

/// <summary>
/// The NWS-to-Open-Meteo fallback (D33) and the pass deadline, over the same real migrated
/// Postgres as <see cref="NightlyPassTests"/> with every provider, the cache and the ledger faked.
/// </summary>
public sealed class NightlyPassFallbackTests(PostgresFixture postgres)
{
    private Task<PassHarness> NewHarness() => PassHarness.CreateAsync(postgres);

    // ---- When the fallback runs ----

    [Fact]
    public async Task Nws_http_failure_falls_back_and_the_alert_names_open_meteo()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha", threshold: 0);
        h.Forecasts.Handler = _ => throw new HttpRequestException("nws down");
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo();

        Assert.Equal(0, await h.RunAsync());

        Assert.Equal([id], h.Fallback.Calls);
        var sent = Assert.Single(h.Ledger.SendCalls);
        Assert.Equal(id, sent.LocationId);
        Assert.Equal(Providers.OpenMeteo, Assert.Single(h.Ledger.Assessments).Provider);
        Assert.Contains("Source: Open-Meteo (NWS unavailable)", sent.Message.Body);
        Assert.Contains("NWS fetch failed", h.Logs.AllText);
    }

    [Fact]
    public async Task Nws_success_never_touches_the_fallback_and_stays_labelled_nws()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0);

        Assert.Equal(0, await h.RunAsync());

        Assert.Empty(h.Fallback.Calls);
        Assert.Equal(Providers.Nws, Assert.Single(h.Ledger.Assessments).Provider);
        Assert.DoesNotContain("Source:", Assert.Single(h.Ledger.SendCalls).Message.Body);
    }

    [Fact]
    public async Task Open_circuit_goes_straight_to_the_fallback_without_refreshing_the_point()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha", threshold: 0);
        h.Forecasts.Handler = _ => throw new BrokenCircuitException();
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo();

        Assert.Equal(0, await h.RunAsync());

        Assert.Empty(h.Cache.RefreshCalls);
        Assert.Single(h.Forecasts.Calls);
        Assert.Equal([id], h.Fallback.Calls);
        Assert.Equal(Providers.OpenMeteo, Assert.Single(h.Ledger.Assessments).Provider);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("bad-json")]
    [InlineData("anything-else")]
    public async Task Any_other_nws_failure_also_falls_back(string kind)
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0);
        h.Forecasts.Handler = _ => throw kind switch
        {
            "timeout" => new TimeoutRejectedException("took too long"),
            "bad-json" => new System.Text.Json.JsonException("bad payload"),
            _ => new InvalidOperationException("surprise"),
        };
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo();

        Assert.Equal(0, await h.RunAsync());

        Assert.Single(h.Fallback.Calls);
        Assert.Single(h.Ledger.SendCalls);
    }

    [Fact]
    public async Task Still_stale_after_the_one_refresh_falls_back_and_exits_0()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha", threshold: 0);
        h.Forecasts.Handler = _ => throw new NwsPointStaleException("gridpoint moved");
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo();

        Assert.Equal(0, await h.RunAsync());

        Assert.Equal([id], h.Cache.RefreshCalls);
        Assert.Equal(2, h.Forecasts.Calls.Count);
        Assert.Equal([id], h.Fallback.Calls);
        Assert.Equal(Providers.OpenMeteo, Assert.Single(h.Ledger.Assessments).Provider);
    }

    [Fact]
    public async Task Point_lookup_failure_falls_back_too()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha", threshold: 0);
        h.Cache.GetFailures[id] = new HttpRequestException("points down");
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo();

        Assert.Equal(0, await h.RunAsync());

        Assert.Empty(h.Forecasts.Calls);
        Assert.Contains("NWS point lookup failed", h.Logs.AllText);
        Assert.Equal(Providers.OpenMeteo, Assert.Single(h.Ledger.Assessments).Provider);
    }

    [Fact]
    public async Task An_nws_zone_the_os_does_not_know_falls_back()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0);
        h.Cache.TimeZoneId = "Mars/Olympus_Mons";
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo();

        Assert.Equal(0, await h.RunAsync());

        Assert.Empty(h.Forecasts.Calls);
        Assert.Equal(Providers.OpenMeteo, Assert.Single(h.Ledger.Assessments).Provider);
    }

    [Fact]
    public async Task Both_providers_failing_exits_2_and_the_log_names_both()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha", threshold: 0);
        h.Forecasts.Handler = _ => throw new HttpRequestException("nws down");
        h.Fallback.Handler = _ => throw new TimeoutRejectedException("open-meteo slow");

        Assert.Equal(2, await h.RunAsync());

        Assert.Equal([id], h.Fallback.Calls);
        Assert.Empty(h.Ledger.SendCalls);
        var text = h.Logs.AllText;
        Assert.Contains("both providers failed", text);
        Assert.Contains("NWS fetch failed: HttpRequestException: nws down", text);
        Assert.Contains("fallback fetch failed: TimeoutRejectedException: open-meteo slow", text);
    }

    [Fact]
    public async Task Gridpoint_only_failure_is_not_a_fallback_trigger()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0);
        h.Forecasts.Handler = _ => FixtureForecast.Full() with
        {
            Amounts = null,
            AmountsUnavailableReason = "gridpoint fetch failed: timeout",
        };

        Assert.Equal(0, await h.RunAsync());

        Assert.Empty(h.Fallback.Calls);
        Assert.Equal(Providers.Nws, Assert.Single(h.Ledger.Assessments).Provider);
    }

    [Fact]
    public async Task Already_sent_night_with_a_cached_zone_skips_the_fallback_call()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha", threshold: 0, timeZoneId: "America/New_York");
        h.Forecasts.Handler = _ => throw new HttpRequestException("nws down");
        h.Ledger.Sent.Add((id, PassHarness.Night));

        Assert.Equal(0, await h.RunAsync());

        Assert.Empty(h.Fallback.Calls);
        Assert.Empty(h.Ledger.SendCalls);
    }

    // ---- Cancellation ----

    [Fact]
    public async Task Our_own_cancellation_is_not_a_provider_failure_and_propagates()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0);
        using var cts = new CancellationTokenSource();
        h.Forecasts.Handler = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync(ct: cts.Token));

        Assert.Empty(h.Fallback.Calls);
        Assert.Empty(h.Ledger.SendCalls);
    }

    [Fact]
    public async Task A_timeout_that_is_not_our_token_does_fall_back()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0);
        // HttpClient reports its own timeout as a cancellation, but not of our token.
        h.Forecasts.Handler = _ => throw new TaskCanceledException("The request was canceled due to the configured timeout.");
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo();

        Assert.Equal(0, await h.RunAsync());

        Assert.Single(h.Fallback.Calls);
    }

    // ---- Zone and night key ----

    /// <summary>
    /// 11:30 UTC on 30 Oct is 07:30 in New York (after the window: the next night, the 30th) but
    /// 04:30 in Los Angeles (still inside the night of the 29th). The zones give different keys.
    /// </summary>
    private static readonly DateTimeOffset ZoneSensitiveInstant = new(2026, 10, 30, 11, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cached_zone_wins_over_the_fallbacks_and_the_night_key_matches_an_nws_run()
    {
        await using var nwsRun = await NewHarness();
        await nwsRun.AddLocationAsync("alpha", threshold: 0, timeZoneId: "America/New_York");
        nwsRun.Clock.SetUtcNow(ZoneSensitiveInstant);
        Assert.Equal(0, await nwsRun.RunAsync());
        var nwsNight = Assert.Single(nwsRun.Ledger.SendCalls).Night;

        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0, timeZoneId: "America/New_York");
        h.Clock.SetUtcNow(ZoneSensitiveInstant);
        h.Forecasts.Handler = _ => throw new HttpRequestException("nws down");
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo("America/Los_Angeles");

        Assert.Equal(0, await h.RunAsync());

        var sent = Assert.Single(h.Ledger.SendCalls);
        Assert.Equal(nwsNight, sent.Night);
        Assert.Equal(new DateOnly(2026, 10, 30), sent.Night);
        Assert.Contains("differs from the cached", h.Logs.AllText);
        Assert.Contains("Time zone: America/New_York", h.Logs.AllText);
    }

    [Fact]
    public async Task Without_a_cached_zone_the_fallbacks_zone_is_used()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha", threshold: 0); // never resolved: no cached zone
        h.Clock.SetUtcNow(ZoneSensitiveInstant);
        h.Cache.GetFailures[id] = new HttpRequestException("points down");
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo("America/Los_Angeles");

        Assert.Equal(0, await h.RunAsync());

        Assert.Equal(new DateOnly(2026, 10, 29), Assert.Single(h.Ledger.SendCalls).Night);
        Assert.Contains("Time zone: America/Los_Angeles", h.Logs.AllText);
        Assert.DoesNotContain("differs from the cached", h.Logs.AllText);
    }

    [Fact]
    public async Task A_cached_zone_the_os_does_not_know_yields_to_the_fallbacks()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("alpha", threshold: 0, timeZoneId: "Mars/Olympus_Mons");
        h.Cache.TimeZoneId = "Mars/Olympus_Mons";
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo();

        Assert.Equal(0, await h.RunAsync());

        Assert.Equal(PassHarness.Night, Assert.Single(h.Ledger.SendCalls).Night);
    }

    [Fact]
    public async Task A_fallback_zone_the_os_does_not_know_with_no_cached_zone_fails_the_location_with_2()
    {
        await using var h = await NewHarness();
        var id = await h.AddLocationAsync("alpha", threshold: 0);
        h.Cache.GetFailures[id] = new HttpRequestException("points down");
        h.Fallback.Handler = _ => FakeFallbackSource.OpenMeteo("Mars/Olympus_Mons");

        Assert.Equal(2, await h.RunAsync());

        Assert.Empty(h.Ledger.SendCalls);
        Assert.Contains("both providers failed", h.Logs.AllText);
    }

    // ---- Deadline ----

    [Fact]
    public async Task Deadline_passing_during_location_1_means_location_2_is_not_started_and_the_exit_is_7()
    {
        await using var h = await NewHarness();
        var first = await h.AddLocationAsync("first", threshold: 0);
        var second = await h.AddLocationAsync("second", threshold: 0);
        var third = await h.AddLocationAsync("third", threshold: 0);
        h.Forecasts.Handler = _ =>
        {
            h.Clock.Advance(TimeSpan.FromSeconds(241));
            return FixtureForecast.Full();
        };

        Assert.Equal(7, await h.RunAsync());

        Assert.Equal([first], h.Cache.GetCalls);
        Assert.Equal(first, Assert.Single(h.Ledger.SendCalls).LocationId); // the started location finished
        var text = h.Logs.AllText;
        Assert.Contains($"Location {second} (second): not evaluated (pass deadline", text);
        Assert.Contains($"Location {third} (third): not evaluated (pass deadline", text);
        Assert.DoesNotContain("(first): not evaluated", text);
    }

    [Fact]
    public async Task An_earlier_failure_keeps_its_code_when_the_deadline_then_passes()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("first", threshold: 0);
        await h.AddLocationAsync("second", threshold: 0);
        h.Forecasts.Handler = _ =>
        {
            h.Clock.Advance(TimeSpan.FromSeconds(241));
            return FixtureForecast.Full();
        };
        h.Ledger.Publish = (_, _, _) => throw new HttpRequestException("ntfy down");

        Assert.Equal(3, await h.RunAsync());

        Assert.Single(h.Cache.GetCalls);
        Assert.Contains("(second): not evaluated", h.Logs.AllText);
    }

    [Fact]
    public async Task Deadline_is_measured_from_the_start_and_comes_from_the_options()
    {
        await using var h = await NewHarness();
        await h.AddLocationAsync("first", threshold: 0);
        await h.AddLocationAsync("second", threshold: 0);
        h.Forecasts.Handler = _ =>
        {
            h.Clock.Advance(TimeSpan.FromSeconds(31));
            return FixtureForecast.Full();
        };

        // 31 s is well inside the 100 s default ...
        Assert.Equal(0, await h.RunAsync());
        Assert.Equal(2, h.Cache.GetCalls.Count);

        // ... and past a 30 s deadline.
        h.Cache.GetCalls.Clear();
        h.Ledger.Sent.Clear();
        Assert.Equal(7, await h.RunAsync(deadline: TimeSpan.FromSeconds(30)));
        Assert.Single(h.Cache.GetCalls);
    }

    [Fact]
    public void Default_deadline_is_100_seconds_and_the_exit_code_is_7()
    {
        Assert.Equal(TimeSpan.FromSeconds(100), new PassOptions().Deadline);
        Assert.Equal(7, ExitCodes.PassDeadline);
    }

    /// <summary>
    /// D37's guarantee, as arithmetic: the last location may start just before the deadline and
    /// then take its worst case. That must still end before Azure kills the replica (300 s), with
    /// margin for the database work around it. Raising the deadline or any pipeline's total
    /// timeout past the budget fails here, not in production.
    /// </summary>
    [Fact]
    public void Deadline_plus_one_worst_case_location_ends_before_the_replica_timeout()
    {
        var perCall = new[] { new ResilienceSettings().TotalTimeout }.Max();
        var worstLocation = perCall * PassOptions.WorstCaseCallsPerLocation;

        Assert.True(PassOptions.DefaultDeadline + worstLocation <= TimeSpan.FromSeconds(280),
            $"{PassOptions.DefaultDeadline} + {worstLocation} exceeds the 280 s budget under the 300 s replica timeout.");
    }
}
