using System.Globalization;
using System.Text;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core.Tasks;

namespace ReminNote.Core.Reminders.Export;

/// <summary>
/// Pure validation for the P3-08 DTOs. It has no filesystem, database,
/// migration or Agent dependency and is therefore safe to use in dry-run
/// tooling before a Candidate restore pipeline exists.
/// </summary>
public static class ReminderExportValidator
{
    private static readonly InstantPattern InstantPattern =
        InstantPattern.CreateWithInvariantCulture(ReminderExportContract.InstantPattern);

    private static readonly HashSet<string> SensitiveFieldNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "secret",
            "secrets",
            "token",
            "accessToken",
            "refreshToken",
            "password",
            "credential",
            "machineId",
            "machineName",
            "userSid",
            "userName",
            "profilePath",
            "absolutePath",
            "runtimeState",
            "transientState",
        };

    public static ReminderExportValidationResult Validate(ReminderExportDocument? document)
    {
        var issues = new List<ReminderExportIssue>();
        if (document is null)
        {
            issues.Add(Issue(ReminderExportErrorCodes.InvalidJson, "导出文档不能为 null。"));
            return new ReminderExportValidationResult(issues);
        }

        if (!string.Equals(document.Schema, ReminderExportContract.Schema, StringComparison.Ordinal))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.UnsupportedSchema,
                $"不支持导出 schema“{document.Schema}”。",
                field: "schema"));
        }

        if (document.SchemaVersion != ReminderExportContract.SchemaVersion)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.UnsupportedSchema,
                $"不支持导出 schemaVersion“{document.SchemaVersion}”。",
                field: "schemaVersion"));
        }

        if (document.Rules is null || document.Schedules is null || document.Instances is null)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.MissingField,
                "rules、schedules、instances 必须是非 null 数组。"));
            return new ReminderExportValidationResult(issues);
        }

        ValidateChecksumShape(document.Checksum, issues);

        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in document.Rules.OrderBy(static value => value?.Id ?? string.Empty, StringComparer.Ordinal))
        {
            if (rule is null)
            {
                issues.Add(Issue(ReminderExportErrorCodes.InvalidJson, "rules 不能包含 null 元素.", ReminderExportEntityKind.Rule));
                continue;
            }

            ValidateRule(rule, ruleIds, issues);
        }

        var scheduleIds = new HashSet<string>(StringComparer.Ordinal);
        var scheduleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schedule in document.Schedules.OrderBy(static value => value?.Id ?? string.Empty, StringComparer.Ordinal))
        {
            if (schedule is null)
            {
                issues.Add(Issue(ReminderExportErrorCodes.InvalidJson, "schedules 不能包含 null 元素.", ReminderExportEntityKind.Schedule));
                continue;
            }

            ValidateSchedule(schedule, scheduleIds, scheduleKeys, issues);
        }

        var instanceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in document.Instances.OrderBy(static value => value?.Id ?? string.Empty, StringComparer.Ordinal))
        {
            if (instance is null)
            {
                issues.Add(Issue(ReminderExportErrorCodes.InvalidJson, "instances 不能包含 null 元素.", ReminderExportEntityKind.Instance));
                continue;
            }

            ValidateInstance(instance, instanceIds, issues);
        }

        ValidateRelations(document, ruleIds, scheduleIds, issues);
        return new ReminderExportValidationResult(issues);
    }

    internal static void RequireValid(ReminderExportDocument document, bool requireChecksum)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = Validate(document);
        if (requireChecksum && string.IsNullOrWhiteSpace(document.Checksum))
        {
            result = AddIssue(
                result,
                Issue(ReminderExportErrorCodes.ChecksumMissing, "导出文档缺少 checksum。", field: "checksum"));
        }

        if (!result.IsValid)
        {
            var first = result.Issues[0];
            throw new ReminderExportContractException(first.Code, first.Message, first.Field);
        }
    }

    internal static bool IsSensitiveFieldName(string name) =>
        SensitiveFieldNames.Contains(name.Normalize(NormalizationForm.FormC));

    internal static Instant ParseInstantOrThrow(string value, string field)
    {
        if (!TryParseInstant(value, out var instant))
        {
            throw new ReminderExportContractException(
                ReminderExportErrorCodes.InvalidTimestamp,
                $"{field} 必须使用 UTC 格式 {ReminderExportContract.InstantPattern}。",
                field);
        }

        return instant;
    }

    internal static string FormatInstant(Instant instant) => InstantPattern.Format(instant);

    private static ReminderExportValidationResult AddIssue(
        ReminderExportValidationResult result,
        ReminderExportIssue issue)
    {
        var issues = result.Issues.ToList();
        issues.Add(issue);
        return new ReminderExportValidationResult(issues);
    }

    private static void ValidateChecksumShape(string? checksum, List<ReminderExportIssue> issues)
    {
        if (string.IsNullOrEmpty(checksum))
        {
            return;
        }

        var expectedLength = ReminderExportContract.ChecksumPrefix.Length + 64;
        if (checksum.Length != expectedLength ||
            !checksum.StartsWith(ReminderExportContract.ChecksumPrefix, StringComparison.Ordinal) ||
            checksum[ReminderExportContract.ChecksumPrefix.Length..].Any(static character =>
                !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.ChecksumMismatch,
                "checksum 必须是 sha256: 后跟 64 个小写十六进制字符。",
                field: "checksum"));
        }
    }

    private static void ValidateRule(
        ReminderRuleExport rule,
        HashSet<string> ruleIds,
        List<ReminderExportIssue> issues)
    {
        var entityId = rule.Id;
        ValidateUniqueId(rule.Id, ReminderExportEntityKind.Rule, ruleIds, issues);
        ValidateUuid(rule.Id, ReminderExportEntityKind.Rule, entityId, "id", issues);
        ValidateUuid(rule.TargetId, ReminderExportEntityKind.Rule, entityId, "targetId", issues);
        ValidateUuid(rule.OccurrenceId, ReminderExportEntityKind.Rule, entityId, "occurrenceId", issues);

        if (!string.Equals(rule.TargetKind, ReminderExportValues.TargetKindTaskInstance, StringComparison.Ordinal))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidEnum,
                "P3-08 只接受 TASK_INSTANCE targetKind；Anime 目标保留给后续集成。",
                ReminderExportEntityKind.Rule,
                entityId,
                "targetKind"));
        }

        if (!IsTaskPurpose(rule.Purpose))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidEnum,
                "purpose 必须是 P3-00 定义的 Task 提醒用途。",
                ReminderExportEntityKind.Rule,
                entityId,
                "purpose"));
        }

        ValidatePriority(rule.Priority, ReminderExportEntityKind.Rule, entityId, "priority", issues);
        ValidateWakePolicy(rule.WakePolicy, entityId, issues);
        if (rule.RuleRevision < 1)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidRevision,
                "ruleRevision 必须从 1 开始。",
                ReminderExportEntityKind.Rule,
                entityId,
                "ruleRevision"));
        }

        var hasCreated = TryParseInstant(rule.CreatedAtUtc, out var createdAt);
        var hasUpdated = TryParseInstant(rule.UpdatedAtUtc, out var updatedAt);
        ValidateTimestamp(hasCreated, ReminderExportEntityKind.Rule, entityId, "createdAtUtc", issues);
        ValidateTimestamp(hasUpdated, ReminderExportEntityKind.Rule, entityId, "updatedAtUtc", issues);
        if (hasCreated && hasUpdated && createdAt > updatedAt)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidRelation,
                "updatedAtUtc 不能早于 createdAtUtc。",
                ReminderExportEntityKind.Rule,
                entityId,
                "updatedAtUtc"));
        }

        ValidateTiming(rule.Timing, entityId, issues);
        ValidateRepeatPolicy(rule.RepeatPolicy, entityId, issues);
    }

    private static void ValidateTiming(
        ReminderTimingExport? timing,
        string entityId,
        List<ReminderExportIssue> issues)
    {
        if (timing is null)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.MissingField,
                "timing 不能为 null。",
                ReminderExportEntityKind.Rule,
                entityId,
                "timing"));
            return;
        }

        if (string.Equals(timing.Kind, ReminderExportValues.TimingRelative, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(timing.Anchor) || timing.OffsetSeconds is null || timing.AtUtc is not null)
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidTiming,
                    "RELATIVE timing 必须且只能包含非空 anchor 与 signed int64 offsetSeconds。",
                    ReminderExportEntityKind.Rule,
                    entityId,
                    "timing"));
            }

            ValidateText(timing.Anchor, ReminderExportEntityKind.Rule, entityId, "timing.anchor", issues);
            return;
        }

        if (string.Equals(timing.Kind, ReminderExportValues.TimingAbsoluteUtc, StringComparison.Ordinal))
        {
            var validAt = timing.AtUtc is not null && TryParseInstant(timing.AtUtc, out _);
            if (!validAt || timing.Anchor is not null || timing.OffsetSeconds is not null)
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidTiming,
                    "ABSOLUTE_UTC timing 必须且只能包含 AtUtc。",
                    ReminderExportEntityKind.Rule,
                    entityId,
                    "timing"));
            }

            if (timing.AtUtc is not null)
            {
                ValidateTimestamp(
                    TryParseInstant(timing.AtUtc, out _),
                    ReminderExportEntityKind.Rule,
                    entityId,
                    "timing.atUtc",
                    issues);
            }

            return;
        }

        issues.Add(Issue(
            ReminderExportErrorCodes.InvalidEnum,
            "timing.kind 必须是 RELATIVE 或 ABSOLUTE_UTC。",
            ReminderExportEntityKind.Rule,
            entityId,
            "timing.kind"));
    }

    private static void ValidateRepeatPolicy(
        ReminderRepeatPolicyExport? repeatPolicy,
        string entityId,
        List<ReminderExportIssue> issues)
    {
        if (repeatPolicy is null)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.MissingField,
                "repeatPolicy 不能为 null。",
                ReminderExportEntityKind.Rule,
                entityId,
                "repeatPolicy"));
            return;
        }

        if (repeatPolicy.Enabled)
        {
            if (repeatPolicy.IntervalSeconds is not > 0)
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidTiming,
                    "启用重复时 intervalSeconds 必须为正数。",
                    ReminderExportEntityKind.Rule,
                    entityId,
                    "repeatPolicy.intervalSeconds"));
            }

            if (repeatPolicy.MaxCount is <= 0)
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidTiming,
                    "maxCount（如存在）必须为正数。",
                    ReminderExportEntityKind.Rule,
                    entityId,
                    "repeatPolicy.maxCount"));
            }
        }
    }

    private static void ValidateSchedule(
        ReminderScheduleExport schedule,
        HashSet<string> scheduleIds,
        HashSet<string> scheduleKeys,
        List<ReminderExportIssue> issues)
    {
        var entityId = schedule.Id;
        ValidateUniqueId(schedule.Id, ReminderExportEntityKind.Schedule, scheduleIds, issues);
        ValidateUuid(schedule.Id, ReminderExportEntityKind.Schedule, entityId, "id", issues);
        ValidateUuid(schedule.RuleId, ReminderExportEntityKind.Schedule, entityId, "ruleId", issues);
        ValidateUuid(schedule.OccurrenceId, ReminderExportEntityKind.Schedule, entityId, "occurrenceId", issues);
        ValidateUuid(schedule.LogicalReminderId, ReminderExportEntityKind.Schedule, entityId, "logicalReminderId", issues);
        if (schedule.OriginScheduleId is not null)
        {
            ValidateUuid(schedule.OriginScheduleId, ReminderExportEntityKind.Schedule, entityId, "originScheduleId", issues);
        }

        var key = $"{schedule.RuleId}\u001f{schedule.OccurrenceId}\u001f{schedule.ScheduleRevision.ToString(CultureInfo.InvariantCulture)}";
        if (!scheduleKeys.Add(key))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.DuplicateId,
                "同一 rule、occurrence 的 scheduleRevision 不得重复。",
                ReminderExportEntityKind.Schedule,
                entityId,
                "scheduleRevision"));
        }

        if (!IsScheduleCause(schedule.Cause))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidEnum,
                "cause 必须是 RULE、SNOOZE 或 REPEAT。",
                ReminderExportEntityKind.Schedule,
                entityId,
                "cause"));
        }

        if (string.Equals(schedule.Cause, ReminderExportValues.ScheduleCauseRule, StringComparison.Ordinal) &&
            schedule.OriginScheduleId is not null)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidRelation,
                "RULE schedule 不得有 originScheduleId。",
                ReminderExportEntityKind.Schedule,
                entityId,
                "originScheduleId"));
        }

        if ((string.Equals(schedule.Cause, ReminderExportValues.ScheduleCauseSnooze, StringComparison.Ordinal) ||
             string.Equals(schedule.Cause, ReminderExportValues.ScheduleCauseRepeat, StringComparison.Ordinal)) &&
            schedule.OriginScheduleId is null)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidRelation,
                "SNOOZE/REPEAT schedule 必须有 originScheduleId。",
                ReminderExportEntityKind.Schedule,
                entityId,
                "originScheduleId"));
        }

        if (schedule.RuleRevision < 1 || schedule.ScheduleRevision < 1)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidRevision,
                "ruleRevision 与 scheduleRevision 必须从 1 开始。",
                ReminderExportEntityKind.Schedule,
                entityId,
                "revision"));
        }

        ValidateTimestamp(
            TryParseInstant(schedule.TriggerAtUtc, out _),
            ReminderExportEntityKind.Schedule,
            entityId,
            "triggerAtUtc",
            issues);
        var createdAt = default(Instant);
        var terminalAt = default(Instant);
        var hasCreated = TryParseInstant(schedule.CreatedAtUtc, out createdAt);
        var hasTerminal = schedule.TerminalAtUtc is not null && TryParseInstant(schedule.TerminalAtUtc, out terminalAt);
        ValidateTimestamp(hasCreated, ReminderExportEntityKind.Schedule, entityId, "createdAtUtc", issues);
        if (schedule.TerminalAtUtc is not null)
        {
            ValidateTimestamp(hasTerminal, ReminderExportEntityKind.Schedule, entityId, "terminalAtUtc", issues);
        }

        if (hasCreated && hasTerminal && terminalAt < createdAt)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidRelation,
                "terminalAtUtc 不能早于 createdAtUtc。",
                ReminderExportEntityKind.Schedule,
                entityId,
                "terminalAtUtc"));
        }

        if (!IsScheduleState(schedule.State))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidEnum,
                "state 必须是 PENDING、CONSUMED、SUPERSEDED、CANCELLED 或 EXPIRED。",
                ReminderExportEntityKind.Schedule,
                entityId,
                "state"));
        }

        var knownState = IsScheduleState(schedule.State);
        var pending = string.Equals(schedule.State, ReminderExportValues.SchedulePending, StringComparison.Ordinal);
        if (pending && (schedule.TerminalReason is not null || schedule.TerminalAtUtc is not null || schedule.ReplacementScheduleId is not null))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidState,
                "PENDING schedule 不得携带 terminalReason、terminalAtUtc 或 replacementScheduleId。",
                ReminderExportEntityKind.Schedule,
                entityId,
                "state"));
        }

        if (knownState && !pending)
        {
            if (schedule.TerminalReason is null || !IsScheduleTerminalReason(schedule.TerminalReason))
            {
                issues.Add(Issue(
                    schedule.TerminalReason is null
                        ? ReminderExportErrorCodes.InvalidState
                        : ReminderExportErrorCodes.InvalidEnum,
                    "终态 schedule 必须携带 P3-00 定义的 terminalReason。",
                    ReminderExportEntityKind.Schedule,
                    entityId,
                    "terminalReason"));
            }

            if (schedule.TerminalAtUtc is null)
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidState,
                    "终态 schedule 必须携带 terminalAtUtc。",
                    ReminderExportEntityKind.Schedule,
                    entityId,
                    "terminalAtUtc"));
            }

            if (string.Equals(schedule.State, ReminderExportValues.ScheduleConsumed, StringComparison.Ordinal) &&
                (!string.Equals(schedule.TerminalReason, ReminderExportValues.ScheduleReasonDueConsumed, StringComparison.Ordinal) ||
                 schedule.ReplacementScheduleId is not null))
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidState,
                    "CONSUMED schedule 必须使用 DUE_CONSUMED 且不得有 replacementScheduleId。",
                    ReminderExportEntityKind.Schedule,
                    entityId,
                    "state"));
            }

            if (string.Equals(schedule.State, ReminderExportValues.ScheduleSuperseded, StringComparison.Ordinal) &&
                schedule.ReplacementScheduleId is null)
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidState,
                    "SUPERSEDED schedule 必须有 replacementScheduleId。",
                    ReminderExportEntityKind.Schedule,
                    entityId,
                    "replacementScheduleId"));
            }

            if ((string.Equals(schedule.State, ReminderExportValues.ScheduleCancelled, StringComparison.Ordinal) ||
                 string.Equals(schedule.State, ReminderExportValues.ScheduleExpired, StringComparison.Ordinal)) &&
                schedule.ReplacementScheduleId is not null)
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidState,
                    "CANCELLED/EXPIRED schedule 不得有 replacementScheduleId。",
                    ReminderExportEntityKind.Schedule,
                    entityId,
                    "replacementScheduleId"));
            }
        }

        if (schedule.ReplacementScheduleId is not null)
        {
            ValidateUuid(schedule.ReplacementScheduleId, ReminderExportEntityKind.Schedule, entityId, "replacementScheduleId", issues);
            if (string.Equals(schedule.ReplacementScheduleId, schedule.Id, StringComparison.Ordinal))
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidRelation,
                    "schedule 不能把自身作为 replacementScheduleId。",
                    ReminderExportEntityKind.Schedule,
                    entityId,
                    "replacementScheduleId"));
            }
        }

        ValidateText(schedule.TimeZoneId, ReminderExportEntityKind.Schedule, entityId, "timeZoneId", issues);
        ValidateText(schedule.TerminalReason, ReminderExportEntityKind.Schedule, entityId, "terminalReason", issues);
    }

    private static void ValidateInstance(
        ReminderInstanceExport instance,
        HashSet<string> instanceIds,
        List<ReminderExportIssue> issues)
    {
        var entityId = instance.Id;
        ValidateUniqueId(instance.Id, ReminderExportEntityKind.Instance, instanceIds, issues);
        ValidateUuid(instance.Id, ReminderExportEntityKind.Instance, entityId, "id", issues);
        ValidateUuid(instance.ScheduleId, ReminderExportEntityKind.Instance, entityId, "scheduleId", issues);
        ValidateUuid(instance.RuleId, ReminderExportEntityKind.Instance, entityId, "ruleId", issues);
        ValidateUuid(instance.OccurrenceId, ReminderExportEntityKind.Instance, entityId, "occurrenceId", issues);
        ValidateUuid(instance.LogicalReminderId, ReminderExportEntityKind.Instance, entityId, "logicalReminderId", issues);
        if (instance.AttemptOrdinal < 1)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidRevision,
                "attemptOrdinal 必须从 1 开始。",
                ReminderExportEntityKind.Instance,
                entityId,
                "attemptOrdinal"));
        }

        if (!IsTaskPurpose(instance.Purpose))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidEnum,
                "purpose 必须是 P3-00 定义的 Task 提醒用途。",
                ReminderExportEntityKind.Instance,
                entityId,
                "purpose"));
        }

        ValidatePriority(instance.Priority, ReminderExportEntityKind.Instance, entityId, "priority", issues);
        var triggeredAt = default(Instant);
        var readAt = default(Instant);
        var resolvedAt = default(Instant);
        var triggeredValid = TryParseInstant(instance.TriggeredAtUtc, out triggeredAt);
        ValidateTimestamp(triggeredValid, ReminderExportEntityKind.Instance, entityId, "triggeredAtUtc", issues);
        var readValid = instance.ReadAtUtc is not null && TryParseInstant(instance.ReadAtUtc, out readAt);
        var resolvedValid = instance.ResolvedAtUtc is not null && TryParseInstant(instance.ResolvedAtUtc, out resolvedAt);
        if (instance.ReadAtUtc is not null)
        {
            ValidateTimestamp(readValid, ReminderExportEntityKind.Instance, entityId, "readAtUtc", issues);
        }

        if (instance.ResolvedAtUtc is not null)
        {
            ValidateTimestamp(resolvedValid, ReminderExportEntityKind.Instance, entityId, "resolvedAtUtc", issues);
        }

        if (!IsInstanceLifecycle(instance.Lifecycle))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidEnum,
                "lifecycle 必须是 UNREAD、READ 或 RESOLVED。",
                ReminderExportEntityKind.Instance,
                entityId,
                "lifecycle"));
        }

        if (string.Equals(instance.Lifecycle, ReminderExportValues.InstanceUnread, StringComparison.Ordinal) &&
            (instance.ReadAtUtc is not null || instance.ResolvedAtUtc is not null || instance.ResolutionAction is not null))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidHistory,
                "UNREAD instance 不得携带读/解决时间或 resolutionAction。",
                ReminderExportEntityKind.Instance,
                entityId,
                "lifecycle"));
        }
        else if (string.Equals(instance.Lifecycle, ReminderExportValues.InstanceRead, StringComparison.Ordinal) &&
                 (instance.ReadAtUtc is null || instance.ResolvedAtUtc is not null || instance.ResolutionAction is not null))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidHistory,
                "READ instance 必须只有 readAtUtc，不能提前解决。",
                ReminderExportEntityKind.Instance,
                entityId,
                "lifecycle"));
        }
        else if (string.Equals(instance.Lifecycle, ReminderExportValues.InstanceResolved, StringComparison.Ordinal) &&
                 (instance.ReadAtUtc is null || instance.ResolvedAtUtc is null || !IsResolutionAction(instance.ResolutionAction)))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidHistory,
                "RESOLVED instance 必须包含 readAtUtc、resolvedAtUtc 与合法 resolutionAction。",
                ReminderExportEntityKind.Instance,
                entityId,
                "lifecycle"));
        }

        if (triggeredValid && readValid && readAt < triggeredAt)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidHistory,
                "readAtUtc 不能早于 triggeredAtUtc。",
                ReminderExportEntityKind.Instance,
                entityId,
                "readAtUtc"));
        }

        if (readValid && resolvedValid && resolvedAt < readAt)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidHistory,
                "resolvedAtUtc 不能早于 readAtUtc。",
                ReminderExportEntityKind.Instance,
                entityId,
                "resolvedAtUtc"));
        }
    }

    private static void ValidateRelations(
        ReminderExportDocument document,
        HashSet<string> ruleIds,
        HashSet<string> scheduleIds,
        List<ReminderExportIssue> issues)
    {
        var rulesById = document.Rules
            .Where(static rule => rule is not null)
            .GroupBy(static rule => rule.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToDictionary(static rule => rule.Id, StringComparer.Ordinal);
        var schedulesById = document.Schedules
            .Where(static schedule => schedule is not null)
            .GroupBy(static schedule => schedule.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToDictionary(static schedule => schedule.Id, StringComparer.Ordinal);

        foreach (var schedule in document.Schedules.OrderBy(static value => value?.Id ?? string.Empty, StringComparer.Ordinal))
        {
            if (schedule is null || !rulesById.TryGetValue(schedule.RuleId, out var rule))
            {
                if (schedule is not null && !ruleIds.Contains(schedule.RuleId))
                {
                    issues.Add(Issue(
                        ReminderExportErrorCodes.InvalidRelation,
                        "schedule.ruleId 必须引用导出的 Rule。",
                        ReminderExportEntityKind.Schedule,
                        schedule.Id,
                        "ruleId"));
                }

                continue;
            }

            if (!string.Equals(schedule.OccurrenceId, rule.OccurrenceId, StringComparison.Ordinal))
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidRelation,
                    "schedule.occurrenceId 必须与 Rule 的 occurrenceId 一致。",
                    ReminderExportEntityKind.Schedule,
                    schedule.Id,
                    "occurrenceId"));
            }

            var relative = string.Equals(rule.Timing.Kind, ReminderExportValues.TimingRelative, StringComparison.Ordinal);
            if (!relative && schedule.TimeZoneId is not null)
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidRelation,
                    "ABSOLUTE_UTC Rule 的 schedule 不应携带 timeZoneId。",
                    ReminderExportEntityKind.Schedule,
                    schedule.Id,
                    "timeZoneId"));
            }

            if (schedule.OriginScheduleId is not null && !schedulesById.ContainsKey(schedule.OriginScheduleId))
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidRelation,
                    "originScheduleId 必须引用导出的 schedule。",
                    ReminderExportEntityKind.Schedule,
                    schedule.Id,
                    "originScheduleId"));
            }

            if (schedule.ReplacementScheduleId is not null && !schedulesById.ContainsKey(schedule.ReplacementScheduleId))
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidRelation,
                    "replacementScheduleId 必须引用导出的 schedule。",
                    ReminderExportEntityKind.Schedule,
                    schedule.Id,
                    "replacementScheduleId"));
            }
        }

        foreach (var instance in document.Instances.OrderBy(static value => value?.Id ?? string.Empty, StringComparer.Ordinal))
        {
            if (instance is null)
            {
                continue;
            }

            if (!schedulesById.TryGetValue(instance.ScheduleId, out var schedule))
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidRelation,
                    "instance.scheduleId 必须引用导出的 schedule。",
                    ReminderExportEntityKind.Instance,
                    instance.Id,
                    "scheduleId"));
                continue;
            }

            if (!string.Equals(instance.RuleId, schedule.RuleId, StringComparison.Ordinal) ||
                !string.Equals(instance.OccurrenceId, schedule.OccurrenceId, StringComparison.Ordinal) ||
                !string.Equals(instance.LogicalReminderId, schedule.LogicalReminderId, StringComparison.Ordinal))
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidRelation,
                    "instance 的 rule、occurrence、logicalReminder 必须与其 schedule 一致。",
                    ReminderExportEntityKind.Instance,
                    instance.Id,
                    "scheduleId"));
            }

            if (IsScheduleState(schedule.State) &&
                !string.Equals(schedule.State, ReminderExportValues.ScheduleConsumed, StringComparison.Ordinal))
            {
                issues.Add(Issue(
                    ReminderExportErrorCodes.InvalidRelation,
                    "instance.scheduleId 必须引用已 CONSUMED 的 schedule。",
                    ReminderExportEntityKind.Instance,
                    instance.Id,
                    "scheduleId"));
            }
        }
    }

    private static void ValidateUniqueId(
        string? id,
        string entityKind,
        HashSet<string> ids,
        List<ReminderExportIssue> issues)
    {
        if (id is not null && !ids.Add(id))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.DuplicateId,
                "同一实体类型内的 ID 不得重复。",
                entityKind,
                id,
                "id"));
        }
    }

    private static void ValidateUuid(
        string? value,
        string entityKind,
        string? entityId,
        string field,
        List<ReminderExportIssue> issues)
    {
        if (value is null || !Guid.TryParseExact(value, ReminderExportContract.UuidFormat, out _) ||
            !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal) ||
            !TaskId.TryParse(value, out _))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidId,
                "ID 必须是非空 UUID v7 的小写 D 表示。",
                entityKind,
                entityId,
                field));
        }
    }

    private static void ValidatePriority(
        string? value,
        string entityKind,
        string? entityId,
        string field,
        List<ReminderExportIssue> issues)
    {
        if (!string.Equals(value, ReminderExportValues.PriorityLow, StringComparison.Ordinal) &&
            !string.Equals(value, ReminderExportValues.PriorityNormal, StringComparison.Ordinal) &&
            !string.Equals(value, ReminderExportValues.PriorityHigh, StringComparison.Ordinal))
        {
            issues.Add(Issue(ReminderExportErrorCodes.InvalidEnum, "priority 必须是 LOW、NORMAL 或 HIGH。", entityKind, entityId, field));
        }
    }

    private static void ValidateWakePolicy(string? value, string? entityId, List<ReminderExportIssue> issues)
    {
        if (!string.Equals(value, ReminderExportValues.WakePolicyDefault, StringComparison.Ordinal) &&
            !string.Equals(value, ReminderExportValues.WakePolicyYes, StringComparison.Ordinal) &&
            !string.Equals(value, ReminderExportValues.WakePolicyNo, StringComparison.Ordinal))
        {
            issues.Add(Issue(ReminderExportErrorCodes.InvalidEnum, "wakePolicy 必须是 DEFAULT、YES 或 NO。", ReminderExportEntityKind.Rule, entityId, "wakePolicy"));
        }
    }

    private static void ValidateTimestamp(
        bool valid,
        string entityKind,
        string? entityId,
        string field,
        List<ReminderExportIssue> issues)
    {
        if (!valid)
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidTimestamp,
                $"{field} 必须使用 UTC 格式 {ReminderExportContract.InstantPattern}。",
                entityKind,
                entityId,
                field));
        }
    }

    private static void ValidateText(
        string? value,
        string entityKind,
        string? entityId,
        string field,
        List<ReminderExportIssue> issues)
    {
        if (value is not null && (value.Length > 256 || value.Any(char.IsControl)))
        {
            issues.Add(Issue(
                ReminderExportErrorCodes.InvalidJson,
                $"{field} 不能包含控制字符且长度不能超过 256。",
                entityKind,
                entityId,
                field));
        }
    }

    private static bool TryParseInstant(string? value, out Instant instant)
    {
        instant = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var result = InstantPattern.Parse(value);
        if (!result.Success)
        {
            return false;
        }

        instant = result.Value;
        return string.Equals(InstantPattern.Format(instant), value, StringComparison.Ordinal);
    }

    private static bool IsTaskPurpose(string? purpose) =>
        string.Equals(purpose, ReminderExportValues.PurposeTaskPreStart, StringComparison.Ordinal) ||
        string.Equals(purpose, ReminderExportValues.PurposeTaskStart, StringComparison.Ordinal) ||
        string.Equals(purpose, ReminderExportValues.PurposeTaskRangeEnd, StringComparison.Ordinal) ||
        string.Equals(purpose, ReminderExportValues.PurposeTaskCustom, StringComparison.Ordinal);

    private static bool IsScheduleCause(string? cause) =>
        string.Equals(cause, ReminderExportValues.ScheduleCauseRule, StringComparison.Ordinal) ||
        string.Equals(cause, ReminderExportValues.ScheduleCauseSnooze, StringComparison.Ordinal) ||
        string.Equals(cause, ReminderExportValues.ScheduleCauseRepeat, StringComparison.Ordinal);

    private static bool IsScheduleState(string? state) =>
        string.Equals(state, ReminderExportValues.SchedulePending, StringComparison.Ordinal) ||
        string.Equals(state, ReminderExportValues.ScheduleConsumed, StringComparison.Ordinal) ||
        string.Equals(state, ReminderExportValues.ScheduleSuperseded, StringComparison.Ordinal) ||
        string.Equals(state, ReminderExportValues.ScheduleCancelled, StringComparison.Ordinal) ||
        string.Equals(state, ReminderExportValues.ScheduleExpired, StringComparison.Ordinal);

    private static bool IsScheduleTerminalReason(string? reason) =>
        string.Equals(reason, ReminderExportValues.ScheduleReasonDueConsumed, StringComparison.Ordinal) ||
        string.Equals(reason, ReminderExportValues.ScheduleReasonRuleRebuilt, StringComparison.Ordinal) ||
        string.Equals(reason, ReminderExportValues.ScheduleReasonTaskPlanChanged, StringComparison.Ordinal) ||
        string.Equals(reason, ReminderExportValues.ScheduleReasonTimeZoneChanged, StringComparison.Ordinal) ||
        string.Equals(reason, ReminderExportValues.ScheduleReasonTaskResultRecorded, StringComparison.Ordinal) ||
        string.Equals(reason, ReminderExportValues.ScheduleReasonRuleDisabled, StringComparison.Ordinal) ||
        string.Equals(reason, ReminderExportValues.ScheduleReasonTaskDeleted, StringComparison.Ordinal) ||
        string.Equals(reason, ReminderExportValues.ScheduleReasonRecoveryObsolete, StringComparison.Ordinal) ||
        string.Equals(reason, ReminderExportValues.ScheduleReasonManualCancelled, StringComparison.Ordinal) ||
        string.Equals(reason, ReminderExportValues.ScheduleReasonReplaced, StringComparison.Ordinal);

    private static bool IsInstanceLifecycle(string? lifecycle) =>
        string.Equals(lifecycle, ReminderExportValues.InstanceUnread, StringComparison.Ordinal) ||
        string.Equals(lifecycle, ReminderExportValues.InstanceRead, StringComparison.Ordinal) ||
        string.Equals(lifecycle, ReminderExportValues.InstanceResolved, StringComparison.Ordinal);

    private static bool IsResolutionAction(string? action) =>
        string.Equals(action, ReminderExportValues.ResolutionDone, StringComparison.Ordinal) ||
        string.Equals(action, ReminderExportValues.ResolutionSnooze, StringComparison.Ordinal) ||
        string.Equals(action, ReminderExportValues.ResolutionSkip, StringComparison.Ordinal) ||
        string.Equals(action, ReminderExportValues.ResolutionIgnore, StringComparison.Ordinal);

    private static ReminderExportIssue Issue(
        string code,
        string message,
        string? entityKind = null,
        string? entityId = null,
        string? field = null) => new(code, message, entityKind, entityId, field);

    internal static class ReminderExportEntityKind
    {
        public const string Rule = "RULE";
        public const string Schedule = "SCHEDULE";
        public const string Instance = "INSTANCE";
    }
}
