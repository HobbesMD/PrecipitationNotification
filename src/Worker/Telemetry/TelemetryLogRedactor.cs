using System.Globalization;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Worker.Telemetry;

/// <summary>
/// The log counterpart of <see cref="TelemetryRedactor"/>: runs on every log record before the
/// OTLP log exporter, which logs reach without passing any span processor (D42, D46).
///
/// <para>
/// Our own log calls never pass an exception object and describe errors with
/// <see cref="Redact.Describe"/>. Framework code does pass them: Polly's retry warnings, EF Core's
/// command errors, the host's own failures. Exported as is, those carry the full message and stack
/// trace, which nothing has vetted. So, for every record:
/// </para>
/// <list type="bullet">
///   <item>The exception object is removed and replaced by two attributes, <c>exception.type</c>
///   and a URL-redacted <c>exception.message</c>: the same type-and-message rule as
///   <see cref="Redact.Describe"/> and the span redactor's exception events. No stack trace.</item>
///   <item>The formatted message has every <c>http(s)://</c> URL rewritten
///   (<see cref="TelemetryRedactor.RedactUrlsIn"/>).</item>
///   <item>Attribute values that are strings, or anything else that isn't a plain number, bool or
///   time, are converted to text and URL-redacted the same way (fail closed: an unknown object's
///   <c>ToString</c> is not trusted).</item>
/// </list>
/// The console logger is not affected; this only governs what leaves the process over OTLP.
/// </summary>
internal sealed class TelemetryLogRedactor : BaseProcessor<LogRecord>
{
    public const string ExceptionType = "exception.type";
    public const string ExceptionMessage = "exception.message";

    public override void OnEnd(LogRecord data)
    {
        if (data.FormattedMessage is { } message)
            data.FormattedMessage = TelemetryRedactor.RedactUrlsIn(message);

        List<KeyValuePair<string, object?>>? attributes = null;
        if (data.Attributes is { Count: > 0 } existing)
        {
            attributes = new List<KeyValuePair<string, object?>>(existing.Count + 2);
            foreach (var (key, value) in existing)
                attributes.Add(new(key, Scrub(value)));
        }

        if (data.Exception is { } ex)
        {
            attributes ??= new List<KeyValuePair<string, object?>>(2);
            attributes.Add(new(ExceptionType, ex.GetType().FullName));
            attributes.Add(new(ExceptionMessage, TelemetryRedactor.RedactUrlsIn(ex.Message)));
            data.Exception = null;
        }

        if (attributes is not null)
            data.Attributes = attributes;
    }

    private static object? Scrub(object? value) => value switch
    {
        null => null,
        string s => TelemetryRedactor.RedactUrlsIn(s),
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan => value,
        _ => TelemetryRedactor.RedactUrlsIn(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
    };
}
