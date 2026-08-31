namespace ReminNote.Infrastructure.Persistence.P25;

public sealed class P25RevisionStateEntity
{
    public string ProfileScope { get; set; } = string.Empty;

    public long CurrentRevision { get; set; }

    public long OldestAvailableRevision { get; set; }
}
