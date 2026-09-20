using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ta.Backend.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialEdgeIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device",
                columns: table => new
                {
                    device_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    device_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    device_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    device_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    device_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device_credential_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device", x => x.device_id);
                    table.CheckConstraint("ck_device_id", "length(device_id) BETWEEN 1 AND 256");
                    table.CheckConstraint("ck_device_token_hash", "device_token_hash ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "gallery_release",
                columns: table => new
                {
                    gallery_release_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    gallery_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    gallery_model_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    gallery_etag = table.Column<string>(type: "character varying(66)", maxLength: 66, nullable: false),
                    gallery_document = table.Column<string>(type: "text", nullable: false),
                    gallery_template_count = table.Column<int>(type: "integer", nullable: false),
                    gallery_published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gallery_release", x => x.gallery_release_id);
                    table.CheckConstraint("ck_gallery_document_size", "octet_length(gallery_document) <= 33554432");
                    table.CheckConstraint("ck_gallery_model_sha256", "gallery_model_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_gallery_template_count", "gallery_template_count BETWEEN 0 AND 10000");
                });

            migrationBuilder.CreateTable(
                name: "attendance_event",
                columns: table => new
                {
                    attendance_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    attendance_identity_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    attendance_gallery_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    attendance_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attendance_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attendance_payload = table.Column<string>(type: "jsonb", nullable: false),
                    attendance_payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attendance_event", x => x.attendance_event_id);
                    table.CheckConstraint("ck_attendance_occurred_at", "attendance_occurred_at > '1970-01-01T00:00:00Z'::timestamptz");
                    table.CheckConstraint("ck_attendance_payload_hash", "attendance_payload_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_attendance_payload_object", "jsonb_typeof(attendance_payload) = 'object'");
                    table.ForeignKey(
                        name: "fk_attendance_event_device",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "device_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_device_occurred_at",
                table: "attendance_event",
                columns: new[] { "device_id", "attendance_occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_identity_occurred_at",
                table: "attendance_event",
                columns: new[] { "attendance_identity_id", "attendance_occurred_at" });

            migrationBuilder.CreateIndex(
                name: "uq_device_token_hash",
                table: "device",
                column: "device_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_gallery_version",
                table: "gallery_release",
                column: "gallery_version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attendance_event");

            migrationBuilder.DropTable(
                name: "gallery_release");

            migrationBuilder.DropTable(
                name: "device");
        }
    }
}
