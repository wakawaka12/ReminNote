using ReminNote.Agent.Writer;

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
    }

    public string Operation { get; }

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
