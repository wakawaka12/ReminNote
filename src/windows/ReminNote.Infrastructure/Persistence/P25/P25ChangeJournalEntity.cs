namespace ReminNote.Infrastructure.Persistence.P25;

public sealed class P25ChangeJournalEntity
{
    public string ProfileScope { get; set; } = string.Empty;

    public long Revision { get; set; }

    public long ChangeOrdinal { get; set; }

    public string BatchId { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string ChangeKind { get; set; } = string.Empty;

    public string ChangedAtUtc { get; set; } = string.Empty;
}
