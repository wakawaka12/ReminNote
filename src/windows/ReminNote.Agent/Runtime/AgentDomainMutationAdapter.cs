using Microsoft.EntityFrameworkCore;
using NodaTime;
using System.Text.Json;
using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Protocol;
using Calculation = ReminNote.Core.Reminders.Calculation;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Time;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Application;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.Reminders;

namespace ReminNote.Agent.Runtime;

internal sealed class AgentMutationMetadata
{
    public string? TaskId { get; set; }

    public string? RuleId { get; set; }

    public bool Deleted { get; set; }
}

/// <summary>
/// Translates the approved protocol payloads into the existing Core use cases
/// while keeping EF on the P2.5 writer transaction supplied by the storage
/// adapter. No repository method here can open a second transaction.
/// </summary>
internal static class AgentDomainMutationAdapter
{
    public static async ValueTask<P25MutationDecision> ApplyAsync(
        P25MutationContext mutation,
        ProtocolRequest request,
        AgentMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var context = ReminNoteDatabase.CreateContext(mutation.Connection);
        context.Database.UseTransaction(mutation.Transaction);
        var repository = new TransactionBoundTaskRepository(context);
        var application = new TaskApplicationService(repository, SystemClock.Instance);

        try
        {
            return request.Operation switch
            {
                ProtocolOperations.TaskCreate => await CreateAsync(context, application, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskRename => await RenameAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskRecordResult => await RecordResultAsync(context, application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskUpdatePlan => await UpdatePlanAsync(context, application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskReorder => await ReorderAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskContinue => await ContinueAsync(context, application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskDelete => await DeleteAsync(context, application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.ReminderRuleUpsert => await UpsertReminderRuleAsync(context, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.ReminderMarkRead => await MarkReadAsync(context, request, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.ReminderResolve => await ResolveAsync(context, request, cancellationToken).ConfigureAwait(false),
                _ => P25MutationDecision.Rejected(ProtocolErrorCodes.InvalidRequest)
            };
        }
        catch (DomainValidationException exception)
        {
            var error = exception.Errors.Count == 0 ? null : exception.Errors[0].Code;
            return P25MutationDecision.Rejected(
                string.IsNullOrWhiteSpace(error) ? ProtocolErrorCodes.InvalidRequest : error);
        }
        catch (KeyNotFoundException)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }
    }

    private static async ValueTask<P25MutationDecision> CreateAsync(
        ReminNoteDbContext context,
        TaskApplicationService application,
        ProtocolRequest request,
        AgentMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        var timeSpec = ParseTimeSpec(payload);
        var snapshot = await application.CreateAsync(
                new CreateTaskCommand(
                    RequireString(payload, "title"),
                    timeSpec),
                cancellationToken)
            .ConfigureAwait(false);
        metadata.TaskId = snapshot.Id.ToString();

        var changes = new List<P25JournalChange>
        {
            new("task", metadata.TaskId, "created")
        };
        var reminder = ReadReminderOptions(payload) ?? DefaultReminderFor(timeSpec);
        if (reminder is not null)
        {
            var reminderChanges = await CreateReminderRuleAsync(
                    context,
                    snapshot.Id.Value,
                    timeSpec,
                    reminder,
                    cancellationToken)
                .ConfigureAwait(false);
            changes.AddRange(reminderChanges);
        }

        return changes.Count == 0
            ? P25MutationDecision.NoOp()
            : P25MutationDecision.Changed(changes);
    }

    private static async ValueTask<P25MutationDecision> RenameAsync(
        TaskApplicationService application,
        TransactionBoundTaskRepository repository,
        ProtocolRequest request,
        AgentMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        var taskId = ParseTaskId(request.Payload, "taskId");
        var before = await repository.FindAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (before is null)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        var title = RequireString(request.Payload, "title");
        metadata.TaskId = taskId.ToString();
        if (string.Equals(before.Title, title.Trim(), StringComparison.Ordinal))
        {
            return P25MutationDecision.NoOp();
        }

        _ = await application.UpdateAsync(
                new UpdateTaskCommand(taskId, title, before.TimeSpec),
                cancellationToken)
            .ConfigureAwait(false);
        return P25MutationDecision.Changed(
            [new P25JournalChange("task", metadata.TaskId, "renamed")]);
    }

    private static async ValueTask<P25MutationDecision> RecordResultAsync(
        ReminNoteDbContext context,
        TaskApplicationService application,
        TransactionBoundTaskRepository repository,
        ProtocolRequest request,
        AgentMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        var taskId = ParseTaskId(request.Payload, "taskId");
        var before = await repository.FindAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (before is null)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        var result = ParseResult(RequireString(request.Payload, "result"));
        var note = ReadNullableString(request.Payload, "note");
        metadata.TaskId = taskId.ToString();
        if (before.Result == result && string.Equals(before.ResultNote, NormalizeNote(note), StringComparison.Ordinal))
        {
            var alreadyCancelled = before.RecordedAt is { } existingRecordedAt
                ? await ReminderPersistenceCommands.CancelPendingForOccurrenceAsync(
                        context,
                        taskId.Value,
                        existingRecordedAt,
                        ScheduleStateReason.TASK_RESULT_RECORDED,
                        cancellationToken)
                    .ConfigureAwait(false)
                : Array.Empty<Guid>();
            return alreadyCancelled.Count == 0
                ? P25MutationDecision.NoOp()
                : P25MutationDecision.Changed(
                    alreadyCancelled.Select(scheduleId => new P25JournalChange(
                        "reminder_schedule",
                        scheduleId.ToString("D"),
                        "cancelled")));
        }

        var recorded = await application.RecordResultAsync(
                new RecordTaskResultCommand(taskId, result, note),
                cancellationToken)
            .ConfigureAwait(false);
        if (recorded is null || recorded.RecordedAt is not { } recordedAtValue)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        var cancelled = await ReminderPersistenceCommands.CancelPendingForOccurrenceAsync(
                context,
                taskId.Value,
                recordedAtValue,
                ScheduleStateReason.TASK_RESULT_RECORDED,
                cancellationToken)
            .ConfigureAwait(false);
        var changes = new List<P25JournalChange>
        {
            new("task", metadata.TaskId, "result_recorded"),
            new("task_history", metadata.TaskId, "result_recorded")
        };
        changes.AddRange(cancelled.Select(scheduleId => new P25JournalChange(
            "reminder_schedule",
            scheduleId.ToString("D"),
            "cancelled")));
        return P25MutationDecision.Changed(
            changes);
    }

    private static async ValueTask<P25MutationDecision> MarkReadAsync(
        ReminNoteDbContext context,
        ProtocolRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ProtocolJson.ReadReminderMarkReadPayload(request);
        var instanceId = ReminderInstanceId.Parse(payload.InstanceId);
        var entity = await context.ReminderInstances
            .SingleOrDefaultAsync(instance => instance.Id == instanceId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        var instance = entity.ToDomain();
        if (!instance.MarkRead(SystemClock.Instance.GetCurrentInstant()))
        {
            return P25MutationDecision.NoOp();
        }

        entity.Apply(instance);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return P25MutationDecision.Changed(
            [new P25JournalChange(
                "reminder_instance",
                instance.Id.ToString(),
                "marked_read")]);
    }

    private static async ValueTask<P25MutationDecision> ResolveAsync(
        ReminNoteDbContext context,
        ProtocolRequest request,
        CancellationToken cancellationToken)
    {
        var payload = ProtocolJson.ReadReminderResolvePayload(request);
        var instanceId = ReminderInstanceId.Parse(payload.InstanceId);
        var entity = await context.ReminderInstances
            .SingleOrDefaultAsync(instance => instance.Id == instanceId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        var instance = entity.ToDomain();
        var resolvedAtUtc = SystemClock.Instance.GetCurrentInstant();
        var action = payload.ToDomainAction();
        var resolvedChanged = instance.Resolve(action, resolvedAtUtc);
        var changes = new List<P25JournalChange>();
        if (resolvedChanged)
        {
            entity.Apply(instance);
            changes.Add(new P25JournalChange(
                "reminder_instance",
                instance.Id.ToString(),
                "resolved"));
        }

        if (!resolvedChanged && action != ResolutionAction.DONE)
        {
            return P25MutationDecision.NoOp();
        }

        // DONE is a cross-aggregate action, not merely a visual dismissal. It
        // records COMPLETED and cancels every future schedule in this
        // occurrence in the same Agent writer transaction. Other occurrences
        // are not selected by the occurrence predicate.
        if (action == ResolutionAction.DONE)
        {
            var schedule = await context.ReminderSchedules
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == instance.ScheduleId.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (schedule is null)
            {
                return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
            }

            var repository = new TransactionBoundTaskRepository(context);
            var taskId = TaskId.From(schedule.OccurrenceId);
            var task = await repository.FindAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (task is null)
            {
                return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
            }

            if (task.Result is not null && task.Result != TaskResult.COMPLETED)
            {
                throw new DomainValidationException(new DomainValidationError(
                    "task.result.conflict",
                    "DONE cannot replace an existing non-COMPLETED task result.",
                    "action"));
            }

            if (task.Result is null)
            {
                var application = new TaskApplicationService(repository, SystemClock.Instance);
                var completed = await application.RecordResultAsync(
                        new RecordTaskResultCommand(taskId, TaskResult.COMPLETED),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (completed is null)
                {
                    return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
                }

                changes.Add(new P25JournalChange("task", taskId.ToString(), "result_recorded"));
                changes.Add(new P25JournalChange("task_history", taskId.ToString(), "result_recorded"));
            }

            var cancelled = await ReminderPersistenceCommands.CancelPendingForOccurrenceAsync(
                    context,
                    schedule.OccurrenceId,
                    resolvedAtUtc,
                    ScheduleStateReason.TASK_RESULT_RECORDED,
                    cancellationToken)
                .ConfigureAwait(false);
            changes.AddRange(cancelled.Select(scheduleId => new P25JournalChange(
                "reminder_schedule",
                scheduleId.ToString("D"),
                "cancelled")));
        }

        if (action == ResolutionAction.SNOOZE)
        {
            var originEntity = await context.ReminderSchedules
                .SingleOrDefaultAsync(
                    schedule => schedule.Id == instance.ScheduleId.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (originEntity is null)
            {
                return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
            }

            var revisions = await context.ReminderSchedules
                .Where(schedule =>
                    schedule.RuleId == originEntity.RuleId &&
                    schedule.OccurrenceId == originEntity.OccurrenceId)
                .Select(schedule => schedule.ScheduleRevision)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var nextRevision = revisions.Count == 0
                ? 1L
                : checked(revisions.Max() + 1);
            var origin = originEntity.ToDomain();
            var snoozed = ReminderSchedule.CreateDerived(
                origin,
                ScheduleCause.SNOOZE,
                nextRevision,
                resolvedAtUtc + Duration.FromSeconds(payload.SnoozeSeconds!.Value),
                resolvedAtUtc,
                origin.TimeZoneId,
                ReminderScheduleId.New());
            context.ReminderSchedules.Add(ReminderScheduleEntity.FromDomain(snoozed));
            changes.Add(new P25JournalChange(
                "reminder_schedule",
                snoozed.Id.ToString(),
                "snoozed"));
        }

        if (changes.Count == 0)
        {
            return P25MutationDecision.NoOp();
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return P25MutationDecision.Changed(changes);
    }

    private static async ValueTask<P25MutationDecision> UpdatePlanAsync(
        ReminNoteDbContext context,
        TaskApplicationService application,
        TransactionBoundTaskRepository repository,
        ProtocolRequest request,
        AgentMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        var taskId = ParseTaskId(request.Payload, "taskId");
        var before = await repository.FindAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (before is null)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        var title = ReadNullableString(request.Payload, "title") ?? before.Title;
        var timeSpec = ParseTimeSpec(request.Payload);
        var reminder = ReadReminderOptions(request.Payload);
        metadata.TaskId = taskId.ToString();
        var taskChanged = !string.Equals(before.Title, title.Trim(), StringComparison.Ordinal) ||
            before.TimeSpec != timeSpec;
        if (!taskChanged && reminder is null)
        {
            return P25MutationDecision.NoOp();
        }

        var changes = new List<P25JournalChange>();
        if (taskChanged)
        {
            _ = await application.UpdateAsync(
                    new UpdateTaskCommand(taskId, title, timeSpec),
                    cancellationToken)
                .ConfigureAwait(false);
            changes.Add(new P25JournalChange("task", metadata.TaskId, "plan_updated"));
        }
        var ruleEntities = await context.ReminderRules
            .Where(rule => rule.TargetId == taskId.Value)
            .OrderBy(rule => rule.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (reminder is not null)
        {
            if (ruleEntities.Count > 1)
            {
                throw new DomainValidationException(new DomainValidationError(
                    "reminder.rule.selection_required",
                    "Updating a task with multiple reminder rules requires command.reminder.rule.upsert.",
                    "reminder"));
            }

            if (ruleEntities.Count == 0)
            {
                changes.AddRange(await CreateReminderRuleAsync(
                        context,
                        taskId.Value,
                        timeSpec,
                        reminder,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
            else
            {
                changes.AddRange(await RebuildReminderRuleAsync(
                        context,
                        ruleEntities[0],
                        before.TimeSpec,
                        timeSpec,
                        reminder,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
        }
        else if (before.TimeSpec != timeSpec)
        {
            foreach (var ruleEntity in ruleEntities)
            {
                changes.AddRange(await RebuildReminderRuleAsync(
                        context,
                        ruleEntity,
                        before.TimeSpec,
                        timeSpec,
                        options: null,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        return changes.Count == 0
            ? P25MutationDecision.NoOp()
            : P25MutationDecision.Changed(changes);
    }

    private static async ValueTask<P25MutationDecision> ReorderAsync(
        TaskApplicationService application,
        TransactionBoundTaskRepository repository,
        ProtocolRequest request,
        AgentMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        var taskId = ParseTaskId(request.Payload, "taskId");
        var before = await repository.FindAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (before is null)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        var sortOrder = RequireInt32(request.Payload, "sortOrder");
        metadata.TaskId = taskId.ToString();
        if (before.SortOrder == sortOrder)
        {
            return P25MutationDecision.NoOp();
        }

        _ = await application.ReorderAsync(
                new ReorderTaskCommand(taskId, sortOrder),
                cancellationToken)
            .ConfigureAwait(false);
        return P25MutationDecision.Changed(
            [new P25JournalChange("task", metadata.TaskId, "reordered")]);
    }

    private static async ValueTask<P25MutationDecision> ContinueAsync(
        ReminNoteDbContext context,
        TaskApplicationService application,
        TransactionBoundTaskRepository repository,
        ProtocolRequest request,
        AgentMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        var sourceTaskId = ParseTaskId(request.Payload, "sourceTaskId");
        if (await repository.FindAsync(sourceTaskId, cancellationToken).ConfigureAwait(false) is null)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        var timeSpec = ParseTimeSpec(request.Payload);
        var reminder = ReadReminderOptions(request.Payload);
        var snapshot = await application.ContinueAsync(
                new ContinueTaskCommand(
                    sourceTaskId,
                    RequireString(request.Payload, "title"),
                    timeSpec,
                    reminder),
                cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        metadata.TaskId = snapshot.Id.ToString();
        var changes = new List<P25JournalChange>
        {
            new("task", metadata.TaskId, "continued"),
            new("task_history", metadata.TaskId, "continued")
        };
        var reminderOptions = reminder ?? DefaultReminderFor(timeSpec);
        if (reminderOptions is not null)
        {
            changes.AddRange(await CreateReminderRuleAsync(
                    context,
                    snapshot.Id.Value,
                    timeSpec,
                    reminderOptions,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return P25MutationDecision.Changed(changes);
    }

    private static async ValueTask<P25MutationDecision> DeleteAsync(
        ReminNoteDbContext context,
        TaskApplicationService application,
        TransactionBoundTaskRepository repository,
        ProtocolRequest request,
        AgentMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        var taskId = ParseTaskId(request.Payload, "taskId");
        if (await repository.FindAsync(taskId, cancellationToken).ConfigureAwait(false) is null)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        var changes = new List<P25JournalChange>();
        var now = SystemClock.Instance.GetCurrentInstant();
        var cancelled = await ReminderPersistenceCommands.CancelPendingForOccurrenceAsync(
                context,
                taskId.Value,
                now,
                ScheduleStateReason.TASK_DELETED,
                cancellationToken)
            .ConfigureAwait(false);
        changes.AddRange(cancelled.Select(scheduleId => new P25JournalChange(
            "reminder_schedule",
            scheduleId.ToString("D"),
            "cancelled")));

        var rules = await context.ReminderRules
            .Where(rule => rule.TargetId == taskId.Value && rule.Enabled)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var ruleEntity in rules)
        {
            var rule = ruleEntity.ToDomain();
            _ = rule.Update(
                rule.Purpose,
                rule.Timing,
                rule.Priority,
                rule.Pinned,
                rule.RepeatPolicy,
                rule.WakePolicy,
                enabled: false,
                now);
            ruleEntity.Apply(rule);
            changes.Add(new P25JournalChange("reminder_rule", rule.Id.ToString(), "disabled"));
        }

        if (!await application.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false))
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        metadata.TaskId = taskId.ToString();
        metadata.Deleted = true;
        changes.Insert(0, new P25JournalChange("task", metadata.TaskId, "deleted"));
        return P25MutationDecision.Changed(changes);
    }

    private static async ValueTask<IReadOnlyList<P25JournalChange>> CreateReminderRuleAsync(
        ReminNoteDbContext context,
        Guid taskId,
        TimeSpec timeSpec,
        ReminderRuleOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var now = SystemClock.Instance.GetCurrentInstant();
        var rule = ReminderRule.CreateForTask(
            taskId,
            options.Purpose,
            options.Timing,
            options.Priority,
            options.Pinned,
            options.EffectiveRepeatPolicy,
            options.WakePolicy,
            options.Enabled,
            now);
        var logicalReminderId = LogicalReminderId.New();
        var calculation = ToCalculationInput(rule, timeSpec, logicalReminderId);
        var derivation = Calculation.ReminderSchedulePlanner.DeriveInitialSchedule(
            calculation,
            new Calculation.ReminderScheduleRequest(
                ReminderScheduleId.New().Value,
                1),
            UserTimeZone(),
            ReminderCalculationLimits());

        context.ReminderRules.Add(ReminderRuleEntity.FromDomain(rule));
        var changes = new List<P25JournalChange>
        {
            new("reminder_rule", rule.Id.Value.ToString("D"), "created")
        };
        if (derivation.IsScheduled && derivation.Schedule is { } plan)
        {
            var schedule = ReminderSchedule.CreateFromRule(
                rule,
                LogicalReminderId.From(plan.LogicalReminderId),
                plan.ScheduleRevision,
                plan.TriggerAtUtc,
                now,
                plan.TimeZoneId,
                ReminderScheduleId.From(plan.ScheduleId));
            context.ReminderSchedules.Add(ReminderScheduleEntity.FromDomain(schedule));
            changes.Add(new P25JournalChange(
                "reminder_schedule",
                schedule.Id.Value.ToString("D"),
                "created"));
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changes;
    }

    private static async ValueTask<IReadOnlyList<P25JournalChange>> RebuildReminderRuleAsync(
        ReminNoteDbContext context,
        ReminderRuleEntity ruleEntity,
        TimeSpec previousTimeSpec,
        TimeSpec nextTimeSpec,
        ReminderRuleOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ruleEntity);
        var previousRule = ruleEntity.ToDomain();
        var nextOptions = options ?? ToOptions(previousRule);
        if (previousTimeSpec == nextTimeSpec && OptionsEqual(previousRule, nextOptions))
        {
            return Array.Empty<P25JournalChange>();
        }

        var pendingEntities = await context.ReminderSchedules
            .Where(schedule =>
                schedule.RuleId == previousRule.Id.Value &&
                schedule.OccurrenceId == previousRule.OccurrenceId.Value &&
                schedule.State == ScheduleState.PENDING)
            .OrderBy(schedule => schedule.ScheduleRevision)
            .ThenBy(schedule => schedule.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var allRevisions = await context.ReminderSchedules
            .Where(schedule =>
                schedule.RuleId == previousRule.Id.Value &&
                schedule.OccurrenceId == previousRule.OccurrenceId.Value)
            .Select(schedule => schedule.ScheduleRevision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var nextScheduleRevision = allRevisions.Count == 0
            ? 1L
            : checked(allRevisions.Max() + 1);
        var previousLogicalReminderId = pendingEntities.Count > 0
            ? pendingEntities[0].LogicalReminderId
            : await context.ReminderSchedules
                .Where(schedule =>
                    schedule.RuleId == previousRule.Id.Value &&
                    schedule.OccurrenceId == previousRule.OccurrenceId.Value)
                .OrderByDescending(schedule => schedule.ScheduleRevision)
                .Select(schedule => (Guid?)schedule.LogicalReminderId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false) ?? LogicalReminderId.New().Value;

        var nextLogicalReminderId = LogicalReminderId.New();
        var previousCalculation = ToCalculationInput(
            previousRule,
            previousTimeSpec,
            LogicalReminderId.From(previousLogicalReminderId));
        var updatedAt = SystemClock.Instance.GetCurrentInstant();
        _ = previousRule.Update(
            nextOptions.Purpose,
            nextOptions.Timing,
            nextOptions.Priority,
            nextOptions.Pinned,
            nextOptions.EffectiveRepeatPolicy,
            nextOptions.WakePolicy,
            nextOptions.Enabled,
            updatedAt);
        var nextCalculation = ToCalculationInput(
            previousRule,
            nextTimeSpec,
            nextLogicalReminderId);
        var pendingSnapshots = pendingEntities
            .Select(entity => ToPendingScheduleSnapshot(entity, previousRule.TargetId))
            .ToArray();
        var planner = Calculation.ReminderSchedulePlanner.Rebuild(
            previousCalculation,
            nextCalculation,
            pendingSnapshots,
            new Calculation.ReminderScheduleRequest(
                ReminderScheduleId.New().Value,
                nextScheduleRevision),
            UserTimeZone(),
            ReminderCalculationLimits());

        ruleEntity.Apply(previousRule);
        var changes = new List<P25JournalChange>
        {
            new("reminder_rule", previousRule.Id.Value.ToString("D"), "updated")
        };
        foreach (var transition in planner.InvalidatedSchedules)
        {
            var entity = pendingEntities.Single(schedule => schedule.Id == transition.ScheduleId);
            var domain = entity.ToDomain();
            if (transition.State == Calculation.ReminderScheduleState.SUPERSEDED)
            {
                domain.Supersede(
                    ReminderScheduleId.From(transition.ReplacementScheduleId!.Value),
                    ToDomainReason(transition.Reason),
                    updatedAt);
            }
            else
            {
                domain.Cancel(ToDomainReason(transition.Reason), updatedAt);
            }

            entity.Apply(domain);
            changes.Add(new P25JournalChange(
                "reminder_schedule",
                entity.Id.ToString("D"),
                transition.State == Calculation.ReminderScheduleState.SUPERSEDED
                    ? "superseded"
                    : "cancelled"));
        }

        if (planner.ReplacementSchedule is { } replacement)
        {
            var schedule = ReminderSchedule.CreateFromRule(
                previousRule,
                LogicalReminderId.From(replacement.LogicalReminderId),
                replacement.ScheduleRevision,
                replacement.TriggerAtUtc,
                updatedAt,
                replacement.TimeZoneId,
                ReminderScheduleId.From(replacement.ScheduleId));
            context.ReminderSchedules.Add(ReminderScheduleEntity.FromDomain(schedule));
            changes.Add(new P25JournalChange(
                "reminder_schedule",
                schedule.Id.Value.ToString("D"),
                "created"));
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changes;
    }

    private static async ValueTask<P25MutationDecision> UpsertReminderRuleAsync(
        ReminNoteDbContext context,
        TransactionBoundTaskRepository repository,
        ProtocolRequest request,
        AgentMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        // The wire mode is the authoritative intent: CREATE always appends a
        // new Rule (so one task may have pre-start and start reminders), while
        // UPDATE must carry the exact Rule ID. A missing mode is inferred only
        // for older clients and follows the same ruleId-based distinction.
        var taskId = ParseTaskId(request.Payload, "taskId");
        var task = await repository.FindAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        var options = ReadReminderOptions(request.Payload)
            ?? throw new DomainValidationException(new DomainValidationError(
                "reminder.options.required",
                "A reminder rule intent is required.",
                "reminder"));

        var hasRuleId = request.Payload.TryGetProperty("ruleId", out var ruleIdValue);
        var mode = request.Payload.TryGetProperty("mode", out var modeValue)
            ? RequireStringValue(modeValue, "mode")
            : hasRuleId
                ? ReminderRuleUpsertModes.Update
                : ReminderRuleUpsertModes.Create;
        if (!ReminderRuleUpsertModes.IsKnown(mode))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.rule.mode.invalid",
                "Reminder rule mode must be CREATE or UPDATE.",
                "mode"));
        }

        if (mode == ReminderRuleUpsertModes.Create && hasRuleId)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.rule.create_rule_id_forbidden",
                "CREATE reminder rule commands must not include ruleId.",
                "ruleId"));
        }

        if (mode == ReminderRuleUpsertModes.Update && !hasRuleId)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.rule.update_rule_id_required",
                "UPDATE reminder rule commands require ruleId.",
                "ruleId"));
        }

        ReminderRuleEntity? ruleEntity = null;
        if (mode == ReminderRuleUpsertModes.Update)
        {
            var ruleId = ReminderRuleId.Parse(RequireStringValue(ruleIdValue, "ruleId"));
            ruleEntity = await context.ReminderRules
                .SingleOrDefaultAsync(rule => rule.Id == ruleId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (ruleEntity is null || ruleEntity.TargetId != taskId.Value)
            {
                return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
            }
        }

        metadata.TaskId = taskId.ToString();
        var changes = mode == ReminderRuleUpsertModes.Create
            ? await CreateReminderRuleAsync(context, taskId.Value, task.TimeSpec, options, cancellationToken).ConfigureAwait(false)
            : await RebuildReminderRuleAsync(context, ruleEntity!, task.TimeSpec, task.TimeSpec, options, cancellationToken).ConfigureAwait(false);
        metadata.RuleId = changes
            .FirstOrDefault(change => change.EntityType == "reminder_rule")?.EntityId
            ?? (mode == ReminderRuleUpsertModes.Update ? ruleEntity!.Id.ToString("D") : null);
        return changes.Count == 0
            ? P25MutationDecision.NoOp()
            : P25MutationDecision.Changed(changes);
    }

    private static ReminderRuleOptions? ReadReminderOptions(JsonElement payload)
    {
        if (!payload.TryGetProperty("reminder", out var reminder))
        {
            return null;
        }

        return ProtocolReminderRulePayloadParser.Parse(reminder).ToOptions();
    }

    private static ReminderRuleOptions? DefaultReminderFor(TimeSpec timeSpec) =>
        timeSpec switch
        {
            TimePointSpec => new ReminderRuleOptions(
                ReminderPurpose.TASK_START,
                ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0)),
            TimeRangeSpec => new ReminderRuleOptions(
                ReminderPurpose.TASK_START,
                ReminderTiming.Relative(ReminderAnchor.RANGE_START, 0)),
            _ => null
        };

    private static ReminderRuleOptions ToOptions(ReminderRule rule) => new(
        rule.Purpose,
        rule.Timing,
        rule.Priority,
        rule.Pinned,
        rule.RepeatPolicy,
        rule.WakePolicy,
        rule.Enabled);

    private static bool OptionsEqual(ReminderRule rule, ReminderRuleOptions options) =>
        rule.Purpose == options.Purpose &&
        Equals(rule.Timing, options.Timing) &&
        rule.Priority == options.Priority &&
        rule.Pinned == options.Pinned &&
        Equals(rule.RepeatPolicy, options.EffectiveRepeatPolicy) &&
        rule.WakePolicy == options.WakePolicy &&
        rule.Enabled == options.Enabled;

    private static Calculation.ReminderRuleCalculationInput ToCalculationInput(
        ReminderRule rule,
        TimeSpec timeSpec,
        LogicalReminderId logicalReminderId) =>
        new(
            rule.Id.Value,
            Calculation.ReminderTargetKind.TASK_INSTANCE,
            rule.TargetId,
            rule.OccurrenceId.Value,
            logicalReminderId.Value,
            ToCalculationPurpose(rule.Purpose),
            ToCalculationTiming(rule.Timing),
            ToCalculationPriority(rule.Priority),
            rule.Pinned,
            rule.Enabled,
            rule.RuleRevision,
            timeSpec);

    private static Calculation.PendingScheduleSnapshot ToPendingScheduleSnapshot(
        ReminderScheduleEntity entity,
        Guid targetId) =>
        new(
            entity.Id,
            entity.RuleId,
            targetId,
            entity.OccurrenceId,
            entity.LogicalReminderId,
            ToCalculationPurpose(entity.PurposeSnapshot),
            ToCalculationPriority(entity.PrioritySnapshot),
            entity.PinnedSnapshot,
            entity.RuleRevision,
            entity.ScheduleRevision,
            entity.TriggerAtUtc,
            entity.TimeZoneId)
        {
            Cause = entity.Cause switch
            {
                ScheduleCause.RULE => Calculation.ReminderScheduleCause.RULE,
                ScheduleCause.SNOOZE => Calculation.ReminderScheduleCause.SNOOZE,
                ScheduleCause.REPEAT => Calculation.ReminderScheduleCause.REPEAT,
                _ => throw new ArgumentOutOfRangeException(nameof(entity))
            },
            OriginScheduleId = entity.OriginScheduleId
        };

    private static Calculation.ReminderPurpose ToCalculationPurpose(ReminderPurpose purpose) =>
        purpose switch
        {
            ReminderPurpose.TASK_PRE_START => Calculation.ReminderPurpose.TASK_PRE_START,
            ReminderPurpose.TASK_START => Calculation.ReminderPurpose.TASK_START,
            ReminderPurpose.TASK_RANGE_END => Calculation.ReminderPurpose.TASK_RANGE_END,
            ReminderPurpose.TASK_CUSTOM => Calculation.ReminderPurpose.TASK_CUSTOM,
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };

    private static Calculation.ReminderPriority ToCalculationPriority(ReminderPriority priority) =>
        priority switch
        {
            ReminderPriority.LOW => Calculation.ReminderPriority.LOW,
            ReminderPriority.NORMAL => Calculation.ReminderPriority.NORMAL,
            ReminderPriority.HIGH => Calculation.ReminderPriority.HIGH,
            _ => throw new ArgumentOutOfRangeException(nameof(priority))
        };

    private static Calculation.ReminderTiming ToCalculationTiming(ReminderTiming timing) =>
        timing switch
        {
            RelativeReminderTiming relative => new Calculation.ReminderTiming.Relative(
                relative.Anchor switch
                {
                    ReminderAnchor.TASK_TIME => Calculation.ReminderAnchor.TASK_TIME,
                    ReminderAnchor.RANGE_START => Calculation.ReminderAnchor.RANGE_START,
                    ReminderAnchor.RANGE_END => Calculation.ReminderAnchor.RANGE_END,
                    _ => throw new ArgumentOutOfRangeException(nameof(timing))
                },
                relative.OffsetSeconds),
            AbsoluteReminderTiming absolute => new Calculation.ReminderTiming.AbsoluteUtc(absolute.AtUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(timing))
        };

    private static Calculation.ReminderCalculationLimits ReminderCalculationLimits() =>
        Calculation.ReminderCalculationLimits.Default;

    private static DateTimeZone UserTimeZone() =>
        new SystemUserTimeZoneProvider().TimeZone;

    private static ScheduleStateReason ToDomainReason(
        Calculation.ReminderScheduleTerminalReason reason) =>
        reason switch
        {
            Calculation.ReminderScheduleTerminalReason.RULE_REVISED => ScheduleStateReason.RULE_REBUILT,
            Calculation.ReminderScheduleTerminalReason.TASK_TIME_CHANGED => ScheduleStateReason.TASK_PLAN_CHANGED,
            Calculation.ReminderScheduleTerminalReason.TIME_ZONE_CHANGED => ScheduleStateReason.TIME_ZONE_CHANGED,
            Calculation.ReminderScheduleTerminalReason.RULE_DISABLED => ScheduleStateReason.RULE_DISABLED,
            Calculation.ReminderScheduleTerminalReason.TASK_RESULT_RECORDED => ScheduleStateReason.TASK_RESULT_RECORDED,
            Calculation.ReminderScheduleTerminalReason.TASK_DELETED => ScheduleStateReason.TASK_DELETED,
            Calculation.ReminderScheduleTerminalReason.RECOVERY_OBSOLETE => ScheduleStateReason.RECOVERY_OBSOLETE,
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };

    private static string RequireStringValue(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            throw new DomainValidationException(new DomainValidationError(
                "ipc.request.invalid",
                $"Payload field '{name}' must be a string.",
                name));
        }

        return text;
    }

    private static TimeSpec ParseTimeSpec(JsonElement payload) =>
        ProtocolTimeSpecPayload.Parse(payload.GetProperty("timeSpec")).ToDomain();

    private static TaskId ParseTaskId(JsonElement payload, string name) =>
        TaskId.Parse(RequireString(payload, name));

    private static TaskResult ParseResult(string value) => value switch
    {
        "COMPLETED" => TaskResult.COMPLETED,
        "MISSED" => TaskResult.MISSED,
        "PARTIAL" => TaskResult.PARTIAL,
        _ => throw new DomainValidationException(new DomainValidationError(
            "task.result.invalid",
            "Task result is not supported.",
            "result"))
    };

    private static string RequireString(JsonElement payload, string name)
    {
        var value = payload.GetProperty(name);
        if (value.ValueKind != System.Text.Json.JsonValueKind.String || value.GetString() is not { } text)
        {
            throw new DomainValidationException(new DomainValidationError(
                "ipc.request.invalid",
                $"Payload field '{name}' must be a string.",
                name));
        }

        return text;
    }

    private static string? ReadNullableString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return null;
        }

        return RequireString(payload, name);
    }

    private static int RequireInt32(JsonElement payload, string name)
    {
        var value = payload.GetProperty(name);
        if (value.ValueKind != System.Text.Json.JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new DomainValidationException(new DomainValidationError(
                "ipc.request.invalid_number",
                $"Payload field '{name}' must be a 32-bit integer.",
                name));
        }

        return result;
    }

    private static string? NormalizeNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note;
}
