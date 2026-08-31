using ReminNote.Core.Protocol;

namespace ReminNote.Agent.Transport;

/// <summary>
/// Default response factory for a valid post-handshake protocol request that
/// cannot be admitted because the connection is already at its in-flight
/// limit. It does not echo the request payload or any peer identity.
/// </summary>
internal sealed class ProtocolOverloadResponder : ITransportOverloadResponder
{
    private readonly string agentInstanceId;
    private readonly Func<long> currentRevision;

    public ProtocolOverloadResponder(Guid agentInstanceId, Func<long> currentRevision)
    {
        if (agentInstanceId == Guid.Empty)
        {
            throw new ArgumentException("Agent instance ID must not be empty.", nameof(agentInstanceId));
        }

        ArgumentNullException.ThrowIfNull(currentRevision);
        this.agentInstanceId = agentInstanceId.ToString("D");
        this.currentRevision = currentRevision;
    }

    public ValueTask<ReadOnlyMemory<byte>> CreateAsync(
        ReadOnlyMemory<byte> requestFrame,
        TransportPeerIdentity peer)
    {
        _ = peer;
        var request = ProtocolJson.DeserializeRequest(requestFrame.Span);
        var response = new ProtocolResponse(
            ProtocolVersion.Current,
            request.RequestId,
            request.Operation,
            agentInstanceId,
            currentRevision(),
            ok: false,
            replayed: false,
            outcome: ProtocolOutcomes.Rejected,
            committedRevision: null,
            payload: null,
            error: new ProtocolError(
                ProtocolErrorCodes.Overloaded,
                retryable: true,
                details: ProtocolJson.ParseObject("{}")));
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(ProtocolJson.SerializeResponse(response));
    }
}
