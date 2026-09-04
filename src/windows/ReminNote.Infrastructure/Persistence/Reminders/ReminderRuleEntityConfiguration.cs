using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Infrastructure.Persistence.Reminders;

/// <remarks>
/// The Reminder model is applied explicitly by ReminNoteDbContext's formal
/// P3 options. Keeping this configuration explicit prevents legacy P2 fixture
/// options from discovering the Reminder tables accidentally.
/// </remarks>
public sealed class ReminderRuleEntityConfiguration
{
    public static void Configure(EntityTypeBuilder<ReminderRuleEntity> builder)
    {
        builder.ToTable("reminder_rules", table =>
        {
            table.HasCheckConstraint(
                "ck_reminder_rules_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("id"));
            table.HasCheckConstraint(
                "ck_reminder_rules_target_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("target_id"));
            table.HasCheckConstraint(
                "ck_reminder_rules_occurrence_id_uuid_v7",
                ReminderPersistenceConventions.UuidV7Check("occurrence_id"));
            table.HasCheckConstraint(
                "ck_reminder_rules_target_kind",
                "target_kind IN ('TASK_INSTANCE')");
            table.HasCheckConstraint(
                "ck_reminder_rules_purpose",
                "purpose IN ('TASK_PRE_START','TASK_START','TASK_RANGE_END','TASK_CUSTOM')");
            table.HasCheckConstraint(
                "ck_reminder_rules_timing_kind",
                "timing_kind IN ('RELATIVE','ABSOLUTE_UTC')");
            table.HasCheckConstraint(
                "ck_reminder_rules_timing_shape",
                "(timing_kind = 'RELATIVE' AND timing_anchor IN ('TASK_TIME','RANGE_START','RANGE_END') " +
                "AND offset_seconds IS NOT NULL AND absolute_at_utc IS NULL) " +
                "OR (timing_kind = 'ABSOLUTE_UTC' AND timing_anchor IS NULL " +
                "AND offset_seconds IS NULL AND absolute_at_utc IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_reminder_rules_purpose_timing",
                "(purpose = 'TASK_CUSTOM' AND timing_kind = 'ABSOLUTE_UTC') " +
                "OR (purpose = 'TASK_RANGE_END' AND timing_kind = 'RELATIVE' AND timing_anchor = 'RANGE_END') " +
                "OR (purpose = 'TASK_START' AND timing_kind = 'RELATIVE' AND timing_anchor IN ('TASK_TIME','RANGE_START')) " +
                "OR (purpose = 'TASK_PRE_START' AND timing_kind = 'RELATIVE' " +
                "AND timing_anchor IN ('TASK_TIME','RANGE_START') AND offset_seconds < 0)");
            table.HasCheckConstraint(
                "ck_reminder_rules_priority",
                "priority IN ('LOW','NORMAL','HIGH')");
            table.HasCheckConstraint(
                "ck_reminder_rules_wake_policy",
                "wake_policy IN ('DEFAULT','YES','NO')");
            table.HasCheckConstraint(
                "ck_reminder_rules_repeat_enabled",
                "repeat_enabled IN (0,1) AND pinned IN (0,1) AND enabled IN (0,1)");
            table.HasCheckConstraint(
                "ck_reminder_rules_repeat_values",
                "(repeat_interval_seconds IS NULL OR repeat_interval_seconds > 0) " +
                "AND (repeat_max_count IS NULL OR repeat_max_count > 0) " +
                "AND (repeat_enabled = 0 OR repeat_interval_seconds IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_reminder_rules_revision",
                "rule_revision >= 1");
            table.HasCheckConstraint(
                "ck_reminder_rules_timestamp_order",
                "updated_at_utc >= created_at_utc");
            table.HasCheckConstraint(
                "ck_reminder_rules_timestamps_format",
                ReminderPersistenceConventions.InstantCheck("created_at_utc") + " AND " +
                ReminderPersistenceConventions.InstantCheck("updated_at_utc") + " AND " +
                ReminderPersistenceConventions.InstantCheck("absolute_at_utc", nullable: true));
        });

        builder.HasKey(entity => entity.Id)
            .HasName("pk_reminder_rules");

        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.GuidConverter)
            .ValueGeneratedNever();

        builder.Property(entity => entity.TargetKind)
            .HasColumnName("target_kind")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(entity => entity.TargetId)
            .HasColumnName("target_id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.GuidConverter)
            .IsRequired();

        builder.Property(entity => entity.OccurrenceId)
            .HasColumnName("occurrence_id")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.GuidConverter)
            .IsRequired();

        builder.Property(entity => entity.Purpose)
            .HasColumnName("purpose")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(entity => entity.TimingKind)
            .HasColumnName("timing_kind")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(entity => entity.TimingAnchor)
            .HasColumnName("timing_anchor")
            .HasColumnType("TEXT")
            .HasConversion<string?>();

        builder.Property(entity => entity.OffsetSeconds)
            .HasColumnName("offset_seconds")
            .HasColumnType("INTEGER");

        builder.Property(entity => entity.AbsoluteAtUtc)
            .HasColumnName("absolute_at_utc")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.NullableInstantConverter);

        builder.Property(entity => entity.Priority)
            .HasColumnName("priority")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(entity => entity.Pinned)
            .HasColumnName("pinned")
            .HasColumnType("INTEGER")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(entity => entity.RepeatEnabled)
            .HasColumnName("repeat_enabled")
            .HasColumnType("INTEGER")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(entity => entity.RepeatIntervalSeconds)
            .HasColumnName("repeat_interval_seconds")
            .HasColumnType("INTEGER");

        builder.Property(entity => entity.RepeatMaxCount)
            .HasColumnName("repeat_max_count")
            .HasColumnType("INTEGER");

        builder.Property(entity => entity.WakePolicy)
            .HasColumnName("wake_policy")
            .HasColumnType("TEXT")
            .HasConversion<string>()
            .IsRequired();

        builder.Property(entity => entity.Enabled)
            .HasColumnName("enabled")
            .HasColumnType("INTEGER")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(entity => entity.RuleRevision)
            .HasColumnName("rule_revision")
            .HasColumnType("INTEGER")
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(entity => entity.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.InstantConverter)
            .IsRequired();

        builder.Property(entity => entity.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("TEXT")
            .HasConversion(ReminderPersistenceConventions.InstantConverter)
            .IsRequired();

        builder.HasIndex(entity => new { entity.TargetKind, entity.TargetId, entity.OccurrenceId })
            .HasDatabaseName("ix_reminder_rules_target_occurrence");
        builder.HasIndex(entity => new { entity.OccurrenceId, entity.Enabled })
            .HasDatabaseName("ix_reminder_rules_occurrence_enabled");
    }
}
