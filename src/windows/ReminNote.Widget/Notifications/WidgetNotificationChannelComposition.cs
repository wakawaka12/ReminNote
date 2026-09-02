using NodaTime;
using System.Windows;
using System.Windows.Threading;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Notifications.Adapters;

namespace ReminNote.Widget.Notifications;

/// <summary>
/// Widget-host composition for the P3 channel seam. The production host
/// registration owns the HWND effect sink; Agent trigger/query orchestration
/// remains outside this UI-only boundary.
/// </summary>
public static class WidgetNotificationChannelComposition
{
    public static WidgetNotificationChannelHost CreateHostOwned(
        IClock clock,
        Func<Window?> widgetWindowProvider,
        Dispatcher dispatcher) =>
        new(clock, widgetWindowProvider, dispatcher);

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
