using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Worker.Pass;

namespace Worker.Hosting;

/// <summary>
/// Where <c>--once</c> leaves the pass's result for <c>Program</c> to return. A holder rather
/// than <see cref="Environment.ExitCode"/> directly, so a test that runs the host does not set
/// the exit code of the test process; <c>Program</c> copies it to <see cref="Environment.ExitCode"/>.
/// </summary>
internal sealed class ExitCodeHolder
{
    public int Code { get; set; }
}

/// <summary>How often the long-running mode runs a pass (<c>Worker:Interval</c>).</summary>
internal sealed record LoopOptions(TimeSpan Interval)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);
}

/// <summary>
/// Runs one <see cref="INightlyPass"/> under the hard deadline, for both hosted services.
///
/// <para>
/// <b>Why a hard backstop.</b> The soft deadline (D37) only stops new locations from starting, and
/// its arithmetic leaves out database time, so it can't promise the pass ends before Azure's 300 s
/// replica timeout. A killed replica loses the exit code and the log of what happened. So each pass
/// runs with a token linked to the host's stopping token and cancelled
/// <see cref="PassOptions.HardDeadline"/> (270 s) after the pass starts, measured with the injected
/// <see cref="TimeProvider"/>. When it fires, in-flight work is cancelled, the error is logged, and
/// the pass result is <see cref="ExitCodes.PassDeadline"/>.
/// </para>
///
/// <para>
/// <b>What a cancelled send does.</b> The pass hands this token to the alert ledger unchanged, so
/// the ledger sees the backstop as <i>our</i> cancellation (D25's <c>IsOurCancellation</c>): no
/// bookkeeping, and the transaction rolls back. An interrupted send therefore leaves the row
/// <c>Pending</c> (or absent) and the next run resends, D25's accepted at-least-once duplicate.
/// After ntfy has accepted, the ledger ignores cancellation and commits <c>Sent</c>.
/// </para>
/// </summary>
internal static class PassBackstop
{
    /// <summary>
    /// The pass's exit code, or <see cref="ExitCodes.PassDeadline"/> when the backstop cancelled it.
    /// Cancellation by <paramref name="stoppingToken"/> (Ctrl+C, SIGTERM) is not a deadline and
    /// propagates, as does any other exception.
    /// </summary>
    public static async Task<int> RunAsync(
        IServiceScopeFactory scopes, PassOptions options, TimeProvider clock, ILogger log, CancellationToken stoppingToken)
    {
        // Started before the scope is created, so everything the pass does is on the clock.
        using var backstop = new CancellationTokenSource(options.HardDeadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, backstop.Token);

        try
        {
            // A scope per pass: the DbContext must not outlive one run.
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<INightlyPass>().RunAsync(linked.Token);
        }
        catch (OperationCanceledException) when (backstop.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            log.LogError(
                "Pass hit the hard deadline of {HardDeadline}; in-flight work was cancelled. "
                + "An interrupted send stays Pending and the next run resends it (D25).",
                options.HardDeadline);
            return ExitCodes.PassDeadline;
        }
    }
}

/// <summary>
/// <c>--once</c>: run one <see cref="INightlyPass"/>, record its exit code, stop the host. This is the
/// scheduled-job shape (Azure Container Apps Job): start, do the night's work, exit. The pass runs
/// under <see cref="PassBackstop"/>, so a pass that runs too long still exits 7 by itself.
/// </summary>
internal sealed class OncePassService(
    IServiceScopeFactory scopes,
    PassOptions options,
    TimeProvider clock,
    ExitCodeHolder exit,
    IHostApplicationLifetime lifetime,
    ILogger<OncePassService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            exit.Code = await PassBackstop.RunAsync(scopes, options, clock, log, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopped from outside (SIGTERM) mid-pass: the pass did not finish, so it did not succeed.
            exit.Code = ExitCodes.Failure;
            log.LogWarning("Pass cancelled before it finished.");
        }
        catch (Exception ex)
        {
            exit.Code = ExitCodes.Failure;
            log.LogError("Pass crashed: {Error}", Redact.Describe(ex));
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}

/// <summary>
/// No flag: run a pass at start, then every <see cref="LoopOptions.Interval"/>, until the host stops.
/// A failing pass is logged and the loop carries on: the next tick may well succeed, and the
/// alert ledger makes repeating a night harmless. Each pass runs under <see cref="PassBackstop"/>,
/// so a hung pass is cancelled and the next interval still runs.
/// </summary>
internal sealed class PassLoopService(
    IServiceScopeFactory scopes,
    LoopOptions loop,
    PassOptions options,
    TimeProvider clock,
    ILogger<PassLoopService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Created before the first pass, so the interval runs from start to start and a tick that
        // fires while a pass is running is not lost.
        using var timer = new PeriodicTimer(loop.Interval, clock);

        try
        {
            do
            {
                await RunPassAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        try
        {
            var code = await PassBackstop.RunAsync(scopes, options, clock, log, ct);
            if (code == ExitCodes.Ok)
                log.LogInformation("Pass finished: ok.");
            else
                log.LogWarning("Pass finished with exit code {Code}; will try again in {Interval}.", code, loop.Interval);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError("Pass crashed: {Error}; will try again in {Interval}.", Redact.Describe(ex), loop.Interval);
        }
    }
}
