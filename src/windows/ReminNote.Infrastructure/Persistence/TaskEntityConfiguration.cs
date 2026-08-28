using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core.Tasks;

namespace ReminNote.Infrastructure.Persistence;

internal sealed class TaskEntityConfiguration : IEntityTypeConfiguration<TaskEntity>
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

    public void Configure(EntityTypeBuilder<TaskEntity> builder)
    {
        builder.ToTable("tasks", table =>
        {
            table.HasCheckConstraint(
                "ck_tasks_id_uuid_v7",
                "length(id) = 36 " +
                "AND id NOT GLOB '*[^0-9A-Fa-f-]*' " +
                "AND substr(id, 9, 1) = '-' " +
                "AND substr(id, 14, 1) = '-' " +
                "AND substr(id, 19, 1) = '-' " +
                "AND substr(id, 24, 1) = '-' " +
                "AND lower(substr(id, 15, 1)) = '7' " +
                "AND lower(substr(id, 20, 1)) IN ('8', '9', 'a', 'b')");
            table.HasCheckConstraint(
                "ck_tasks_title_not_blank",
                "length(trim(title)) > 0");
            table.HasCheckConstraint(
                "ck_tasks_time_shape",
                "(time_type = 0 AND time_point IS NULL AND range_start IS NULL AND range_end IS NULL) " +
                "OR (time_type = 1 AND time_point IS NOT NULL AND range_start IS NULL AND range_end IS NULL) " +
                "OR (time_type = 2 AND time_point IS NULL AND range_start IS NOT NULL AND range_end IS NOT NULL " +
                "AND range_start <> range_end)");
            table.HasCheckConstraint(
                "ck_tasks_time_values",
                $"(time_point IS NULL OR (time_point >= 0 AND time_point < {NanosecondsPerDay})) " +
                $"AND (range_start IS NULL OR (range_start >= 0 AND range_start < {NanosecondsPerDay})) " +
                $"AND (range_end IS NULL OR (range_end >= 0 AND range_end < {NanosecondsPerDay}))");
            table.HasCheckConstraint(
                "ck_tasks_result_value",
                "result IS NULL OR result IN (0, 1, 2)");
            table.HasCheckConstraint(
                "ck_tasks_result_metadata",
                "(result IS NULL AND result_recorded_at IS NULL AND result_note IS NULL) " +
                "OR (result IS NOT NULL AND result_recorded_at IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_tasks_result_timestamp_order",
                "result_recorded_at IS NULL OR " +
                "(result_recorded_at >= created_at AND result_recorded_at <= updated_at)");
            table.HasCheckConstraint(
                "ck_tasks_partial_requires_range",
                "result IS NULL OR result <> 2 OR time_type = 2");
            table.HasCheckConstraint(
                "ck_tasks_timestamp_order",
                "updated_at >= created_at");
        });

        builder.HasKey(entity => entity.Id)
            .HasName("pk_tasks");

        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("TEXT")
            .HasConversion(GuidConverter)
            .ValueGeneratedNever();

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
    }
}
