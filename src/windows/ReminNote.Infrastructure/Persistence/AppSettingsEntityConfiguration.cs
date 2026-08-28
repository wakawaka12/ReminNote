using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;
using NodaTime.Text;

namespace ReminNote.Infrastructure.Persistence;

internal sealed class AppSettingsEntityConfiguration : IEntityTypeConfiguration<AppSettingsEntity>
{
    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'");

    private static readonly ValueConverter<Instant, string> InstantConverter = new(
        value => InstantPattern.Format(value),
        value => InstantPattern.Parse(value).GetValueOrThrow());

    public void Configure(EntityTypeBuilder<AppSettingsEntity> builder)
    {
        builder.ToTable("app_settings", table =>
        {
            table.HasCheckConstraint("ck_app_settings_singleton_id", "id = 1");
            table.HasCheckConstraint(
                "ck_app_settings_workday_boundary",
                "workday_boundary_minutes >= 0 AND workday_boundary_minutes <= 1439");
        });

        builder.HasKey(entity => entity.Id)
            .HasName("pk_app_settings");

        builder.Property(entity => entity.Id)
            .HasColumnName("id")
            .HasColumnType("INTEGER")
            .ValueGeneratedNever();

        builder.Property(entity => entity.WorkdayBoundaryMinutes)
            .HasColumnName("workday_boundary_minutes")
            .HasColumnType("INTEGER")
            .IsRequired();

        builder.Property(entity => entity.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("TEXT")
            .HasConversion(InstantConverter)
            .IsRequired();
    }
}
