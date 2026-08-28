using NodaTime;
using ReminNote.Core.Tasks;
using DomainTask = ReminNote.Core.Tasks.Task;

namespace ReminNote.Core.Application;

/// <summary>
/// Persistence boundary. Implementations own transactions and storage; Core
/// does not know about EF Core, SQLite, or database paths.
/// </summary>
public interface ITaskRepository
{
    ValueTask<DomainTask?> FindAsync(TaskId id, CancellationToken cancellationToken = default);

    ValueTask AddAsync(DomainTask task, CancellationToken cancellationToken = default);

    ValueTask UpdateAsync(DomainTask task, CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(TaskId id, CancellationToken cancellationToken = default);
}

public sealed record TaskQuery(LocalDate PlanDate);

public interface ITaskQueryService
{
    ValueTask<TaskSnapshot?> FindAsync(TaskId id, CancellationToken cancellationToken = default);

    IAsyncEnumerable<TaskSnapshot> ListAsync(
        TaskQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record CreateTaskCommand(string Title, TimeSpec TimeSpec);

public sealed record UpdateTaskCommand(TaskId TaskId, string Title, TimeSpec TimeSpec);

public sealed record RecordTaskResultCommand(
    TaskId TaskId,
    TaskResult Result,
    string? Note = null);

/// <summary>
/// Explicit application boundary for Task CRUD and result recording. A
/// concrete implementation obtains time from Noda Time's IClock and persists
/// through ITaskRepository.
/// </summary>
public interface ITaskApplicationService
{
    ValueTask<TaskSnapshot> CreateAsync(
        CreateTaskCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<TaskSnapshot?> UpdateAsync(
        UpdateTaskCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<TaskSnapshot?> RecordResultAsync(
        RecordTaskResultCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(
        TaskId taskId,
        CancellationToken cancellationToken = default);
}
