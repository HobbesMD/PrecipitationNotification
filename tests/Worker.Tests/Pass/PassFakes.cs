using Microsoft.Extensions.Logging;
using Persistence;
using Worker.Alerts;
using Worker.Forecast;
using Worker.Nws;
using Worker.Pass;

namespace Worker.Tests.Pass;

/// <summary>A point per location, versioned so a refresh is visible: <c>https://hourly.invalid/{id}/v{version}</c>.</summary>
internal sealed class FakeGridpointCache : IGridpointCache
{
    private readonly Dictionary<int, int> _versions = [];

    public string TimeZoneId { get; set; } = "America/New_York";

    /// <summary>Per-location failure for <see cref="GetAsync"/>.</summary>
    public Dictionary<int, Exception> GetFailures { get; } = [];

    public List<int> GetCalls { get; } = [];

    public List<int> RefreshCalls { get; } = [];

    public Task<NwsPoint> GetAsync(Location location, CancellationToken ct)
    {
        GetCalls.Add(location.Id);
        if (GetFailures.TryGetValue(location.Id, out var failure))
            throw failure;
        return Task.FromResult(PointFor(location.Id));
    }

    public Task<NwsPoint> RefreshAsync(Location location, CancellationToken ct)
    {
        RefreshCalls.Add(location.Id);
        _versions[location.Id] = _versions.GetValueOrDefault(location.Id, 1) + 1;
        return Task.FromResult(PointFor(location.Id));
    }

    private NwsPoint PointFor(int id) =>
        new(TimeZoneId, $"https://hourly.invalid/{id}/v{_versions.GetValueOrDefault(id, 1)}", null);
}

/// <summary>What a fetch asked for: which location, which version of its point, and how many times that location was fetched.</summary>
internal sealed record FetchCall(int LocationId, int PointVersion, int Attempt);

internal sealed class FakeForecastSource : IForecastSource
{
    private readonly Dictionary<int, int> _attempts = [];

    public List<FetchCall> Calls { get; } = [];

    /// <summary>Default: the full captured forecast.</summary>
    public Func<FetchCall, NwsForecast> Handler { get; set; } = _ => FixtureForecast.Full();

    public Task<NwsForecast> GetForecastAsync(NwsPoint point, CancellationToken ct)
    {
        var parts = new Uri(point.ForecastHourlyUrl).AbsolutePath.Trim('/').Split('/');
        var id = int.Parse(parts[0]);
        var attempt = _attempts[id] = _attempts.GetValueOrDefault(id) + 1;
        var call = new FetchCall(id, int.Parse(parts[1][1..]), attempt);
        Calls.Add(call);
        return Task.FromResult(Handler(call));
    }
}

/// <summary>An in-memory idempotency key set. The real one is the database's unique index.</summary>
internal sealed class FakeAlertLedger : IAlertLedger
{
    public HashSet<(int LocationId, DateOnly Night)> Sent { get; } = [];

    public List<(int LocationId, DateOnly Night)> IsSentCalls { get; } = [];

    public List<(int LocationId, DateOnly Night, ComposedMessage Message)> SendCalls { get; } = [];

    public int TotalCalls => IsSentCalls.Count + SendCalls.Count;

    /// <summary>Runs inside <see cref="SendOnceAsync"/> before the night is recorded; throw to fail the publish.</summary>
    public Func<int, ComposedMessage, CancellationToken, Task>? Publish { get; set; }

    public Task<bool> IsSentAsync(int locationId, DateOnly nightDate, CancellationToken ct)
    {
        IsSentCalls.Add((locationId, nightDate));
        return Task.FromResult(Sent.Contains((locationId, nightDate)));
    }

    public async Task<SendOutcome> SendOnceAsync(
        int locationId, DateOnly nightDate, NightAssessment assessment, ComposedMessage msg, CancellationToken ct)
    {
        SendCalls.Add((locationId, nightDate, msg));
        if (Sent.Contains((locationId, nightDate)))
            return SendOutcome.AlreadySent;

        if (Publish is not null)
            await Publish(locationId, msg, ct);

        Sent.Add((locationId, nightDate));
        return SendOutcome.Sent;
    }
}

/// <summary>Keeps every log line, with the exception text if any, so a test can search all of it.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly List<string> _lines = [];
    private readonly object _gate = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
                return [.. _lines];
        }
    }

    public string AllText => string.Join("\n", Lines);

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // The structured values too: a secret hidden in an argument is still a leak to a JSON logger.
            var args = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : "";
            var line = $"{logLevel} {category}: {formatter(state, exception)} | {args} {exception}";
            lock (owner._gate)
                owner._lines.Add(line);
        }
    }
}

/// <summary>The captured 2026-10-29 pre-fall-back forecast, mapped the way production maps it.</summary>
internal static class FixtureForecast
{
    private const string Dir = "nws/2026-10-29T1900Z_pre-dst-fallback/";

    /// <summary>All 156 hours, 2026-10-29 14:00 EDT onward. Every PoP is under 50.</summary>
    public static NwsForecast Full()
    {
        var points = FixtureFiles.Load<PointResponse>(Dir + "points.json");
        var hourly = FixtureFiles.Load<HourlyForecastResponse>(Dir + "forecast-hourly.json");
        var grid = FixtureFiles.Load<GridpointResponse>(Dir + "gridpoint.json");
        var p = points.Properties!;
        var point = new NwsPoint(p.TimeZone!, p.ForecastHourly!, p.ForecastGridData);
        return NwsMapper.ToForecast(point, hourly.Properties!.Periods!, grid.Properties);
    }

    /// <summary>The forecast cut off: no hours starting at or after <paramref name="cut"/>.</summary>
    public static NwsForecast Truncated(DateTimeOffset cut)
    {
        var full = Full();
        return full with { Hours = full.Hours.Where(h => h.Start < cut).ToList() };
    }
}

/// <summary>Answers every request with one status and remembers each request line (method and full URL).</summary>
internal sealed class StubHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add($"{request.Method} {request.RequestUri}");
        return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
    }
}
