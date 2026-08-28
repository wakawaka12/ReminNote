using NodaTime;
using ReminNote.Core.Time;

namespace ReminNote.Infrastructure.Application;

public sealed class SystemUserTimeZoneProvider : IUserTimeZoneProvider
{
    public SystemUserTimeZoneProvider(DateTimeZone? timeZone = null)
    {
        TimeZone = timeZone ?? DateTimeZoneProviders.Tzdb.GetSystemDefault();
    }

    public DateTimeZone TimeZone { get; }
}
