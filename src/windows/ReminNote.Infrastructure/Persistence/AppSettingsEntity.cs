using NodaTime;

namespace ReminNote.Infrastructure.Persistence;

public sealed class AppSettingsEntity
{
    public int Id { get; set; }

    public int WorkdayBoundaryMinutes { get; set; }

    public Instant UpdatedAt { get; set; }
}
