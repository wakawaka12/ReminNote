using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ReminNote.Infrastructure.Persistence.P25;

internal sealed class P25CommandReceiptEntityConfiguration : IEntityTypeConfiguration<P25CommandReceiptEntity>
{
    public void Configure(EntityTypeBuilder<P25CommandReceiptEntity> builder)
    {
        builder.ToTable("command_receipt", table =>
        {
            table.HasCheckConstraint("ck_command_receipt_sid", "length(actual_user_sid) BETWEEN 1 AND 256");
            table.HasCheckConstraint("ck_command_receipt_idempotency_key", "length(idempotency_key) = 36");
            table.HasCheckConstraint("ck_command_receipt_operation", "length(operation) BETWEEN 1 AND 96");
            table.HasCheckConstraint("ck_command_receipt_hash_version", "length(hash_version) BETWEEN 1 AND 32");
            table.HasCheckConstraint("ck_command_receipt_hash", "length(canonical_payload_hash) = 32");
            table.HasCheckConstraint(
                "ck_command_receipt_status",
                "status IN ('PENDING','COMMITTED','REJECTED_STALE','REJECTED','ROLLED_BACK','CANCELLED','TIMED_OUT','UNKNOWN')");
            table.HasCheckConstraint("ck_command_receipt_changed", "changed IN (0, 1)");
            table.HasCheckConstraint("ck_command_receipt_committed_revision", "committed_revision IS NULL OR committed_revision >= 0");
            table.HasCheckConstraint("ck_command_receipt_error_code", "error_code IS NULL OR length(error_code) BETWEEN 1 AND 160");
            table.HasCheckConstraint("ck_command_receipt_first_accepted", "length(first_accepted_at_utc) BETWEEN 1 AND 64");
            table.HasCheckConstraint("ck_command_receipt_updated", "length(updated_at_utc) BETWEEN 1 AND 64");
            table.HasCheckConstraint("ck_command_receipt_first_request_id", "length(first_request_id) = 36");
            table.HasCheckConstraint("ck_command_receipt_last_request_id", "length(last_request_id) = 36");
            table.HasCheckConstraint("ck_command_receipt_attempt_count", $"attempt_count BETWEEN 1 AND {P25StorageLimits.MaxReceiptAttempts}");
            table.HasCheckConstraint("ck_command_receipt_agent_instance_id", "agent_instance_id IS NULL OR length(agent_instance_id) = 36");
        });

        builder.HasKey(entity => new { entity.ActualUserSid, entity.ProfileScope, entity.IdempotencyKey })
            .HasName("pk_command_receipt");
        builder.Property(entity => entity.ActualUserSid).HasColumnName("actual_user_sid").HasColumnType("TEXT").HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.ProfileScope).HasColumnName("profile_scope").HasColumnType("TEXT").HasMaxLength(67).IsRequired();
        builder.Property(entity => entity.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType("TEXT").HasMaxLength(36).IsRequired();
        builder.Property(entity => entity.Operation).HasColumnName("operation").HasColumnType("TEXT").HasMaxLength(96).IsRequired();
        builder.Property(entity => entity.HashVersion).HasColumnName("hash_version").HasColumnType("TEXT").HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.CanonicalPayloadHash).HasColumnName("canonical_payload_hash").HasColumnType("BLOB").IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasColumnType("TEXT").HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.Changed).HasColumnName("changed").HasColumnType("INTEGER").IsRequired();
        builder.Property(entity => entity.CommittedRevision).HasColumnName("committed_revision").HasColumnType("INTEGER");
        builder.Property(entity => entity.ErrorCode).HasColumnName("error_code").HasColumnType("TEXT").HasMaxLength(160);
        builder.Property(entity => entity.FirstAcceptedAtUtc).HasColumnName("first_accepted_at_utc").HasColumnType("TEXT").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("TEXT").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.FirstRequestId).HasColumnName("first_request_id").HasColumnType("TEXT").HasMaxLength(36).IsRequired();
        builder.Property(entity => entity.LastRequestId).HasColumnName("last_request_id").HasColumnType("TEXT").HasMaxLength(36).IsRequired();
        builder.Property(entity => entity.AttemptCount).HasColumnName("attempt_count").HasColumnType("INTEGER").IsRequired();
        builder.Property(entity => entity.AgentInstanceId).HasColumnName("agent_instance_id").HasColumnType("TEXT").HasMaxLength(36);

        builder.HasOne<P25RevisionStateEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ProfileScope)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(entity => new { entity.ProfileScope, entity.IdempotencyKey })
            .HasDatabaseName("ix_command_receipt_profile_key");
    }
}
