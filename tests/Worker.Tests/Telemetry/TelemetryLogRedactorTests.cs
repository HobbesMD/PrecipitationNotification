using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using Worker.Telemetry;

namespace Worker.Tests.Telemetry;

/// <summary>
/// D46. Framework code (Polly, EF Core, the host) logs exception objects; the OTLP log exporter
/// would send their full message and stack trace. <see cref="TelemetryLogRedactor"/> runs first
/// and cuts every record down to what <see cref="Redact.Describe"/> would say. These go through
/// <c>AddWorkerTelemetry</c> with an exporter added afterwards, so they also prove the order.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class TelemetryLogRedactorTests
{
    private const string Topic = "log-topic-q8w2e4r6";

    private sealed record Exported(
        string? FormattedMessage, string? ExceptionTypeOnRecord, IReadOnlyDictionary<string, object?> Attributes);

    /// <summary>Logs through the Worker's telemetry with an in-memory exporter registered after it.</summary>
    private static List<Exported> Export(Action<ILogger> log)
    {
        var records = new List<LogRecord>();
        var exported = new List<Exported>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkerTelemetry(new ConfigurationBuilder().Build());
        services.ConfigureOpenTelemetryLoggerProvider(b => b.AddInMemoryExporter(records));

        using (var provider = services.BuildServiceProvider())
        {
            log(provider.GetRequiredService<ILoggerFactory>().CreateLogger("Polly"));
            // Copied out at once: the SDK may reuse a record after export.
            foreach (var r in records)
                exported.Add(new Exported(
                    r.FormattedMessage,
                    r.Exception?.GetType().FullName,
                    (r.Attributes ?? []).ToDictionary(a => a.Key, a => a.Value)));
        }
        return exported;
    }

    private static Exception Thrown(string message)
    {
        try
        {
            throw new HttpRequestException(message);
        }
        catch (HttpRequestException ex)
        {
            return ex; // has a stack trace, as a real one would
        }
    }

    [Fact]
    public void An_exception_object_leaves_only_its_type_and_a_redacted_message()
    {
        var ex = Thrown($"POST https://ntfy.sh/{Topic} failed");

        var record = Assert.Single(Export(l => l.LogWarning(ex, "Execution attempt. Result: {Result}", "Failed")));

        Assert.Null(record.ExceptionTypeOnRecord); // the object, and with it the stack trace, is gone
        Assert.Equal(typeof(HttpRequestException).FullName, record.Attributes[TelemetryLogRedactor.ExceptionType]);
        Assert.Equal("POST https://ntfy.sh/{redacted} failed", record.Attributes[TelemetryLogRedactor.ExceptionMessage]);
        Assert.DoesNotContain(record.Attributes.Values, v => Convert.ToString(v, CultureInfo.InvariantCulture)!.Contains(Topic));
    }

    [Fact]
    public void Urls_in_the_message_and_in_string_or_object_arguments_are_redacted()
    {
        var record = Assert.Single(Export(l => l.LogInformation(
            "Request {Url} to {Target} took {Ms} ms",
            new Uri("https://api.weather.gov/points/40.7128,-74.006"),
            $"https://ntfy.sh/{Topic}",
            42)));

        Assert.Equal(
            "Request https://api.weather.gov/points/{redacted} to https://ntfy.sh/{redacted} took 42 ms",
            record.FormattedMessage);
        Assert.Equal("https://api.weather.gov/points/{redacted}", record.Attributes["Url"]);
        Assert.Equal("https://ntfy.sh/{redacted}", record.Attributes["Target"]);
        Assert.Equal(42, record.Attributes["Ms"]); // numbers stay numbers
    }

    [Fact]
    public void A_record_with_nothing_to_redact_is_unchanged()
    {
        var record = Assert.Single(Export(l => l.LogInformation("Pass finished with exit code {ExitCode}", 0)));

        Assert.Equal("Pass finished with exit code 0", record.FormattedMessage);
        Assert.Equal(0, record.Attributes["ExitCode"]);
        Assert.False(record.Attributes.ContainsKey(TelemetryLogRedactor.ExceptionType));
    }
}
