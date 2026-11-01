using Microsoft.EntityFrameworkCore;
using Persistence;
using TestSupport;
using Worker.Hosting;
using Worker.Tests.Pass;

namespace Worker.Tests.Hosting;

/// <summary><c>--migrate</c>: applies the schema, exits 0, or 6 on failure without echoing the connection string.</summary>
public sealed class MigratorTests(PostgresFixture postgres)
{
    private static PrecipDbContext Open(string connectionString) =>
        new(PrecipDbContext.Configure(new DbContextOptionsBuilder<PrecipDbContext>(), connectionString).Options);

    [Fact]
    public async Task Empty_database_gets_every_migration_and_exits_0()
    {
        var cs = await EmptyDatabase.CreateAsync(postgres);
        var output = new StringWriter();

        var code = await Migrator.RunAsync(cs, output, TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Contains("Applied", output.ToString());
        await using var db = Open(cs);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Already_migrated_database_is_a_no_op_that_exits_0()
    {
        var cs = await postgres.CreateDatabaseAsync();
        var output = new StringWriter();

        var code = await Migrator.RunAsync(cs, output, TestContext.Current.CancellationToken);

        Assert.Equal(0, code);
        Assert.Contains("up to date", output.ToString());
    }

    [Fact]
    public async Task Unreachable_database_exits_6_and_prints_no_password()
    {
        var output = new StringWriter();

        var code = await Migrator.RunAsync(DeadDatabase.ConnectionString, output, TestContext.Current.CancellationToken);

        Assert.Equal(6, code);
        Assert.Contains("Migration failed", output.ToString());
        Assert.DoesNotContain(DeadDatabase.Password, output.ToString());
    }
}
