namespace Worker.Pass;

/// <summary>
/// One run over every active location: resolve the point, fetch, evaluate, send once.
/// Shared by <c>--once</c> (the scheduled job) and the long-running
/// <c>BackgroundService</c> mode.
/// </summary>
internal sealed class NightlyPass
{
    /// <summary>Runs one pass and returns the process exit code (0 when every location succeeded).</summary>
    public Task<int> RunAsync(CancellationToken ct) => throw new NotImplementedException();
}
