using Npgsql;
using Persistence;
using TestSupport;

namespace Api.Tests;

/// <summary>
/// The invariants live in the schema, so prove them in raw SQL, beneath EF: a
/// second writer that bypasses the C# validation (a psql session, a future job)
/// must still be stopped by Postgres itself.
/// </summary>
public sealed class SchemaSmokeTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 23, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Second_alert_for_the_same_location_and_night_violates_the_unique_index()
    {
        await using var conn = await OpenAsync();
        var locationId = await InsertLocationAsync(conn);

        await InsertHistoryAsync(conn, locationId, new DateOnly(2026, 10, 29));

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => InsertHistoryAsync(conn, locationId, new DateOnly(2026, 10, 29)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState); // 23505
        Assert.Equal("23505", ex.SqlState);
        Assert.Equal("ux_alert_history_location_night", ex.ConstraintName);
        Assert.Equal(PrecipDbContext.AlertHistoryUniqueIndex, ex.ConstraintName);

        // The key is the pair: the next night for the same location is a new alert.
        await InsertHistoryAsync(conn, locationId, new DateOnly(2026, 10, 30));
    }

    [Fact]
    public async Task Threshold_above_100_violates_the_check_constraint()
    {
        await using var conn = await OpenAsync();
        var locationId = await InsertLocationAsync(conn);

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO alert_rules (location_id, probability_threshold, window_start, window_end)
            VALUES (@location_id, 101, TIME '20:00', TIME '07:00')
            """, conn);
        cmd.Parameters.AddWithValue("location_id", locationId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("23514", ex.SqlState); // check_violation
        Assert.Equal("ck_alert_rules_probability_threshold", ex.ConstraintName);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(await postgres.CreateDatabaseAsync());
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    /// <summary>A made-up location; real coordinates never appear in source (D21).</summary>
    private static async Task<int> InsertLocationAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO locations (name, latitude, longitude, created_at)
            VALUES ('smoke-test', 10.0000, 20.0000, @now)
            RETURNING id
            """, conn);
        cmd.Parameters.AddWithValue("now", Now);
        return (int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task InsertHistoryAsync(NpgsqlConnection conn, int locationId, DateOnly night)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO alert_history (location_id, night_date, status, attempts, created_at, last_attempt_at)
            VALUES (@location_id, @night, 'Pending', 1, @now, @now)
            """, conn);
        cmd.Parameters.AddWithValue("location_id", locationId);
        cmd.Parameters.AddWithValue("night", night);
        cmd.Parameters.AddWithValue("now", Now);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
