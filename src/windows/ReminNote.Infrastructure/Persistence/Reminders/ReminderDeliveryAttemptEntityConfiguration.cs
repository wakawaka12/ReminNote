using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Infrastructure.Persistence.Reminders;

public sealed class ReminderDeliveryAttemptEntityConfiguration
{
    public static void Configure(EntityTypeBuilder<ReminderDeliveryAttemptEntity> builder)
    {
        builder.ToTable("reminder_delivery_attempts", table =>
        {
            table.HasCheckConstraint(
                "ck_reminder_delivery_attempts_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("attempt_id"));
            table.HasCheckConstraint(
                "ck_reminder_delivery_attempts_instance_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("instance_id"));
            table.HasCheckConstraint(
                "ck_reminder_delivery_attempts_channel",
                "channel IN ('TOAST','TRAY','WIDGET','SOUND','WAKE_TIMER')");
            table.HasCheckConstraint(
                "ck_reminder_delivery_attempts_outcome",
                "outcome IN ('DELIVERED','BLOCKED','UNAVAILABLE','FAILED','SUPPRESSED_QUIET_HOURS','NOT_ATTEMPTED')");
            table.HasCheckConstraint(
                "ck_reminder_delivery_attempts_error_code",
                $"error_code IS NULL OR length(trim(error_code)) BETWEEN 1 AND {ReminderPersistenceConventions.MaxErrorCodeBytes}");
            table.HasCheckConstraint(
                "ck_reminder_delivery_attempts_timestamp_format",
                ReminderPersistenceConventions.InstantCheck("attempted_at_utc"));
        });

        builder.HasKey(entity => entity.AttemptId)
            .HasName("pk_reminder_delivery_attempts");
        builder.Property(entity => entity.AttemptId)
            .HasColumnName("attempt_id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.GuidConverter)
            .ValueGeneratedNever();
        builder.Property(entity => entity.InstanceId)
            .HasColumnName("instance_id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.GuidConverter)
            .IsRequired();
        builder.Property(entity => entity.Channel)
            .HasColumnName("channel")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsRequired();
        builder.Property(entity => entity.AttemptedAtUtc)
            .HasColumnName("attempted_at_utc")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.InstantConverter)
            .IsRequired();
        builder.Property(entity => entity.Outcome)
            .HasColumnName("outcome")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsRequired();
        builder.Property(entity => entity.ErrorCode)
            .HasColumnName("error_code")
            .HasColumnType("TEXT")
            .HasMaxLength(ReminderPersistenceConventions.MaxErrorCodeBytes);

        builder.HasIndex(entity => new { entity.InstanceId, entity.AttemptedAtUtc })
            .HasDatabaseName("ix_reminder_delivery_attempts_instance_time");
        builder.HasIndex(entity => new { entity.InstanceId, entity.Channel })
            .HasDatabaseName("ix_reminder_delivery_attempts_instance_channel");

        builder.HasOne<ReminderInstanceEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.InstanceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
