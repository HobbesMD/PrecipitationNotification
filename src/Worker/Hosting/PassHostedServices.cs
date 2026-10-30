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
/// <c>--once</c>: run one <see cref="INightlyPass"/>, record its exit code, stop the host. This is the
/// scheduled-job shape (Azure Container Apps Job): start, do the night's work, exit.
/// </summary>
internal sealed class OncePassService(
    IServiceScopeFactory scopes,
    ExitCodeHolder exit,
    IHostApplicationLifetime lifetime,
    ILogger<OncePassService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            exit.Code = await scope.ServiceProvider.GetRequiredService<INightlyPass>().RunAsync(stoppingToken);
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
            log.LogError("Pass crashed: {Error}", $"{ex.GetType().Name}: {ex.Message}");
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
/// alert ledger makes repeating a night harmless.
/// </summary>
internal sealed class PassLoopService(
    IServiceScopeFactory scopes,
    LoopOptions options,
    TimeProvider clock,
    ILogger<PassLoopService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Created before the first pass, so the interval runs from start to start and a tick that
        // fires while a pass is running is not lost.
        using var timer = new PeriodicTimer(options.Interval, clock);

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
            // A scope per pass: the DbContext must not outlive one run.
            await using var scope = scopes.CreateAsyncScope();
            var code = await scope.ServiceProvider.GetRequiredService<INightlyPass>().RunAsync(ct);
            if (code == ExitCodes.Ok)
                log.LogInformation("Pass finished: ok.");
            else
                log.LogWarning("Pass finished with exit code {Code}; will try again in {Interval}.", code, options.Interval);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError("Pass crashed: {Error}; will try again in {Interval}.", $"{ex.GetType().Name}: {ex.Message}", options.Interval);
        }
    }
}
