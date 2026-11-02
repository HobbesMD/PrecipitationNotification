using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Persistence;
using TestSupport;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Worker.Hosting;
using Worker.Nws;

namespace Worker.Tests.Telemetry;

/// <summary>A value that must never appear in exported telemetry, with a label safe to print.</summary>
internal sealed record Secret(string Label, string Value);

/// <summary>One recorded HTTP request as WireMock saw it.</summary>
internal sealed record SeenRequest(string Method, string Path, DateTime At);

/// <summary>Everything the --once host exported (in memory), plus what the fake servers received.</summary>
internal sealed class LeakRun
{
    public int ExitCode { get; init; }

    public List<Activity> Spans { get; init; } = [];

    public List<MetricSnapshot> Metrics { get; init; } = [];

    public List<LogRecord> Logs { get; init; } = [];

    /// <summary>Resource attributes of the tracer, meter and logger providers, labelled by signal.</summary>
    public List<(string Signal, string Key, string Value)> ResourceAttributes { get; init; } = [];

    public List<SeenRequest> Requests { get; init; } = [];

    public List<Secret> Secrets { get; init; } = [];

    public string Stderr { get; init; } = "";
}

/// <summary>What the fake servers answer.</summary>
internal sealed record LeakStubs(int NwsStatus = 200, int OpenMeteoStatus = 200, int NtfyStatus = 200, int HeartbeatStatus = 200);

/// <summary>
/// Builds the real <c>--once</c> host the way <c>Program</c> does (AddWorkerServices + AddOncePass,
/// then BoundedShutdown), against WireMock, with in-memory exporters for traces, metrics and logs
/// added after the Worker's own telemetry. Whatever an OTLP exporter would have been handed ends
/// up in the returned <see cref="LeakRun"/>.
/// </summary>
internal static class LeakTestHost
{
    public const string Topic = "leak-topic-7f3k9q2w";
    public const string HeartbeatUuid = "5b1e0c7a-9d34-4e2f-a8c1-3f6d2b9e0a71";
    public const string DbRolePassword = "leak-db-pw-Zk4m8Xq2";
    public const string PointsFixtureDir = "nws/2026-10-29T1900Z_pre-dst-fallback";
    public const decimal Latitude = 40.7128m;
    public const decimal Longitude = -74.0060m;

    public static readonly DateTimeOffset Now = new(2026, 10, 29, 21, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A fresh migrated database plus a role with a distinctive password (the container's own is
    /// "postgres", a substring of "postgresql", which would make the password search meaningless).
    /// Returns the admin connection string (for seeding) and the role's (for the host).
    /// </summary>
    public static async Task<(string Admin, string Role)> CreateDatabaseAsync(PostgresFixture postgres)
    {
        // This collection runs last, after hundreds of tests left idle pools; free them.
        NpgsqlConnection.ClearAllPools();
        var admin = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        var role = $"leak_{Guid.NewGuid():N}";
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (var sql in new[]
            {
                $"CREATE ROLE {role} LOGIN PASSWORD '{DbRolePassword}'",
                $"GRANT ALL ON ALL TABLES IN SCHEMA public TO {role}",
                $"GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO {role}",
            })
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        var roleString = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Username = role, Password = DbRolePassword }.ConnectionString;
        return (admin.ConnectionString, roleString);
    }

    /// <summary>An active location with a threshold-0 rule and no cached time zone, written as the admin.</summary>
    public static async Task SeedLocationAsync(string adminConnectionString)
    {
        await using var db = new PrecipDbContext(PostgresFixture.Options(adminConnectionString));
        db.Locations.Add(new Location
        {
            Name = "Home",
            Latitude = Latitude,
            Longitude = Longitude,
            IsActive = true,
            TimeZoneId = null,
            CreatedAt = Now,
            AlertRule = new Persistence.AlertRule { ProbabilityThreshold = 0, WindowStart = new TimeOnly(20, 0), WindowEnd = new TimeOnly(7, 0) },
        });
        await db.SaveChangesAsync();
    }

    public static string HeartbeatUrl(WireMockServer server) => $"{server.Url}/ping/{HeartbeatUuid}";

    /// <summary>The secret forms for a run; the password, grid cell and zone are read from what the run really uses.</summary>
    public static List<Secret> SecretsFor(string connectionString)
    {
        var points = System.Text.Json.JsonDocument.Parse(FixtureFiles.Read($"{PointsFixtureDir}/points.json")).RootElement.GetProperty("properties");
        var gridX = points.GetProperty("gridX").GetInt32();
        var gridY = points.GetProperty("gridY").GetInt32();
        var zone = points.GetProperty("timeZone").GetString()!;
        var password = new NpgsqlConnectionStringBuilder(connectionString).Password!;
        return
        [
            new("ntfy topic", Topic),
            new("heartbeat uuid", HeartbeatUuid),
            new("db password", password),
            new("latitude", "40.7128"),
            new("longitude -74.006", "-74.006"),
            new("longitude 74.006", "74.006"),
            new("lat,lon pair", "40.7128,-74.006"),
            new("lat,lon as NwsCoordinates formats it", NwsCoordinates.Format(Latitude, Longitude)),
            new("grid x,y", $"{gridX},{gridY}"),
            new("time zone", zone),
        ];
    }

    public static void ConfigureServer(WireMockServer server, LeakStubs stubs)
    {
        var dir = PointsFixtureDir;

        if (stubs.NwsStatus == 200)
        {
            var points = FixtureFiles.Read($"{dir}/points.json").Replace("https://api.weather.gov", server.Url);
            Serve(server, "/points/*", points, "application/geo+json");
            Serve(server, "/gridpoints/OKX/33,42/forecast/hourly", FixtureFiles.Read($"{dir}/forecast-hourly.json"), "application/geo+json");
            Serve(server, "/gridpoints/OKX/33,42", FixtureFiles.Read($"{dir}/gridpoint.json"), "application/geo+json");
        }
        else
        {
            server.Given(Request.Create().WithPath("/points/*", "/gridpoints/*").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(stubs.NwsStatus));
        }

        server.Given(Request.Create().WithPath("/v1/forecast").UsingGet())
            .RespondWith(stubs.OpenMeteoStatus == 200
                ? Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                    .WithBody(FixtureFiles.Read("open-meteo/2026-10-30T1417Z_pre-dst-fallback/forecast.json"))
                : Response.Create().WithStatusCode(stubs.OpenMeteoStatus));
        server.Given(Request.Create().WithPath("/" + Topic).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(stubs.NtfyStatus));
        server.Given(Request.Create().WithPath("/ping/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(stubs.HeartbeatStatus));
    }

    private static void Serve(WireMockServer server, string path, string body, string contentType) =>
        server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", contentType).WithBody(body));

    public static async Task<LeakRun> RunAsync(
        WireMockServer server, string connectionString, IReadOnlyDictionary<string, string?>? extraConfig = null)
    {
        var config = new AppConfig(
            NtfyBaseUrl: server.Url!,
            NtfyTopic: Topic,
            UserAgent: "leak-test (nobody@example.invalid)",
            ConnectionString: connectionString,
            HeartbeatUrl: HeartbeatUrl(server));

        var spans = new List<Activity>();
        var metrics = new List<MetricSnapshot>();
        var logs = new List<LogRecord>();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var settings = new Dictionary<string, string?>
        {
            ["Nws:BaseUrl"] = server.Url,
            ["OpenMeteo:BaseUrl"] = server.Url,
        };
        foreach (var (key, value) in extraConfig ?? new Dictionary<string, string?>())
            settings[key] = value;
        builder.Configuration.AddInMemoryCollection(settings);

        // First, so TryAdd(TimeProvider.System) in AddWorkerServices keeps it.
        var clock = new FakeTimeProvider(Now);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddWorkerServices(config, dryRun: false);
        builder.Services.AddOncePass();

        // After AddWorkerServices: these exporters run after the TelemetryRedactor processor, so they
        // see exactly what OTLP would have been sent.
        builder.Services.ConfigureOpenTelemetryTracerProvider(b => b.AddInMemoryExporter(spans));
        builder.Services.ConfigureOpenTelemetryMeterProvider(b => b.AddInMemoryExporter(metrics));
        builder.Services.ConfigureOpenTelemetryLoggerProvider(b => b.AddInMemoryExporter(logs));

        var host = builder.Build();
        var exit = host.Services.GetRequiredService<ExitCodeHolder>();
        var resource = new List<(string, string, string)>();
        AddResource(resource, "traces", host.Services.GetRequiredService<TracerProvider>().GetResource());
        AddResource(resource, "metrics", host.Services.GetRequiredService<MeterProvider>().GetResource());
        AddResource(resource, "logs", host.Services.GetRequiredService<LoggerProvider>().GetResource());

        var stderr = new StringWriter();
        // Polly waits on the injected TimeProvider for its backoff and the heartbeat for its attempt
        // timeout, so a clock that never moves would hang a retry. Tick it in real time instead.
        using var ticking = new CancellationTokenSource();
        var ticker = Task.Run(async () =>
        {
            while (!ticking.IsCancellationRequested)
            {
                await Task.Delay(5);
                clock.Advance(TimeSpan.FromMilliseconds(5));
            }
        });
        await BoundedShutdown.RunAsync(host, BoundedShutdown.FlushBudget, stderr);
        await ticking.CancelAsync();
        await ticker;
        NpgsqlConnection.ClearAllPools();

        return new LeakRun
        {
            ExitCode = exit.Code,
            Spans = spans,
            Metrics = metrics,
            Logs = logs,
            ResourceAttributes = resource,
            Requests = server.LogEntries
                .OrderBy(e => e.RequestMessage?.DateTime)
                .Select(e => new SeenRequest(e.RequestMessage?.Method ?? "", e.RequestMessage?.Path ?? "", e.RequestMessage?.DateTime ?? default))
                .ToList(),
            Secrets = SecretsFor(connectionString),
            Stderr = stderr.ToString(),
        };
    }

    private static void AddResource(List<(string, string, string)> into, string signal, OpenTelemetry.Resources.Resource resource)
    {
        foreach (var (key, value) in resource.Attributes)
            into.Add((signal, key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""));
    }
}
