using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Worker;
using Worker.Hosting;
using Worker.Pass;

// Modes (see CLAUDE.md, "Worker design note"):
//   --self-check  prove the runtime has real tz data, then exit (0 or 5). Needs no config.
//   --migrate     apply EF migrations, then exit (0, or 6 on failure). Never run automatically.
//   --once        run one nightly pass, then exit with its code: the scheduled-job shape. Pings the
//                 heartbeat (PRECIP_HEARTBEAT_URL) at start and with the exit code at the end; a
//                 config error before the host starts sends no ping, and the missed ping alerts.
//   (no flag)     run a pass at start, then every Worker:Interval (default 01:00:00): the local shape.
//   --dry-run     with --once or the loop: compose and print the message, never publish,
//                 never touch alert_history, never ping the heartbeat.
//
// Telemetry: traces, metrics and logs go over OTLP only when OTEL_EXPORTER_OTLP_ENDPOINT is set.
// A malformed OTEL_* value turns export off for the run with a warning; it never stops the pass.
//
// Exit codes: 0 ok, 1 no active locations / bad config / unexpected, 2 both forecast providers
// failed (NWS and the Open-Meteo fallback), 3 ntfy publish, 4 incomplete forecast, 5 runtime
// self-check, 6 database unreachable, migrations pending or --migrate failed, 7 pass deadline
// reached with locations left unevaluated, or the hard deadline cancelled the pass. See ExitCodes.

// Before .env and config on purpose: the check needs neither, so CI can run it
// against the built image with nothing set.
if (args.Contains("--self-check")) return RuntimeSelfCheck.Run(Console.Out);

// Spans are recorded in-process, but no trace context leaves it: HttpClient would otherwise add a
// traceparent header to every request to NWS, Open-Meteo, ntfy and healthchecks.io. They have no
// use for our trace ids, and nothing about this process needs telling to a third party.
DistributedContextPropagator.Current = DistributedContextPropagator.CreateNoOutputPropagator();

var envFile = Persistence.DotEnv.Load();
Console.WriteLine(envFile is null
    ? "No .env file found; relying on process environment."
    : $"Loaded config from {envFile}");

if (args.Contains("--migrate"))
{
    string connectionString;
    try
    {
        connectionString = AppConfig.ConnectionStringFromEnvironment();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"Config error: {ex.Message}");
        return ExitCodes.Failure;
    }

    return Environment.ExitCode = await Migrator.RunAsync(connectionString, Console.Out, CancellationToken.None);
}

AppConfig config;
try
{
    config = AppConfig.FromEnvironment();
}
catch (InvalidOperationException ex)
{
    // Names the variable, never the value (D22).
    Console.Error.WriteLine($"Config error: {ex.Message}");
    return ExitCodes.Failure;
}

var once = args.Contains("--once");
var builder = Host.CreateApplicationBuilder(args);

try
{
    // Reads Resilience:*, Worker:PassDeadline and Worker:HardDeadline; a malformed or invalid value
    // must fail like any other config error.
    builder.Services.AddWorkerServices(config, dryRun: args.Contains("--dry-run"));
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Config error: {ex.Message}");
    return ExitCodes.Failure;
}
if (once)
{
    builder.Services.AddOncePass();
}
else
{
    TimeSpan interval;
    try
    {
        interval = builder.Configuration.GetValue<TimeSpan?>("Worker:Interval") ?? LoopOptions.DefaultInterval;
    }
    catch (InvalidOperationException)
    {
        interval = TimeSpan.Zero; // unparseable: reported below, without echoing the value
    }

    if (interval <= TimeSpan.Zero)
    {
        Console.Error.WriteLine("Config error: Worker:Interval must be a positive time span, e.g. 01:00:00.");
        return ExitCodes.Failure;
    }

    builder.Services.AddPassLoop(interval);
}

var host = builder.Build();
// Resolved before running: disposing the host disposes its service provider.
var exit = host.Services.GetRequiredService<ExitCodeHolder>();
// Not host.RunAsync(): that disposes the host, which flushes telemetry, with no time limit.
// BoundedShutdown caps the flush so the run always exits inside Azure's 300 s replica timeout.
if (!await BoundedShutdown.RunAsync(host, BoundedShutdown.FlushBudget, Console.Error))
    return Environment.ExitCode = ExitCodes.Failure;

// Only --once sets a code; the loop exits 0 on a normal shutdown.
return Environment.ExitCode = exit.Code;
