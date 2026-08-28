using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ReminNote.Infrastructure.Persistence;

#nullable disable
#pragma warning disable CA1861

namespace ReminNote.Infrastructure.Persistence.Migrations;

/// <summary>
/// P2 forward migration. It preserves the P1 tasks table and backfills one
/// immutable imported-result snapshot for every existing recorded result.
/// </summary>
[Migration("20260828120000_P2TaskLoop")]
[DbContext(typeof(ReminNoteDbContext))]
public partial class P2TaskLoop : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQLite cannot add CHECK constraints or a self-referencing foreign
        // key to an existing table with ALTER TABLE. Rebuild the P1 table in
        // the same migration transaction so old rows and the new invariants
        // move together. P1 had no title-length constraint; retaining long
        // legacy titles here avoids data loss during upgrade. New/renamed
        // tasks enforce the current limit in Core.
        migrationBuilder.Sql("""
            CREATE TABLE tasks_p2
            (
                id TEXT NOT NULL CONSTRAINT pk_tasks PRIMARY KEY,
                title TEXT NOT NULL,
                time_type INTEGER NOT NULL,
                local_date TEXT NOT NULL,
                time_point INTEGER NULL,
                range_start INTEGER NULL,
                range_end INTEGER NULL,
                result INTEGER NULL,
                result_recorded_at TEXT NULL,
                result_note TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                continued_from_task_id TEXT NULL,
                CONSTRAINT ck_tasks_id_uuid_v7 CHECK
                    (length(id) = 36
                     AND id NOT GLOB '*[^0-9A-Fa-f-]*'
                     AND substr(id, 9, 1) = '-'
                     AND substr(id, 14, 1) = '-'
                     AND substr(id, 19, 1) = '-'
                     AND substr(id, 24, 1) = '-'
                     AND lower(substr(id, 15, 1)) = '7'
                     AND lower(substr(id, 20, 1)) IN ('8', '9', 'a', 'b')),
                CONSTRAINT ck_tasks_title_not_blank CHECK
                    (length(trim(title)) > 0),
                CONSTRAINT ck_tasks_time_shape CHECK
                    ((time_type = 0 AND time_point IS NULL
                      AND range_start IS NULL AND range_end IS NULL)
                     OR (time_type = 1 AND time_point IS NOT NULL
                         AND range_start IS NULL AND range_end IS NULL)
                     OR (time_type = 2 AND time_point IS NULL
                         AND range_start IS NOT NULL AND range_end IS NOT NULL
                         AND range_start <> range_end)),
                CONSTRAINT ck_tasks_time_values CHECK
                    ((time_point IS NULL
                      OR (time_point >= 0 AND time_point < 86400000000000))
                     AND (range_start IS NULL
                          OR (range_start >= 0 AND range_start < 86400000000000))
                     AND (range_end IS NULL
                          OR (range_end >= 0 AND range_end < 86400000000000))),
                CONSTRAINT ck_tasks_result_value CHECK
                    (result IS NULL OR result IN (0, 1, 2)),
                CONSTRAINT ck_tasks_result_metadata CHECK
                    ((result IS NULL AND result_recorded_at IS NULL
                      AND result_note IS NULL)
                     OR (result IS NOT NULL AND result_recorded_at IS NOT NULL)),
                CONSTRAINT ck_tasks_result_timestamp_order CHECK
                    (result_recorded_at IS NULL
                     OR (result_recorded_at >= created_at
                         AND result_recorded_at <= updated_at)),
                CONSTRAINT ck_tasks_partial_requires_range CHECK
                    (result IS NULL OR result <> 2 OR time_type = 2),
                CONSTRAINT ck_tasks_timestamp_order CHECK
                    (updated_at >= created_at),
                CONSTRAINT ck_tasks_sort_order_non_negative CHECK
                    (sort_order >= 0),
                CONSTRAINT ck_tasks_continuation_not_self CHECK
                    (continued_from_task_id IS NULL
                     OR continued_from_task_id <> id),
                FOREIGN KEY (continued_from_task_id)
                    REFERENCES tasks_p2(id) ON DELETE SET NULL
            );

            INSERT INTO tasks_p2
                (id, title, time_type, local_date, time_point, range_start,
                 range_end, result, result_recorded_at, result_note, created_at,
                 updated_at, sort_order, continued_from_task_id)
            SELECT id, title, time_type, local_date, time_point, range_start,
                   range_end, result, result_recorded_at, result_note, created_at,
                   updated_at, 0, NULL
            FROM tasks;

            DROP TABLE tasks;
            ALTER TABLE tasks_p2 RENAME TO tasks;
            CREATE INDEX ix_tasks_continued_from_task_id
                ON tasks (continued_from_task_id);
            """);

        migrationBuilder.Sql("""
            CREATE TRIGGER tasks_continuation_requires_partial_insert
            BEFORE INSERT ON tasks
            WHEN NEW.continued_from_task_id IS NOT NULL
                 AND NOT EXISTS
                     (SELECT 1 FROM tasks source
                      WHERE source.id = NEW.continued_from_task_id
                        AND source.time_type = 2
                        AND source.result = 2)
            BEGIN
                SELECT RAISE(ABORT,
                    'task continuation source must be a PARTIAL RANGE task');
            END;

            CREATE TRIGGER tasks_continuation_requires_partial_update
            BEFORE UPDATE OF continued_from_task_id ON tasks
            WHEN NEW.continued_from_task_id IS NOT NULL
                 AND NOT EXISTS
                     (SELECT 1 FROM tasks source
                      WHERE source.id = NEW.continued_from_task_id
                        AND source.time_type = 2
                        AND source.result = 2)
            BEGIN
                SELECT RAISE(ABORT,
                    'task continuation source must be a PARTIAL RANGE task');
            END;
            """);

        migrationBuilder.CreateTable(
            name: "app_settings",
            columns: table => new
            {
                id = table.Column<int>(type: "INTEGER", nullable: false),
                workday_boundary_minutes = table.Column<int>(type: "INTEGER", nullable: false),
                updated_at = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_app_settings", x => x.id);
                table.CheckConstraint("ck_app_settings_singleton_id", "id = 1");
                table.CheckConstraint(
                    "ck_app_settings_workday_boundary",
                    "workday_boundary_minutes >= 0 AND workday_boundary_minutes <= 1439");
            });

        migrationBuilder.CreateTable(
            name: "task_history",
            columns: table => new
            {
                id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                task_id = table.Column<string>(type: "TEXT", nullable: false),
                kind = table.Column<int>(type: "INTEGER", nullable: false),
                occurred_at = table.Column<string>(type: "TEXT", nullable: false),
                title = table.Column<string>(type: "TEXT", nullable: false),
                time_type = table.Column<int>(type: "INTEGER", nullable: false),
                local_date = table.Column<string>(type: "TEXT", nullable: false),
                time_point = table.Column<long>(type: "INTEGER", nullable: true),
                range_start = table.Column<long>(type: "INTEGER", nullable: true),
                range_end = table.Column<long>(type: "INTEGER", nullable: true),
                result = table.Column<int>(type: "INTEGER", nullable: true),
                result_recorded_at = table.Column<string>(type: "TEXT", nullable: true),
                result_note = table.Column<string>(type: "TEXT", nullable: true),
                sort_order = table.Column<int>(type: "INTEGER", nullable: false),
                continued_from_task_id = table.Column<string>(type: "TEXT", nullable: true),
                related_task_id = table.Column<string>(type: "TEXT", nullable: true),
                created_at = table.Column<string>(type: "TEXT", nullable: false),
                updated_at = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_task_history", x => x.id);
                table.CheckConstraint(
                    "ck_task_history_title_not_blank",
                    "length(trim(title)) > 0");
                table.CheckConstraint(
                    "ck_task_history_id_uuid_v7",
                    "length(task_id) = 36 " +
                    "AND task_id NOT GLOB '*[^0-9A-Fa-f-]*' " +
                    "AND substr(task_id, 9, 1) = '-' " +
                    "AND substr(task_id, 14, 1) = '-' " +
                    "AND substr(task_id, 19, 1) = '-' " +
                    "AND substr(task_id, 24, 1) = '-' " +
                    "AND lower(substr(task_id, 15, 1)) = '7' " +
                    "AND lower(substr(task_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                table.CheckConstraint(
                    "ck_task_history_kind",
                    "kind IN (0, 1, 2, 3, 4)");
                table.CheckConstraint(
                    "ck_task_history_time_shape",
                    "(time_type = 0 AND time_point IS NULL AND range_start IS NULL AND range_end IS NULL) " +
                    "OR (time_type = 1 AND time_point IS NOT NULL AND range_start IS NULL AND range_end IS NULL) " +
                    "OR (time_type = 2 AND time_point IS NULL AND range_start IS NOT NULL AND range_end IS NOT NULL " +
                    "AND range_start <> range_end)");
                table.CheckConstraint(
                    "ck_task_history_time_values",
                    "(time_point IS NULL OR (time_point >= 0 AND time_point < 86400000000000)) " +
                    "AND (range_start IS NULL OR (range_start >= 0 AND range_start < 86400000000000)) " +
                    "AND (range_end IS NULL OR (range_end >= 0 AND range_end < 86400000000000))");
                table.CheckConstraint(
                    "ck_task_history_result_metadata",
                    "(result IS NULL AND result_recorded_at IS NULL AND result_note IS NULL) " +
                    "OR (result IS NOT NULL AND result_recorded_at IS NOT NULL)");
                table.CheckConstraint(
                    "ck_task_history_result_value",
                    "result IS NULL OR result IN (0, 1, 2)");
                table.CheckConstraint(
                    "ck_task_history_result_timestamp_order",
                    "result_recorded_at IS NULL OR " +
                    "(result_recorded_at >= created_at AND result_recorded_at <= updated_at)");
                table.CheckConstraint(
                    "ck_task_history_timestamp_order",
                    "updated_at >= created_at AND occurred_at >= created_at");
                table.CheckConstraint(
                    "ck_task_history_partial_requires_range",
                    "result IS NULL OR result <> 2 OR time_type = 2");
                table.CheckConstraint(
                    "ck_task_history_sort_order_non_negative",
                    "sort_order >= 0");
                table.CheckConstraint(
                    "ck_task_history_continuation_metadata",
                    "(kind <> 2 AND related_task_id IS NULL) " +
                    "OR (kind = 2 AND related_task_id IS NOT NULL " +
                    "AND continued_from_task_id = related_task_id " +
                    "AND task_id <> related_task_id)");
                table.ForeignKey(
                    name: "fk_task_history_tasks_task_id",
                    column: x => x.task_id,
                    principalTable: "tasks",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_task_history_tasks_continued_from_task_id",
                    column: x => x.continued_from_task_id,
                    principalTable: "tasks",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "fk_task_history_tasks_related_task_id",
                    column: x => x.related_task_id,
                    principalTable: "tasks",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "ix_task_history_task_id_id",
            table: "task_history",
            columns: new[] { "task_id", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_task_history_continued_from_task_id",
            table: "task_history",
            column: "continued_from_task_id");

        migrationBuilder.CreateIndex(
            name: "ix_task_history_related_task_id",
            table: "task_history",
            column: "related_task_id");

        migrationBuilder.Sql("""
            INSERT INTO app_settings (id, workday_boundary_minutes, updated_at)
            VALUES (1, 0, '1970-01-01T00:00:00.000000000Z');
            """);

        migrationBuilder.Sql("""
            INSERT INTO task_history
                (task_id, kind, occurred_at, title, time_type, local_date,
                 time_point, range_start, range_end, result, result_recorded_at,
                 result_note, sort_order, continued_from_task_id, related_task_id,
                 created_at, updated_at)
            SELECT id, 4, COALESCE(result_recorded_at, updated_at), title, time_type, local_date,
                   time_point, range_start, range_end, result, result_recorded_at,
                   result_note, sort_order, continued_from_task_id, NULL,
                   created_at, updated_at
            FROM tasks
            WHERE result IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS tasks_continuation_requires_partial_insert;
            DROP TRIGGER IF EXISTS tasks_continuation_requires_partial_update;
            """);
        migrationBuilder.DropTable(name: "app_settings");
        migrationBuilder.DropTable(name: "task_history");
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS ix_tasks_continued_from_task_id;

            CREATE TABLE tasks_p1
            (
                id TEXT NOT NULL CONSTRAINT pk_tasks PRIMARY KEY,
                title TEXT NOT NULL,
                time_type INTEGER NOT NULL,
                local_date TEXT NOT NULL,
                time_point INTEGER NULL,
                range_start INTEGER NULL,
                range_end INTEGER NULL,
                result INTEGER NULL,
                result_recorded_at TEXT NULL,
                result_note TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                CONSTRAINT ck_tasks_id_uuid_v7 CHECK
                    (length(id) = 36
                     AND id NOT GLOB '*[^0-9A-Fa-f-]*'
                     AND substr(id, 9, 1) = '-'
                     AND substr(id, 14, 1) = '-'
                     AND substr(id, 19, 1) = '-'
                     AND substr(id, 24, 1) = '-'
                     AND lower(substr(id, 15, 1)) = '7'
                     AND lower(substr(id, 20, 1)) IN ('8', '9', 'a', 'b')),
                CONSTRAINT ck_tasks_title_not_blank CHECK
                    (length(trim(title)) > 0),
                CONSTRAINT ck_tasks_time_shape CHECK
                    ((time_type = 0 AND time_point IS NULL
                      AND range_start IS NULL AND range_end IS NULL)
                     OR (time_type = 1 AND time_point IS NOT NULL
                         AND range_start IS NULL AND range_end IS NULL)
                     OR (time_type = 2 AND time_point IS NULL
                         AND range_start IS NOT NULL AND range_end IS NOT NULL
                         AND range_start <> range_end)),
                CONSTRAINT ck_tasks_time_values CHECK
                    ((time_point IS NULL
                      OR (time_point >= 0 AND time_point < 86400000000000))
                     AND (range_start IS NULL
                          OR (range_start >= 0 AND range_start < 86400000000000))
                     AND (range_end IS NULL
                          OR (range_end >= 0 AND range_end < 86400000000000))),
                CONSTRAINT ck_tasks_result_value CHECK
                    (result IS NULL OR result IN (0, 1, 2)),
                CONSTRAINT ck_tasks_result_metadata CHECK
                    ((result IS NULL AND result_recorded_at IS NULL
                      AND result_note IS NULL)
                     OR (result IS NOT NULL AND result_recorded_at IS NOT NULL)),
                CONSTRAINT ck_tasks_result_timestamp_order CHECK
                    (result_recorded_at IS NULL
                     OR (result_recorded_at >= created_at
                         AND result_recorded_at <= updated_at)),
                CONSTRAINT ck_tasks_partial_requires_range CHECK
                    (result IS NULL OR result <> 2 OR time_type = 2),
                CONSTRAINT ck_tasks_timestamp_order CHECK
                    (updated_at >= created_at)
            );

            INSERT INTO tasks_p1
                (id, title, time_type, local_date, time_point, range_start,
                 range_end, result, result_recorded_at, result_note, created_at,
                 updated_at)
            SELECT id, title, time_type, local_date, time_point, range_start,
                   range_end, result, result_recorded_at, result_note, created_at,
                   updated_at
            FROM tasks;

            DROP TABLE tasks;
            ALTER TABLE tasks_p1 RENAME TO tasks;
            """);
    }
}
#pragma warning restore CA1861
