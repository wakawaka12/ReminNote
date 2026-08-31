using System.Globalization;
using System.Text.Json;
using ReminNote.Agent.Transport;
using ReminNote.Core.Protocol;
using ReminNote.Infrastructure.Persistence.P25;

namespace ReminNote.Agent.Runtime;

internal sealed class AgentProtocolHandshake : ITransportHandshake
{
    private readonly string agentInstanceId;
    private readonly string profileScope;
    private readonly Func<ValueTask<long>> currentRevision;

    public AgentProtocolHandshake(
        Guid agentInstanceId,
        string profileScope,
        Func<ValueTask<long>> currentRevision)
    {
        this.agentInstanceId = agentInstanceId.ToString("D");
        this.profileScope = profileScope;
        this.currentRevision = currentRevision ?? throw new ArgumentNullException(nameof(currentRevision));
        ProtocolProfileScope.Validate(profileScope);
    }

    public async ValueTask<TransportHandshakeResult> HandleAsync(
        ReadOnlyMemory<byte> firstOrPendingFrame,
        TransportPeerIdentity peer,
        CancellationToken cancellationToken)
    {
        var request = ProtocolJson.DeserializeRequest(firstOrPendingFrame.Span);
        if (request.Operation != ProtocolOperations.SessionHello)
        {
            return await FailureAsync(request, ProtocolErrorCodes.AgentNotReady, "session.hello is required before business requests.", close: true, cancellationToken).ConfigureAwait(false);
        }

        var hello = ProtocolJson.ReadHelloPayload(request);
        if (hello.ProfileHint is not null &&
            !string.Equals(hello.ProfileHint, profileScope, StringComparison.Ordinal))
        {
            return await FailureAsync(request, ProtocolErrorCodes.ProfileMismatch, "The hello profile does not match this endpoint.", close: true, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(peer.ProfileScope, profileScope, StringComparison.Ordinal))
        {
            return await FailureAsync(request, ProtocolErrorCodes.ProfileMismatch, "The transport profile is not accepted.", close: true, cancellationToken).ConfigureAwait(false);
        }

        var negotiation = ProtocolVersionNegotiator.Negotiate(
            hello.SupportedProtocolVersions,
            [ProtocolVersion.Current],
            hello.RequestedFeatures,
            hello.RequiredFeatures,
            ["reconnect", "command.status", "changes.get_since"]);
        var revision = await currentRevision().ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(new
        {
            acceptedFeatures = negotiation.AcceptedFeatures,
            downgradedFeatures = negotiation.DowngradedFeatures,
            ready = true,
            selectedProtocolVersion = negotiation.SelectedProtocolVersion.ToString()
        });
        var response = new ProtocolResponse(
            negotiation.SelectedProtocolVersion,
            request.RequestId,
            request.Operation,
            agentInstanceId,
            revision,
            ok: true,
            replayed: false,
            outcome: ProtocolOutcomes.NoOp,
            committedRevision: null,
            payload,
            error: null);
        return new TransportHandshakeResult(true, false, ProtocolJson.SerializeResponse(response));
    }

    private async ValueTask<TransportHandshakeResult> FailureAsync(
        ProtocolRequest request,
        string code,
        string message,
        bool close,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var revision = await currentRevision().ConfigureAwait(false);
        var response = new ProtocolResponse(
            ProtocolVersion.Current,
            request.RequestId,
            request.Operation,
            agentInstanceId,
            revision,
            ok: false,
            replayed: false,
            outcome: ProtocolOutcomes.Rejected,
            committedRevision: null,
            payload: null,
            error: new ProtocolError(
                code,
                ProtocolErrorCodes.TryGetDefaultRetryable(code, out var retryable) && retryable,
                ProtocolJson.ParseObject("{}"),
                message));
        return new TransportHandshakeResult(false, close, ProtocolJson.SerializeResponse(response));
    }
}

internal sealed class AgentProtocolDispatcher : ITransportRequestDispatcher
{
    private readonly P25StorageStore store;
    private readonly string profileScope;
    private readonly string databasePath;
    private readonly Guid agentInstanceId;

    public AgentProtocolDispatcher(P25StorageStore store, string profileScope, string databasePath, Guid agentInstanceId)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.profileScope = profileScope;
        this.databasePath = Path.GetFullPath(databasePath ?? throw new ArgumentNullException(nameof(databasePath)));
        this.agentInstanceId = agentInstanceId;
        ProtocolProfileScope.Validate(profileScope);
    }

    public async ValueTask<ReadOnlyMemory<byte>> DispatchAsync(
        ReadOnlyMemory<byte> requestFrame,
        TransportPeerIdentity peer)
    {
        var request = ProtocolJson.DeserializeRequest(requestFrame.Span);
        if (!string.Equals(peer.ProfileScope, profileScope, StringComparison.Ordinal))
        {
            return ErrorResponse(request, ProtocolErrorCodes.ProfileMismatch, "The request profile is not accepted.");
        }

        return request.Operation switch
        {
            ProtocolOperations.CommandStatus => await StatusAsync(request, peer).ConfigureAwait(false),
            ProtocolOperations.ChangesGetSince => await ChangesAsync(request).ConfigureAwait(false),
            ProtocolOperations.RequestCancel => await CancelAsync(request, peer).ConfigureAwait(false),
            ProtocolOperations.SessionHello => ErrorResponse(request, ProtocolErrorCodes.AgentNotReady, "session.hello is only valid as the first request."),
            _ when ProtocolOperations.IsMutation(request.Operation) => await MutationAsync(request, peer).ConfigureAwait(false),
            _ => ErrorResponse(request, ProtocolErrorCodes.InvalidRequest, "The operation is not supported by the Agent runtime.")
        };
    }

    private async ValueTask<ReadOnlyMemory<byte>> MutationAsync(ProtocolRequest request, TransportPeerIdentity peer)
    {
        var hash = RnCj1Canonicalizer.ComputeHash(
            profileScope,
            request.Operation,
            request.ExpectedRevision!.Value,
            request.Payload);
        var metadata = new AgentMutationMetadata();
        var storageRequest = new P25CommandRequest(
            peer.UserSid,
            profileScope,
            request.IdempotencyKey!,
            request.Operation,
            P25StorageLimits.CanonicalHashVersion,
            hash.Hash,
            request.ExpectedRevision.Value,
            request.RequestId,
            agentInstanceId.ToString("D"));

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(request.TimeoutMs));
        P25WriteResult result;
        try
        {
            result = await store.Writer.ExecuteMutationAsync(
                    storageRequest,
                    (context, token) => AgentDomainMutationAdapter.ApplyAsync(context, request, metadata, token),
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ErrorResponse(request, ProtocolErrorCodes.TransactionFailed, exception.Message);
        }

        if (metadata.TaskId is null &&
            request.Operation == ProtocolOperations.TaskCreate &&
            result.CommittedRevision is { } committedRevision &&
            committedRevision > 0)
        {
            metadata.TaskId = await store.FindJournalEntityIdAsync(
                    committedRevision,
                    "task",
                    "created")
                .ConfigureAwait(false);
        }

        var state = await store.ReadRevisionStateAsync().ConfigureAwait(false);
        var outcome = ToProtocolOutcome(result.Outcome);
        var success = result.Outcome is P25MutationOutcome.Changed or P25MutationOutcome.NoOp or P25MutationOutcome.Replayed;
        JsonElement? payload = success
            ? CreateMutationPayload(metadata, result)
            : null;
        var errorCode = result.ErrorCode;
        var error = success
            ? null
            : new ProtocolError(
                errorCode ?? ProtocolErrorCodes.TransactionFailed,
                GetRetryable(errorCode ?? ProtocolErrorCodes.TransactionFailed),
                ProtocolJson.ParseObject("{}"));
        var committedRevisionForResponse = success ? result.CommittedRevision : null;
        var response = new ProtocolResponse(
            ProtocolVersion.Current,
            request.RequestId,
            request.Operation,
            agentInstanceId.ToString("D"),
            state.CurrentRevision,
            success,
            result.Replayed,
            outcome,
            committedRevisionForResponse,
            payload,
            error);
        return ProtocolJson.SerializeResponse(response);
    }

    private async ValueTask<ReadOnlyMemory<byte>> StatusAsync(
        ProtocolRequest request,
        TransportPeerIdentity peer)
    {
        var statusRequest = ProtocolJson.ReadStatusPayload(request);
        var receipt = await store.Writer.GetReceiptAsync(
                peer.UserSid,
                statusRequest.IdempotencyKey)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            return ErrorResponse(request, ProtocolErrorCodes.NotFound, "The command receipt does not exist.");
        }

        var state = await store.ReadRevisionStateAsync().ConfigureAwait(false);
        var status = new ProtocolStatusResponsePayload(
            ToProtocolStatus(receipt.Status),
            receipt.Operation,
            Convert.ToHexString(receipt.CanonicalPayloadHash).ToLowerInvariant(),
            receipt.Changed,
            receipt.CommittedRevision,
            receipt.ErrorCode,
            GetRetryable(receipt.ErrorCode));
        var response = new ProtocolResponse(
            ProtocolVersion.Current,
            request.RequestId,
            request.Operation,
            agentInstanceId.ToString("D"),
            state.CurrentRevision,
            ok: true,
            replayed: false,
            outcome: ProtocolOutcomes.NoOp,
            committedRevision: null,
            ProtocolJson.CreateStatusResponsePayload(status),
            error: null);
        return ProtocolJson.SerializeResponse(response);
    }

    private async ValueTask<ReadOnlyMemory<byte>> ChangesAsync(ProtocolRequest request)
    {
        var changesRequest = ProtocolJson.ReadChangesPayload(request);
        await using var reader = await P25StorageReader.OpenReadOnlyAsync(
                databasePath,
                profileScope)
            .ConfigureAwait(false);
        var page = await reader.GetChangesAsync(
                changesRequest.AfterRevision,
                changesRequest.MaxRevisions)
            .ConfigureAwait(false);
        var state = await store.ReadRevisionStateAsync().ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToElement(new
        {
            page.SnapshotUpperBound,
            page.FromExclusive,
            page.ToInclusive,
            page.HasMore,
            page.FullRefreshRequired,
            outcome = page.Outcome.ToString(),
            errorCode = page.ErrorCode,
            batches = page.Batches.Select(batch => new
            {
                batch.Revision,
                batch.BatchId,
                changes = batch.Changes.Select(change => new
                {
                    change.EntityType,
                    change.EntityId,
                    change.ChangeKind
                }).ToArray()
            }).ToArray()
        });
        var response = new ProtocolResponse(
            ProtocolVersion.Current,
            request.RequestId,
            request.Operation,
            agentInstanceId.ToString("D"),
            state.CurrentRevision,
            ok: true,
            replayed: false,
            outcome: ProtocolOutcomes.NoOp,
            committedRevision: null,
            payload,
            error: null);
        return ProtocolJson.SerializeResponse(response);
    }

    private async ValueTask<ReadOnlyMemory<byte>> CancelAsync(
        ProtocolRequest request,
        TransportPeerIdentity peer)
    {
        var cancel = ProtocolJson.ReadCancelPayload(request);
        var receipt = await store.Writer.GetReceiptAsync(peer.UserSid, cancel.TargetIdempotencyKey).ConfigureAwait(false);
        return receipt is null
            ? ErrorResponse(request, ProtocolErrorCodes.NotFound, "The target command receipt does not exist.")
            : ErrorResponse(request, ProtocolErrorCodes.Cancelled, "Cancellation is accepted only before a writer transaction begins.");
    }

    private static JsonElement CreateMutationPayload(AgentMutationMetadata metadata, P25WriteResult result)
    {
        if (metadata.Deleted)
        {
            return JsonSerializer.SerializeToElement(new { deleted = true, taskId = metadata.TaskId });
        }

        return metadata.TaskId is { } taskId
            ? JsonSerializer.SerializeToElement(new { changed = result.Changed, taskId })
            : JsonSerializer.SerializeToElement(new { changed = result.Changed });
    }

    private ReadOnlyMemory<byte> ErrorResponse(ProtocolRequest request, string code, string message)
    {
        var response = new ProtocolResponse(
            ProtocolVersion.Current,
            request.RequestId,
            request.Operation,
            agentInstanceId.ToString("D"),
            0,
            ok: false,
            replayed: false,
            outcome: ProtocolOutcomes.Rejected,
            committedRevision: null,
            payload: null,
            error: new ProtocolError(code, GetRetryable(code), ProtocolJson.ParseObject("{}"), message));
        return ProtocolJson.SerializeResponse(response);
    }

    private static string ToProtocolOutcome(P25MutationOutcome outcome) => outcome switch
    {
        P25MutationOutcome.Changed => ProtocolOutcomes.Changed,
        P25MutationOutcome.NoOp => ProtocolOutcomes.NoOp,
        P25MutationOutcome.Replayed => ProtocolOutcomes.Replayed,
        P25MutationOutcome.Stale => ProtocolOutcomes.Stale,
        P25MutationOutcome.Rejected => ProtocolOutcomes.Rejected,
        P25MutationOutcome.RolledBack => ProtocolOutcomes.RolledBack,
        P25MutationOutcome.Cancelled => ProtocolOutcomes.Cancelled,
        P25MutationOutcome.TimedOut => ProtocolOutcomes.Timeout,
        P25MutationOutcome.Pending => ProtocolOutcomes.Pending,
        P25MutationOutcome.Unknown => ProtocolOutcomes.Unknown,
        _ => ProtocolOutcomes.Unknown
    };

    private static string ToProtocolStatus(P25ReceiptStatus status) => status switch
    {
        P25ReceiptStatus.Pending => ProtocolReceiptStatuses.Pending,
        P25ReceiptStatus.Committed => ProtocolReceiptStatuses.Committed,
        P25ReceiptStatus.RejectedStale => ProtocolReceiptStatuses.RejectedStale,
        P25ReceiptStatus.Rejected => ProtocolReceiptStatuses.Rejected,
        P25ReceiptStatus.RolledBack => ProtocolReceiptStatuses.RolledBack,
        P25ReceiptStatus.Cancelled => ProtocolReceiptStatuses.Cancelled,
        P25ReceiptStatus.TimedOut => ProtocolReceiptStatuses.TimedOut,
        P25ReceiptStatus.Unknown => ProtocolReceiptStatuses.Unknown,
        _ => ProtocolReceiptStatuses.Unknown
    };

    private static bool GetRetryable(string? errorCode) =>
        errorCode is not null && ProtocolErrorCodes.TryGetDefaultRetryable(errorCode, out var retryable) && retryable;

}
