namespace ReminNote.Core.Reminders.Export;

public enum ReminderRestoreDestination
{
    Candidate,
    Active,
}

public enum ReminderRestorePlanStatus
{
    Ready,
    RequiresConfirmation,
    Conflict,
    Blocked,
}

public static class ReminderRestoreActionKinds
{
    public const string CreateRule = "CREATE_RULE";
    public const string SkipIdenticalRule = "SKIP_IDENTICAL_RULE";
    public const string RebuildSchedulesFromRules = "REBUILD_SCHEDULES_FROM_RULES";
    public const string DeferPendingSchedules = "DEFER_PENDING_SCHEDULES";
    public const string ImportInstanceHistory = "IMPORT_INSTANCE_HISTORY";
    public const string SkipIdenticalInstance = "SKIP_IDENTICAL_INSTANCE";
    public const string Conflict = "CONFLICT";
}

public static class ReminderRestoreScheduleStrategy
{
    public const string RebuildFromRules = "REBUILD_FROM_RULES";
}

/// <summary>
/// Inputs supplied by the eventual P2.75 Candidate adapter. Dictionaries are
/// snapshots, not persistence handles; this phase never reads a database.
/// </summary>
public sealed record ReminderRestorePlanRequest(
    ReminderExportDocument Document,
    ReminderRestoreDestination Destination = ReminderRestoreDestination.Candidate,
    bool DryRun = true,
    bool Confirmed = false,
    bool CandidatePipelineReady = false,
    long CurrentGlobalRevision = 0,
    long? ExpectedGlobalRevision = null,
    IReadOnlyDictionary<string, ReminderRuleExport>? ExistingRules = null,
    IReadOnlyDictionary<string, ReminderInstanceExport>? ExistingInstances = null);

public sealed record ReminderRestoreAction(
    string Kind,
    string EntityKind,
    string EntityId,
    string Reason);

public sealed record ReminderRestorePlan(
    ReminderRestorePlanStatus Status,
    bool IsDryRun,
    bool CanApply,
    bool CandidateOnly,
    bool WritesActiveDatabase,
    bool ActivatesPendingSchedules,
    string ScheduleStrategy,
    string SourceChecksum,
    long? ExpectedGlobalRevision,
    int RulesToCreate,
    int RulesSkipped,
    int SchedulesToRebuild,
    int PendingSchedulesDeferred,
    int InstancesToImport,
    int InstancesSkipped,
    IReadOnlyList<ReminderRestoreAction> Actions,
    IReadOnlyList<ReminderExportIssue> Issues);

/// <summary>
/// Produces a deterministic, side-effect-free restore plan. It intentionally
/// stops at the Candidate boundary defined by P2.75: callers must hand the
/// plan to the Agent/Candidate pipeline during P3-09 integration.
/// </summary>
public static class ReminderRestorePlanner
{
    public static ReminderRestorePlan CreatePlan(ReminderRestorePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ReminderExportIssue>();
        var document = request.Document;
        var validation = ReminderExportValidator.Validate(document);
        issues.AddRange(validation.Issues);

        if (document is null)
        {
            return BuildPlan(
                request,
                ReminderRestorePlanStatus.Blocked,
                issues,
                sourceChecksum: string.Empty,
                actions: Array.Empty<ReminderRestoreAction>(),
                rulesToCreate: 0,
                rulesSkipped: 0,
                schedulesToRebuild: 0,
                pendingSchedulesDeferred: 0,
                instancesToImport: 0,
                instancesSkipped: 0);
        }

        if (string.IsNullOrWhiteSpace(document.Checksum))
        {
            issues.Add(new ReminderExportIssue(
                ReminderExportErrorCodes.ChecksumMissing,
                "受控恢复必须从带 checksum 的已验证 artifact 开始。",
                Field: "checksum"));
        }
        else if (validation.IsValid)
        {
            var expectedChecksum = ReminderExportJson.ComputeChecksum(document);
            if (!string.Equals(expectedChecksum, document.Checksum, StringComparison.Ordinal))
            {
                issues.Add(new ReminderExportIssue(
                    ReminderExportErrorCodes.ChecksumMismatch,
                    "受控恢复 artifact 的 checksum 与 canonical 内容不匹配。",
                    Field: "checksum"));
            }
        }

        if (request.Destination == ReminderRestoreDestination.Active)
        {
            issues.Add(new ReminderExportIssue(
                ReminderExportErrorCodes.RestoreActiveWriteForbidden,
                "P3-08 不允许把导出直接写入 Active；必须走 P2.75 Candidate。"));
        }

        if (!request.DryRun && !request.CandidatePipelineReady)
        {
            issues.Add(new ReminderExportIssue(
                ReminderExportErrorCodes.RestoreCandidateRequired,
                "非 dry-run 受控恢复必须声明 Candidate pipeline 已就绪。"));
        }

        if (request.CurrentGlobalRevision < 0 ||
            (request.ExpectedGlobalRevision is long expected && expected < 0))
        {
            issues.Add(new ReminderExportIssue(
                ReminderExportErrorCodes.InvalidRevision,
                "global revision 不能为负数。",
                Field: "globalRevision"));
        }
        else if (request.ExpectedGlobalRevision is long expectedRevision &&
                 expectedRevision != request.CurrentGlobalRevision)
        {
            issues.Add(new ReminderExportIssue(
                ReminderExportErrorCodes.RestoreGlobalRevisionConflict,
                "Candidate 预期 global revision 与当前快照不一致；拒绝静默覆盖。",
                Field: "expectedGlobalRevision"));
        }

        var existingRules = request.ExistingRules ?? EmptyRuleMap;
        var existingInstances = request.ExistingInstances ?? EmptyInstanceMap;
        var actions = new List<ReminderRestoreAction>();
        var rulesToCreate = 0;
        var rulesSkipped = 0;
        foreach (var rule in document.Rules.OrderBy(static value => value.Id, StringComparer.Ordinal))
        {
            if (existingRules.TryGetValue(rule.Id, out var existingRule))
            {
                if (existingRule == rule)
                {
                    rulesSkipped++;
                    actions.Add(new ReminderRestoreAction(
                        ReminderRestoreActionKinds.SkipIdenticalRule,
                        "RULE",
                        rule.Id,
                        "相同 ID 且内容/修订完全一致，保持现有 Rule。"));
                }
                else
                {
                    issues.Add(new ReminderExportIssue(
                        ReminderExportErrorCodes.RestoreRuleConflict,
                        "同一 Rule ID 的内容或 ruleRevision 不一致；不允许覆盖。",
                        "RULE",
                        rule.Id,
                        "ruleRevision"));
                    actions.Add(new ReminderRestoreAction(
                        ReminderRestoreActionKinds.Conflict,
                        "RULE",
                        rule.Id,
                        "revision/content conflict"));
                }
            }
            else
            {
                rulesToCreate++;
                actions.Add(new ReminderRestoreAction(
                    ReminderRestoreActionKinds.CreateRule,
                    "RULE",
                    rule.Id,
                    "导入 Rule truth；由 Candidate adapter 映射到 P3-01 根实体。"));
            }
        }

        var schedulesToRebuild = document.Schedules.Count;
        var pendingSchedulesDeferred = document.Schedules.Count(static schedule =>
            string.Equals(schedule.State, ReminderExportValues.SchedulePending, StringComparison.Ordinal));
        foreach (var rule in document.Rules.OrderBy(static value => value.Id, StringComparer.Ordinal))
        {
            actions.Add(new ReminderRestoreAction(
                ReminderRestoreActionKinds.RebuildSchedulesFromRules,
                "SCHEDULE",
                rule.Id,
                "Schedule 是可重建投影，不把导出行作为直接激活指令。"));
        }

        if (pendingSchedulesDeferred > 0)
        {
            actions.Add(new ReminderRestoreAction(
                ReminderRestoreActionKinds.DeferPendingSchedules,
                "SCHEDULE",
                "pending",
                "pending schedule 等待 Agent/P2.75 校验后再决定，当前绝不激活。"));
        }

        var instancesToImport = 0;
        var instancesSkipped = 0;
        foreach (var instance in document.Instances.OrderBy(static value => value.Id, StringComparer.Ordinal))
        {
            if (existingInstances.TryGetValue(instance.Id, out var existingInstance))
            {
                if (existingInstance == instance)
                {
                    instancesSkipped++;
                    actions.Add(new ReminderRestoreAction(
                        ReminderRestoreActionKinds.SkipIdenticalInstance,
                        "INSTANCE",
                        instance.Id,
                        "Immutable history 已存在且完全一致，保持原记录。"));
                }
                else
                {
                    issues.Add(new ReminderExportIssue(
                        ReminderExportErrorCodes.RestoreInstanceConflict,
                        "同一 Instance ID 的不可变事实不一致；拒绝修改历史。",
                        "INSTANCE",
                        instance.Id,
                        "id"));
                    actions.Add(new ReminderRestoreAction(
                        ReminderRestoreActionKinds.Conflict,
                        "INSTANCE",
                        instance.Id,
                        "immutable history conflict"));
                }
            }
            else
            {
                instancesToImport++;
                actions.Add(new ReminderRestoreAction(
                    ReminderRestoreActionKinds.ImportInstanceHistory,
                    "INSTANCE",
                    instance.Id,
                    "仅追加 immutable history；不修改既有 Instance。"));
            }
        }

        var status = DetermineStatus(request, issues);
        var orderedActions = actions
            .OrderBy(static action => action.EntityKind, StringComparer.Ordinal)
            .ThenBy(static action => action.EntityId, StringComparer.Ordinal)
            .ThenBy(static action => action.Kind, StringComparer.Ordinal)
            .ToArray();
        var sourceChecksum = string.IsNullOrWhiteSpace(document.Checksum)
            ? string.Empty
            : document.Checksum!;
        var canApply = status == ReminderRestorePlanStatus.Ready &&
                       !request.DryRun &&
                       request.Destination == ReminderRestoreDestination.Candidate &&
                       request.CandidatePipelineReady &&
                       request.Confirmed;

        return BuildPlan(
            request,
            status,
            issues,
            sourceChecksum,
            orderedActions,
            rulesToCreate,
            rulesSkipped,
            schedulesToRebuild,
            pendingSchedulesDeferred,
            instancesToImport,
            instancesSkipped,
            canApply);
    }

    private static ReminderRestorePlanStatus DetermineStatus(
        ReminderRestorePlanRequest request,
        List<ReminderExportIssue> issues)
    {
        if (issues.Any(static issue =>
                issue.Code is ReminderExportErrorCodes.RestoreRuleConflict or
                ReminderExportErrorCodes.RestoreInstanceConflict or
                ReminderExportErrorCodes.RestoreGlobalRevisionConflict))
        {
            return ReminderRestorePlanStatus.Conflict;
        }

        if (issues.Count > 0)
        {
            return ReminderRestorePlanStatus.Blocked;
        }

        if (!request.DryRun && !request.Confirmed)
        {
            return ReminderRestorePlanStatus.RequiresConfirmation;
        }

        return ReminderRestorePlanStatus.Ready;
    }

    private static ReminderRestorePlan BuildPlan(
        ReminderRestorePlanRequest request,
        ReminderRestorePlanStatus status,
        IReadOnlyList<ReminderExportIssue> issues,
        string sourceChecksum,
        IReadOnlyList<ReminderRestoreAction> actions,
        int rulesToCreate,
        int rulesSkipped,
        int schedulesToRebuild,
        int pendingSchedulesDeferred,
        int instancesToImport,
        int instancesSkipped,
        bool canApply = false)
    {
        return new ReminderRestorePlan(
            status,
            request.DryRun,
            canApply,
            request.Destination == ReminderRestoreDestination.Candidate,
            WritesActiveDatabase: false,
            ActivatesPendingSchedules: false,
            ReminderRestoreScheduleStrategy.RebuildFromRules,
            sourceChecksum,
            request.ExpectedGlobalRevision,
            rulesToCreate,
            rulesSkipped,
            schedulesToRebuild,
            pendingSchedulesDeferred,
            instancesToImport,
            instancesSkipped,
            actions,
            issues);
    }

    private static readonly IReadOnlyDictionary<string, ReminderRuleExport> EmptyRuleMap =
        new Dictionary<string, ReminderRuleExport>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, ReminderInstanceExport> EmptyInstanceMap =
        new Dictionary<string, ReminderInstanceExport>(StringComparer.Ordinal);
}
