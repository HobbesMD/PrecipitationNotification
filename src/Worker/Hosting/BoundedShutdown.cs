using Microsoft.Extensions.Hosting;

namespace Worker.Hosting;

/// <summary>
/// Runs the host and then disposes it within a fixed budget.
///
/// <para>
/// <b>Why.</b> Disposing the host disposes the OpenTelemetry providers, and that is when a
/// <c>--once</c> run flushes its spans, metrics and logs. Each OTLP export is bounded by its own
/// timeout, but there are three providers and the metrics one exports again on shutdown: against a
/// collector that accepts the connection and never answers, disposal was measured at 10 s, not the
/// 5 s a single timeout suggests. Added to the 280 s maximum hard deadline (D38) and two 4 s
/// heartbeat pings, that is 298 s of Azure's 300 s replica timeout, a bound by arithmetic only.
/// So the guarantee is made by construction instead, as D38 did for the pass: disposal gets
/// <see cref="FlushBudget"/>, and when that runs out the process exits anyway with the pass's
/// code. What is lost is the unsent telemetry, never the exit code.
/// </para>
/// </summary>
internal static class BoundedShutdown
{
    /// <summary>How long disposing the host (flushing telemetry) may take before it is abandoned.</summary>
    public static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts the host, waits for it to stop (the <c>--once</c> service stops it after the finish
    /// ping; the loop on Ctrl+C/SIGTERM), then disposes it within <paramref name="budget"/>.
    /// Unlike <c>HostingAbstractionsHostExtensions.RunAsync</c>, which disposes without a limit.
    /// </summary>
    public static async Task RunAsync(IHost host, TimeSpan budget, TextWriter error, TimeProvider? clock = null)
    {
        try
        {
            await host.StartAsync();
            await host.WaitForShutdownAsync();
        }
        finally
        {
            await DisposeWithinAsync(host, budget, error, clock ?? TimeProvider.System);
        }
    }

    /// <summary>
    /// Disposes on a pool thread so a provider that blocks synchronously in <c>Dispose</c> cannot
    /// hold this one. Never throws: a disposal fault or overrun is reported and the caller returns.
    /// </summary>
    public static async Task DisposeWithinAsync(IHost host, TimeSpan budget, TextWriter error, TimeProvider clock)
    {
        var disposal = Task.Run(async () =>
        {
            if (host is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                host.Dispose();
        });

        try
        {
            await disposal.WaitAsync(budget, clock);
        }
        catch (TimeoutException)
        {
            error.WriteLine($"Shutdown: telemetry flush did not finish within {budget.TotalSeconds:0} s; exiting without it.");
        }
        catch (Exception ex)
        {
            error.WriteLine($"Shutdown: disposing the host failed: {Redact.Describe(ex)}");
        }
    }
}
