using System.Diagnostics;
using System.Net;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Worker.Tests.Resilience;

public class GetResilienceTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Two_503s_then_200_succeeds_after_exactly_3_requests()
    {
        using var s = new WireMockSupport();
        s.Sequence(new Step(503), new Step(503), new Step(200));

        using var response = await s.Client.GetAsync(s.Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, s.RequestCount);
    }

    [Fact]
    public async Task Always_500_makes_exactly_3_requests_and_returns_the_final_500_without_throwing()
    {
        using var s = new WireMockSupport();
        s.Sequence(new Step(500));

        // Exhausted retries on a status code return the last response; only exceptions throw.
        using var response = await s.Client.GetAsync(s.Url);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(3, s.RequestCount);
    }

    [Fact]
    public async Task A_404_is_returned_after_exactly_1_request()
    {
        using var s = new WireMockSupport();
        s.Sequence(new Step(404));

        using var response = await s.Client.GetAsync(s.Url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, s.RequestCount);
    }

    [Fact]
    public async Task A_400_is_not_retried()
    {
        using var s = new WireMockSupport();
        s.Sequence(new Step(400));

        using var response = await s.Client.GetAsync(s.Url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, s.RequestCount);
    }

    [Fact]
    public async Task A_429_then_200_succeeds_after_2_requests()
    {
        using var s = new WireMockSupport();
        s.Sequence(new Step(429), new Step(200));

        using var response = await s.Client.GetAsync(s.Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, s.RequestCount);
    }

    [Fact]
    public async Task A_response_slower_than_the_attempt_timeout_is_retried()
    {
        using var s = new WireMockSupport();
        s.Sequence(new Step(200, Slow), new Step(200));

        using var response = await s.Client.GetAsync(s.Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, s.Arrived); // the log lags a delayed reply
    }

    [Fact]
    public async Task When_every_response_is_slow_the_call_fails_after_three_attempt_timeouts()
    {
        using var s = new WireMockSupport();
        s.Sequence(new Step(200, Slow));

        var sw = Stopwatch.StartNew();
        // Each 1 s reply outlasts the 300 ms attempt timeout, so all three attempts time out
        // (about 0.9 s plus two short backoffs), well before the 3 s total timeout. The last
        // attempt's timeout surfaces as TimeoutRejectedException; the exact type is asserted so a
        // change in how Polly reports it is noticed.
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => s.Client.GetAsync(s.Url));
        sw.Stop();

        Assert.Equal(3, s.Arrived);
        // Under the 3 s total timeout, so it was the attempts that ran out, not the total.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task The_total_timeout_cuts_retries_short_when_it_is_shorter_than_the_attempts()
    {
        // With the Fast settings three 300 ms attempts end before the 3 s total fires, so this
        // case shrinks the total below one attempt to prove the outer strategy works on its own.
        using var s = new WireMockSupport(WireMockSupport.Fast with { AttemptTimeout = TimeSpan.FromSeconds(5), TotalTimeout = TimeSpan.FromMilliseconds(500) });
        s.Sequence(new Step(200, Slow));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => s.Client.GetAsync(s.Url));
        sw.Stop();

        AssertTotalTimeout(ex);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.Equal(1, s.Arrived);
    }

    [Fact]
    public void The_client_timeout_equals_the_total_timeout()
    {
        // HttpClient.Timeout bounds the body, which Polly can't see (D33).
        using var s = new WireMockSupport();
        Assert.Equal(WireMockSupport.Fast.TotalTimeout, s.Client.Timeout);
    }

    /// <summary>
    /// HttpClient.Timeout equals the total timeout, and its clock starts a moment before Polly's,
    /// so whichever fires first reports the timeout: Polly as TimeoutRejectedException, HttpClient
    /// as TaskCanceledException wrapping a TimeoutException. Both mean "the budget ran out".
    /// </summary>
    internal static void AssertTotalTimeout(Exception ex) =>
        Assert.True(
            ex is TimeoutRejectedException or TaskCanceledException { InnerException: TimeoutException },
            $"expected a timeout, got {ex.GetType().Name}: {ex.Message}");

    [Fact]
    public async Task Open_breaker_rejects_with_0_requests_and_a_successful_probe_closes_it()
    {
        using var s = new WireMockSupport();
        s.Sequence(new Step(500));

        // Each call is up to 3 attempts, each a sample. With a minimum throughput of 4 and
        // a 100% failure rate the breaker opens during the second call.
        var calls = 0;
        while (calls < 5)
        {
            calls++;
            try { (await s.Client.GetAsync(s.Url)).Dispose(); }
            catch (BrokenCircuitException) { break; }
        }
        var tripped = s.RequestCount;
        Assert.InRange(calls, 2, 4);
        Assert.Equal(4, tripped);

        // Open: fails fast, nothing reaches the server.
        await Assert.ThrowsAsync<BrokenCircuitException>(() => s.Client.GetAsync(s.Url));
        Assert.Equal(tripped, s.RequestCount);

        // After the 1 s break the next call is the probe.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        s.Reset();
        s.Sequence(new Step(200));
        using var probe = await s.Client.GetAsync(s.Url);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        Assert.Equal(1, s.RequestCount);

        // Closed again: ordinary traffic flows.
        using var after = await s.Client.GetAsync(s.Url);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(2, s.RequestCount);
    }
}
