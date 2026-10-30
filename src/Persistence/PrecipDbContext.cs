using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace Persistence;

/// <summary>
/// The schema. Invariants that matter are enforced by Postgres, not just by C#:
/// the one-alert-per-night unique index, the coordinate and threshold ranges, the
/// midnight-crossing window and the status names are all constraints, so a second
/// writer (the API, a psql session, a future job) can't get around them.
///
/// Names are snake_case via EFCore.NamingConventions, applied in
/// <see cref="OnConfiguring"/> rather than at each registration so no host can
/// forget it and end up with a model that doesn't match the migrated tables.
/// </summary>
public sealed class PrecipDbContext(DbContextOptions<PrecipDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Raised as <c>PostgresException.ConstraintName</c> (SQLSTATE 23505) when a second
    /// row for the same location and night is inserted. The ledger keys off this name.
    /// </summary>
    public const string AlertHistoryUniqueIndex = "ux_alert_history_location_night";

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<AlertRule> AlertRules => Set<AlertRule>();

    public DbSet<AlertHistory> AlertHistory => Set<AlertHistory>();

    /// <summary>
    /// One place for the provider setup, so the Worker, the API, the design-time
    /// factory and the test fixture all build the same options.
    /// </summary>
    public static DbContextOptionsBuilder<PrecipDbContext> Configure(
        DbContextOptionsBuilder<PrecipDbContext> builder, string connectionString) =>
        builder.UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Npgsql 9+ tries GSSAPI (Kerberos) session encryption first. The chiseled runtime
            // image has no libgssapi_krb5, so every connect printed a "cannot load library" error
            // to stderr before falling back. We authenticate by password over TLS; never Kerberos.
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString);

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Location>(e =>
        {
            e.ToTable("locations", t =>
            {
                t.HasCheckConstraint("ck_locations_latitude", "latitude BETWEEN -90 AND 90");
                t.HasCheckConstraint("ck_locations_longitude", "longitude BETWEEN -180 AND 180");
            });

            e.HasKey(l => l.Id);

            e.Property(l => l.Name).IsRequired();
            e.HasIndex(l => l.Name).IsUnique();

            // numeric(p,4): NWS takes at most 4 decimal places; a fifth is an error, not a rounding.
            e.Property(l => l.Latitude).HasPrecision(6, 4);
            e.Property(l => l.Longitude).HasPrecision(7, 4);

            // The sentinel is true so EF sends an explicit false and omits true, letting the
            // database default supply it. With the usual sentinel (false), EF would omit false
            // and the default would silently turn an inactive location active.
            e.Property(l => l.IsActive).HasDefaultValue(true).HasSentinel(true);

            e.Property(l => l.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<AlertRule>(e =>
        {
            e.ToTable("alert_rules", t =>
            {
                t.HasCheckConstraint(
                    "ck_alert_rules_probability_threshold", "probability_threshold BETWEEN 0 AND 100");
                // D5: the window must cross midnight. A same-day window would resolve to 28 hours.
                t.HasCheckConstraint("ck_alert_rules_window_crosses_midnight", "window_start > window_end");
            });

            e.HasKey(r => r.Id);

            // One rule per location (the FK gets a unique index), removed with its location.
            e.HasOne(r => r.Location)
                .WithOne(l => l.AlertRule)
                .HasForeignKey<AlertRule>(r => r.LocationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlertHistory>(e =>
        {
            var statuses = string.Join(", ", Enum.GetNames<AlertStatus>().Select(n => $"'{n}'"));
            e.ToTable("alert_history", t =>
                t.HasCheckConstraint("ck_alert_history_status", $"status IN ({statuses})"));

            e.HasKey(h => h.Id);

            // RESTRICT: deleting a location must not erase the record of what was sent for it.
            e.HasOne(h => h.Location)
                .WithMany()
                .HasForeignKey(h => h.LocationId)
                .OnDelete(DeleteBehavior.Restrict);

            e.Property(h => h.Status).HasConversion<string>().IsRequired();

            // The idempotency key (CLAUDE.md problem 2), enforced by the database.
            e.HasIndex(h => new { h.LocationId, h.NightDate })
                .IsUnique()
                .HasDatabaseName(AlertHistoryUniqueIndex);
        });
    }
}
