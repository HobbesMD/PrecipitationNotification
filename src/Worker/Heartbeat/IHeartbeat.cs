namespace Worker.Heartbeat;

/// <summary>
/// The dead-man's switch: tells an outside watcher (healthchecks.io) that a <c>--once</c> run
/// started and how it ended, so silence stops being ambiguous (CLAUDE.md, problem 3).
///
/// <para>
/// <b>Contract.</b> Neither method ever throws, not even when the token is cancelled, and each
/// returns within about 4 s by itself. A heartbeat that fails is logged and forgotten: it must
/// never change the run's exit code or eat into the pass's time budget.
/// </para>
/// </summary>
internal interface IHeartbeat
{
    /// <summary>The run has started (lets the watcher see a run that starts but never finishes).</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>The run finished with <paramref name="exitCode"/> (0 = healthy, anything else = failed).</summary>
    Task FinishAsync(int exitCode, CancellationToken ct);
}

/// <summary>No heartbeat configured, a dry run, or the long-running local loop.</summary>
internal sealed class NoOpHeartbeat : IHeartbeat
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task FinishAsync(int exitCode, CancellationToken ct) => Task.CompletedTask;
}
