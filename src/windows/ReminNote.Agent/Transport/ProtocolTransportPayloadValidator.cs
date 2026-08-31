using ReminNote.Core.Protocol;
using ReminNote.Core.Transport;

namespace ReminNote.Agent.Transport;

internal sealed class ProtocolTransportPayloadValidator : ITransportPayloadValidator
{
    public void Validate(ReadOnlyMemory<byte> frame)
    {
        try
        {
            _ = ProtocolJson.Deserialize(frame.Span);
        }
        catch (ProtocolContractException exception)
        {
            throw new TransportFailureException(
                TransportFailureKind.InvalidJson,
                exception.Message,
                connectionMustClose: true,
                innerException: exception);
        }
    }
}
