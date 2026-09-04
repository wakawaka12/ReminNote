using Microsoft.EntityFrameworkCore;
using NodaTime;
using System.Text.Json;
using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Application;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.Reminders;

namespace ReminNote.Agent.Runtime;

internal sealed class AgentMutationMetadata
{
    public string? TaskId { get; set; }

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
                ProtocolOperations.TaskCreate => await CreateAsync(application, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskRename => await RenameAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskRecordResult => await RecordResultAsync(context, application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskUpdatePlan => await UpdatePlanAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskReorder => await ReorderAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskContinue => await ContinueAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskDelete => await DeleteAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
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
        TaskApplicationService application,
        ProtocolRequest request,
        AgentMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        var snapshot = await application.CreateAsync(
                new CreateTaskCommand(
                    RequireString(payload, "title"),
                    ParseTimeSpec(payload)),
                cancellationToken)
            .ConfigureAwait(false);
        metadata.TaskId = snapshot.Id.ToString();
        return P25MutationDecision.Changed(
            [new P25JournalChange("task", metadata.TaskId, "created")]);
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
        if (!instance.Resolve(action, resolvedAtUtc))
        {
            return P25MutationDecision.NoOp();
        }

        entity.Apply(instance);
        var changes = new List<P25JournalChange>
        {
            new("reminder_instance", instance.Id.ToString(), "resolved")
        };
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

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return P25MutationDecision.Changed(changes);
    }

    private static async ValueTask<P25MutationDecision> UpdatePlanAsync(
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
        metadata.TaskId = taskId.ToString();
        if (string.Equals(before.Title, title.Trim(), StringComparison.Ordinal) && before.TimeSpec == timeSpec)
        {
            return P25MutationDecision.NoOp();
        }

        _ = await application.UpdateAsync(
                new UpdateTaskCommand(taskId, title, timeSpec),
                cancellationToken)
            .ConfigureAwait(false);
        return P25MutationDecision.Changed(
            [new P25JournalChange("task", metadata.TaskId, "plan_updated")]);
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

        var snapshot = await application.ContinueAsync(
                new ContinueTaskCommand(
                    sourceTaskId,
                    RequireString(request.Payload, "title"),
                    ParseTimeSpec(request.Payload)),
                cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        metadata.TaskId = snapshot.Id.ToString();
        return P25MutationDecision.Changed(
            [
                new P25JournalChange("task", metadata.TaskId, "continued"),
                new P25JournalChange("task_history", metadata.TaskId, "continued")
            ]);
    }

    private static async ValueTask<P25MutationDecision> DeleteAsync(
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

        if (!await application.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false))
        {
            return P25MutationDecision.Rejected(ProtocolErrorCodes.NotFound);
        }

        metadata.TaskId = taskId.ToString();
        metadata.Deleted = true;
        return P25MutationDecision.Changed(
            [new P25JournalChange("task", metadata.TaskId, "deleted")]);
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
