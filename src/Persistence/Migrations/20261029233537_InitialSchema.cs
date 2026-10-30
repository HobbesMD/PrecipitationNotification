using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "locations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    latitude = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    longitude = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    time_zone_id = table.Column<string>(type: "text", nullable: true),
                    forecast_hourly_url = table.Column<string>(type: "text", nullable: true),
                    forecast_grid_data_url = table.Column<string>(type: "text", nullable: true),
                    gridpoint_resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_locations", x => x.id);
                    table.CheckConstraint("ck_locations_latitude", "latitude BETWEEN -90 AND 90");
                    table.CheckConstraint("ck_locations_longitude", "longitude BETWEEN -180 AND 180");
                });

            migrationBuilder.CreateTable(
                name: "alert_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    location_id = table.Column<int>(type: "integer", nullable: false),
                    night_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    peak_probability = table.Column<int>(type: "integer", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_history", x => x.id);
                    table.CheckConstraint("ck_alert_history_status", "status IN ('Pending', 'Sent')");
                    table.ForeignKey(
                        name: "fk_alert_history_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    location_id = table.Column<int>(type: "integer", nullable: false),
                    probability_threshold = table.Column<int>(type: "integer", nullable: false),
                    window_start = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    window_end = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_rules", x => x.id);
                    table.CheckConstraint("ck_alert_rules_probability_threshold", "probability_threshold BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_alert_rules_window_crosses_midnight", "window_start > window_end");
                    table.ForeignKey(
                        name: "fk_alert_rules_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_alert_history_location_night",
                table: "alert_history",
                columns: new[] { "location_id", "night_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_location_id",
                table: "alert_rules",
                column: "location_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_locations_name",
                table: "locations",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_history");

            migrationBuilder.DropTable(
                name: "alert_rules");

            migrationBuilder.DropTable(
                name: "locations");
        }
    }
}
