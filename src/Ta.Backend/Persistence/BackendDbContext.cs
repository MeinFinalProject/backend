using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ta.Backend.Features.Attendance;
using Ta.Backend.Features.Biometrics;
using Ta.Backend.Features.Devices;

namespace Ta.Backend.Persistence;

public sealed class BackendDbContext(DbContextOptions<BackendDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<GalleryRelease> GalleryReleases => Set<GalleryRelease>();
    public DbSet<AttendanceEvent> AttendanceEvents => Set<AttendanceEvent>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Device>(b =>
        {
            b.ToTable("device", t =>
            {
                t.HasCheckConstraint("ck_device_id", "length(device_id) BETWEEN 1 AND 256");
                t.HasCheckConstraint("ck_device_token_hash", "device_token_hash ~ '^[0-9a-f]{64}$'");
            });
            b.HasKey(d => d.DeviceId).HasName("pk_device");
            b.Property(d => d.DeviceId).HasMaxLength(256);
            b.Property(d => d.DeviceName).HasMaxLength(200).IsRequired();
            b.Property(d => d.DeviceTokenHash).HasMaxLength(64).IsRequired();
            b.HasIndex(d => d.DeviceTokenHash).IsUnique().HasDatabaseName("uq_device_token_hash");
        });
        model.Entity<GalleryRelease>(b =>
        {
            b.ToTable("gallery_release", t =>
            {
                t.HasCheckConstraint("ck_gallery_template_count", "gallery_template_count BETWEEN 0 AND 10000");
                t.HasCheckConstraint("ck_gallery_model_sha256", "gallery_model_sha256 ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_gallery_document_size", "octet_length(gallery_document) <= 33554432");
            });
            b.HasKey(g => g.GalleryReleaseId).HasName("pk_gallery_release");
            b.Property(g => g.GalleryVersion).HasMaxLength(256).IsRequired();
            b.Property(g => g.GalleryModelSha256).HasMaxLength(64).IsRequired();
            b.Property(g => g.GalleryEtag).HasMaxLength(66).IsRequired();
            b.Property(g => g.GalleryDocument).IsRequired();
            b.HasIndex(g => g.GalleryVersion).IsUnique().HasDatabaseName("uq_gallery_version");
        });
        model.Entity<AttendanceEvent>(b =>
        {
            b.ToTable("attendance_event", t =>
            {
                t.HasCheckConstraint("ck_attendance_payload_hash", "attendance_payload_hash ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_attendance_payload_object", "jsonb_typeof(attendance_payload) = 'object'");
                t.HasCheckConstraint("ck_attendance_occurred_at", "attendance_occurred_at > '1970-01-01T00:00:00Z'::timestamptz");
            });
            b.HasKey(e => e.AttendanceEventId).HasName("pk_attendance_event");
            b.Property(e => e.AttendanceEventId).ValueGeneratedNever();
            b.Property(e => e.DeviceId).HasMaxLength(256).IsRequired();
            b.Property(e => e.AttendanceIdentityId).HasMaxLength(256).IsRequired();
            b.Property(e => e.AttendanceGalleryVersion).HasMaxLength(256).IsRequired();
            b.Property(e => e.AttendancePayload).HasColumnType("jsonb").IsRequired();
            b.Property(e => e.AttendancePayloadHash).HasMaxLength(64).IsRequired();
            b.HasOne<Device>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_attendance_event_device");
            b.HasIndex(e => new { e.DeviceId, e.AttendanceOccurredAt }).HasDatabaseName("ix_attendance_device_occurred_at");
            b.HasIndex(e => new { e.AttendanceIdentityId, e.AttendanceOccurredAt }).HasDatabaseName("ix_attendance_identity_occurred_at");
        });
        model.Entity<DeviceOperationalStatus>(b =>
        {
            b.ToTable("device_operational_status", t =>
            {
                t.HasCheckConstraint("ck_device_operational_counts", "device_installed_template_count BETWEEN 0 AND 10000 AND device_outbox_pending_count >= 0 AND device_outbox_dead_count >= 0");
                t.HasCheckConstraint("ck_device_operational_frame_age", "device_frame_age_ms IS NULL OR device_frame_age_ms BETWEEN 0 AND 86400000");
                t.HasCheckConstraint("ck_device_operational_state", "device_runtime_state IN ('starting','running','persistence_blocked','gallery_error','stopping')");
                t.HasCheckConstraint("ck_device_operational_model", "device_model_sha256 ~ '^[0-9a-f]{64}$'");
            });
            b.HasKey(s => s.DeviceId).HasName("pk_device_operational_status");
            b.Property(s => s.DeviceId).HasMaxLength(256);
            b.Property(s => s.DeviceRuntimeState).HasMaxLength(32);
            b.Property(s => s.DeviceInstalledGalleryVersion).HasMaxLength(256);
            b.Property(s => s.DeviceModelSha256).HasMaxLength(64);
            b.HasOne<Device>().WithOne().HasForeignKey<DeviceOperationalStatus>(s => s.DeviceId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_device_operational_status_device");
        });
        model.ConfigureAcademic();
        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
    }
}
