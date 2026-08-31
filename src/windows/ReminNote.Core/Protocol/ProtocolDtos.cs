using System.Text.Json;

namespace ReminNote.Core.Protocol;

public interface IProtocolEnvelope
{
    ProtocolVersion ProtocolVersion { get; }

    string MessageType { get; }

    string Operation { get; }

    void Validate();
}

/// <summary>
/// Version 1 request envelope. Payload is retained as JSON because the Core
/// contract owns wire shape while individual command handlers own domain DTOs.
/// </summary>
public sealed record ProtocolRequest : IProtocolEnvelope
{
    public ProtocolRequest(
        ProtocolVersion protocolVersion,
        string requestId,
        string clientKind,
        string clientInstanceId,
        DateTimeOffset sentAtUtc,
        int timeoutMs,
        string operation,
        JsonElement payload,
        string? idempotencyKey = null,
        long? expectedRevision = null)
    {
        ProtocolVersion = protocolVersion;
        RequestId = requestId;
        ClientKind = clientKind;
        ClientInstanceId = clientInstanceId;
        SentAtUtc = sentAtUtc;
        TimeoutMs = timeoutMs;
        Operation = operation;
        Payload = payload;
        IdempotencyKey = idempotencyKey;
        ExpectedRevision = expectedRevision;
    }

    public ProtocolVersion ProtocolVersion { get; }

    public string MessageType => ProtocolMessageTypes.Request;

    public string RequestId { get; }

    public string ClientKind { get; }

    public string ClientInstanceId { get; }

    public DateTimeOffset SentAtUtc { get; }

    public int TimeoutMs { get; }

    public string Operation { get; }

    public JsonElement Payload { get; }

    public string? IdempotencyKey { get; }

    public long? ExpectedRevision { get; }

    /// <summary>
    /// Creates a mutation attempt with a fresh wire RequestId. The caller owns
    /// the stable idempotency key and expected revision.
    /// </summary>
    public static ProtocolRequest CreateMutationAttempt(
        string clientKind,
        string clientInstanceId,
        DateTimeOffset sentAtUtc,
        int timeoutMs,
        string operation,
        JsonElement payload,
        string idempotencyKey,
        long expectedRevision) =>
        new(
            ProtocolVersion.Current,
            ProtocolIds.NewWireRequestId(),
            clientKind,
            clientInstanceId,
            sentAtUtc,
            timeoutMs,
            operation,
            payload,
            idempotencyKey,
            expectedRevision);

    /// <summary>
    /// Creates a read/status/cancel attempt with a fresh wire RequestId.
    /// </summary>
    public static ProtocolRequest CreateReadAttempt(
        string clientKind,
        string clientInstanceId,
        DateTimeOffset sentAtUtc,
        int timeoutMs,
        string operation,
        JsonElement payload) =>
        new(
            ProtocolVersion.Current,
            ProtocolIds.NewWireRequestId(),
            clientKind,
            clientInstanceId,
            sentAtUtc,
            timeoutMs,
            operation,
            payload);

    /// <summary>
    /// Rebuilds this logical command as a new wire attempt while preserving its
    /// stable client, operation, payload, key and expected revision.
    /// </summary>
    public ProtocolRequest CreateRetryAttempt(DateTimeOffset sentAtUtc, int timeoutMs) =>
        new(
            ProtocolVersion,
            ProtocolIds.NewWireRequestId(),
            ClientKind,
            ClientInstanceId,
            sentAtUtc,
            timeoutMs,
            Operation,
            Payload,
            IdempotencyKey,
            ExpectedRevision);

    public void Validate() => ProtocolEnvelopeValidator.Validate(this);
}

/// <summary>
/// Response envelope. A response always carries both payload and error slots on
/// the wire; exactly one slot is non-null according to Ok.
/// </summary>
public sealed record ProtocolResponse : IProtocolEnvelope
{
    public ProtocolResponse(
        ProtocolVersion protocolVersion,
        string requestId,
        string operation,
        string agentInstanceId,
        long serverRevision,
        bool ok,
        bool replayed,
        string outcome,
        long? committedRevision,
        JsonElement? payload,
        ProtocolError? error)
    {
        ProtocolVersion = protocolVersion;
        RequestId = requestId;
        Operation = operation;
        AgentInstanceId = agentInstanceId;
        ServerRevision = serverRevision;
        Ok = ok;
        Replayed = replayed;
        Outcome = outcome;
        CommittedRevision = committedRevision;
        Payload = payload;
        Error = error;
    }

    public ProtocolVersion ProtocolVersion { get; }

    public string MessageType => ProtocolMessageTypes.Response;

    public string RequestId { get; }

    public string Operation { get; }

    public string AgentInstanceId { get; }

    public long ServerRevision { get; }

    public bool Ok { get; }

    public bool Replayed { get; }

    public string Outcome { get; }

    public long? CommittedRevision { get; }

    public JsonElement? Payload { get; }

    public ProtocolError? Error { get; }

    public void Validate() => ProtocolEnvelopeValidator.Validate(this);
}

/// <summary>
/// Best-effort post-commit event. Events carry safe metadata only and are not a
/// replacement for durable changes or receipt state.
/// </summary>
public sealed record ProtocolEvent : IProtocolEnvelope
{
    public ProtocolEvent(
        ProtocolVersion protocolVersion,
        Guid eventId,
        string eventType,
        Guid agentInstanceId,
        DateTimeOffset occurredAtUtc,
        long revision,
        JsonElement payload)
    {
        ProtocolVersion = protocolVersion;
        EventId = eventId;
        EventType = eventType;
        AgentInstanceId = agentInstanceId;
        OccurredAtUtc = occurredAtUtc;
        Revision = revision;
        Payload = payload;
    }

    public ProtocolVersion ProtocolVersion { get; }

    public string MessageType => ProtocolMessageTypes.Event;

    public string Operation => string.Empty;

    public Guid EventId { get; }

    public string EventType { get; }

    public Guid AgentInstanceId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public long Revision { get; }

    public JsonElement Payload { get; }

    public void Validate() => ProtocolEnvelopeValidator.Validate(this);
}

/// <summary>
/// Machine-readable error object nested in a response envelope.
/// </summary>
public sealed record ProtocolError
{
    public ProtocolError(
        string code,
        bool retryable,
        JsonElement details,
        string? humanMessage = null)
    {
        Code = code;
        Retryable = retryable;
        Details = details;
        HumanMessage = humanMessage;
    }

    public string Code { get; }

    public bool Retryable { get; }

    public JsonElement Details { get; }

    public string? HumanMessage { get; }

    public void Validate() => ProtocolEnvelopeValidator.Validate(this);
}

/// <summary>
/// Typed payload DTOs for the non-mutation request operations. They are kept
/// separate from the envelope so command.status/request.cancel remain pure
/// protocol concerns and do not become business services.
/// </summary>
public sealed record ProtocolStatusRequestPayload(string IdempotencyKey)
{
    public void Validate() => ProtocolValidation.RequireLowercaseUuid(IdempotencyKey, nameof(IdempotencyKey));
}

/// <summary>
/// Typed command.status request view. The wire representation remains the
/// ordinary request envelope with operation command.status.
/// </summary>
public sealed record ProtocolStatusRequest(ProtocolRequest Envelope)
{
    public ProtocolStatusRequestPayload Payload => ProtocolJson.ReadStatusPayload(Envelope);

    public static ProtocolStatusRequest Create(
        string clientKind,
        string clientInstanceId,
        DateTimeOffset sentAtUtc,
        int timeoutMs,
        string idempotencyKey) =>
        new(ProtocolRequest.CreateReadAttempt(
            clientKind,
            clientInstanceId,
            sentAtUtc,
            timeoutMs,
            ProtocolOperations.CommandStatus,
            ProtocolJson.CreateStatusPayload(idempotencyKey)));

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Envelope);
        if (Envelope.Operation != ProtocolOperations.CommandStatus)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "The envelope is not command.status.",
                nameof(Envelope));
        }

        Envelope.Validate();
    }
}

public sealed record ProtocolCancelRequestPayload(
    string TargetRequestId,
    string TargetIdempotencyKey)
{
    public void Validate()
    {
        ProtocolValidation.RequireLowercaseUuid(TargetRequestId, nameof(TargetRequestId));
        ProtocolValidation.RequireLowercaseUuid(TargetIdempotencyKey, nameof(TargetIdempotencyKey));
    }
}

/// <summary>
/// Typed request.cancel view. Cancellation is best-effort control metadata;
/// it never carries a mutation idempotency key of its own.
/// </summary>
public sealed record ProtocolCancelRequest(ProtocolRequest Envelope)
{
    public ProtocolCancelRequestPayload Payload => ProtocolJson.ReadCancelPayload(Envelope);

    public static ProtocolCancelRequest Create(
        string clientKind,
        string clientInstanceId,
        DateTimeOffset sentAtUtc,
        string targetRequestId,
        string targetIdempotencyKey) =>
        new(ProtocolRequest.CreateReadAttempt(
            clientKind,
            clientInstanceId,
            sentAtUtc,
            timeoutMs: 1_000,
            operation: ProtocolOperations.RequestCancel,
            payload: ProtocolJson.CreateCancelPayload(targetRequestId, targetIdempotencyKey)));

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Envelope);
        if (Envelope.Operation != ProtocolOperations.RequestCancel)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "The envelope is not request.cancel.",
                nameof(Envelope));
        }

        Envelope.Validate();
    }
}

public sealed record ProtocolChangesRequestPayload(long AfterRevision, int MaxRevisions)
{
    public void Validate()
    {
        ProtocolValidation.RequireNonNegativeRevision(AfterRevision, nameof(AfterRevision));
        if (MaxRevisions is < 1 or > 128)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "maxRevisions must be between 1 and 128.",
                nameof(MaxRevisions));
        }
    }
}

public sealed record ProtocolHelloPayload(
    IReadOnlyList<ProtocolVersion> SupportedProtocolVersions,
    IReadOnlyList<string> RequestedFeatures,
    IReadOnlyList<string> RequiredFeatures,
    long? LastSeenRevision = null,
    string? ProfileHint = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(SupportedProtocolVersions);
        ArgumentNullException.ThrowIfNull(RequestedFeatures);
        ArgumentNullException.ThrowIfNull(RequiredFeatures);

        if (SupportedProtocolVersions.Count is < 1 or > ProtocolLimits.MaxSupportedProtocolVersions)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "supportedProtocolVersions must contain between 1 and 8 versions.",
                nameof(SupportedProtocolVersions));
        }

        ValidateFeatures(RequestedFeatures, nameof(RequestedFeatures));
        ValidateFeatures(RequiredFeatures, nameof(RequiredFeatures));

        if (LastSeenRevision is { } revision)
        {
            ProtocolValidation.RequireNonNegativeRevision(revision, nameof(LastSeenRevision));
        }

        if (ProfileHint is not null)
        {
            ProtocolValidation.RequireUtf8ByteLength(ProfileHint, 128, nameof(ProfileHint));
        }
    }

    private static void ValidateFeatures(IReadOnlyList<string> features, string fieldName)
    {
        if (features.Count > 64)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Feature lists exceed the protocol limit.",
                fieldName);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            if (string.IsNullOrWhiteSpace(feature) || !seen.Add(feature))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    "Feature names must be non-empty and unique.",
                    fieldName);
            }

            ProtocolValidation.RequireUtf8ByteLength(feature, 96, fieldName);
        }
    }
}

public sealed record ProtocolStatusResponsePayload(
    string Status,
    string Operation,
    string CanonicalPayloadHash,
    bool Changed,
    long? CommittedRevision,
    string? ErrorCode,
    bool Retryable)
{
    public void Validate()
    {
        if (Status is not (
                ProtocolReceiptStatuses.Pending or
                ProtocolReceiptStatuses.Committed or
                ProtocolReceiptStatuses.RejectedStale or
                ProtocolReceiptStatuses.Rejected or
                ProtocolReceiptStatuses.RolledBack or
                ProtocolReceiptStatuses.Cancelled or
                ProtocolReceiptStatuses.TimedOut or
                ProtocolReceiptStatuses.Unknown) || !ProtocolOperations.IsMutation(Operation))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Status payload requires status and operation.");
        }

        ProtocolValidation.RequireOperation(Operation);
        ProtocolValidation.RequireUtf8ByteLength(Status, 32, nameof(Status));
        ProtocolValidation.RequireUtf8ByteLength(CanonicalPayloadHash, 64, nameof(CanonicalPayloadHash));
        if (CanonicalPayloadHash.Length != 64 ||
            CanonicalPayloadHash.Any(character => !IsLowerHex(character)))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "canonicalPayloadHash must be 64 lowercase hexadecimal characters.",
                nameof(CanonicalPayloadHash));
        }

        if (CommittedRevision is < 0)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidNumber,
                "committedRevision must be non-negative when present.",
                nameof(CommittedRevision));
        }

        if (ErrorCode is not null)
        {
            ProtocolErrorValidation.ValidateCode(ErrorCode);
        }

        switch (Status)
        {
            case ProtocolReceiptStatuses.Pending:
                RequireStatus(Changed == false, "PENDING status must have changed=false.");
                RequireStatus(CommittedRevision is null, "PENDING status must not have committedRevision.");
                RequireStatus(ErrorCode is null, "PENDING status must not have an error code.");
                RequireStatus(Retryable, "PENDING status must be retryable.");
                break;

            case ProtocolReceiptStatuses.Committed:
                RequireStatus(CommittedRevision is not null, "COMMITTED status requires committedRevision.");
                RequireStatus(ErrorCode is null, "COMMITTED status must not have an error code.");
                RequireStatus(!Retryable, "COMMITTED status must not be retryable.");
                break;

            case ProtocolReceiptStatuses.RejectedStale:
                RequireStatus(!Changed && CommittedRevision is null, "REJECTED_STALE status cannot commit a change.");
                RequireStatus(ErrorCode == ProtocolErrorCodes.ExpectedRevisionMismatch, "REJECTED_STALE requires expected mismatch.");
                RequireStatus(!Retryable, "REJECTED_STALE status must not be retryable.");
                break;

            case ProtocolReceiptStatuses.Rejected:
                RequireStatus(!Changed && CommittedRevision is null, "REJECTED status cannot commit a change.");
                RequireStatus(ErrorCode is not null, "REJECTED status requires an error code.");
                RequireStatus(
                    Retryable == GetDefaultRetryable(ErrorCode!),
                    "REJECTED retryable must match the error-code policy.");
                break;

            case ProtocolReceiptStatuses.RolledBack:
                RequireStatus(!Changed && CommittedRevision is null, "ROLLED_BACK status cannot commit a change.");
                RequireStatus(ErrorCode == ProtocolErrorCodes.TransactionFailed, "ROLLED_BACK requires transaction_failed.");
                RequireStatus(Retryable, "ROLLED_BACK status must be retryable.");
                break;

            case ProtocolReceiptStatuses.Cancelled:
                RequireStatus(!Changed && CommittedRevision is null, "CANCELLED status cannot commit a change.");
                RequireStatus(ErrorCode == ProtocolErrorCodes.Cancelled, "CANCELLED requires request.cancelled.");
                RequireStatus(!Retryable, "CANCELLED status must not be retryable.");
                break;

            case ProtocolReceiptStatuses.TimedOut:
                RequireStatus(!Changed && CommittedRevision is null, "TIMED_OUT status cannot commit a change.");
                RequireStatus(ErrorCode == ProtocolErrorCodes.Timeout, "TIMED_OUT requires request.timeout.");
                RequireStatus(Retryable, "TIMED_OUT status must be retryable.");
                break;

            case ProtocolReceiptStatuses.Unknown:
                RequireStatus(!Changed && CommittedRevision is null, "UNKNOWN status cannot claim a committed change.");
                RequireStatus(ErrorCode is not null, "UNKNOWN status requires an error code.");
                RequireStatus(Retryable, "UNKNOWN status must be retryable.");
                break;
        }
    }

    private static void RequireStatus(bool condition, string message)
    {
        if (!condition)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                message);
        }
    }

    private static bool IsLowerHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool GetDefaultRetryable(string errorCode) =>
        ProtocolErrorCodes.TryGetDefaultRetryable(errorCode, out var retryable) && retryable;
}
