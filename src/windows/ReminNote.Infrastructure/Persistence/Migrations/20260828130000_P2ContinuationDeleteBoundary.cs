using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ReminNote.Infrastructure.Persistence;

#nullable disable

namespace ReminNote.Infrastructure.Persistence.Migrations;

/// <summary>
/// Repairs P2 databases created before continuation-source deletion was
/// guarded. The trigger removes only continuation history that would become
/// invalid when a source Task is hard-deleted; later history of child Tasks is
/// retained and its nullable source relation is cleared by the existing FK.
/// </summary>
[Migration("20260828130000_P2ContinuationDeleteBoundary")]
[DbContext(typeof(ReminNoteDbContext))]
public partial class P2ContinuationDeleteBoundary : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TRIGGER IF NOT EXISTS tasks_delete_continuation_history
            BEFORE DELETE ON tasks
            BEGIN
                DELETE FROM task_history
                WHERE related_task_id = OLD.id;
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // P2TaskLoop itself owns this trigger. Rolling back only this repair
        // migration must therefore leave the P2 baseline invariant intact.
        migrationBuilder.Sql("""
            CREATE TRIGGER IF NOT EXISTS tasks_delete_continuation_history
            BEFORE DELETE ON tasks
            BEGIN
                DELETE FROM task_history
                WHERE related_task_id = OLD.id;
            END;
            """);
    }
}
