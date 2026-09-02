using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Infrastructure.Persistence.Reminders;

public sealed class ReminderScheduleEntityConfiguration
{
    public static void Configure(EntityTypeBuilder<ReminderScheduleEntity> builder)
    {
        builder.ToTable("reminder_schedules", table =>
        {
            table.HasCheckConstraint(
                "ck_reminder_schedules_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("id"));
            table.HasCheckConstraint(
                "ck_reminder_schedules_rule_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("rule_id"));
            table.HasCheckConstraint(
                "ck_reminder_schedules_occurrence_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("occurrence_id"));
            table.HasCheckConstraint(
                "ck_reminder_schedules_logical_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("logical_reminder_id"));
            table.HasCheckConstraint(
                "ck_reminder_schedules_origin_id_uuid_v7",
                ReminderPersistenceConventions.NullableUuidV7Check("origin_schedule_id"));
            table.HasCheckConstraint(
                "ck_reminder_schedules_replacement_id_uuid_v7",
                ReminderPersistenceConventions.NullableUuidV7Check("replacement_schedule_id"));
            table.HasCheckConstraint(
                "ck_reminder_schedules_cause",
                "cause IN ('RULE','SNOOZE','REPEAT')");
            table.HasCheckConstraint(
                "ck_reminder_schedules_origin_shape",
                "(cause = 'RULE' AND origin_schedule_id IS NULL) " +
                "OR (cause IN ('SNOOZE','REPEAT') AND origin_schedule_id IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_reminder_schedules_revision",
                "rule_revision >= 1 AND schedule_revision >= 1");
            table.HasCheckConstraint(
                "ck_reminder_schedules_purpose_snapshot",
                "purpose_snapshot IN ('TASK_PRE_START','TASK_START','TASK_RANGE_END','TASK_CUSTOM')");
            table.HasCheckConstraint(
                "ck_reminder_schedules_priority_snapshot",
                "priority_snapshot IN ('LOW','NORMAL','HIGH')");
            table.HasCheckConstraint(
                "ck_reminder_schedules_pinned_snapshot",
                "pinned_snapshot IN (0,1)");
            table.HasCheckConstraint(
                "ck_reminder_schedules_state",
                "state IN ('PENDING','CONSUMED','SUPERSEDED','CANCELLED','EXPIRED')");
            table.HasCheckConstraint(
                "ck_reminder_schedules_reason",
                "terminal_reason IS NULL OR terminal_reason IN " +
                "('DUE_CONSUMED','RULE_REBUILT','TASK_PLAN_CHANGED','TIME_ZONE_CHANGED','TASK_RESULT_RECORDED','RULE_DISABLED','TASK_DELETED','RECOVERY_OBSOLETE','MANUAL_CANCELLED','REPLACED')");
            table.HasCheckConstraint(
                "ck_reminder_schedules_state_shape",
                "(state = 'PENDING' AND terminal_reason IS NULL AND replacement_schedule_id IS NULL AND terminal_at_utc IS NULL) " +
                "OR (state = 'CONSUMED' AND terminal_reason = 'DUE_CONSUMED' AND replacement_schedule_id IS NULL AND terminal_at_utc IS NOT NULL) " +
                "OR (state = 'SUPERSEDED' AND terminal_reason IS NOT NULL AND replacement_schedule_id IS NOT NULL AND terminal_at_utc IS NOT NULL) " +
                "OR (state IN ('CANCELLED','EXPIRED') AND terminal_reason IS NOT NULL AND replacement_schedule_id IS NULL AND terminal_at_utc IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_reminder_schedules_replacement_not_self",
                "replacement_schedule_id IS NULL OR replacement_schedule_id <> id");
            table.HasCheckConstraint(
                "ck_reminder_schedules_timestamp_order",
                "terminal_at_utc IS NULL OR terminal_at_utc >= created_at_utc");
            table.HasCheckConstraint(
                "ck_reminder_schedules_timestamps_format",
                ReminderPersistenceConventions.InstantCheck("trigger_at_utc") + " AND " +
                ReminderPersistenceConventions.InstantCheck("created_at_utc") + " AND " +
                ReminderPersistenceConventions.InstantCheck("terminal_at_utc", nullable: true));
            table.HasCheckConstraint(
                "ck_reminder_schedules_timezone",
                $"time_zone_id IS NULL OR length(trim(time_zone_id)) BETWEEN 1 AND {ReminderPersistenceConventions.MaxTimeZoneIdBytes}");
        });

        builder.HasKey(entity => entity.Id)
            .HasName("pk_reminder_schedules");

        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.GuidConverter)
            .ValueGeneratedNever();
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
        builder.Property(entity => entity.OriginScheduleId)
            .HasColumnName("origin_schedule_id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.NullableGuidConverter);
        builder.Property(entity => entity.Cause)
            .HasColumnName("cause")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsRequired();
        builder.Property(entity => entity.RuleRevision)
            .HasColumnName("rule_revision")
            .HasColumnType("INTEGER")
            .IsRequired();
        builder.Property(entity => entity.ScheduleRevision)
            .HasColumnName("schedule_revision")
            .HasColumnType("INTEGER")
            .IsRequired();
        builder.Property(entity => entity.TriggerAtUtc)
            .HasColumnName("trigger_at_utc")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.InstantConverter)
            .IsRequired();
        builder.Property(entity => entity.TimeZoneId)
            .HasColumnName("time_zone_id")
            .HasColumnType("TEXT")
            .HasMaxLength(ReminderPersistenceConventions.MaxTimeZoneIdBytes);
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
        builder.Property(entity => entity.State)
            .HasColumnName("state")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsConcurrencyToken()
            .IsRequired();
        builder.Property(entity => entity.TerminalReason)
            .HasColumnName("terminal_reason")
            .HasColumnType("TEXT")
            .HasConversion<string?>();
        builder.Property(entity => entity.ReplacementScheduleId)
            .HasColumnName("replacement_schedule_id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.NullableGuidConverter);
        builder.Property(entity => entity.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.InstantConverter)
            .IsRequired();
        builder.Property(entity => entity.TerminalAtUtc)
            .HasColumnName("terminal_at_utc")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.NullableInstantConverter);

        builder.HasIndex(entity => new { entity.RuleId, entity.OccurrenceId, entity.ScheduleRevision })
            .HasDatabaseName("ux_reminder_schedules_rule_occurrence_revision")
            .IsUnique();
        builder.HasIndex(entity => new { entity.State, entity.TriggerAtUtc })
            .HasDatabaseName("ix_reminder_schedules_state_trigger_at_utc");
        builder.HasIndex(entity => new { entity.OccurrenceId, entity.State })
            .HasDatabaseName("ix_reminder_schedules_occurrence_state");
        builder.HasIndex(entity => new { entity.LogicalReminderId, entity.ScheduleRevision })
            .HasDatabaseName("ix_reminder_schedules_logical_revision");

        builder.HasOne<ReminderRuleEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.RuleId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ReminderScheduleEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.OriginScheduleId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ReminderScheduleEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ReplacementScheduleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
