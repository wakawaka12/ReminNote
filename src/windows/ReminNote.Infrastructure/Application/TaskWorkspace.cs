using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;
using ReminNote.Core.Time;
using ReminNote.Core.Today;
using ReminNote.Infrastructure.Persistence;

using TaskAggregate = ReminNote.Core.Tasks.Task;

namespace ReminNote.Infrastructure.Application;

/// <summary>
/// P2 local composition facade. Each operation owns its DbContext, while the
/// Main and Widget hosts share the same application/query contracts and named
/// write gate. It is intentionally replaceable by the P2.5 Agent client.
/// </summary>
public sealed class TaskWorkspace :
    ITaskApplicationService,
    ITaskQueryService,
    ITaskHistoryQueryService,
    ITodayQueryService,
    IWorkdaySettingsStore,
    IDisposable
{
    private readonly string repositoryRoot;
    private readonly IClock clock;
    private readonly IUserTimeZoneProvider timeZoneProvider;
    private readonly ITaskWriteGate writeGate;
    private readonly IDisposable? ownedWriteGate;
    private readonly object initializationLock = new();
    private int disposed;
    private int initialized;
    private Exception? initializationFailure;

    public TaskWorkspace(
        string repositoryRoot,
        IClock? clock = null,
        IUserTimeZoneProvider? timeZoneProvider = null,
        ITaskWriteGate? writeGate = null)
    {
        this.repositoryRoot = ReminNoteDatabase.ValidateRepositoryRoot(repositoryRoot);
        this.clock = clock ?? SystemClock.Instance;
        this.timeZoneProvider = timeZoneProvider ?? new SystemUserTimeZoneProvider();
        this.writeGate = writeGate ?? new CrossProcessTaskWriteGate();
        ownedWriteGate = writeGate is null ? (IDisposable)this.writeGate : null;
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        lock (initializationLock)
        {
            if (Volatile.Read(ref initialized) != 0)
            {
                return;
            }

            if (initializationFailure is not null)
            {
                throw new InvalidOperationException(
                    "Task workspace initialization previously failed; restore or replace the development database before retrying.",
                    initializationFailure);
            }

            try
            {
                using var lease = writeGate.Enter();
                using var context = CreateContextCore();
                context.Database.Migrate();
                Volatile.Write(ref initialized, 1);
            }
            catch (Exception exception)
            {
                initializationFailure = exception;
                throw;
            }
        }
    }

    public ValueTask<TaskSnapshot> CreateAsync(
        CreateTaskCommand command,
        CancellationToken cancellationToken = default) =>
        RunApplicationAsync((application, token) => application.CreateAsync(command, token), cancellationToken);

    public ValueTask<TaskSnapshot?> UpdateAsync(
        UpdateTaskCommand command,
        CancellationToken cancellationToken = default) =>
        RunApplicationAsync((application, token) => application.UpdateAsync(command, token), cancellationToken);

    public ValueTask<TaskSnapshot?> RecordResultAsync(
        RecordTaskResultCommand command,
        CancellationToken cancellationToken = default) =>
        RunApplicationAsync((application, token) => application.RecordResultAsync(command, token), cancellationToken);

    public ValueTask<bool> DeleteAsync(
        TaskId taskId,
        CancellationToken cancellationToken = default) =>
        RunApplicationAsync((application, token) => application.DeleteAsync(taskId, token), cancellationToken);

    public ValueTask<TaskSnapshot?> ReorderAsync(
        ReorderTaskCommand command,
        CancellationToken cancellationToken = default) =>
        RunApplicationAsync((application, token) => application.ReorderAsync(command, token), cancellationToken);

    public ValueTask<TaskSnapshot?> ContinueAsync(
        ContinueTaskCommand command,
        CancellationToken cancellationToken = default) =>
        RunApplicationAsync((application, token) => application.ContinueAsync(command, token), cancellationToken);

    public async ValueTask<TaskSnapshot?> FindAsync(
        TaskId id,
        CancellationToken cancellationToken = default)
    {
        using var context = CreateContext();
        return await new TaskQueryService(context).FindAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TaskSnapshot> ListAsync(
        TaskQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var context = CreateContext();
        await foreach (var task in new TaskQueryService(context)
            .ListAsync(query, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return task;
        }
    }

    public async IAsyncEnumerable<TaskHistoryRecord> ListAsync(
        TaskId taskId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var context = CreateContext();
        await foreach (var history in new TaskHistoryQueryService(context)
            .ListAsync(taskId, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return history;
        }
    }

    public async ValueTask<TodayReadModel> GetAsync(
        TodayQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        using var context = CreateContext();
        var settings = new AppSettingsRepository(context, clock);
        var query = new TaskQueryService(context);
        return await new TodayQueryService(query, timeZoneProvider, settings)
            .GetAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<WorkdaySettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        using var context = CreateContext();
        return await new AppSettingsRepository(context, clock)
            .GetAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask SetAsync(
        WorkdaySettings settings,
        CancellationToken cancellationToken = default)
    {
        using var lease = writeGate.Enter(cancellationToken);
        using var context = CreateContext();
        await new AppSettingsRepository(context, clock)
            .SetAsync(settings, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            ownedWriteGate?.Dispose();
        }
    }

    private async ValueTask<T> RunApplicationAsync<T>(
        Func<ITaskApplicationService, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        using var context = CreateContext();
        var application = new TaskApplicationService(
            new TaskRepository(context),
            clock,
            writeGate);
        return await operation(application, cancellationToken).ConfigureAwait(false);
    }

    private ReminNoteDbContext CreateContext()
    {
        EnsureInitialized();
        return CreateContextCore();
    }

    private ReminNoteDbContext CreateContextCore()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        return ReminNoteDatabase.CreateDevelopmentContext(repositoryRoot);
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (Volatile.Read(ref initialized) == 0)
        {
            if (initializationFailure is not null)
            {
                throw new InvalidOperationException(
                    "Task workspace initialization failed; normal Task reads and writes are disabled.",
                    initializationFailure);
            }

            throw new InvalidOperationException(
                "TaskWorkspace.Initialize must succeed before using the Task workspace.");
        }
    }
}
