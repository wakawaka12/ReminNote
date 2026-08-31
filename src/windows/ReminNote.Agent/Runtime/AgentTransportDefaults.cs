using ReminNote.Core.Protocol;
using ReminNote.Core.Transport;

namespace ReminNote.Agent.Runtime;

internal static class AgentTransportDefaults
{
    public static TransportLimits CreateLimits() => new(new LimitSource());

    private sealed class LimitSource : ITransportLimitSource
    {
        public int MaxFrameBytes => ProtocolLimits.MaxFrameBytes;
        public int MaxCommandPayloadBytes => ProtocolLimits.MaxWriteCommandPayloadBytes;
        public int MaxEventPayloadBytes => ProtocolLimits.MaxEventPayloadBytes;
        public int MaxErrorDetailsBytes => ProtocolLimits.MaxErrorDetailsBytes;
        public int MaxSuccessPayloadBytes => ProtocolLimits.MaxSuccessPayloadBytes;
        public int MaxJsonDepth => ProtocolLimits.MaxJsonNestingDepth;
        public int MaxInFlightRequests => ProtocolLimits.MaxInFlightPerConnection;
        public int MaxQueuedRequests => ProtocolLimits.MaxQueuedRequestsPerProfile;
        public TimeSpan ConnectDeadline => TimeSpan.FromMilliseconds(ProtocolLimits.ConnectDeadlineMilliseconds);
        public TimeSpan HelloQueryStatusDeadline => TimeSpan.FromMilliseconds(ProtocolLimits.ReadDeadlineMilliseconds);
        public TimeSpan DefaultMutationTimeout => TimeSpan.FromMilliseconds(ProtocolLimits.DefaultMutationTimeoutMilliseconds);
        public TimeSpan CancelTimeout => TimeSpan.FromMilliseconds(ProtocolLimits.ReadDeadlineMilliseconds);
        public TimeSpan AbsoluteRequestDeadline => TimeSpan.FromMilliseconds(ProtocolLimits.MaxRequestDeadlineMilliseconds);
    }
}
