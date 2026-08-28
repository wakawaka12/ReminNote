using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;

namespace ReminNote.Infrastructure.Persistence;

internal sealed class TaskHistoryEntityConfiguration : IEntityTypeConfiguration<TaskHistoryEntity>
{
    private const long NanosecondsPerDay = 86_400_000_000_000;

    private static readonly ValueConverter<Guid, string> GuidConverter = new(
        value => value.ToString("D"),
        value => Guid.Parse(value));

    private static readonly LocalDatePattern LocalDatePattern =
        NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("uuuu-MM-dd");

    private static readonly ValueConverter<LocalDate, string> LocalDateConverter = new(
        value => LocalDatePattern.Format(value),
        value => LocalDatePattern.Parse(value).GetValueOrThrow());

    private static readonly ValueConverter<LocalTime, long> LocalTimeConverter = new(
        value => value.NanosecondOfDay,
        value => LocalTime.FromNanosecondsSinceMidnight(value));

    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'");

    private static readonly ValueConverter<Instant, string> InstantConverter = new(
        value => InstantPattern.Format(value),
        value => InstantPattern.Parse(value).GetValueOrThrow());

    public void Configure(EntityTypeBuilder<TaskHistoryEntity> builder)
    {
        builder.ToTable("task_history", table =>
        {
            table.HasCheckConstraint(
                "ck_task_history_id_uuid_v7",
                "length(task_id) = 36 " +
                "AND task_id NOT GLOB '*[^0-9A-Fa-f-]*' " +
                "AND substr(task_id, 9, 1) = '-' " +
                "AND substr(task_id, 14, 1) = '-' " +
                "AND substr(task_id, 19, 1) = '-' " +
                "AND substr(task_id, 24, 1) = '-' " +
                "AND lower(substr(task_id, 15, 1)) = '7' " +
                "AND lower(substr(task_id, 20, 1)) IN ('8', '9', 'a', 'b')");
            table.HasCheckConstraint(
                "ck_task_history_kind",
                "kind IN (0, 1, 2, 3, 4)");
            table.HasCheckConstraint(
                "ck_task_history_title_not_blank",
                "length(trim(title)) > 0");
            table.HasCheckConstraint(
                "ck_task_history_time_shape",
                "(time_type = 0 AND time_point IS NULL AND range_start IS NULL AND range_end IS NULL) " +
                "OR (time_type = 1 AND time_point IS NOT NULL AND range_start IS NULL AND range_end IS NULL) " +
                "OR (time_type = 2 AND time_point IS NULL AND range_start IS NOT NULL AND range_end IS NOT NULL " +
                "AND range_start <> range_end)");
            table.HasCheckConstraint(
                "ck_task_history_time_values",
                $"(time_point IS NULL OR (time_point >= 0 AND time_point < {NanosecondsPerDay})) " +
                $"AND (range_start IS NULL OR (range_start >= 0 AND range_start < {NanosecondsPerDay})) " +
                $"AND (range_end IS NULL OR (range_end >= 0 AND range_end < {NanosecondsPerDay}))");
            table.HasCheckConstraint(
                "ck_task_history_result_metadata",
                "(result IS NULL AND result_recorded_at IS NULL AND result_note IS NULL) " +
                "OR (result IS NOT NULL AND result_recorded_at IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_task_history_result_value",
                "result IS NULL OR result IN (0, 1, 2)");
            table.HasCheckConstraint(
                "ck_task_history_result_timestamp_order",
                "result_recorded_at IS NULL OR " +
                "(result_recorded_at >= created_at AND result_recorded_at <= updated_at)");
            table.HasCheckConstraint(
                "ck_task_history_timestamp_order",
                "updated_at >= created_at AND occurred_at >= created_at");
            table.HasCheckConstraint(
                "ck_task_history_partial_requires_range",
                "result IS NULL OR result <> 2 OR time_type = 2");
            table.HasCheckConstraint(
                "ck_task_history_sort_order_non_negative",
                "sort_order >= 0");
            table.HasCheckConstraint(
                "ck_task_history_continuation_metadata",
                "(kind <> 2 AND related_task_id IS NULL) " +
                "OR (kind = 2 AND related_task_id IS NOT NULL " +
                "AND continued_from_task_id = related_task_id " +
                "AND task_id <> related_task_id)");
        });

        builder.HasKey(entity => entity.Id)
            .HasName("pk_task_history");

        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("INTEGER")
            .ValueGeneratedOnAdd();

        builder.Property(entity => entity.TaskId)
            .HasColumnName("task_id")
            .HasColumnType("TEXT")
            .HasConversion(GuidConverter)
            .IsRequired();

        builder.HasIndex(entity => new { entity.TaskId, entity.Id })
            .HasDatabaseName("ix_task_history_task_id_id");

        builder.Property(entity => entity.Kind)
            .HasColumnName("kind")
            .HasColumnType("INTEGER")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(entity => entity.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("TEXT")
            .HasConversion(InstantConverter)
            .IsRequired();

        builder.Property(entity => entity.Title)
            .HasColumnName("title")
            .HasColumnType("TEXT")
            .IsRequired();

        builder.Property(entity => entity.TimeType)
            .HasColumnName("time_type")
            .HasColumnType("INTEGER")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(entity => entity.LocalDate)
            .HasColumnName("local_date")
            .HasColumnType("TEXT")
            .HasConversion(LocalDateConverter)
            .IsRequired();

        builder.Property(entity => entity.TimePoint)
            .HasColumnName("time_point")
            .HasColumnType("INTEGER")
            .HasConversion(LocalTimeConverter);

        builder.Property(entity => entity.RangeStart)
            .HasColumnName("range_start")
            .HasColumnType("INTEGER")
            .HasConversion(LocalTimeConverter);

        builder.Property(entity => entity.RangeEnd)
            .HasColumnName("range_end")
            .HasColumnType("INTEGER")
            .HasConversion(LocalTimeConverter);

        builder.Property(entity => entity.Result)
            .HasColumnName("result")
            .HasColumnType("INTEGER")
            .HasConversion<int?>();

        builder.Property(entity => entity.ResultRecordedAt)
            .HasColumnName("result_recorded_at")
            .HasColumnType("TEXT")
            .HasConversion(InstantConverter);

        builder.Property(entity => entity.ResultNote)
            .HasColumnName("result_note")
            .HasColumnType("TEXT");

        builder.Property(entity => entity.SortOrder)
            .HasColumnName("sort_order")
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(entity => entity.ContinuedFromTaskId)
            .HasColumnName("continued_from_task_id")
            .HasColumnType("TEXT")
            .HasConversion(GuidConverter);

        builder.Property(entity => entity.RelatedTaskId)
            .HasColumnName("related_task_id")
            .HasColumnType("TEXT")
            .HasConversion(GuidConverter);

        builder.HasIndex(entity => entity.ContinuedFromTaskId)
            .HasDatabaseName("ix_task_history_continued_from_task_id");

        builder.HasIndex(entity => entity.RelatedTaskId)
            .HasDatabaseName("ix_task_history_related_task_id");

        builder.Property(entity => entity.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("TEXT")
            .HasConversion(InstantConverter)
            .IsRequired();

        builder.Property(entity => entity.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("TEXT")
            .HasConversion(InstantConverter)
            .IsRequired();

        builder.HasOne<TaskEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TaskEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ContinuedFromTaskId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<TaskEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.RelatedTaskId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
