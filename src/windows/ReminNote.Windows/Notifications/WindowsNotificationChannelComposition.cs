using NodaTime;
using System.Windows;
using System.Windows.Threading;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Notifications.Adapters;

namespace ReminNote.Windows.Notifications;

/// <summary>
/// Main-host composition for the channel adapter seam. The safe default is
/// deliberately unverified: it registers the adapter shape while reporting
/// unavailable and making no Windows API calls.
/// </summary>
public static class WindowsNotificationChannelComposition
{
    /// <summary>
    /// Creates the host-owned Windows catalog. Toast registration remains
    /// fail-closed unless the normal Main startup supplied a verified option;
    /// safe/test composition therefore cannot claim a desktop Toast effect.
    /// </summary>
    public static WindowsNotificationChannelHost CreateHostOwned(
        IClock clock,
        Func<Window?> mainWindowProvider,
        Dispatcher dispatcher,
        WindowsNotificationHostOptions? options = null) =>
        new(clock, mainWindowProvider, dispatcher, options);

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
        Func<NotificationChannelId, NotificationChannelStatus> statusFactory,
        bool useTaskbarFlash = false)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(effectSinkFactory);
        ArgumentNullException.ThrowIfNull(statusFactory);

        NotificationChannelAdapter tray = useTaskbarFlash
            ? new TaskbarFlashNotificationChannel(
                StatusSource(statusFactory, NotificationChannelId.Tray),
                EffectSink(effectSinkFactory, NotificationSurfaceKind.TASKBAR_FLASH),
                clock)
            : new TrayNotificationChannel(
                StatusSource(statusFactory, NotificationChannelId.Tray),
                EffectSink(effectSinkFactory, NotificationSurfaceKind.TRAY),
                clock);

        return new NotificationChannelCatalog(
        [
            new WindowsToastNotificationChannel(
                StatusSource(statusFactory, NotificationChannelId.Toast),
                EffectSink(effectSinkFactory, NotificationSurfaceKind.TOAST),
                clock),
            tray,
            new SoundNotificationChannel(
                StatusSource(statusFactory, NotificationChannelId.Sound),
                EffectSink(effectSinkFactory, NotificationSurfaceKind.SOUND),
                clock),
            new WakeTimerNotificationChannel(
                StatusSource(statusFactory, NotificationChannelId.WakeTimer),
                EffectSink(effectSinkFactory, NotificationSurfaceKind.WAKE_TIMER),
                clock),
        ]);
    }

    private static DelegateNotificationChannelStatusSource StatusSource(
        Func<NotificationChannelId, NotificationChannelStatus> statusFactory,
        NotificationChannelId channelId) =>
        new(() => statusFactory(channelId));

    private static INotificationChannelEffectSink EffectSink(
        Func<NotificationSurfaceKind, INotificationChannelEffectSink> effectSinkFactory,
        NotificationSurfaceKind surfaceKind) =>
        effectSinkFactory(surfaceKind) ?? throw new InvalidOperationException(
            $"No effect sink was supplied for '{surfaceKind}'.");
}
