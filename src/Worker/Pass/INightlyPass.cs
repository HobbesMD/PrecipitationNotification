namespace Worker.Pass;

/// <summary>One run over every active location. The seam the hosted services run, so they can be tested without a database.</summary>
internal interface INightlyPass
{
    /// <summary>Runs one pass and returns the process exit code (0 when every location succeeded).</summary>
    Task<int> RunAsync(CancellationToken ct);
}

/// <summary>Per-process switches for <see cref="NightlyPass"/>.</summary>
/// <param name="DryRun">Compose and print the message, but never publish and never touch <c>alert_history</c>.</param>
internal sealed record PassOptions(bool DryRun = false);
