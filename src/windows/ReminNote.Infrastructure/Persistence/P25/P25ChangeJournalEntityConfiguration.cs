using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ReminNote.Infrastructure.Persistence.P25;

internal sealed class P25ChangeJournalEntityConfiguration : IEntityTypeConfiguration<P25ChangeJournalEntity>
{
    public void Configure(EntityTypeBuilder<P25ChangeJournalEntity> builder)
    {
        builder.ToTable("change_journal", table =>
        {
            table.HasCheckConstraint("ck_change_journal_revision", "revision > 0");
            table.HasCheckConstraint("ck_change_journal_ordinal", "change_ordinal >= 0");
            table.HasCheckConstraint("ck_change_journal_batch_id", "length(batch_id) BETWEEN 1 AND 96");
            table.HasCheckConstraint("ck_change_journal_entity_type", "length(entity_type) BETWEEN 1 AND 128");
            table.HasCheckConstraint("ck_change_journal_entity_id", "length(entity_id) BETWEEN 1 AND 256");
            table.HasCheckConstraint("ck_change_journal_change_kind", "length(change_kind) BETWEEN 1 AND 64");
            table.HasCheckConstraint("ck_change_journal_changed_at", "length(changed_at_utc) BETWEEN 1 AND 64");
        });

        builder.HasKey(entity => new { entity.ProfileScope, entity.Revision, entity.ChangeOrdinal })
            .HasName("pk_change_journal");
        builder.Property(entity => entity.ProfileScope).HasColumnName("profile_scope").HasColumnType("TEXT").HasMaxLength(67).IsRequired();
        builder.Property(entity => entity.Revision).HasColumnName("revision").HasColumnType("INTEGER").IsRequired();
        builder.Property(entity => entity.ChangeOrdinal).HasColumnName("change_ordinal").HasColumnType("INTEGER").IsRequired();
        builder.Property(entity => entity.BatchId).HasColumnName("batch_id").HasColumnType("TEXT").HasMaxLength(96).IsRequired();
        builder.Property(entity => entity.EntityType).HasColumnName("entity_type").HasColumnType("TEXT").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.EntityId).HasColumnName("entity_id").HasColumnType("TEXT").HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.ChangeKind).HasColumnName("change_kind").HasColumnType("TEXT").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.ChangedAtUtc).HasColumnName("changed_at_utc").HasColumnType("TEXT").HasMaxLength(64).IsRequired();

        builder.HasOne<P25RevisionStateEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ProfileScope)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(entity => new { entity.ProfileScope, entity.Revision, entity.ChangeOrdinal })
            .HasDatabaseName("ix_change_journal_profile_revision");
    }
}
