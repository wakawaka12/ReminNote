using NodaTime;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Notifications.Adapters;

namespace ReminNote.Agent.Notifications;

/// <summary>
/// Agent-side default for the cross-process host boundary. Agent cannot own
/// the Main or Widget HWND, WinRT registration, audio session, or wake timer,
/// so the absence of an injected host bridge is represented by known channel
/// adapters whose health is explicitly UNAVAILABLE. No process-local host
/// object is manufactured and no OS effect sink is invoked.
/// </summary>
public static class AgentNotificationChannelComposition
{
    public static NotificationChannelCatalog CreateFailClosed(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new NotificationChannelCatalog(
        [
            new WindowsToastNotificationChannel(
                StatusSource(NotificationChannelId.Toast, clock),
                new UnavailableNotificationChannelEffectSink(),
                clock),
            new TrayNotificationChannel(
                StatusSource(NotificationChannelId.Tray, clock),
                new UnavailableNotificationChannelEffectSink(),
                clock),
            new WidgetNotificationChannel(
                StatusSource(NotificationChannelId.Widget, clock),
                new UnavailableNotificationChannelEffectSink(),
                clock),
            new SoundNotificationChannel(
                StatusSource(NotificationChannelId.Sound, clock),
                new UnavailableNotificationChannelEffectSink(),
                clock),
            new WakeTimerNotificationChannel(
                StatusSource(NotificationChannelId.WakeTimer, clock),
                new UnavailableNotificationChannelEffectSink(),
                clock)
        ]);
    }

    private static DelegateNotificationChannelStatusSource StatusSource(
        NotificationChannelId channelId,
        IClock clock) =>
        new DelegateNotificationChannelStatusSource(
            () => NotificationChannelAdapterDefaults.SafeUnavailableStatus(
                channelId,
                clock.GetCurrentInstant(),
                NotificationErrorCodes.ChannelUnavailable));
}
