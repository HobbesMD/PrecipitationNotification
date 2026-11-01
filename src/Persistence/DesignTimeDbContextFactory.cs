using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the context without starting a host. Generating a
/// migration never connects, so the localhost default (no password: nothing
/// secret in source) is enough; <c>dotnet ef database update</c> against a real
/// server reads <c>ConnectionStrings__Precip</c>, the same variable the apps use.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PrecipDbContext>
{
    public const string ConnectionStringVariable = "ConnectionStrings__Precip";

    private const string LocalDefault = "Host=localhost;Port=5432;Database=precip;Username=postgres";

    public PrecipDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } cs
            ? cs
            : LocalDefault;

        var options = PrecipDbContext.Configure(new DbContextOptionsBuilder<PrecipDbContext>(), connectionString);
        return new PrecipDbContext(options.Options);
    }
}
