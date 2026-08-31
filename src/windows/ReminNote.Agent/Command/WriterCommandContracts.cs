using System.Text.Json;
using ReminNote.Agent.Writer;
using ReminNote.Core.Protocol;

namespace ReminNote.Agent.Command;

/// <summary>
/// Internal, non-serialized command record for P2.5-03 tests. The public
/// P2.5-01 request contract will be supplied by the integration window.
/// </summary>
internal sealed class WriterCommandRequest
{
    public WriterCommandRequest(
        string operation,
        string idempotencyKey,
        ReadOnlyMemory<byte> canonicalPayloadHash,
        long expectedRevision)
        : this(
            operation,
            idempotencyKey,
            canonicalPayloadHash,
            expectedRevision,
            requestId: null,
            actualUserSid: null,
            profileScope: null,
            payload: null)
    {
    }

    private WriterCommandRequest(
        string operation,
        string idempotencyKey,
        ReadOnlyMemory<byte> canonicalPayloadHash,
        long expectedRevision,
        string? requestId,
        string? actualUserSid,
        string? profileScope,
        JsonElement? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation, nameof(operation));
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey, nameof(idempotencyKey));
        if (canonicalPayloadHash.Length != 32)
        {
            throw new ArgumentException(
                "A writer command hash must contain exactly 32 bytes.",
                nameof(canonicalPayloadHash));
        }

        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision),
                "Expected revision cannot be negative.");
        }

        Operation = operation;
        IdempotencyKey = idempotencyKey;
        CanonicalPayloadHash = canonicalPayloadHash.ToArray();
        ExpectedRevision = expectedRevision;

        if (requestId is null)
        {
            if (actualUserSid is not null || profileScope is not null || payload is not null)
            {
                throw new ArgumentException(
                    "Protocol identity and payload must be supplied together with RequestId.",
                    nameof(requestId));
            }

            return;
        }

        ProtocolValidation.RequireLowercaseUuid(requestId, nameof(requestId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actualUserSid, nameof(actualUserSid));
        ArgumentException.ThrowIfNullOrWhiteSpace(profileScope, nameof(profileScope));
        ProtocolProfileScope.Validate(profileScope);
        if (payload is not { } protocolPayload || protocolPayload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "A protocol-backed writer command must retain an object payload.",
                nameof(payload));
        }

        ProtocolOperationSchemas.ValidatePayload(operation, protocolPayload);
        RequestId = requestId;
        ActualUserSid = actualUserSid;
        ProfileScope = profileScope;
        Payload = protocolPayload.Clone();
    }

    /// <summary>
    /// Adapts one validated wire attempt to the writer seam. RequestId is kept
    /// for attempt diagnostics only; the actor still deduplicates by
    /// idempotencyKey plus canonical hash.
    /// </summary>
    public static WriterCommandRequest FromProtocolRequest(
        ProtocolRequest request,
        string actualUserSid,
        string profileScope)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (!ProtocolOperations.IsMutation(request.Operation) ||
            request.IdempotencyKey is null ||
            request.ExpectedRevision is null)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Only complete mutation requests can enter the writer seam.");
        }

        var hash = RnCj1Canonicalizer.ComputeHash(
            profileScope,
            request.Operation,
            request.ExpectedRevision.Value,
            request.Payload);
        return new WriterCommandRequest(
            request.Operation,
            request.IdempotencyKey,
            hash.Hash,
            request.ExpectedRevision.Value,
            request.RequestId,
            actualUserSid,
            profileScope,
            request.Payload);
    }

    public string Operation { get; }

    public string? RequestId { get; }

    public string? ActualUserSid { get; }

    public string? ProfileScope { get; }

    public JsonElement? Payload { get; }

    public string IdempotencyKey { get; }

    public ReadOnlyMemory<byte> CanonicalPayloadHash { get; }

    public long ExpectedRevision { get; }

    public bool HasSameHash(ReadOnlySpan<byte> otherHash) =>
        CanonicalPayloadHash.Span.SequenceEqual(otherHash);
}

/// <summary>
/// Internal executor seam. A future adapter will translate approved P2
/// command payloads into domain operations without exposing persistence types
/// to transport clients.
/// </summary>
internal interface IWriterCommandExecutor
{
    ValueTask<WriterExecutionResult> ExecuteAsync(
        IWriterTransaction transaction,
        CancellationToken cancellationToken = default);
}
