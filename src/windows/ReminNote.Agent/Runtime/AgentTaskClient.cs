using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Runtime.Versioning;
using NodaTime;
using ReminNote.Agent.Transport;
using ReminNote.Core.Application;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Application;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Tasks;
using ReminNote.Core.Time;
using ReminNote.Core.Today;
using ReminNote.Core.Transport;
using ReminNote.Infrastructure.Application;
using ReminNote.Infrastructure.Persistence.P25;

namespace ReminNote.Agent.Runtime;

public sealed class AgentCommandException : InvalidOperationException
{
    public AgentCommandException(string code, bool retryable, string message)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}

/// <summary>
/// Main/Widget application boundary. Mutations use the scoped business pipe;
/// reads use the same profile's OS read-only SQLite projection. A lost reply
/// retries the same idempotency key with a new RequestId exactly once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentTaskClient :
    ITaskApplicationService,
    ITaskQueryService,
    ITodayQueryService,
    IWorkdaySettingsStore,
    IReminderQueryService,
    IReminderCommandClient,
    IAsyncDisposable,
    IDisposable
{
    private readonly ResolvedTransportProfile profile;
    private readonly string clientKind;
    private readonly string clientInstanceId = ProtocolIds.NewClientInstanceId();
    private readonly TransportLimits limits = AgentTransportDefaults.CreateLimits();
    private readonly NamedPipeTransportClient transport;
    private readonly ReadOnlyTaskServices readOnly;
    private readonly ReadOnlyReminderQueryService reminderReadOnly;
    private readonly SemaphoreSlim roundTripGate = new(1, 1);
    private bool sessionReady;
    private int disposed;

    public AgentTaskClient(
        string repositoryRoot,
        string clientKind = ProtocolClientKinds.Main,
        string? profileName = null,
        string? dataRoot = null)
    {
        if (clientKind is not (ProtocolClientKinds.Main or ProtocolClientKinds.Widget))
        {
            throw new ArgumentException("The Agent client kind must be main or widget.", nameof(clientKind));
        }

        var arguments = new TransportProfileArguments(
            DataRoot: dataRoot,
            RepoRoot: dataRoot is null ? repositoryRoot : null,
            Profile: profileName);
        profile = TransportProfileResolver.ResolveForCurrentUser(
            arguments,
            new ProtocolTransportProfileContractAdapter());
        this.clientKind = clientKind;
        transport = new NamedPipeTransportClient(
            new NamedPipeTransportEndpoint(profile, NamedPipeEndpointKind.Business),
            limits);
        readOnly = new ReadOnlyTaskServices(profile.DatabasePath);
        reminderReadOnly = new ReadOnlyReminderQueryService(profile.DatabasePath, profile.ProfileScope);
    }

    public string ProfileScope => profile.ProfileScope;

    public string DatabasePath => profile.DatabasePath;

    public async ValueTask<TaskSnapshot> CreateAsync(
        CreateTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var response = await ExecuteMutationAsync(
                ProtocolOperations.TaskCreate,
                ProtocolJson.CreateTaskCreatePayload(command.Title, command.TimeSpec, command.Reminder),
                cancellationToken)
            .ConfigureAwait(false);
        return await ReadResponseTaskAsync(response, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentCommandException(ProtocolErrorCodes.IntegrityFailed, true, "Agent create response did not identify a task.");
    }

    public async ValueTask<TaskSnapshot?> UpdateAsync(
        UpdateTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var response = await ExecuteMutationAsync(
                    ProtocolOperations.TaskUpdatePlan,
                    ProtocolJson.CreateTaskUpdatePlanPayload(command.TaskId.ToString(), command.Title, command.TimeSpec, command.Reminder),
                    cancellationToken)
                .ConfigureAwait(false);
            return await ReadResponseTaskAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCommandException exception) when (exception.Code == ProtocolErrorCodes.NotFound)
        {
            return null;
        }
    }

    public async ValueTask<TaskSnapshot?> RecordResultAsync(
        RecordTaskResultCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var response = await ExecuteMutationAsync(
                    ProtocolOperations.TaskRecordResult,
                    ProtocolJson.CreateTaskRecordResultPayload(command.TaskId.ToString(), command.Result.ToString(), command.Note),
                    cancellationToken)
                .ConfigureAwait(false);
            return await ReadResponseTaskAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCommandException exception) when (exception.Code == ProtocolErrorCodes.NotFound)
        {
            return null;
        }
    }

    public async ValueTask<bool> DeleteAsync(
        TaskId taskId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await ExecuteMutationAsync(
                    ProtocolOperations.TaskDelete,
                    ProtocolJson.CreateTaskDeletePayload(taskId.ToString()),
                    cancellationToken)
                .ConfigureAwait(false);
            return response.Payload is { } payload &&
                payload.TryGetProperty("deleted", out var deleted) &&
                deleted.ValueKind == JsonValueKind.True;
        }
        catch (AgentCommandException exception) when (exception.Code == ProtocolErrorCodes.NotFound)
        {
            return false;
        }
    }

    public async ValueTask<TaskSnapshot?> ReorderAsync(
        ReorderTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var response = await ExecuteMutationAsync(
                    ProtocolOperations.TaskReorder,
                    ProtocolJson.CreateTaskReorderPayload(command.TaskId.ToString(), command.SortOrder),
                    cancellationToken)
                .ConfigureAwait(false);
            return await ReadResponseTaskAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCommandException exception) when (exception.Code == ProtocolErrorCodes.NotFound)
        {
            return null;
        }
    }

    public async ValueTask<TaskSnapshot?> ContinueAsync(
        ContinueTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var response = await ExecuteMutationAsync(
                    ProtocolOperations.TaskContinue,
                    ProtocolJson.CreateTaskContinuePayload(
                        command.SourceTaskId.ToString(),
                        command.Title,
                        command.TimeSpec,
                        command.Reminder),
                    cancellationToken)
                .ConfigureAwait(false);
            return await ReadResponseTaskAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCommandException exception) when (exception.Code == ProtocolErrorCodes.NotFound)
        {
            return null;
        }
    }

    public ValueTask<TaskSnapshot?> FindAsync(TaskId id, CancellationToken cancellationToken = default) =>
        readOnly.FindAsync(id, cancellationToken);

    public async IAsyncEnumerable<TaskSnapshot> ListAsync(
        TaskQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var task in readOnly.ListAsync(query, cancellationToken).ConfigureAwait(false))
        {
            yield return task;
        }
    }

    public ValueTask<TodayReadModel> GetAsync(
        TodayQueryRequest request,
        CancellationToken cancellationToken = default) =>
        readOnly.GetAsync(request, cancellationToken);

    public ValueTask<WorkdaySettings> GetAsync(CancellationToken cancellationToken = default) =>
        readOnly.GetAsync(cancellationToken);

    public ValueTask<ReminderReadSnapshot> GetAsync(
        ReminderQuery query,
        CancellationToken cancellationToken = default) =>
        reminderReadOnly.GetAsync(query, cancellationToken);

    /// <summary>
    /// Creates or updates the reminder rule selected by a task-level command.
    /// The operation is still committed by the Agent writer; this method only
    /// exposes the public IPC client seam to callers that need more than the
    /// default task-start rule materialized by TaskCreate.
    /// </summary>
    public async ValueTask<ReminderCommandResult> UpsertReminderRuleAsync(
        TaskId taskId,
        ReminderRuleOptions options,
        ReminderRuleId? ruleId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            var response = await ExecuteMutationAsync(
                    ProtocolOperations.ReminderRuleUpsert,
                    ProtocolJson.CreateReminderRuleUpsertPayload(
                        taskId.ToString(),
                        options,
                        ruleId?.ToString()),
                    cancellationToken)
                .ConfigureAwait(false);
            return MapReminderCommandResponse(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentCommandException exception) when (!IsFatal(exception))
        {
            return MapReminderCommandFailure(exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return new ReminderCommandResult(
                ReminderCommandOutcome.Unavailable,
                ErrorCode: ProtocolErrorCodes.AgentUnavailable);
        }
    }

    /// <summary>
    /// Sends a Task reminder action through the business pipe. The Agent owns
    /// the ReminderInstance transition and any cross-aggregate DONE/SNOOZE
    /// effects; there is deliberately no call to the legacy Task writer here.
    /// </summary>
    public async ValueTask<ReminderCommandResult> ExecuteAsync(
        ReminderActionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            var response = await ExecuteMutationAsync(
                    ProtocolOperations.ReminderResolve,
                    ProtocolJson.CreateReminderResolvePayload(
                        command.InstanceId.ToString(),
                        command.Action,
                        command.SnoozeSeconds),
                    command.ExpectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            return MapReminderCommandResponse(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentCommandException exception) when (!IsFatal(exception))
        {
            return MapReminderCommandFailure(exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return new ReminderCommandResult(
                ReminderCommandOutcome.Unavailable,
                ErrorCode: ProtocolErrorCodes.AgentUnavailable);
        }
    }

    public async ValueTask<ReminderCommandResult> MarkReadAsync(
        ReminderMarkReadCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();

        try
        {
            var response = await ExecuteMutationAsync(
                    ProtocolOperations.ReminderMarkRead,
                    ProtocolJson.CreateReminderMarkReadPayload(command.InstanceId.ToString()),
                    command.ExpectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            return MapReminderCommandResponse(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentCommandException exception) when (!IsFatal(exception))
        {
            return MapReminderCommandFailure(exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return new ReminderCommandResult(
                ReminderCommandOutcome.Unavailable,
                ErrorCode: ProtocolErrorCodes.AgentUnavailable);
        }
    }

    public ValueTask SetAsync(WorkdaySettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        throw new InvalidOperationException(
            "Workday settings writes require an Agent command contract and are not part of P2.5 Task cutover.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await roundTripGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            sessionReady = false;
        }
        finally
        {
            roundTripGate.Release();
            roundTripGate.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async ValueTask<ProtocolResponse> ExecuteMutationAsync(
        string operation,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var expectedRevision = await ReadCurrentRevisionAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteMutationAsync(operation, payload, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ProtocolResponse> ExecuteMutationAsync(
        string operation,
        JsonElement payload,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var key = ProtocolIds.NewIdempotencyKey();
        var request = ProtocolRequest.CreateMutationAttempt(
            clientKind,
            clientInstanceId,
            DateTimeOffset.UtcNow,
            ProtocolLimits.DefaultMutationTimeoutMilliseconds,
            operation,
            payload,
            key,
            expectedRevision);
        var response = await SendWithOneRetryAsync(request, cancellationToken).ConfigureAwait(false);
        ThrowIfError(response);
        return response;
    }

    private async ValueTask<ProtocolResponse> SendWithOneRetryAsync(
        ProtocolRequest request,
        CancellationToken cancellationToken)
    {
        await roundTripGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            try
            {
                return await SendAttemptAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsReconnectable(exception) && !cancellationToken.IsCancellationRequested)
            {
                await DisconnectAfterFailureAsync().ConfigureAwait(false);
                var retry = request.CreateRetryAttempt(DateTimeOffset.UtcNow, request.TimeoutMs);
                return await SendAttemptAsync(retry, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            roundTripGate.Release();
        }
    }

    private async ValueTask<ProtocolResponse> SendAttemptAsync(
        ProtocolRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var timeout = limits.ValidateTimeoutMilliseconds(request.TimeoutMs);
        await transport.SendFrameAsync(ProtocolJson.SerializeRequest(request), timeout, cancellationToken)
            .ConfigureAwait(false);
        var frame = await transport.ReceiveFrameAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            throw new IOException("Agent closed the business pipe before returning a response.");
        }

        var response = ProtocolJson.DeserializeResponse(frame);
        if (response.RequestId != request.RequestId || response.Operation != request.Operation)
        {
            throw new IOException("Agent response correlation did not match the request attempt.");
        }

        return response;
    }

    private async ValueTask EnsureSessionAsync(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        if (sessionReady && transport.State == TransportConnectionState.Connected)
        {
            return;
        }

        sessionReady = false;
        if (transport.State != TransportConnectionState.Connected)
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        var hello = new ProtocolHelloPayload(
            [ProtocolVersion.Current],
            ["reconnect", "command.status", "changes.get_since"],
            ["reconnect"],
            ProfileHint: profile.ProfileScope);
        var request = ProtocolRequest.CreateReadAttempt(
            clientKind,
            clientInstanceId,
            DateTimeOffset.UtcNow,
            ProtocolLimits.ReadDeadlineMilliseconds,
            ProtocolOperations.SessionHello,
            ProtocolJson.CreateHelloPayload(hello));
        var timeout = limits.HelloQueryStatusDeadline;
        await transport.SendFrameAsync(ProtocolJson.SerializeRequest(request), timeout, cancellationToken)
            .ConfigureAwait(false);
        var frame = await transport.ReceiveFrameAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            throw new IOException("Agent closed the business pipe during hello.");
        }

        var response = ProtocolJson.DeserializeResponse(frame);
        if (response.RequestId != request.RequestId || response.Operation != ProtocolOperations.SessionHello)
        {
            throw new IOException("Agent hello response correlation failed.");
        }

        ThrowIfError(response);
        sessionReady = true;
    }

    private async ValueTask<long> ReadCurrentRevisionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(profile.DatabasePath, cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT current_revision
            FROM revision_state
            WHERE profile_scope = $profileScope;
            """;
        command.Parameters.AddWithValue("$profileScope", profile.ProfileScope);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            throw new AgentCommandException(ProtocolErrorCodes.StorageNotReady, true, "The Agent profile revision state is not ready.");
        }

        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async ValueTask<TaskSnapshot?> ReadResponseTaskAsync(
        ProtocolResponse response,
        CancellationToken cancellationToken)
    {
        if (response.Payload is not { } payload ||
            !payload.TryGetProperty("taskId", out var taskIdValue) ||
            taskIdValue.ValueKind != JsonValueKind.String ||
            taskIdValue.GetString() is not { } taskIdText)
        {
            return null;
        }

        return await readOnly.FindAsync(TaskId.Parse(taskIdText), cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfError(ProtocolResponse response)
    {
        if (response.Ok)
        {
            return;
        }

        var error = response.Error!;
        throw new AgentCommandException(
            error.Code,
            error.Retryable,
            error.HumanMessage ?? $"Agent command failed with {error.Code}.");
    }

    private static ReminderCommandResult MapReminderCommandResponse(ProtocolResponse response)
    {
        if (!response.Ok)
        {
            var error = response.Error;
            return error is null
                ? new ReminderCommandResult(ReminderCommandOutcome.Unavailable, ErrorCode: ProtocolErrorCodes.AgentUnavailable)
                : MapReminderCommandFailure(new AgentCommandException(error.Code, error.Retryable, error.HumanMessage ?? error.Code));
        }

        var outcome = response.Outcome switch
        {
            ProtocolOutcomes.Changed => ReminderCommandOutcome.Changed,
            ProtocolOutcomes.NoOp or ProtocolOutcomes.Replayed => ReminderCommandOutcome.NoOp,
            ProtocolOutcomes.Stale => ReminderCommandOutcome.Stale,
            _ => ReminderCommandOutcome.Rejected
        };
        return new ReminderCommandResult(outcome, response.CommittedRevision);
    }

    private static ReminderCommandResult MapReminderCommandFailure(AgentCommandException exception)
    {
        var outcome = exception.Code == ProtocolErrorCodes.ExpectedRevisionMismatch
            ? ReminderCommandOutcome.Stale
            : IsUnavailableCode(exception.Code)
                ? ReminderCommandOutcome.Unavailable
                : ReminderCommandOutcome.Rejected;
        return new ReminderCommandResult(outcome, ErrorCode: exception.Code);
    }

    private static bool IsUnavailableCode(string code) => code is
        ProtocolErrorCodes.AgentUnavailable or
        ProtocolErrorCodes.AgentNotReady or
        ProtocolErrorCodes.AgentShuttingDown or
        ProtocolErrorCodes.Timeout or
        ProtocolErrorCodes.Overloaded or
        ProtocolErrorCodes.StorageNotReady or
        ProtocolErrorCodes.StorageBusy or
        ProtocolErrorCodes.TransactionFailed;

    private static bool IsFatal(Exception exception) => exception switch
    {
        OutOfMemoryException or StackOverflowException or AccessViolationException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsFatal),
        _ => exception.InnerException is not null && IsFatal(exception.InnerException)
    };

    private async ValueTask DisconnectAfterFailureAsync()
    {
        sessionReady = false;
        try
        {
            await transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The next ConnectAsync owns the finite reconnect attempt.
        }
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(disposed != 0, this);

    private static bool IsReconnectable(Exception exception) => exception switch
    {
        AgentCommandException => false,
        OperationCanceledException => false,
        TransportFailureException => true,
        IOException => true,
        TimeoutException => true,
        _ => false
    };
}
