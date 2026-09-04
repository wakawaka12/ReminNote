using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.Reminders;

#nullable disable

namespace ReminNote.Infrastructure.Persistence.Migrations;

/// <summary>
/// Additive P3-04 migration for the Agent-owned notification attempt event
/// stream. It intentionally does not create or alter Rule/Schedule/Instance
/// tables; those remain owned by the P3-03 persistence integration.
/// </summary>
[Migration("20260904090000_P304")]
[DbContext(typeof(ReminNoteDbContext))]
public partial class P304 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(ReminderNotificationAttemptSchema.CreateTableSql);
        migrationBuilder.Sql("""
            CREATE INDEX IF NOT EXISTS ix_notification_attempt_key
                ON notification_delivery_attempt_events
                    (profile_scope, instance_id, channel, idempotency_key, event_ordinal);
            """);
        migrationBuilder.Sql("""
            CREATE INDEX IF NOT EXISTS ix_notification_attempt_logical
                ON notification_delivery_attempt_events
                    (profile_scope, instance_id, logical_reminder_id, channel, attempt_number, event_ordinal);
            """);
        migrationBuilder.Sql("""
            CREATE INDEX IF NOT EXISTS ix_notification_attempt_recovery
                ON notification_delivery_attempt_events
                    (profile_scope, state, retryable, next_attempt_at_utc);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_notification_attempt_recovery;");
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_notification_attempt_logical;");
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_notification_attempt_key;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS notification_delivery_attempt_events;");
    }
}
