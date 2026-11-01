using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Worker.Telemetry;

/// <summary>OpenTelemetry registration for the Worker: traces, metrics and logs over OTLP.</summary>
internal static class WorkerTelemetry
{
    /// <summary>
    /// Wave 0 stub: registers nothing. Replaced in Block 6 by the real OTLP wiring and the
    /// redaction processor. Must never throw: <c>AddWorkerServices</c> calls it.
    /// </summary>
    public static IServiceCollection AddWorkerTelemetry(this IServiceCollection services, IConfiguration? configuration) =>
        services;
}
