using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NodaTime;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Notifications.Adapters;

namespace ReminNote.Widget.Notifications;

/// <summary>
/// Host-owned Widget effect composition. The widget surface is represented by
/// a real HWND and the effect is a taskbar flash; no Core reminder data or UI
/// read model is mutated here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WidgetNotificationChannelHost : IDisposable
{
    private readonly WidgetNotificationEffectSink effectSink;
    private bool disposed;

    public WidgetNotificationChannelHost(
        IClock clock,
        Func<Window?> widgetWindowProvider,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(widgetWindowProvider);
        ArgumentNullException.ThrowIfNull(dispatcher);

        effectSink = new WidgetNotificationEffectSink(widgetWindowProvider, dispatcher);
        Catalog = new NotificationChannelCatalog(
        [
            new WidgetNotificationChannel(
                new DelegateNotificationChannelStatusSource(
                    () => effectSink.GetStatus(clock.GetCurrentInstant())),
                effectSink,
                clock),
        ]);
    }

    public NotificationChannelCatalog Catalog { get; }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        effectSink.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Flashes the visible Widget HWND through the Win32 taskbar API. A false
/// return from FlashWindowEx is not treated as failure: it reports the prior
/// foreground state, while exceptions and an invalid HWND remain fail-closed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WidgetNotificationEffectSink : INotificationChannelEffectSink, IDisposable
{
    private const uint FlashwTray = 0x00000002;
    private const uint FlashwTimerNoForeground = 0x0000000C;

    private static readonly IReadOnlyList<NotificationCapability> WidgetCapabilities =
    [
        NotificationCapability.PRESENT,
        NotificationCapability.REPLACE_LOGICAL_REMINDER,
    ];

    private readonly WidgetWindowHandleResolver windowResolver;
    private readonly object gate = new();
    private string? lastLogicalReplacementKey;
    private bool disposed;

    public WidgetNotificationEffectSink(
        Func<Window?> widgetWindowProvider,
        Dispatcher dispatcher)
    {
        windowResolver = new WidgetWindowHandleResolver(widgetWindowProvider, dispatcher);
    }

    public string? LastLogicalReplacementKey
    {
        get
        {
            lock (gate)
            {
                return lastLogicalReplacementKey;
            }
        }
    }

    public NotificationChannelStatus GetStatus(Instant observedAtUtc)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WidgetNotificationResponse.Status(
                observedAtUtc,
                NotificationChannelHealth.UNAVAILABLE,
                WidgetNotificationResponse.Unavailable);
        }

        lock (gate)
        {
            if (disposed)
            {
                return WidgetNotificationResponse.Status(
                    observedAtUtc,
                    NotificationChannelHealth.UNAVAILABLE,
                    WidgetNotificationResponse.Disposed);
            }
        }

        return windowResolver.GetSnapshot().Handle == IntPtr.Zero
            ? WidgetNotificationResponse.Status(
                observedAtUtc,
                NotificationChannelHealth.UNAVAILABLE,
                WidgetNotificationResponse.WindowUnavailable)
            : WidgetNotificationResponse.Status(
                observedAtUtc,
                NotificationChannelHealth.HEALTHY,
                "windows.widget.ready");
    }

    public ValueTask<NotificationChannelDeliveryResponse> PresentAsync(
        NotificationChannelEffectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(
                WidgetNotificationResponse.UnavailableResponse(
                    WidgetNotificationResponse.Unavailable));
        }

        var snapshot = windowResolver.GetSnapshot();
        if (snapshot.Handle == IntPtr.Zero || !snapshot.IsVisible)
        {
            return ValueTask.FromResult(
                WidgetNotificationResponse.UnavailableResponse(
                    WidgetNotificationResponse.WindowUnavailable));
        }

        lock (gate)
        {
            if (disposed)
            {
                return ValueTask.FromResult(
                    WidgetNotificationResponse.UnavailableResponse(
                        WidgetNotificationResponse.Disposed));
            }

            var flashInfo = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                WindowHandle = snapshot.Handle,
                Flags = FlashwTray | FlashwTimerNoForeground,
                Count = 3,
                Timeout = 0,
            };

            try
            {
                _ = FlashWindowEx(ref flashInfo);
                lastLogicalReplacementKey = request.LogicalReplacementKey;
                return ValueTask.FromResult(
                    WidgetNotificationResponse.DeliveredResponse());
            }
            catch (DllNotFoundException)
            {
                return ValueTask.FromResult(
                    WidgetNotificationResponse.UnavailableResponse(
                        WidgetNotificationResponse.Unavailable));
            }
            catch (EntryPointNotFoundException)
            {
                return ValueTask.FromResult(
                    WidgetNotificationResponse.UnavailableResponse(
                        WidgetNotificationResponse.Unavailable));
            }
            catch (UnauthorizedAccessException)
            {
                return ValueTask.FromResult(
                    WidgetNotificationResponse.BlockedResponse(
                        WidgetNotificationResponse.Blocked));
            }
            catch (Exception)
            {
                return ValueTask.FromResult(
                    WidgetNotificationResponse.FailedResponse(
                        WidgetNotificationResponse.Failed));
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo flashInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }
}

[SupportedOSPlatform("windows")]
internal static class WidgetNotificationResponse
{
    public const string WindowUnavailable = "windows.widget.window.unavailable";
    public const string Unavailable = "windows.widget.unavailable";
    public const string Blocked = "windows.widget.blocked";
    public const string Failed = "windows.widget.failed";
    public const string Disposed = "windows.notification.host.disposed";

    public static NotificationChannelStatus Status(
        Instant observedAtUtc,
        NotificationChannelHealth health,
        string code) =>
        new(
            NotificationChannelId.Widget,
            [
                NotificationCapability.PRESENT,
                NotificationCapability.REPLACE_LOGICAL_REMINDER,
            ],
            health,
            observedAtUtc,
            code);

    public static NotificationChannelDeliveryResponse DeliveredResponse() =>
        new(NotificationDeliveryOutcome.DELIVERED);

    public static NotificationChannelDeliveryResponse UnavailableResponse(string code) =>
        new(NotificationDeliveryOutcome.UNAVAILABLE, code);

    public static NotificationChannelDeliveryResponse BlockedResponse(string code) =>
        new(NotificationDeliveryOutcome.BLOCKED, code);

    public static NotificationChannelDeliveryResponse FailedResponse(string code) =>
        new(NotificationDeliveryOutcome.FAILED, code);
}

[SupportedOSPlatform("windows")]
internal readonly record struct WidgetWindowSnapshot(IntPtr Handle, bool IsVisible)
{
    public static WidgetWindowSnapshot Unavailable { get; } = new(IntPtr.Zero, false);
}

[SupportedOSPlatform("windows")]
internal sealed class WidgetWindowHandleResolver
{
    private readonly Func<Window?> windowProvider;
    private readonly Dispatcher dispatcher;

    public WidgetWindowHandleResolver(Func<Window?> windowProvider, Dispatcher dispatcher)
    {
        this.windowProvider = windowProvider ?? throw new ArgumentNullException(nameof(windowProvider));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public WidgetWindowSnapshot GetSnapshot()
    {
        try
        {
            return dispatcher.CheckAccess()
                ? GetSnapshotOnDispatcher()
                : dispatcher.Invoke(GetSnapshotOnDispatcher);
        }
        catch (ObjectDisposedException)
        {
            return WidgetWindowSnapshot.Unavailable;
        }
        catch (InvalidOperationException)
        {
            return WidgetWindowSnapshot.Unavailable;
        }
    }

    private WidgetWindowSnapshot GetSnapshotOnDispatcher()
    {
        var window = windowProvider();
        if (window is null || !window.IsVisible)
        {
            return WidgetWindowSnapshot.Unavailable;
        }

        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && IsWindow(handle) && IsWindowVisible(handle)
            ? new WidgetWindowSnapshot(handle, true)
            : WidgetWindowSnapshot.Unavailable;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);
}
