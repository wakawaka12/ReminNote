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
