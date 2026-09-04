using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace ReminNote.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P3ReminderPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reminder_rules",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    target_kind = table.Column<string>(type: "TEXT", nullable: false),
                    target_id = table.Column<string>(type: "TEXT", nullable: false),
                    occurrence_id = table.Column<string>(type: "TEXT", nullable: false),
                    purpose = table.Column<string>(type: "TEXT", nullable: false),
                    timing_kind = table.Column<string>(type: "TEXT", nullable: false),
                    timing_anchor = table.Column<string>(type: "TEXT", nullable: true),
                    offset_seconds = table.Column<long>(type: "INTEGER", nullable: true),
                    absolute_at_utc = table.Column<string>(type: "TEXT", nullable: true),
                    priority = table.Column<string>(type: "TEXT", nullable: false),
                    pinned = table.Column<int>(type: "INTEGER", nullable: false),
                    repeat_enabled = table.Column<int>(type: "INTEGER", nullable: false),
                    repeat_interval_seconds = table.Column<long>(type: "INTEGER", nullable: true),
                    repeat_max_count = table.Column<int>(type: "INTEGER", nullable: true),
                    wake_policy = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<int>(type: "INTEGER", nullable: false),
                    rule_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reminder_rules", x => x.id);
                    table.CheckConstraint("ck_reminder_rules_id_uuid_v7", "id IS NOT NULL AND length(id) = 36 AND id = lower(id) AND id NOT GLOB '*[^0-9a-f-]*' AND substr(id, 9, 1) = '-' AND substr(id, 14, 1) = '-' AND substr(id, 19, 1) = '-' AND substr(id, 24, 1) = '-' AND lower(substr(id, 15, 1)) = '7' AND lower(substr(id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_rules_occurrence_id_uuid_v7", "occurrence_id IS NOT NULL AND length(occurrence_id) = 36 AND occurrence_id = lower(occurrence_id) AND occurrence_id NOT GLOB '*[^0-9a-f-]*' AND substr(occurrence_id, 9, 1) = '-' AND substr(occurrence_id, 14, 1) = '-' AND substr(occurrence_id, 19, 1) = '-' AND substr(occurrence_id, 24, 1) = '-' AND lower(substr(occurrence_id, 15, 1)) = '7' AND lower(substr(occurrence_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_rules_priority", "priority IN ('LOW','NORMAL','HIGH')");
                    table.CheckConstraint("ck_reminder_rules_purpose", "purpose IN ('TASK_PRE_START','TASK_START','TASK_RANGE_END','TASK_CUSTOM')");
                    table.CheckConstraint("ck_reminder_rules_purpose_timing", "(purpose = 'TASK_CUSTOM' AND timing_kind = 'ABSOLUTE_UTC') OR (purpose = 'TASK_RANGE_END' AND timing_kind = 'RELATIVE' AND timing_anchor = 'RANGE_END') OR (purpose = 'TASK_START' AND timing_kind = 'RELATIVE' AND timing_anchor IN ('TASK_TIME','RANGE_START')) OR (purpose = 'TASK_PRE_START' AND timing_kind = 'RELATIVE' AND timing_anchor IN ('TASK_TIME','RANGE_START') AND offset_seconds < 0)");
                    table.CheckConstraint("ck_reminder_rules_repeat_enabled", "repeat_enabled IN (0,1) AND pinned IN (0,1) AND enabled IN (0,1)");
                    table.CheckConstraint("ck_reminder_rules_repeat_values", "(repeat_interval_seconds IS NULL OR repeat_interval_seconds > 0) AND (repeat_max_count IS NULL OR repeat_max_count > 0) AND (repeat_enabled = 0 OR repeat_interval_seconds IS NOT NULL)");
                    table.CheckConstraint("ck_reminder_rules_revision", "rule_revision >= 1");
                    table.CheckConstraint("ck_reminder_rules_target_id_uuid_v7", "target_id IS NOT NULL AND length(target_id) = 36 AND target_id = lower(target_id) AND target_id NOT GLOB '*[^0-9a-f-]*' AND substr(target_id, 9, 1) = '-' AND substr(target_id, 14, 1) = '-' AND substr(target_id, 19, 1) = '-' AND substr(target_id, 24, 1) = '-' AND lower(substr(target_id, 15, 1)) = '7' AND lower(substr(target_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_rules_target_kind", "target_kind IN ('TASK_INSTANCE')");
                    table.CheckConstraint("ck_reminder_rules_timestamp_order", "updated_at_utc >= created_at_utc");
                    table.CheckConstraint("ck_reminder_rules_timestamps_format", "length(created_at_utc) = 30 AND length(updated_at_utc) = 30 AND absolute_at_utc IS NULL OR length(absolute_at_utc) = 30");
                    table.CheckConstraint("ck_reminder_rules_timing_kind", "timing_kind IN ('RELATIVE','ABSOLUTE_UTC')");
                    table.CheckConstraint("ck_reminder_rules_timing_shape", "(timing_kind = 'RELATIVE' AND timing_anchor IN ('TASK_TIME','RANGE_START','RANGE_END') AND offset_seconds IS NOT NULL AND absolute_at_utc IS NULL) OR (timing_kind = 'ABSOLUTE_UTC' AND timing_anchor IS NULL AND offset_seconds IS NULL AND absolute_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_reminder_rules_wake_policy", "wake_policy IN ('DEFAULT','YES','NO')");
                });

            migrationBuilder.CreateTable(
                name: "reminder_schedules",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    rule_id = table.Column<string>(type: "TEXT", nullable: false),
                    occurrence_id = table.Column<string>(type: "TEXT", nullable: false),
                    logical_reminder_id = table.Column<string>(type: "TEXT", nullable: false),
                    origin_schedule_id = table.Column<string>(type: "TEXT", nullable: true),
                    cause = table.Column<string>(type: "TEXT", nullable: false),
                    rule_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    schedule_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    trigger_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    time_zone_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    purpose_snapshot = table.Column<string>(type: "TEXT", nullable: false),
                    priority_snapshot = table.Column<string>(type: "TEXT", nullable: false),
                    pinned_snapshot = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    terminal_reason = table.Column<string>(type: "TEXT", nullable: true),
                    replacement_schedule_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    terminal_at_utc = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reminder_schedules", x => x.id);
                    table.CheckConstraint("ck_reminder_schedules_cause", "cause IN ('RULE','SNOOZE','REPEAT')");
                    table.CheckConstraint("ck_reminder_schedules_id_uuid_v7", "id IS NOT NULL AND length(id) = 36 AND id = lower(id) AND id NOT GLOB '*[^0-9a-f-]*' AND substr(id, 9, 1) = '-' AND substr(id, 14, 1) = '-' AND substr(id, 19, 1) = '-' AND substr(id, 24, 1) = '-' AND lower(substr(id, 15, 1)) = '7' AND lower(substr(id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_schedules_logical_id_uuid_v7", "logical_reminder_id IS NOT NULL AND length(logical_reminder_id) = 36 AND logical_reminder_id = lower(logical_reminder_id) AND logical_reminder_id NOT GLOB '*[^0-9a-f-]*' AND substr(logical_reminder_id, 9, 1) = '-' AND substr(logical_reminder_id, 14, 1) = '-' AND substr(logical_reminder_id, 19, 1) = '-' AND substr(logical_reminder_id, 24, 1) = '-' AND lower(substr(logical_reminder_id, 15, 1)) = '7' AND lower(substr(logical_reminder_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_schedules_occurrence_id_uuid_v7", "occurrence_id IS NOT NULL AND length(occurrence_id) = 36 AND occurrence_id = lower(occurrence_id) AND occurrence_id NOT GLOB '*[^0-9a-f-]*' AND substr(occurrence_id, 9, 1) = '-' AND substr(occurrence_id, 14, 1) = '-' AND substr(occurrence_id, 19, 1) = '-' AND substr(occurrence_id, 24, 1) = '-' AND lower(substr(occurrence_id, 15, 1)) = '7' AND lower(substr(occurrence_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_schedules_origin_id_uuid_v7", "origin_schedule_id IS NULL OR (origin_schedule_id IS NOT NULL AND length(origin_schedule_id) = 36 AND origin_schedule_id = lower(origin_schedule_id) AND origin_schedule_id NOT GLOB '*[^0-9a-f-]*' AND substr(origin_schedule_id, 9, 1) = '-' AND substr(origin_schedule_id, 14, 1) = '-' AND substr(origin_schedule_id, 19, 1) = '-' AND substr(origin_schedule_id, 24, 1) = '-' AND lower(substr(origin_schedule_id, 15, 1)) = '7' AND lower(substr(origin_schedule_id, 20, 1)) IN ('8', '9', 'a', 'b'))");
                    table.CheckConstraint("ck_reminder_schedules_origin_shape", "(cause = 'RULE' AND origin_schedule_id IS NULL) OR (cause IN ('SNOOZE','REPEAT') AND origin_schedule_id IS NOT NULL)");
                    table.CheckConstraint("ck_reminder_schedules_pinned_snapshot", "pinned_snapshot IN (0,1)");
                    table.CheckConstraint("ck_reminder_schedules_priority_snapshot", "priority_snapshot IN ('LOW','NORMAL','HIGH')");
                    table.CheckConstraint("ck_reminder_schedules_purpose_snapshot", "purpose_snapshot IN ('TASK_PRE_START','TASK_START','TASK_RANGE_END','TASK_CUSTOM')");
                    table.CheckConstraint("ck_reminder_schedules_reason", "terminal_reason IS NULL OR terminal_reason IN ('DUE_CONSUMED','RULE_REBUILT','TASK_PLAN_CHANGED','TIME_ZONE_CHANGED','TASK_RESULT_RECORDED','RULE_DISABLED','TASK_DELETED','RECOVERY_OBSOLETE','MANUAL_CANCELLED','REPLACED')");
                    table.CheckConstraint("ck_reminder_schedules_replacement_id_uuid_v7", "replacement_schedule_id IS NULL OR (replacement_schedule_id IS NOT NULL AND length(replacement_schedule_id) = 36 AND replacement_schedule_id = lower(replacement_schedule_id) AND replacement_schedule_id NOT GLOB '*[^0-9a-f-]*' AND substr(replacement_schedule_id, 9, 1) = '-' AND substr(replacement_schedule_id, 14, 1) = '-' AND substr(replacement_schedule_id, 19, 1) = '-' AND substr(replacement_schedule_id, 24, 1) = '-' AND lower(substr(replacement_schedule_id, 15, 1)) = '7' AND lower(substr(replacement_schedule_id, 20, 1)) IN ('8', '9', 'a', 'b'))");
                    table.CheckConstraint("ck_reminder_schedules_replacement_not_self", "replacement_schedule_id IS NULL OR replacement_schedule_id <> id");
                    table.CheckConstraint("ck_reminder_schedules_revision", "rule_revision >= 1 AND schedule_revision >= 1");
                    table.CheckConstraint("ck_reminder_schedules_rule_id_uuid_v7", "rule_id IS NOT NULL AND length(rule_id) = 36 AND rule_id = lower(rule_id) AND rule_id NOT GLOB '*[^0-9a-f-]*' AND substr(rule_id, 9, 1) = '-' AND substr(rule_id, 14, 1) = '-' AND substr(rule_id, 19, 1) = '-' AND substr(rule_id, 24, 1) = '-' AND lower(substr(rule_id, 15, 1)) = '7' AND lower(substr(rule_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_schedules_state", "state IN ('PENDING','CONSUMED','SUPERSEDED','CANCELLED','EXPIRED')");
                    table.CheckConstraint("ck_reminder_schedules_state_shape", "(state = 'PENDING' AND terminal_reason IS NULL AND replacement_schedule_id IS NULL AND terminal_at_utc IS NULL) OR (state = 'CONSUMED' AND terminal_reason = 'DUE_CONSUMED' AND replacement_schedule_id IS NULL AND terminal_at_utc IS NOT NULL) OR (state = 'SUPERSEDED' AND terminal_reason IS NOT NULL AND replacement_schedule_id IS NOT NULL AND terminal_at_utc IS NOT NULL) OR (state IN ('CANCELLED','EXPIRED') AND terminal_reason IS NOT NULL AND replacement_schedule_id IS NULL AND terminal_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_reminder_schedules_timestamp_order", "terminal_at_utc IS NULL OR terminal_at_utc >= created_at_utc");
                    table.CheckConstraint("ck_reminder_schedules_timestamps_format", "length(trigger_at_utc) = 30 AND length(created_at_utc) = 30 AND terminal_at_utc IS NULL OR length(terminal_at_utc) = 30");
                    table.CheckConstraint("ck_reminder_schedules_timezone", "time_zone_id IS NULL OR length(trim(time_zone_id)) BETWEEN 1 AND 128");
                    table.ForeignKey(
                        name: "FK_reminder_schedules_reminder_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "reminder_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reminder_schedules_reminder_schedules_origin_schedule_id",
                        column: x => x.origin_schedule_id,
                        principalTable: "reminder_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reminder_schedules_reminder_schedules_replacement_schedule_id",
                        column: x => x.replacement_schedule_id,
                        principalTable: "reminder_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reminder_instances",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    schedule_id = table.Column<string>(type: "TEXT", nullable: false),
                    rule_id = table.Column<string>(type: "TEXT", nullable: false),
                    occurrence_id = table.Column<string>(type: "TEXT", nullable: false),
                    logical_reminder_id = table.Column<string>(type: "TEXT", nullable: false),
                    attempt_ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    purpose_snapshot = table.Column<string>(type: "TEXT", nullable: false),
                    priority_snapshot = table.Column<string>(type: "TEXT", nullable: false),
                    pinned_snapshot = table.Column<int>(type: "INTEGER", nullable: false),
                    triggered_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    lifecycle = table.Column<string>(type: "TEXT", nullable: false),
                    read_at_utc = table.Column<string>(type: "TEXT", nullable: true),
                    resolved_at_utc = table.Column<string>(type: "TEXT", nullable: true),
                    resolution_action = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reminder_instances", x => x.id);
                    table.CheckConstraint("ck_reminder_instances_attempt_ordinal", "attempt_ordinal >= 1");
                    table.CheckConstraint("ck_reminder_instances_id_uuid_v7", "id IS NOT NULL AND length(id) = 36 AND id = lower(id) AND id NOT GLOB '*[^0-9a-f-]*' AND substr(id, 9, 1) = '-' AND substr(id, 14, 1) = '-' AND substr(id, 19, 1) = '-' AND substr(id, 24, 1) = '-' AND lower(substr(id, 15, 1)) = '7' AND lower(substr(id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_instances_lifecycle", "lifecycle IN ('UNREAD','READ','RESOLVED')");
                    table.CheckConstraint("ck_reminder_instances_lifecycle_shape", "(lifecycle = 'UNREAD' AND read_at_utc IS NULL AND resolved_at_utc IS NULL AND resolution_action IS NULL) OR (lifecycle = 'READ' AND read_at_utc IS NOT NULL AND resolved_at_utc IS NULL AND resolution_action IS NULL) OR (lifecycle = 'RESOLVED' AND read_at_utc IS NOT NULL AND resolved_at_utc IS NOT NULL AND resolution_action IS NOT NULL)");
                    table.CheckConstraint("ck_reminder_instances_logical_id_uuid_v7", "logical_reminder_id IS NOT NULL AND length(logical_reminder_id) = 36 AND logical_reminder_id = lower(logical_reminder_id) AND logical_reminder_id NOT GLOB '*[^0-9a-f-]*' AND substr(logical_reminder_id, 9, 1) = '-' AND substr(logical_reminder_id, 14, 1) = '-' AND substr(logical_reminder_id, 19, 1) = '-' AND substr(logical_reminder_id, 24, 1) = '-' AND lower(substr(logical_reminder_id, 15, 1)) = '7' AND lower(substr(logical_reminder_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_instances_occurrence_id_uuid_v7", "occurrence_id IS NOT NULL AND length(occurrence_id) = 36 AND occurrence_id = lower(occurrence_id) AND occurrence_id NOT GLOB '*[^0-9a-f-]*' AND substr(occurrence_id, 9, 1) = '-' AND substr(occurrence_id, 14, 1) = '-' AND substr(occurrence_id, 19, 1) = '-' AND substr(occurrence_id, 24, 1) = '-' AND lower(substr(occurrence_id, 15, 1)) = '7' AND lower(substr(occurrence_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_instances_pinned_snapshot", "pinned_snapshot IN (0,1)");
                    table.CheckConstraint("ck_reminder_instances_priority_snapshot", "priority_snapshot IN ('LOW','NORMAL','HIGH')");
                    table.CheckConstraint("ck_reminder_instances_purpose_snapshot", "purpose_snapshot IN ('TASK_PRE_START','TASK_START','TASK_RANGE_END','TASK_CUSTOM')");
                    table.CheckConstraint("ck_reminder_instances_resolution_action", "resolution_action IS NULL OR resolution_action IN ('DONE','SNOOZE','WATCHED','WATCH_LATER','SKIP','IGNORE')");
                    table.CheckConstraint("ck_reminder_instances_rule_id_uuid_v7", "rule_id IS NOT NULL AND length(rule_id) = 36 AND rule_id = lower(rule_id) AND rule_id NOT GLOB '*[^0-9a-f-]*' AND substr(rule_id, 9, 1) = '-' AND substr(rule_id, 14, 1) = '-' AND substr(rule_id, 19, 1) = '-' AND substr(rule_id, 24, 1) = '-' AND lower(substr(rule_id, 15, 1)) = '7' AND lower(substr(rule_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_instances_schedule_id_uuid_v7", "schedule_id IS NOT NULL AND length(schedule_id) = 36 AND schedule_id = lower(schedule_id) AND schedule_id NOT GLOB '*[^0-9a-f-]*' AND substr(schedule_id, 9, 1) = '-' AND substr(schedule_id, 14, 1) = '-' AND substr(schedule_id, 19, 1) = '-' AND substr(schedule_id, 24, 1) = '-' AND lower(substr(schedule_id, 15, 1)) = '7' AND lower(substr(schedule_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_instances_timestamp_order", "(read_at_utc IS NULL OR read_at_utc >= triggered_at_utc) AND (resolved_at_utc IS NULL OR (read_at_utc IS NOT NULL AND resolved_at_utc >= read_at_utc))");
                    table.CheckConstraint("ck_reminder_instances_timestamps_format", "length(triggered_at_utc) = 30 AND read_at_utc IS NULL OR length(read_at_utc) = 30 AND resolved_at_utc IS NULL OR length(resolved_at_utc) = 30");
                    table.ForeignKey(
                        name: "FK_reminder_instances_reminder_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "reminder_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reminder_instances_reminder_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalTable: "reminder_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reminder_delivery_attempts",
                columns: table => new
                {
                    attempt_id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    channel = table.Column<string>(type: "TEXT", nullable: false),
                    attempted_at_utc = table.Column<string>(type: "TEXT", nullable: false),
                    outcome = table.Column<string>(type: "TEXT", nullable: false),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reminder_delivery_attempts", x => x.attempt_id);
                    table.CheckConstraint("ck_reminder_delivery_attempts_channel", "channel IN ('TOAST','TRAY','WIDGET','SOUND','WAKE_TIMER')");
                    table.CheckConstraint("ck_reminder_delivery_attempts_error_code", "error_code IS NULL OR length(trim(error_code)) BETWEEN 1 AND 160");
                    table.CheckConstraint("ck_reminder_delivery_attempts_id_uuid_v7", "attempt_id IS NOT NULL AND length(attempt_id) = 36 AND attempt_id = lower(attempt_id) AND attempt_id NOT GLOB '*[^0-9a-f-]*' AND substr(attempt_id, 9, 1) = '-' AND substr(attempt_id, 14, 1) = '-' AND substr(attempt_id, 19, 1) = '-' AND substr(attempt_id, 24, 1) = '-' AND lower(substr(attempt_id, 15, 1)) = '7' AND lower(substr(attempt_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_delivery_attempts_instance_id_uuid_v7", "instance_id IS NOT NULL AND length(instance_id) = 36 AND instance_id = lower(instance_id) AND instance_id NOT GLOB '*[^0-9a-f-]*' AND substr(instance_id, 9, 1) = '-' AND substr(instance_id, 14, 1) = '-' AND substr(instance_id, 19, 1) = '-' AND substr(instance_id, 24, 1) = '-' AND lower(substr(instance_id, 15, 1)) = '7' AND lower(substr(instance_id, 20, 1)) IN ('8', '9', 'a', 'b')");
                    table.CheckConstraint("ck_reminder_delivery_attempts_outcome", "outcome IN ('DELIVERED','BLOCKED','UNAVAILABLE','FAILED','SUPPRESSED_QUIET_HOURS','NOT_ATTEMPTED')");
                    table.CheckConstraint("ck_reminder_delivery_attempts_timestamp_format", "length(attempted_at_utc) = 30");
                    table.ForeignKey(
                        name: "FK_reminder_delivery_attempts_reminder_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "reminder_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reminder_delivery_attempts_instance_channel",
                table: "reminder_delivery_attempts",
                columns: new[] { "instance_id", "channel" });

            migrationBuilder.CreateIndex(
                name: "ix_reminder_delivery_attempts_instance_time",
                table: "reminder_delivery_attempts",
                columns: new[] { "instance_id", "attempted_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_reminder_instances_logical_triggered_at",
                table: "reminder_instances",
                columns: new[] { "logical_reminder_id", "triggered_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_reminder_instances_occurrence_lifecycle",
                table: "reminder_instances",
                columns: new[] { "occurrence_id", "lifecycle" });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_instances_rule_id",
                table: "reminder_instances",
                column: "rule_id");

            migrationBuilder.CreateIndex(
                name: "ux_reminder_instances_logical_attempt",
                table: "reminder_instances",
                columns: new[] { "logical_reminder_id", "attempt_ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_reminder_instances_schedule_id",
                table: "reminder_instances",
                column: "schedule_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reminder_rules_occurrence_enabled",
                table: "reminder_rules",
                columns: new[] { "occurrence_id", "enabled" });

            migrationBuilder.CreateIndex(
                name: "ix_reminder_rules_target_occurrence",
                table: "reminder_rules",
                columns: new[] { "target_kind", "target_id", "occurrence_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reminder_schedules_logical_revision",
                table: "reminder_schedules",
                columns: new[] { "logical_reminder_id", "schedule_revision" });

            migrationBuilder.CreateIndex(
                name: "ix_reminder_schedules_occurrence_state",
                table: "reminder_schedules",
                columns: new[] { "occurrence_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_schedules_origin_schedule_id",
                table: "reminder_schedules",
                column: "origin_schedule_id");

            migrationBuilder.CreateIndex(
                name: "IX_reminder_schedules_replacement_schedule_id",
                table: "reminder_schedules",
                column: "replacement_schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_reminder_schedules_state_trigger_at_utc",
                table: "reminder_schedules",
                columns: new[] { "state", "trigger_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_reminder_schedules_rule_occurrence_revision",
                table: "reminder_schedules",
                columns: new[] { "rule_id", "occurrence_id", "schedule_revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reminder_delivery_attempts");

            migrationBuilder.DropTable(
                name: "reminder_instances");

            migrationBuilder.DropTable(
                name: "reminder_schedules");

            migrationBuilder.DropTable(
                name: "reminder_rules");
        }
    }
}
