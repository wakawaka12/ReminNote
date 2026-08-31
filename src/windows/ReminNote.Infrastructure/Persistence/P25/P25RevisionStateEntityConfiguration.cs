using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ReminNote.Infrastructure.Persistence.P25;

internal sealed class P25RevisionStateEntityConfiguration : IEntityTypeConfiguration<P25RevisionStateEntity>
{
    public void Configure(EntityTypeBuilder<P25RevisionStateEntity> builder)
    {
        builder.ToTable("revision_state", table =>
        {
            table.HasCheckConstraint("ck_revision_state_current_revision", "current_revision >= 0");
            table.HasCheckConstraint("ck_revision_state_oldest_revision", "oldest_available_revision >= 0");
            table.HasCheckConstraint(
                "ck_revision_state_revision_order",
                "oldest_available_revision = 0 OR oldest_available_revision <= current_revision");
        });

        builder.HasKey(entity => entity.ProfileScope).HasName("pk_revision_state");
        builder.Property(entity => entity.ProfileScope)
            .HasColumnName("profile_scope")
            .HasColumnType("TEXT")
            .HasMaxLength(67)
            .IsRequired();
        builder.Property(entity => entity.CurrentRevision)
            .HasColumnName("current_revision")
            .HasColumnType("INTEGER")
            .IsRequired();
        builder.Property(entity => entity.OldestAvailableRevision)
            .HasColumnName("oldest_available_revision")
            .HasColumnType("INTEGER")
            .IsRequired();
    }
}
