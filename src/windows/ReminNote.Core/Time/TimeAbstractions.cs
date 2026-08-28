using NodaTime;

namespace ReminNote.Core.Time;

/// <summary>
/// Supplies the user's selected civil-time zone. Implementations belong to
/// the application/infrastructure layer, not to the Core domain.
/// </summary>
public interface IUserTimeZoneProvider
{
    DateTimeZone TimeZone { get; }
}

/// <summary>
/// Maps an absolute instant to the logical workday used by TODAY and review
/// behavior. It must not rewrite the real calendar date or UTC timestamps.
/// </summary>
public interface IWorkdayService
{
    LocalDate GetWorkday(Instant instant);

    LocalDate GetWorkday(LocalDateTime localDateTime);
}

/// <summary>
/// Persisted local-civil setting for the logical workday boundary. P2 uses
/// minute precision only; it never changes the calendar date stored on a Task.
/// </summary>
public sealed record WorkdaySettings
{
    public WorkdaySettings(LocalTime boundary)
    {
        if (boundary.NanosecondOfDay % 60_000_000_000L != 0)
        {
            throw new DomainValidationException(new DomainValidationError(
                "workday.boundary.minute_precision",
                "Workday boundary must use whole minutes.",
                nameof(boundary)));
        }

        Boundary = boundary;
    }

    public WorkdaySettings(int boundaryMinutes)
        : this(CreateBoundary(boundaryMinutes))
    {
    }

    public LocalTime Boundary { get; }

    public int BoundaryMinutes => Boundary.Hour * 60 + Boundary.Minute;

    public static WorkdaySettings Default { get; } = new(0);

    private static LocalTime CreateBoundary(int boundaryMinutes)
    {
        if (boundaryMinutes is < 0 or > 1_439)
        {
            throw new DomainValidationException(new DomainValidationError(
                "workday.boundary.out_of_range",
                "Workday boundary must be between 00:00 and 23:59.",
                nameof(boundaryMinutes)));
        }

        return LocalTime.FromMinutesSinceMidnight(boundaryMinutes);
    }
}

public interface IWorkdaySettingsStore
{
    ValueTask<WorkdaySettings> GetAsync(CancellationToken cancellationToken = default);

    ValueTask SetAsync(
        WorkdaySettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pure workday calculation shared by Today and review logic.
/// </summary>
public sealed class WorkdayService : IWorkdayService
{
    private readonly DateTimeZone timeZone;
    private readonly LocalTime boundary;

    public WorkdayService(DateTimeZone timeZone, LocalTime boundary)
    {
        this.timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        boundary = new WorkdaySettings(boundary).Boundary;
        this.boundary = boundary;
    }

    public LocalDate GetWorkday(Instant instant) =>
        GetWorkday(instant.InZone(timeZone).LocalDateTime);

    public LocalDate GetWorkday(LocalDateTime localDateTime) =>
        localDateTime.TimeOfDay < boundary
            ? localDateTime.Date.PlusDays(-1)
            : localDateTime.Date;
}
