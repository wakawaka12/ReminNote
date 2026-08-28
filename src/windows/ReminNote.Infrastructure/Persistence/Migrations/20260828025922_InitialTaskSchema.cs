using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReminNote.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTaskSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tasks",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    time_type = table.Column<int>(type: "INTEGER", nullable: false),
                    local_date = table.Column<string>(type: "TEXT", nullable: false),
                    time_point = table.Column<long>(type: "INTEGER", nullable: true),
                    range_start = table.Column<long>(type: "INTEGER", nullable: true),
                    range_end = table.Column<long>(type: "INTEGER", nullable: true),
                    result = table.Column<int>(type: "INTEGER", nullable: true),
                    result_recorded_at = table.Column<string>(type: "TEXT", nullable: true),
                    result_note = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tasks", x => x.id);
                    table.CheckConstraint("ck_tasks_id_uuid_v7", "length(id) = 36 AND id NOT GLOB '*[^0-9A-Fa-f-]*' AND substr(id, 9, 1) = '-' AND substr(id, 14, 1) = '-' AND substr(id, 19, 1) = '-' AND substr(id, 24, 1) = '-' AND lower(substr(id, 15, 1)) = '7' AND lower(substr(id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_tasks_partial_requires_range", "result IS NULL OR result <> 2 OR time_type = 2");
                    table.CheckConstraint("ck_tasks_result_metadata", "(result IS NULL AND result_recorded_at IS NULL AND result_note IS NULL) OR (result IS NOT NULL AND result_recorded_at IS NOT NULL)");
                    table.CheckConstraint("ck_tasks_result_timestamp_order", "result_recorded_at IS NULL OR (result_recorded_at >= created_at AND result_recorded_at <= updated_at)");
                    table.CheckConstraint("ck_tasks_result_value", "result IS NULL OR result IN (0, 1, 2)");
                    table.CheckConstraint("ck_tasks_time_shape", "(time_type = 0 AND time_point IS NULL AND range_start IS NULL AND range_end IS NULL) OR (time_type = 1 AND time_point IS NOT NULL AND range_start IS NULL AND range_end IS NULL) OR (time_type = 2 AND time_point IS NULL AND range_start IS NOT NULL AND range_end IS NOT NULL AND range_start <> range_end)");
                    table.CheckConstraint("ck_tasks_time_values", "(time_point IS NULL OR (time_point >= 0 AND time_point < 86400000000000)) AND (range_start IS NULL OR (range_start >= 0 AND range_start < 86400000000000)) AND (range_end IS NULL OR (range_end >= 0 AND range_end < 86400000000000))");
                    table.CheckConstraint("ck_tasks_timestamp_order", "updated_at >= created_at");
                    table.CheckConstraint("ck_tasks_title_not_blank", "length(trim(title)) > 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tasks");
        }
    }
}
