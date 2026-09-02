using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Infrastructure.Persistence.Reminders;

public sealed class ReminderInstanceEntityConfiguration
{
    public static void Configure(EntityTypeBuilder<ReminderInstanceEntity> builder)
    {
        builder.ToTable("reminder_instances", table =>
        {
            table.HasCheckConstraint(
                "ck_reminder_instances_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("id"));
            table.HasCheckConstraint(
                "ck_reminder_instances_schedule_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("schedule_id"));
            table.HasCheckConstraint(
                "ck_reminder_instances_rule_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("rule_id"));
            table.HasCheckConstraint(
                "ck_reminder_instances_occurrence_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("occurrence_id"));
            table.HasCheckConstraint(
                "ck_reminder_instances_logical_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("logical_reminder_id"));
            table.HasCheckConstraint(
                "ck_reminder_instances_attempt_ordinal",
                "attempt_ordinal >= 1");
            table.HasCheckConstraint(
                "ck_reminder_instances_purpose_snapshot",
                "purpose_snapshot IN ('TASK_PRE_START','TASK_START','TASK_RANGE_END','TASK_CUSTOM')");
            table.HasCheckConstraint(
                "ck_reminder_instances_priority_snapshot",
                "priority_snapshot IN ('LOW','NORMAL','HIGH')");
            table.HasCheckConstraint(
                "ck_reminder_instances_pinned_snapshot",
                "pinned_snapshot IN (0,1)");
            table.HasCheckConstraint(
                "ck_reminder_instances_lifecycle",
                "lifecycle IN ('UNREAD','READ','RESOLVED')");
            table.HasCheckConstraint(
                "ck_reminder_instances_resolution_action",
                "resolution_action IS NULL OR resolution_action IN ('DONE','SNOOZE','WATCHED','WATCH_LATER','SKIP','IGNORE')");
            table.HasCheckConstraint(
                "ck_reminder_instances_lifecycle_shape",
                "(lifecycle = 'UNREAD' AND read_at_utc IS NULL AND resolved_at_utc IS NULL AND resolution_action IS NULL) " +
                "OR (lifecycle = 'READ' AND read_at_utc IS NOT NULL AND resolved_at_utc IS NULL AND resolution_action IS NULL) " +
                "OR (lifecycle = 'RESOLVED' AND read_at_utc IS NOT NULL AND resolved_at_utc IS NOT NULL AND resolution_action IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_reminder_instances_timestamp_order",
                "(read_at_utc IS NULL OR read_at_utc >= triggered_at_utc) " +
                "AND (resolved_at_utc IS NULL OR (read_at_utc IS NOT NULL AND resolved_at_utc >= read_at_utc))");
            table.HasCheckConstraint(
                "ck_reminder_instances_timestamps_format",
                ReminderPersistenceConventions.InstantCheck("triggered_at_utc") + " AND " +
                ReminderPersistenceConventions.InstantCheck("read_at_utc", nullable: true) + " AND " +
                ReminderPersistenceConventions.InstantCheck("resolved_at_utc", nullable: true));
        });

        builder.HasKey(entity => entity.Id)
            .HasName("pk_reminder_instances");
        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.GuidConverter)
            .ValueGeneratedNever();
        builder.Property(entity => entity.ScheduleId)
            .HasColumnName("schedule_id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.GuidConverter)
            .IsRequired();
        builder.Property(entity => entity.RuleId)
            .HasColumnName("rule_id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.GuidConverter)
            .IsRequired();
        builder.Property(entity => entity.OccurrenceId)
            .HasColumnName("occurrence_id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.GuidConverter)
            .IsRequired();
        builder.Property(entity => entity.LogicalReminderId)
            .HasColumnName("logical_reminder_id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.GuidConverter)
            .IsRequired();
        builder.Property(entity => entity.AttemptOrdinal)
            .HasColumnName("attempt_ordinal")
            .HasColumnType("INTEGER")
            .IsRequired();
        builder.Property(entity => entity.PurposeSnapshot)
            .HasColumnName("purpose_snapshot")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsRequired();
        builder.Property(entity => entity.PrioritySnapshot)
            .HasColumnName("priority_snapshot")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsRequired();
        builder.Property(entity => entity.PinnedSnapshot)
            .HasColumnName("pinned_snapshot")
            .HasColumnType("INTEGER")
            .HasConversion<int>()
            .IsRequired();
        builder.Property(entity => entity.TriggeredAtUtc)
            .HasColumnName("triggered_at_utc")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.InstantConverter)
            .IsRequired();
        builder.Property(entity => entity.Lifecycle)
            .HasColumnName("lifecycle")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsConcurrencyToken()
            .IsRequired();
        builder.Property(entity => entity.ReadAtUtc)
            .HasColumnName("read_at_utc")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.NullableInstantConverter);
        builder.Property(entity => entity.ResolvedAtUtc)
            .HasColumnName("resolved_at_utc")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.NullableInstantConverter);
        builder.Property(entity => entity.ResolutionAction)
            .HasColumnName("resolution_action")
            .HasColumnType("TEXT")
            .HasConversion<string?>();

        builder.HasIndex(entity => entity.ScheduleId)
            .HasDatabaseName("ux_reminder_instances_schedule_id")
            .IsUnique();
        builder.HasIndex(entity => new { entity.LogicalReminderId, entity.AttemptOrdinal })
            .HasDatabaseName("ux_reminder_instances_logical_attempt")
            .IsUnique();
        builder.HasIndex(entity => new { entity.OccurrenceId, entity.Lifecycle })
            .HasDatabaseName("ix_reminder_instances_occurrence_lifecycle");
        builder.HasIndex(entity => new { entity.LogicalReminderId, entity.TriggeredAtUtc })
            .HasDatabaseName("ix_reminder_instances_logical_triggered_at");

        builder.HasOne<ReminderScheduleEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ScheduleId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ReminderRuleEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.RuleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
