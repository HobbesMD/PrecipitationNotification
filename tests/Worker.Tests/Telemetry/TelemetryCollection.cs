namespace Worker.Tests.Telemetry;

/// <summary>
/// ActivityListeners, MeterListeners and OpenTelemetry providers are process-global: one test's
/// listener sees every other running test's spans and measurements. Every test that listens joins
/// this collection; DisableParallelization makes xunit run it on its own, after the parallel ones.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    public const string Name = "Telemetry";
}
