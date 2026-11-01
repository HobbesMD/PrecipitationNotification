using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Worker.Resilience;

/// <summary>
/// The resilience pipeline for idempotent GETs to weather providers (NWS, Open-Meteo).
/// Retrying a GET can't cause a side effect, so this pipeline may retry freely.
/// </summary>
internal static class GetResilience
{
    /// <summary>
    /// Adds the pipeline to <paramref name="builder"/>, outermost strategy first:
    /// <list type="number">
    /// <item><description>
    /// <b>Total timeout</b> (<see cref="ResilienceSettings.TotalTimeout"/>). Bounds every attempt plus
    /// every backoff delay, so a flaky provider can't hold the job past its replica timeout (D20).
    /// When it fires the call throws <see cref="TimeoutRejectedException"/>.
    /// </description></item>
    /// <item><description>
    /// <b>Retry</b> (<see cref="ResilienceSettings.MaxAttempts"/> total attempts, so MaxAttempts - 1
    /// retries; exponential backoff with jitter from <see cref="ResilienceSettings.BackoffBaseDelay"/>).
    /// Absorbs the brief provider hiccups (5xx, 408, 429, network errors, an attempt timing out) that
    /// would otherwise lose a night's alert. Jitter stops several callers retrying in lockstep.
    /// It never retries 404 or any other 4xx: a 404 means the cached gridpoint is stale (D27) and the
    /// caller must see it immediately, and a 400-class error will not fix itself on a second try.
    /// If every attempt returns a 5xx the final response is returned, not thrown, so callers see the
    /// real status code; if the last attempt throws, the exception propagates.
    /// </description></item>
    /// <item><description>
    /// <b>Circuit breaker</b> (<see cref="ResilienceSettings.BreakerFailureRatio"/> over
    /// <see cref="ResilienceSettings.BreakerSamplingDuration"/>, at least
    /// <see cref="ResilienceSettings.BreakerMinimumThroughput"/> calls, open for
    /// <see cref="ResilienceSettings.BreakDuration"/>). Counts the same outcomes as the retry, one
    /// sample per attempt. When a provider is clearly down it fails fast with
    /// <see cref="BrokenCircuitException"/> instead of spending the timeout budget on each location.
    /// There is one breaker per named or typed client, so one per provider, not per request.
    /// It matters <i>within</i> a pass (several locations, or the hourly local loop) but not across
    /// nightly <c>--once</c> runs: its state lives in memory and dies with the process, so every
    /// nightly run starts closed.
    /// </description></item>
    /// <item><description>
    /// <b>Attempt timeout</b> (<see cref="ResilienceSettings.AttemptTimeout"/>). Innermost, so one hung
    /// connection costs one attempt, not the whole budget; the retry sees it as a failure and tries
    /// again. The breaker counts it too.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Two clocks, two jobs.</b> Polly's total timeout bounds the retries: every attempt and
    /// every backoff delay, up to the moment the response headers arrive. It can't see the body,
    /// because the pipeline returns as soon as it has a response. <see cref="HttpClient.Timeout"/>
    /// is set to the same <see cref="ResilienceSettings.TotalTimeout"/> and bounds the whole call,
    /// body included, as long as the caller lets <c>HttpClient</c> buffer the body inside
    /// <c>SendAsync</c> (the default <see cref="HttpCompletionOption.ResponseContentRead"/>; see
    /// <c>NwsClient</c> and <c>OpenMeteoClient</c>). Without it, a server that sent headers and
    /// then stalled mid-body would hang the run until Azure killed the replica.
    /// </para>
    /// <para>
    /// The two clocks have the same length and start a moment apart, so a call that exhausts the
    /// budget fails as either <see cref="TimeoutRejectedException"/> (Polly fired first) or a
    /// <see cref="TaskCanceledException"/> with an inner <see cref="TimeoutException"/> while the
    /// caller's token is not cancelled (<c>HttpClient</c> fired first). Both are "the provider
    /// timed out" to every caller: <c>NightlyPass</c> falls back on either (D36).
    /// </para>
    /// </summary>
    public static IHttpClientBuilder AddGetResilience(this IHttpClientBuilder builder, ResilienceSettings settings)
    {
        // Run after the client's own configure delegate (registration order), so this one wins.
        builder.ConfigureHttpClient(c => c.Timeout = settings.TotalTimeout);

        builder.AddResilienceHandler("get-resilience", pipeline =>
        {
            // Transient: 5xx, 408, 429, HttpRequestException and TimeoutRejectedException.
            // Deliberately excludes 404 and other 4xx, and BrokenCircuitException.
            static ValueTask<bool> IsTransient(Outcome<HttpResponseMessage> o) =>
                ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(o));

            pipeline.AddTimeout(settings.TotalTimeout);

            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                // MaxRetryAttempts excludes the first attempt.
                MaxRetryAttempts = Math.Max(settings.MaxAttempts - 1, 0),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = settings.BackoffBaseDelay,
                ShouldHandle = args => IsTransient(args.Outcome),
            });

            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = settings.BreakerFailureRatio,
                MinimumThroughput = settings.BreakerMinimumThroughput,
                SamplingDuration = settings.BreakerSamplingDuration,
                BreakDuration = settings.BreakDuration,
                ShouldHandle = args => IsTransient(args.Outcome),
            });

            pipeline.AddTimeout(settings.AttemptTimeout);
        });

        return builder;
    }
}
