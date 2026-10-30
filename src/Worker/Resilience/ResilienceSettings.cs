namespace Worker.Resilience;

/// <summary>
/// The numbers behind the HTTP resilience pipelines, in one record so production uses the
/// defaults and tests can shrink every delay to milliseconds without changing the pipeline's shape.
///
/// The defaults are sized for a once-a-night job, not an interactive request. A few seconds of
/// backoff costs nothing, but each fetch must still finish well inside the job's replica timeout.
/// </summary>
internal sealed record ResilienceSettings
{
    /// <summary>Per-attempt timeout, so one slow response can't eat the whole budget.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Total attempts including the first, so 3 means two retries.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Outer timeout across every attempt and every backoff delay.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The first retry delay; later delays grow exponentially from it.</summary>
    public TimeSpan BackoffBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The window over which the circuit breaker measures the failure ratio.</summary>
    public TimeSpan BreakerSamplingDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Calls needed in the window before the breaker may open at all.</summary>
    public int BreakerMinimumThroughput { get; init; } = 4;

    /// <summary>The failure ratio (0 to 1) within the window that opens the breaker.</summary>
    public double BreakerFailureRatio { get; init; } = 0.5;

    /// <summary>How long an open breaker rejects calls before letting one probe through.</summary>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);
}
