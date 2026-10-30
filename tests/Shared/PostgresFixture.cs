using Microsoft.EntityFrameworkCore;
using Npgsql;
using Persistence;
using Testcontainers.PostgreSql;
using TestSupport;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace TestSupport;

/// <summary>
/// One real Postgres per test assembly, with a fresh database per test.
///
/// Migrations run once, into a template database; each
/// <see cref="CreateDatabaseAsync"/> is then a <c>CREATE DATABASE … TEMPLATE</c>,
/// a file copy that takes milliseconds. Tests get full isolation (their own
/// constraints, sequences and rows) without paying for a migration each, and the
/// schema they run against is the one the migrations build, not
/// <c>EnsureCreated</c>'s idea of it.
///
/// The container starts on first use, not when the assembly starts: the hundreds of
/// pure decision-engine tests in the same assembly must still run without Docker.
///
/// Source-linked into Worker.Tests and Api.Tests (tests/Shared). Connection strings
/// here are throwaway test credentials, but are still never printed.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string Image = "postgres:17-alpine";
    private const string TemplateDatabase = "precip_template";

    // Built on first use too: Build() itself fails when Docker isn't reachable.
    private PostgreSqlContainer? _container;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly Lazy<Task> _started;

    public PostgresFixture() => _started = new Lazy<Task>(StartAsync);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Creates an empty, fully migrated database for one test and returns its
    /// connection string. Databases are never dropped: the container is.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        await _started.Value;

        var name = $"test_{Guid.NewGuid():N}";

        // Serialized: CREATE DATABASE fails if another session is using the template,
        // and a concurrent copy from it counts.
        await _createLock.WaitAsync();
        try
        {
            await using var admin = new NpgsqlConnection(Container.GetConnectionString());
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE {TemplateDatabase}", admin);
            await create.ExecuteNonQueryAsync();
        }
        finally
        {
            _createLock.Release();
        }

        return ConnectionStringFor(name, pooling: true);
    }

    /// <summary>Context options for a database from <see cref="CreateDatabaseAsync"/>.</summary>
    public static DbContextOptions<PrecipDbContext> Options(string connectionString) =>
        PrecipDbContext.Configure(new DbContextOptionsBuilder<PrecipDbContext>(), connectionString).Options;

    public async ValueTask DisposeAsync()
    {
        _createLock.Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private PostgreSqlContainer Container =>
        _container ?? throw new InvalidOperationException("The Postgres container has not been started.");

    private async Task StartAsync()
    {
        _container = new PostgreSqlBuilder(Image).Build();
        await _container.StartAsync();

        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {TemplateDatabase}", admin);
            await create.ExecuteNonQueryAsync();
        }

        // No pooling: a pooled idle connection to the template would make every
        // CREATE DATABASE … TEMPLATE fail with "being accessed by other users".
        await using (var db = new PrecipDbContext(Options(ConnectionStringFor(TemplateDatabase, pooling: false))))
            await db.Database.MigrateAsync();

        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            // Lock the template against accidental use: nothing may connect to it again.
            await using var seal = new NpgsqlCommand(
                $"ALTER DATABASE {TemplateDatabase} WITH IS_TEMPLATE true ALLOW_CONNECTIONS false", admin);
            await seal.ExecuteNonQueryAsync();
        }
    }

    private string ConnectionStringFor(string database, bool pooling) =>
        new NpgsqlConnectionStringBuilder(Container.GetConnectionString())
        {
            Database = database,
            Pooling = pooling,
        }.ConnectionString;
}
