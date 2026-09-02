using System.Collections.ObjectModel;

namespace ReminNote.Core.Reminders.Export;

/// <summary>
/// Stable wire-level identifiers used by the P3-08 reminder export contract.
/// The contract deliberately does not expose persistence or transport types.
/// </summary>
public static class ReminderExportContract
{
    public const string Schema = "reminnote.reminders.export";
    public const int SchemaVersion = 1;
    public const string ContractVersion = "RN-P3-08-EXPORT-1.0.0";
    public const string InstantPattern = "uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'";
    public const string UuidFormat = "D";
    public const string ChecksumAlgorithm = "sha256";
    public const string ChecksumPrefix = "sha256:";
}

public static class ReminderExportValues
{
    public const string TargetKindTaskInstance = "TASK_INSTANCE";

    public const string PurposeTaskPreStart = "TASK_PRE_START";
    public const string PurposeTaskStart = "TASK_START";
    public const string PurposeTaskRangeEnd = "TASK_RANGE_END";
    public const string PurposeTaskCustom = "TASK_CUSTOM";

    public const string PriorityLow = "LOW";
    public const string PriorityNormal = "NORMAL";
    public const string PriorityHigh = "HIGH";

    public const string WakePolicyDefault = "DEFAULT";
    public const string WakePolicyYes = "YES";
    public const string WakePolicyNo = "NO";

    public const string TimingRelative = "RELATIVE";
    public const string TimingAbsoluteUtc = "ABSOLUTE_UTC";

    public const string ScheduleCauseRule = "RULE";
    public const string ScheduleCauseSnooze = "SNOOZE";
    public const string ScheduleCauseRepeat = "REPEAT";

    public const string ScheduleReasonDueConsumed = "DUE_CONSUMED";
    public const string ScheduleReasonRuleRebuilt = "RULE_REBUILT";
    public const string ScheduleReasonTaskPlanChanged = "TASK_PLAN_CHANGED";
    public const string ScheduleReasonTimeZoneChanged = "TIME_ZONE_CHANGED";
    public const string ScheduleReasonTaskResultRecorded = "TASK_RESULT_RECORDED";
    public const string ScheduleReasonRuleDisabled = "RULE_DISABLED";
    public const string ScheduleReasonTaskDeleted = "TASK_DELETED";
    public const string ScheduleReasonRecoveryObsolete = "RECOVERY_OBSOLETE";
    public const string ScheduleReasonManualCancelled = "MANUAL_CANCELLED";
    public const string ScheduleReasonReplaced = "REPLACED";

    public const string SchedulePending = "PENDING";
    public const string ScheduleConsumed = "CONSUMED";
    public const string ScheduleSuperseded = "SUPERSEDED";
    public const string ScheduleCancelled = "CANCELLED";
    public const string ScheduleExpired = "EXPIRED";

    public const string InstanceUnread = "UNREAD";
    public const string InstanceRead = "READ";
    public const string InstanceResolved = "RESOLVED";

    public const string ResolutionDone = "DONE";
    public const string ResolutionSnooze = "SNOOZE";
    public const string ResolutionSkip = "SKIP";
    public const string ResolutionIgnore = "IGNORE";
}

/// <summary>
/// Export envelope. Checksum is the SHA-256 of the canonical envelope with
/// the checksum member omitted. It is intentionally the only envelope
/// metadata: machine, user, secret and runtime-transient values have no DTO.
/// </summary>
public sealed record ReminderExportDocument(
    string Schema,
    int SchemaVersion,
    IReadOnlyList<ReminderRuleExport> Rules,
    IReadOnlyList<ReminderScheduleExport> Schedules,
    IReadOnlyList<ReminderInstanceExport> Instances,
    string? Checksum)
{
    public static ReminderExportDocument Create(
        IEnumerable<ReminderRuleExport> rules,
        IEnumerable<ReminderScheduleExport> schedules,
        IEnumerable<ReminderInstanceExport> instances,
        int schemaVersion = ReminderExportContract.SchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(instances);

        return new ReminderExportDocument(
            ReminderExportContract.Schema,
            schemaVersion,
            new ReadOnlyCollection<ReminderRuleExport>(rules.ToArray()),
            new ReadOnlyCollection<ReminderScheduleExport>(schedules.ToArray()),
            new ReadOnlyCollection<ReminderInstanceExport>(instances.ToArray()),
            Checksum: null);
    }
}

/// <summary>
/// A timing value from the Rule truth. Relative values keep an anchor and a
/// signed offset; absolute values keep a UTC Instant string. Exactly one shape
/// is valid for a given Kind.
/// </summary>
public sealed record ReminderTimingExport(
    string Kind,
    string? Anchor,
    long? OffsetSeconds,
    string? AtUtc);

public sealed record ReminderRepeatPolicyExport(
    bool Enabled,
    long? IntervalSeconds,
    int? MaxCount);

/// <summary>
/// Rule truth. Target references are intentionally narrow and do not include
/// Task titles, notes, profile paths or other private display data.
/// </summary>
public sealed record ReminderRuleExport(
    string Id,
    string TargetKind,
    string TargetId,
    string OccurrenceId,
    string Purpose,
    ReminderTimingExport Timing,
    string Priority,
    bool Pinned,
    ReminderRepeatPolicyExport RepeatPolicy,
    string WakePolicy,
    bool Enabled,
    long RuleRevision,
    string CreatedAtUtc,
    string UpdatedAtUtc);

/// <summary>
/// Rebuildable schedule projection. A restore plan never treats this record
/// as permission to activate a pending schedule directly.
/// </summary>
public sealed record ReminderScheduleExport(
    string Id,
    string RuleId,
    string OccurrenceId,
    string LogicalReminderId,
    string? OriginScheduleId,
    string Cause,
    long RuleRevision,
    long ScheduleRevision,
    string TriggerAtUtc,
    string? TimeZoneId,
    string State,
    string? TerminalReason,
    string? ReplacementScheduleId,
    string CreatedAtUtc,
    string? TerminalAtUtc);

/// <summary>
/// Immutable trigger/lifecycle history. Existing records may only be
/// accepted byte-for-byte/field-for-field identically during planning.
/// </summary>
public sealed record ReminderInstanceExport(
    string Id,
    string ScheduleId,
    string RuleId,
    string OccurrenceId,
    string LogicalReminderId,
    int AttemptOrdinal,
    string Purpose,
    string Priority,
    bool Pinned,
    string TriggeredAtUtc,
    string Lifecycle,
    string? ReadAtUtc,
    string? ResolvedAtUtc,
    string? ResolutionAction);
