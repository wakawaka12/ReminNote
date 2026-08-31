namespace ReminNote.Infrastructure.Persistence.P25;

public sealed class P25CommandReceiptEntity
{
    public string ActualUserSid { get; set; } = string.Empty;

    public string ProfileScope { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string HashVersion { get; set; } = string.Empty;

    public byte[] CanonicalPayloadHash { get; set; } = [];

    public string Status { get; set; } = string.Empty;

    public bool Changed { get; set; }

    public long? CommittedRevision { get; set; }

    public string? ErrorCode { get; set; }

    public string FirstAcceptedAtUtc { get; set; } = string.Empty;

    public string UpdatedAtUtc { get; set; } = string.Empty;

    public string FirstRequestId { get; set; } = string.Empty;

    public string LastRequestId { get; set; } = string.Empty;

    public int AttemptCount { get; set; }

    public string? AgentInstanceId { get; set; }
}
