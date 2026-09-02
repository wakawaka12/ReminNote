using Microsoft.EntityFrameworkCore;
using NodaTime;
using System.Text.Json;
using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Protocol;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Application;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.P25;

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
                ProtocolOperations.TaskRecordResult => await RecordResultAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskUpdatePlan => await UpdatePlanAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskReorder => await ReorderAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskContinue => await ContinueAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                ProtocolOperations.TaskDelete => await DeleteAsync(application, repository, request, metadata, cancellationToken).ConfigureAwait(false),
                // P3-03 owns the durable Reminder transaction. Keep the
                // operation on the business pipe so clients fail closed while
                // this integration is absent; never redirect it to P2 Task
                // writer code.
                ProtocolOperations.ReminderMarkRead or ProtocolOperations.ReminderResolve =>
                    P25MutationDecision.Rejected(ProtocolErrorCodes.AgentNotReady),
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
            return P25MutationDecision.NoOp();
        }

        _ = await application.RecordResultAsync(
                new RecordTaskResultCommand(taskId, result, note),
                cancellationToken)
            .ConfigureAwait(false);
        return P25MutationDecision.Changed(
            [
                new P25JournalChange("task", metadata.TaskId, "result_recorded"),
                new P25JournalChange("task_history", metadata.TaskId, "result_recorded")
            ]);
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
