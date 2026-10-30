namespace Worker.Pass;

/// <summary>One run over every active location. The seam the hosted services run, so they can be tested without a database.</summary>
internal interface INightlyPass
{
    /// <summary>Runs one pass and returns the process exit code (0 when every location succeeded).</summary>
    Task<int> RunAsync(CancellationToken ct);
}

/// <summary>Per-process switches for <see cref="NightlyPass"/>.</summary>
/// <param name="DryRun">Compose and print the message, but never publish and never touch <c>alert_history</c>.</param>
/// <remarks>Also carries the two deadlines, <see cref="Deadline"/> (soft) and <see cref="HardDeadline"/>.</remarks>
internal sealed record PassOptions(bool DryRun = false)
{
    /// <summary>
    /// 100 s, the soft deadline (D37). It only stops new locations from starting, so on its own
    /// it bounds nothing: database time isn't in the per-call arithmetic, and a location that has
    /// started runs until its own HTTP timeouts end it. What it buys is that a location started
    /// just before it normally finishes well inside <see cref="DefaultHardDeadline"/>: one
    /// location's worst case is six calls of at most 30 s (points, hourly and grid together, a
    /// stale refresh, the retried fetch, the Open-Meteo fallback, ntfy), 100 + 180 = 280 s. The
    /// actual bound is the hard deadline. A typical location takes 1-2 s.
    /// </summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(100);

    /// <summary>
    /// 270 s, the hard backstop: the hosted service cancels the whole pass at this point, in-flight
    /// work included. 30 s under Azure's 300 s replica timeout, so the cancelled work can unwind,
    /// the error is logged and the process exits 7 itself. A killed replica would lose both.
    /// </summary>
    public static readonly TimeSpan DefaultHardDeadline = TimeSpan.FromSeconds(270);

    /// <summary>
    /// The largest <see cref="HardDeadline"/> startup accepts: 280 s, which leaves 20 s for the
    /// cancellation to unwind and the process to exit before Azure's 300 s replica timeout.
    /// </summary>
    public static readonly TimeSpan MaxHardDeadline = TimeSpan.FromSeconds(280);

    /// <summary>
    /// How long into the pass a new location may still be started (config <c>Worker:PassDeadline</c>).
    /// Measured from the very start of <see cref="INightlyPass.RunAsync"/>, database checks included.
    /// </summary>
    public TimeSpan Deadline { get; init; } = DefaultDeadline;

    /// <summary>
    /// When the hosted service cancels the pass outright (config <c>Worker:HardDeadline</c>),
    /// measured from the start of each pass. Must be longer than <see cref="Deadline"/> and at most
    /// <see cref="MaxHardDeadline"/>; startup enforces both.
    /// </summary>
    public TimeSpan HardDeadline { get; init; } = DefaultHardDeadline;
}
