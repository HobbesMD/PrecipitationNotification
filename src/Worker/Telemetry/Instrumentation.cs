using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Worker.Telemetry;

/// <summary>
/// The Worker's own telemetry names, shared by the code that emits spans and metrics
/// (<c>NightlyPass</c>, the heartbeat) and the code that exports and redacts them
/// (<c>WorkerTelemetry</c>, <c>TelemetryRedactor</c>). Production code emits through
/// <see cref="System.Diagnostics"/> only; the OpenTelemetry SDK is wired up in one place.
///
/// Nothing secret may go on a span or a metric (D22): no coordinates, URLs, topic, connection
/// string or exception object. Location id and name are allowed on spans (the logs already carry
/// them) but never as metric tags.
/// </summary>
internal static class Instrumentation
{
    /// <summary>The ActivitySource and Meter name: what <c>AddSource</c>/<c>AddMeter</c> subscribe to.</summary>
    public const string Name = "Precip.Worker";

    /// <summary>Every <c>precip.*</c> span starts here.</summary>
    public static readonly ActivitySource Source = new(Name);

    /// <summary>Every <c>precip.*</c> instrument is created on this meter.</summary>
    public static readonly Meter Meter = new(Name);

    /// <summary>Span names.</summary>
    public static class Spans
    {
        /// <summary>One nightly pass over every active location: the root of a run's trace.</summary>
        public const string Pass = "precip.pass";

        /// <summary>One location within a pass.</summary>
        public const string Location = "precip.location";

        /// <summary>One provider's fetch (NWS, or the Open-Meteo fallback); HTTP spans nest under it.</summary>
        public const string Fetch = "precip.fetch";

        /// <summary>The pure decision: <c>ForecastEvaluator.Evaluate</c>.</summary>
        public const string Evaluate = "precip.evaluate";

        /// <summary>The once-per-night send through the alert ledger.</summary>
        public const string Notify = "precip.notify";
    }

    /// <summary>Attribute (span tag and metric tag) keys.</summary>
    public static class Attributes
    {
        /// <summary>Location row id. Spans only, never a metric tag.</summary>
        public const string LocationId = "precip.location.id";

        /// <summary>Location display name, e.g. "Home". Spans only, never a metric tag.</summary>
        public const string LocationName = "precip.location.name";

        /// <summary>The local night date (D2), <c>yyyy-MM-dd</c>.</summary>
        public const string NightDate = "precip.night_date";

        /// <summary>"NWS" or "Open-Meteo" (the forecast's Provider string).</summary>
        public const string Provider = "precip.provider";

        /// <summary>One of the <see cref="Outcomes"/> values.</summary>
        public const string Outcome = "precip.outcome";

        /// <summary>A process exit code from <c>ExitCodes</c>.</summary>
        public const string ExitCode = "precip.exit_code";

        /// <summary>How many active locations the pass found.</summary>
        public const string LocationsCount = "precip.locations.count";

        /// <summary>Hours in the window at or above the threshold.</summary>
        public const string Hits = "precip.hits";

        /// <summary>Highest probability in the window, percent.</summary>
        public const string PeakProbability = "precip.peak_probability";

        /// <summary>The evaluator's verdict.</summary>
        public const string ShouldAlert = "precip.should_alert";

        /// <summary>The ledger's result: "Sent" or "AlreadySent".</summary>
        public const string SendOutcome = "precip.send_outcome";
    }

    /// <summary>Values of <see cref="Attributes.Outcome"/> for one location.</summary>
    public static class Outcomes
    {
        /// <summary>Evaluated; nothing at or above the threshold.</summary>
        public const string Dry = "dry";

        /// <summary>Sent tonight's alert (or composed it, in a dry run).</summary>
        public const string Alerted = "alerted";

        /// <summary>Tonight's alert had already gone out; nothing published.</summary>
        public const string AlreadySent = "already_sent";

        /// <summary>Any non-zero exit code for this location.</summary>
        public const string Failed = "failed";

        /// <summary>Not started: the pass deadline had passed (D37).</summary>
        public const string SkippedDeadline = "skipped_deadline";

        /// <summary>Interrupted: the hard deadline or a shutdown cancelled it mid-run.</summary>
        public const string Cancelled = "cancelled";
    }
}
