using NodaTime;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Notifications.Adapters;

namespace ReminNote.Widget.Notifications;

/// <summary>
/// Widget-host composition for the P3 channel seam. It is not wired into the
/// current mock UI lifecycle; P3-03/P3-06/P3-09 must supply the Agent-owned
/// trigger/query path before this becomes a production registration.
/// </summary>
public static class WidgetNotificationChannelComposition
{
    public static NotificationChannelCatalog CreateSafeDefault(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var observedAtUtc = clock.GetCurrentInstant();
        return Create(
            clock,
            _ => new UnavailableNotificationChannelEffectSink(),
            channelId => NotificationChannelAdapterDefaults.SafeUnavailableStatus(
                channelId,
                observedAtUtc));
    }

    public static NotificationChannelCatalog Create(
        IClock clock,
        Func<NotificationSurfaceKind, INotificationChannelEffectSink> effectSinkFactory,
        Func<NotificationChannelId, NotificationChannelStatus> statusFactory)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(effectSinkFactory);
        ArgumentNullException.ThrowIfNull(statusFactory);

        return new NotificationChannelCatalog(
        [
            new WidgetNotificationChannel(
                new DelegateNotificationChannelStatusSource(
                    () => statusFactory(NotificationChannelId.Widget)),
                effectSinkFactory(NotificationSurfaceKind.WIDGET) ??
                    throw new InvalidOperationException(
                        "No effect sink was supplied for 'WIDGET'."),
                clock),
        ]);
    }
}
