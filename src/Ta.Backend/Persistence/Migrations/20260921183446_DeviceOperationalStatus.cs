using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ta.Backend.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceOperationalStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_operational_status",
                columns: table => new
                {
                    device_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    device_last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device_runtime_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    device_frame_age_ms = table.Column<long>(type: "bigint", nullable: true),
                    device_installed_gallery_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    device_model_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    device_installed_template_count = table.Column<int>(type: "integer", nullable: false),
                    device_outbox_pending_count = table.Column<long>(type: "bigint", nullable: false),
                    device_outbox_dead_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_operational_status", x => x.device_id);
                    table.CheckConstraint("ck_device_operational_counts", "device_installed_template_count BETWEEN 0 AND 10000 AND device_outbox_pending_count >= 0 AND device_outbox_dead_count >= 0");
                    table.CheckConstraint("ck_device_operational_frame_age", "device_frame_age_ms IS NULL OR device_frame_age_ms BETWEEN 0 AND 86400000");
                    table.CheckConstraint("ck_device_operational_model", "device_model_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_device_operational_state", "device_runtime_state IN ('starting','running','persistence_blocked','gallery_error','stopping')");
                    table.ForeignKey(
                        name: "fk_device_operational_status_device_id",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "device_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_operational_status");
        }
    }
}
