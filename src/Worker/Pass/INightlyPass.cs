namespace Worker.Pass;

/// <summary>One run over every active location. The seam the hosted services run, so they can be tested without a database.</summary>
internal interface INightlyPass
{
    /// <summary>Runs one pass and returns the process exit code (0 when every location succeeded).</summary>
    Task<int> RunAsync(CancellationToken ct);
}

/// <summary>Per-process switches for <see cref="NightlyPass"/>.</summary>
/// <param name="DryRun">Compose and print the message, but never publish and never touch <c>alert_history</c>.</param>
/// <remarks>Also carries <see cref="Deadline"/>.</remarks>
internal sealed record PassOptions(bool DryRun = false)
{
    /// <summary>
    /// 100 s, chosen so the guarantee holds (D37): the deadline only stops new locations from
    /// starting, and one location's worst case is six calls of at most 30 s each (points, hourly
    /// and grid together, a stale refresh, the retried fetch, the Open-Meteo fallback, ntfy) =
    /// 180 s. 100 + 180 = 280 s, under Azure's 300 s replica timeout. A typical location takes 1-2 s.
    /// </summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(100);

    /// <summary>Calls one location can make in the worst case, each capped by its pipeline's total timeout.</summary>
    public const int WorstCaseCallsPerLocation = 6;

    /// <summary>
    /// How long into the pass a new location may still be started (config <c>Worker:PassDeadline</c>).
    /// Measured from the start of <see cref="INightlyPass.RunAsync"/>.
    /// </summary>
    public TimeSpan Deadline { get; init; } = DefaultDeadline;
}
