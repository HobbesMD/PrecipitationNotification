using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Persistence;
using TestSupport;
using Worker.Pass;

namespace Worker.Tests.Pass;

/// <summary>
/// A <see cref="NightlyPass"/> over a real, migrated Postgres database (a fresh copy per test)
/// with the ledger, cache and forecast source faked. The clock starts at 17:00 EDT on
/// 2026-10-29 (the evening of the captured forecast), so the night being evaluated is 2026-10-29.
/// </summary>
internal sealed class PassHarness : IAsyncDisposable
{
    /// <summary>The night the captured forecast's evening falls in.</summary>
    public static readonly DateOnly Night = new(2026, 10, 29);

    private PassHarness(PrecipDbContext db) => Db = db;

    public PrecipDbContext Db { get; }

    public FakeAlertLedger Ledger { get; } = new();

    public FakeGridpointCache Cache { get; } = new();

    public FakeForecastSource Forecasts { get; } = new();

    public LogCapture Logs { get; } = new();

    public FakeTimeProvider Clock { get; } =
        new(new DateTimeOffset(2026, 10, 29, 21, 0, 0, TimeSpan.Zero));

    public static async Task<PassHarness> CreateAsync(PostgresFixture postgres) =>
        new(new PrecipDbContext(PostgresFixture.Options(await postgres.CreateDatabaseAsync())));

    /// <summary>A harness over a context the caller built, e.g. one pointed at a dead server.</summary>
    public static PassHarness Over(PrecipDbContext db) => new(db);

    /// <summary>
    /// Adds a location with a 20:00 to 07:00 rule and returns its id. Threshold 0 makes every
    /// hour a hit (the captured forecast is dry); 50 makes the night dry.
    /// </summary>
    public async Task<int> AddLocationAsync(
        string name, int threshold = 0, bool active = true, bool withRule = true,
        decimal latitude = 40.7128m, decimal longitude = -74.0060m)
    {
        var location = new Location
        {
            Name = name,
            Latitude = latitude,
            Longitude = longitude,
            IsActive = active,
            CreatedAt = Clock.GetUtcNow(),
            AlertRule = withRule
                ? new Persistence.AlertRule
                {
                    ProbabilityThreshold = threshold,
                    WindowStart = new TimeOnly(20, 0),
                    WindowEnd = new TimeOnly(7, 0),
                }
                : null,
        };
        Db.Locations.Add(location);
        await Db.SaveChangesAsync();
        return location.Id;
    }

    public Task<int> RunAsync(bool dryRun = false, CancellationToken ct = default)
    {
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        var pass = new NightlyPass(
            Db, Cache, Forecasts, Ledger, Clock, new PassOptions(dryRun), new Logger<NightlyPass>(factory));
        return pass.RunAsync(ct);
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

/// <summary>An empty database (no schema at all) on the fixture's server, for migration tests.</summary>
internal static class EmptyDatabase
{
    public static async Task<string> CreateAsync(PostgresFixture postgres)
    {
        var template = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        var name = $"empty_{Guid.NewGuid():N}";

        var admin = new NpgsqlConnectionStringBuilder(template.ConnectionString) { Database = "postgres", Pooling = false };
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
            await create.ExecuteNonQueryAsync();
        }

        template.Database = name;
        template.Pooling = false;
        return template.ConnectionString;
    }
}

/// <summary>A connection string for a server that is not there. The password is for the leak checks.</summary>
internal static class DeadDatabase
{
    public const string Password = "pw-secret-abc";

    public const string ConnectionString =
        $"Host=127.0.0.1;Port=1;Database=precip;Username=u;Password={Password};Timeout=2;Pooling=false";

    public static PrecipDbContext CreateContext() =>
        new(PrecipDbContext.Configure(new DbContextOptionsBuilder<PrecipDbContext>(), ConnectionString).Options);
}
