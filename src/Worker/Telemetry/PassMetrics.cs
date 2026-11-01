using System.Diagnostics.Metrics;

namespace Worker.Telemetry;

/// <summary>
/// The pass's metric instruments, on the shared <see cref="Instrumentation.Meter"/>. No location id
/// or name is ever a tag: tags here are low-cardinality and carry nothing identifying.
/// </summary>
internal static class PassMetrics
{
    private static readonly Counter<long> Passes =
        Instrumentation.Meter.CreateCounter<long>("precip.passes", description: "Nightly passes, by exit code.");

    private static readonly Histogram<double> PassDuration =
        Instrumentation.Meter.CreateHistogram<double>("precip.pass.duration", unit: "s", description: "Wall time of one pass.");

    private static readonly Counter<long> Locations =
        Instrumentation.Meter.CreateCounter<long>("precip.locations", description: "Locations handled, by outcome and provider.");

    private static readonly Counter<long> Fallbacks =
        Instrumentation.Meter.CreateCounter<long>("precip.provider.fallbacks", description: "Times the fallback provider was attempted.");

    private static readonly Counter<long> AlertsSent =
        Instrumentation.Meter.CreateCounter<long>("precip.alerts.sent", description: "Alerts sent, by provider.");

    public static void RecordPass(int exitCode) =>
        Passes.Add(1, new KeyValuePair<string, object?>(Instrumentation.Attributes.ExitCode, exitCode));

    public static void RecordDuration(double seconds) => PassDuration.Record(seconds);

    public static void RecordLocation(string outcome, string? provider) =>
        Locations.Add(
            1,
            new KeyValuePair<string, object?>(Instrumentation.Attributes.Outcome, outcome),
            new KeyValuePair<string, object?>(Instrumentation.Attributes.Provider, provider ?? "none"));

    public static void RecordFallback() => Fallbacks.Add(1);

    public static void RecordAlertSent(string provider) =>
        AlertsSent.Add(1, new KeyValuePair<string, object?>(Instrumentation.Attributes.Provider, provider));
}
