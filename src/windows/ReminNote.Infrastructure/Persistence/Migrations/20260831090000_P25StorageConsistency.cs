using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ReminNote.Infrastructure.Persistence;

#nullable disable
#pragma warning disable CA1861

namespace ReminNote.Infrastructure.Persistence.Migrations;

/// <summary>
/// Formal additive P2.5 storage migration. It adds only the durable revision,
/// journal and receipt tables; the existing P1/P2 task tables are untouched.
/// Profile rows are seeded by P25StorageSchema.EnsureProfileAsync after the
/// migration has completed for the resolved profile.
/// </summary>
[Migration("20260831090000_P25StorageConsistency")]
[DbContext(typeof(ReminNoteDbContext))]
public partial class P25StorageConsistency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "revision_state",
            columns: table => new
            {
                profile_scope = table.Column<string>(type: "TEXT", maxLength: 67, nullable: false),
                current_revision = table.Column<long>(type: "INTEGER", nullable: false),
                oldest_available_revision = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_revision_state", x => x.profile_scope);
                table.CheckConstraint("ck_revision_state_current_revision", "current_revision >= 0");
                table.CheckConstraint("ck_revision_state_oldest_revision", "oldest_available_revision >= 0");
                table.CheckConstraint(
                    "ck_revision_state_revision_order",
                    "oldest_available_revision = 0 OR oldest_available_revision <= current_revision");
            });

        migrationBuilder.CreateTable(
            name: "change_journal",
            columns: table => new
            {
                profile_scope = table.Column<string>(type: "TEXT", maxLength: 67, nullable: false),
                revision = table.Column<long>(type: "INTEGER", nullable: false),
                change_ordinal = table.Column<long>(type: "INTEGER", nullable: false),
                batch_id = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                entity_type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                entity_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                change_kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                changed_at_utc = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_change_journal", x => new { x.profile_scope, x.revision, x.change_ordinal });
                table.CheckConstraint("ck_change_journal_revision", "revision > 0");
                table.CheckConstraint("ck_change_journal_ordinal", "change_ordinal >= 0");
                table.CheckConstraint("ck_change_journal_batch_id", "length(batch_id) BETWEEN 1 AND 96");
                table.CheckConstraint("ck_change_journal_entity_type", "length(entity_type) BETWEEN 1 AND 128");
                table.CheckConstraint("ck_change_journal_entity_id", "length(entity_id) BETWEEN 1 AND 256");
                table.CheckConstraint("ck_change_journal_change_kind", "length(change_kind) BETWEEN 1 AND 64");
                table.CheckConstraint("ck_change_journal_changed_at", "length(changed_at_utc) BETWEEN 1 AND 64");
                table.ForeignKey(
                    name: "fk_change_journal_revision_state_profile_scope",
                    column: x => x.profile_scope,
                    principalTable: "revision_state",
                    principalColumn: "profile_scope",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "command_receipt",
            columns: table => new
            {
                actual_user_sid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                profile_scope = table.Column<string>(type: "TEXT", maxLength: 67, nullable: false),
                idempotency_key = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                operation = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                hash_version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                canonical_payload_hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                changed = table.Column<bool>(type: "INTEGER", nullable: false),
                committed_revision = table.Column<long>(type: "INTEGER", nullable: true),
                error_code = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                first_accepted_at_utc = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                updated_at_utc = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                first_request_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                last_request_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                agent_instance_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_command_receipt", x => new { x.actual_user_sid, x.profile_scope, x.idempotency_key });
                table.CheckConstraint("ck_command_receipt_sid", "length(actual_user_sid) BETWEEN 1 AND 256");
                table.CheckConstraint("ck_command_receipt_idempotency_key", "length(idempotency_key) = 36");
                table.CheckConstraint("ck_command_receipt_operation", "length(operation) BETWEEN 1 AND 96");
                table.CheckConstraint("ck_command_receipt_hash_version", "length(hash_version) BETWEEN 1 AND 32");
                table.CheckConstraint("ck_command_receipt_hash", "length(canonical_payload_hash) = 32");
                table.CheckConstraint("ck_command_receipt_status", "status IN ('PENDING','COMMITTED','REJECTED_STALE','REJECTED','ROLLED_BACK','CANCELLED','TIMED_OUT','UNKNOWN')");
                table.CheckConstraint("ck_command_receipt_changed", "changed IN (0, 1)");
                table.CheckConstraint("ck_command_receipt_committed_revision", "committed_revision IS NULL OR committed_revision >= 0");
                table.CheckConstraint("ck_command_receipt_error_code", "error_code IS NULL OR length(error_code) BETWEEN 1 AND 160");
                table.CheckConstraint("ck_command_receipt_first_accepted", "length(first_accepted_at_utc) BETWEEN 1 AND 64");
                table.CheckConstraint("ck_command_receipt_updated", "length(updated_at_utc) BETWEEN 1 AND 64");
                table.CheckConstraint("ck_command_receipt_first_request_id", "length(first_request_id) = 36");
                table.CheckConstraint("ck_command_receipt_last_request_id", "length(last_request_id) = 36");
                table.CheckConstraint("ck_command_receipt_attempt_count", "attempt_count BETWEEN 1 AND 2");
                table.CheckConstraint("ck_command_receipt_agent_instance_id", "agent_instance_id IS NULL OR length(agent_instance_id) = 36");
                table.ForeignKey(
                    name: "fk_command_receipt_revision_state_profile_scope",
                    column: x => x.profile_scope,
                    principalTable: "revision_state",
                    principalColumn: "profile_scope",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_change_journal_profile_revision",
            table: "change_journal",
            columns: new[] { "profile_scope", "revision", "change_ordinal" });

        migrationBuilder.CreateIndex(
            name: "ix_command_receipt_profile_key",
            table: "command_receipt",
            columns: new[] { "profile_scope", "idempotency_key" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "change_journal");
        migrationBuilder.DropTable(name: "command_receipt");
        migrationBuilder.DropTable(name: "revision_state");
    }
}
