using Microsoft.EntityFrameworkCore;
using Persistence;
using Worker.Pass;

namespace Worker.Hosting;

/// <summary>
/// <c>--migrate</c>: applies the EF migrations and exits. Never run automatically: a schema
/// change is a deliberate act, not a side effect of a nightly job starting up.
/// </summary>
internal static class Migrator
{
    /// <summary>Returns 0, or <see cref="ExitCodes.Database"/> on any failure. Prints no connection details.</summary>
    public static async Task<int> RunAsync(string connectionString, TextWriter output, CancellationToken ct)
    {
        try
        {
            var options = PrecipDbContext.Configure(new DbContextOptionsBuilder<PrecipDbContext>(), connectionString).Options;
            await using var db = new PrecipDbContext(options);

            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            await db.Database.MigrateAsync(ct);
            output.WriteLine(pending.Count == 0
                ? "Database is up to date."
                : $"Applied {pending.Count} migration(s): {string.Join(", ", pending)}.");
            return ExitCodes.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            output.WriteLine($"Migration failed: {ex.GetType().Name}: {ex.Message}");
            return ExitCodes.Database;
        }
    }
}
