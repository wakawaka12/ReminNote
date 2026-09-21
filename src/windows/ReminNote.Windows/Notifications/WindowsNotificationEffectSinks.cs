using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NodaTime;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Notifications.Adapters;

namespace ReminNote.Windows.Notifications;

/// <summary>
/// Host configuration for the Windows notification surfaces. Normal Main
/// startup performs the unpackaged-app AUMID/Start-menu registration and only
/// sets the verified flag after reading the shortcut back successfully. Tests
/// and headless gates keep the default fail-closed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNotificationHostOptions
{
    public const string DefaultApplicationUserModelId = "ReminNote.Desktop";

    public WindowsNotificationHostOptions(
        string? applicationUserModelId = null,
        bool toastRegistrationVerified = false)
    {
        ApplicationUserModelId = string.IsNullOrWhiteSpace(applicationUserModelId)
            ? DefaultApplicationUserModelId
            : applicationUserModelId;
        if (ApplicationUserModelId.Length > 128)
        {
            throw new ArgumentException(
                "The Windows AUMID must be bounded.",
                nameof(applicationUserModelId));
        }

        ToastRegistrationVerified = toastRegistrationVerified;
    }

    public string ApplicationUserModelId { get; }

    public bool ToastRegistrationVerified { get; }
}

/// <summary>
/// Host-owned composition for the real Windows effects. The catalog is
/// deliberately exposed as the Core port; the concrete sinks are owned and
/// disposed with this host and never touch Reminder persistence.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNotificationChannelHost : IDisposable
{
    private readonly WindowsToastNotificationEffectSink toastSink;
    private readonly WindowsTrayNotificationEffectSink traySink;
    private readonly WindowsSoundNotificationEffectSink soundSink;
    private readonly WindowsWakeTimerNotificationEffectSink wakeTimerSink;
    private bool disposed;

    public WindowsNotificationChannelHost(
        IClock clock,
        Func<Window?> mainWindowProvider,
        Dispatcher dispatcher,
        WindowsNotificationHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(mainWindowProvider);
        ArgumentNullException.ThrowIfNull(dispatcher);

        toastSink = new WindowsToastNotificationEffectSink(
            options ?? new WindowsNotificationHostOptions());
        traySink = new WindowsTrayNotificationEffectSink(mainWindowProvider, dispatcher);
        soundSink = new WindowsSoundNotificationEffectSink();
        wakeTimerSink = new WindowsWakeTimerNotificationEffectSink(clock);

        Catalog = new NotificationChannelCatalog(
        [
            new WindowsToastNotificationChannel(
                new DelegateNotificationChannelStatusSource(
                    () => toastSink.GetStatus(clock.GetCurrentInstant())),
                toastSink,
                clock),
            new TrayNotificationChannel(
                new DelegateNotificationChannelStatusSource(
                    () => traySink.GetStatus(clock.GetCurrentInstant())),
                traySink,
                clock),
            new SoundNotificationChannel(
                new DelegateNotificationChannelStatusSource(
                    () => WindowsSoundNotificationEffectSink.GetStatus(clock.GetCurrentInstant())),
                soundSink,
                clock),
            new WakeTimerNotificationChannel(
                new DelegateNotificationChannelStatusSource(
                    () => wakeTimerSink.GetStatus(clock.GetCurrentInstant())),
                wakeTimerSink,
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
        wakeTimerSink.Dispose();
        traySink.Dispose();
        GC.SuppressFinalize(this);
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsNotificationResponse
{
    public const string ToastRegistrationUnverified = "windows.toast.registration.unverified";
    public const string ToastUnavailable = "windows.toast.unavailable";
    public const string ToastBlocked = "windows.toast.blocked";
    public const string ToastFailed = "windows.toast.failed";
    public const string TrayWindowUnavailable = "windows.tray.window.unavailable";
    public const string TrayUnavailable = "windows.tray.unavailable";
    public const string TrayBlocked = "windows.tray.blocked";
    public const string TrayFailed = "windows.tray.failed";
    public const string SoundUnavailable = "windows.sound.unavailable";
    public const string SoundBlocked = "windows.sound.blocked";
    public const string SoundFailed = "windows.sound.failed";
    public const string WakeTimerUnavailable = "windows.wake_timer.unavailable";
    public const string WakeTimerBlocked = "windows.wake_timer.blocked";
    public const string WakeTimerFailed = "windows.wake_timer.failed";
    public const string WakeTimerScheduleMissing = "windows.wake_timer.schedule.missing";
    public const string WakeTimerSchedulePast = "windows.wake_timer.schedule.past";
    public const string WakeTimerCapacity = "windows.wake_timer.capacity";
    public const string HostDisposed = "windows.notification.host.disposed";

    public static NotificationChannelDeliveryResponse Delivered() =>
        new(NotificationDeliveryOutcome.DELIVERED);

    public static NotificationChannelDeliveryResponse Unavailable(string code) =>
        new(NotificationDeliveryOutcome.UNAVAILABLE, code);

    public static NotificationChannelDeliveryResponse Blocked(string code) =>
        new(NotificationDeliveryOutcome.BLOCKED, code);

    public static NotificationChannelDeliveryResponse Failed(string code) =>
        new(NotificationDeliveryOutcome.FAILED, code);

    public static NotificationChannelDeliveryResponse NotAttempted(string code) =>
        new(NotificationDeliveryOutcome.NOT_ATTEMPTED, code);

    public static NotificationChannelStatus Status(
        NotificationChannelId channelId,
        IReadOnlyList<NotificationCapability> capabilities,
        NotificationChannelHealth health,
        Instant observedAtUtc,
        string code) =>
        new(channelId, capabilities, health, observedAtUtc, code);
}

[SupportedOSPlatform("windows")]
internal readonly record struct WindowsWindowSnapshot(IntPtr Handle, bool IsVisible)
{
    public static WindowsWindowSnapshot Unavailable { get; } = new(IntPtr.Zero, false);
}

/// <summary>
/// Performs WPF access on the UI dispatcher and returns only a native HWND to
/// the effect implementation. This keeps cross-thread window access bounded.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWindowHandleResolver
{
    private readonly Func<Window?> windowProvider;
    private readonly Dispatcher dispatcher;

    public WindowsWindowHandleResolver(Func<Window?> windowProvider, Dispatcher dispatcher)
    {
        this.windowProvider = windowProvider ?? throw new ArgumentNullException(nameof(windowProvider));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public WindowsWindowSnapshot GetSnapshot()
    {
        try
        {
            return dispatcher.CheckAccess()
                ? GetSnapshotOnDispatcher()
                : dispatcher.Invoke(GetSnapshotOnDispatcher);
        }
        catch (ObjectDisposedException)
        {
            return WindowsWindowSnapshot.Unavailable;
        }
        catch (InvalidOperationException)
        {
            return WindowsWindowSnapshot.Unavailable;
        }
    }

    private WindowsWindowSnapshot GetSnapshotOnDispatcher()
    {
        var window = windowProvider();
        if (window is null || !window.IsVisible)
        {
            return WindowsWindowSnapshot.Unavailable;
        }

        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && WindowsNative.IsWindow(handle)
            ? new WindowsWindowSnapshot(handle, true)
            : WindowsWindowSnapshot.Unavailable;
    }
}

/// <summary>
/// Real WinRT toast presenter. No arbitrary Reminder text crosses this seam;
/// the host uses bounded summary metadata and the validated logical UUID as
/// the launch, tag and replacement identity.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsToastNotificationEffectSink : INotificationChannelEffectSink
{
    private readonly WindowsNotificationHostOptions options;

    public WindowsToastNotificationEffectSink(WindowsNotificationHostOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public NotificationChannelStatus GetStatus(Instant observedAtUtc)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WindowsNotificationResponse.Status(
                NotificationChannelId.Toast,
                ToastCapabilities,
                NotificationChannelHealth.UNAVAILABLE,
                observedAtUtc,
                WindowsNotificationResponse.ToastUnavailable);
        }

        if (!options.ToastRegistrationVerified)
        {
            return WindowsNotificationResponse.Status(
                NotificationChannelId.Toast,
                ToastCapabilities,
                NotificationChannelHealth.UNAVAILABLE,
                observedAtUtc,
                WindowsNotificationResponse.ToastRegistrationUnverified);
        }

        try
        {
            using var scope = WindowsRuntimeScope.Enter();
            using var notifier = CreateNotifier();
            return WindowsNotificationResponse.Status(
                NotificationChannelId.Toast,
                ToastCapabilities,
                NotificationChannelHealth.HEALTHY,
                observedAtUtc,
                "windows.toast.ready");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return StatusFromException(observedAtUtc, exception);
        }
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
                WindowsNotificationResponse.Unavailable(
                    WindowsNotificationResponse.ToastUnavailable));
        }

        if (!options.ToastRegistrationVerified)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Unavailable(
                    WindowsNotificationResponse.ToastRegistrationUnverified));
        }

        try
        {
            using var scope = WindowsRuntimeScope.Enter();
            using var notifier = CreateNotifier();
            using var document = CreateToastDocument(request);
            using var factory = GetActivationFactory(
                "Windows.UI.Notifications.ToastNotification",
                ToastNotificationFactoryIid);
            using var toast = CreateToast(factory, document);
            using var toast2 = toast.QueryInterface(ToastNotification2Iid);
            using var tag = WindowsRuntimeString.Create(request.LogicalReplacementKey);
            using var group = WindowsRuntimeString.Create("ReminNote");

            WindowsRuntime.ThrowIfFailed(
                GetDelegate<PutStringDelegate>(toast2.Value, 6)(toast2.Value, tag.Value),
                "IToastNotification2.put_Tag");
            WindowsRuntime.ThrowIfFailed(
                GetDelegate<PutStringDelegate>(toast2.Value, 8)(toast2.Value, group.Value),
                "IToastNotification2.put_Group");
            WindowsRuntime.ThrowIfFailed(
                GetDelegate<ShowToastDelegate>(notifier.Value, 6)(notifier.Value, toast.Value),
                "IToastNotifier.Show");

            return ValueTask.FromResult(WindowsNotificationResponse.Delivered());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ValueTask.FromResult(StatusFromException(exception));
        }
    }

    private static readonly IReadOnlyList<NotificationCapability> ToastCapabilities =
    [
        NotificationCapability.PRESENT,
        NotificationCapability.REPLACE_LOGICAL_REMINDER,
    ];

    private ComReference CreateNotifier()
    {
        using var manager = GetActivationFactory(
            "Windows.UI.Notifications.ToastNotificationManager",
            ToastNotificationManagerStaticsIid);
        using var appUserModelId = WindowsRuntimeString.Create(options.ApplicationUserModelId);
        var notifier = IntPtr.Zero;
        WindowsRuntime.ThrowIfFailed(
            GetDelegate<CreateNotifierWithIdDelegate>(manager.Value, 7)(
                manager.Value,
                appUserModelId.Value,
                out notifier),
            "IToastNotificationManagerStatics.CreateToastNotifierWithId");
        return new ComReference(notifier);
    }

    private static ComReference CreateToastDocument(NotificationChannelEffectRequest request)
    {
        using var documentObjectClass = WindowsRuntimeString.Create("Windows.Data.Xml.Dom.XmlDocument");
        var documentObject = IntPtr.Zero;
        WindowsRuntime.ThrowIfFailed(
            WindowsRuntime.RoActivateInstance(documentObjectClass.Value, out documentObject),
            "RoActivateInstance(XmlDocument)");
        using var documentInspectable = new ComReference(documentObject);
        using var document = documentInspectable.QueryInterface(XmlDocumentIoIid);
        using var xml = WindowsRuntimeString.Create(BuildToastXml(request));
        WindowsRuntime.ThrowIfFailed(
            GetDelegate<LoadXmlDelegate>(document.Value, 6)(document.Value, xml.Value),
            "IXmlDocumentIO.LoadXml");
        return documentInspectable.QueryInterface(XmlDocumentIid);
    }

    private static ComReference CreateToast(ComReference factory, ComReference document)
    {
        var toast = IntPtr.Zero;
        WindowsRuntime.ThrowIfFailed(
            GetDelegate<CreateToastDelegate>(factory.Value, 6)(
                factory.Value,
                document.Value,
                out toast),
            "IToastNotificationFactory.CreateToastNotification");
        return new ComReference(toast);
    }

    private static ComReference GetActivationFactory(string className, Guid interfaceId)
    {
        using var classNameString = WindowsRuntimeString.Create(className);
        var factory = IntPtr.Zero;
        WindowsRuntime.ThrowIfFailed(
            WindowsRuntime.RoGetActivationFactory(
                classNameString.Value,
                ref interfaceId,
                out factory),
            $"RoGetActivationFactory({className})");
        return new ComReference(factory);
    }

    private static string BuildToastXml(NotificationChannelEffectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var summary = request.Summary;
        var launch = summary is { Count: > 1 }
            ? ReminderNotificationActivation.CreateSummaryUri(summary.LogicalReminderIds)
            : ReminderNotificationActivation.CreateSingleUri(request.LogicalReminderId);
        var message = summary is { Count: > 1 }
            ? $"有 {summary.Count} 项提醒需要查看。"
            : "有一项提醒需要查看。";
        // Explicit protocol activation makes the launch URI a ShellExecute
        // request. The App's protocol registration starts/forwards the URI
        // through the profile-scoped single-instance pipe, so this path works
        // both for an existing Main window and for a cold launch.
        return $"<toast activationType=\"protocol\" launch=\"{launch}\"><visual><binding template=\"ToastGeneric\"><text>ReminNote</text><text>{message}</text></binding></visual></toast>";
    }

    private static NotificationChannelStatus StatusFromException(
        Instant observedAtUtc,
        Exception exception) =>
        WindowsRuntime.IsAccessDenied(exception)
            ? WindowsNotificationResponse.Status(
                NotificationChannelId.Toast,
                ToastCapabilities,
                NotificationChannelHealth.BLOCKED,
                observedAtUtc,
                WindowsNotificationResponse.ToastBlocked)
            : WindowsRuntime.IsUnavailable(exception)
                ? WindowsNotificationResponse.Status(
                    NotificationChannelId.Toast,
                    ToastCapabilities,
                    NotificationChannelHealth.UNAVAILABLE,
                    observedAtUtc,
                    WindowsNotificationResponse.ToastUnavailable)
                : WindowsNotificationResponse.Status(
                    NotificationChannelId.Toast,
                    ToastCapabilities,
                    NotificationChannelHealth.UNKNOWN,
                    observedAtUtc,
                    WindowsNotificationResponse.ToastFailed);

    private static NotificationChannelDeliveryResponse StatusFromException(
        Exception exception) =>
        WindowsRuntime.IsAccessDenied(exception)
            ? WindowsNotificationResponse.Blocked(WindowsNotificationResponse.ToastBlocked)
            : WindowsRuntime.IsUnavailable(exception)
                ? WindowsNotificationResponse.Unavailable(WindowsNotificationResponse.ToastUnavailable)
                : WindowsNotificationResponse.Failed(WindowsNotificationResponse.ToastFailed);

    private static T GetDelegate<T>(IntPtr instance, int slot)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(WindowsRuntime.GetVtableEntry(instance, slot));

    private static readonly Guid ToastNotificationManagerStaticsIid =
        new("50ac103f-d235-4598-bbef-98fe4d1a3ad4");
    private static readonly Guid ToastNotificationFactoryIid =
        new("04124b20-82c6-4229-b109-fd9ed4662b53");
    private static readonly Guid ToastNotification2Iid =
        new("9dfb9fd1-143a-490e-90bf-b9fba7132de7");
    private static readonly Guid XmlDocumentIid =
        new("f7f3a506-1e87-42d6-bcfb-b8c809fa5494");
    private static readonly Guid XmlDocumentIoIid =
        new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateNotifierWithIdDelegate(
        IntPtr self,
        IntPtr applicationUserModelId,
        out IntPtr notifier);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateToastDelegate(
        IntPtr self,
        IntPtr document,
        out IntPtr toast);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LoadXmlDelegate(IntPtr self, IntPtr xml);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutStringDelegate(IntPtr self, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShowToastDelegate(IntPtr self, IntPtr toast);
}

/// <summary>
/// Host-owned Shell_NotifyIcon presenter. The stable native slot is the
/// replacement identity; each request also retains the logical key for
/// diagnostics while the core attempt remains the source of truth.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrayNotificationEffectSink : INotificationChannelEffectSink, IDisposable
{
    private const uint NotifyIconId = 1;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NotifyIconIcon = 0x00000002;
    private const uint NotifyIconTip = 0x00000004;
    private const uint NotifyIconGuid = 0x00000080;

    private static readonly IReadOnlyList<NotificationCapability> TrayCapabilities =
    [
        NotificationCapability.PRESENT,
        NotificationCapability.REPLACE_LOGICAL_REMINDER,
    ];

    private readonly WindowsWindowHandleResolver windowResolver;
    private readonly object gate = new();
    private IntPtr registeredWindow;
    private Guid registeredLogicalReminderId;
    private bool iconRegistered;
    private bool disposed;
    private string? lastLogicalReplacementKey;

    public WindowsTrayNotificationEffectSink(
        Func<Window?> mainWindowProvider,
        Dispatcher dispatcher)
    {
        windowResolver = new WindowsWindowHandleResolver(mainWindowProvider, dispatcher);
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
            return WindowsNotificationResponse.Status(
                NotificationChannelId.Tray,
                TrayCapabilities,
                NotificationChannelHealth.UNAVAILABLE,
                observedAtUtc,
                WindowsNotificationResponse.TrayUnavailable);
        }

        lock (gate)
        {
            if (disposed)
            {
                return WindowsNotificationResponse.Status(
                    NotificationChannelId.Tray,
                    TrayCapabilities,
                    NotificationChannelHealth.UNAVAILABLE,
                    observedAtUtc,
                    WindowsNotificationResponse.HostDisposed);
            }
        }

        return windowResolver.GetSnapshot().Handle == IntPtr.Zero
            ? WindowsNotificationResponse.Status(
                NotificationChannelId.Tray,
                TrayCapabilities,
                NotificationChannelHealth.UNAVAILABLE,
                observedAtUtc,
                WindowsNotificationResponse.TrayWindowUnavailable)
            : WindowsNotificationResponse.Status(
                NotificationChannelId.Tray,
                TrayCapabilities,
                NotificationChannelHealth.HEALTHY,
                observedAtUtc,
                "windows.tray.ready");
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
                WindowsNotificationResponse.Unavailable(
                    WindowsNotificationResponse.TrayUnavailable));
        }

        try
        {
            var snapshot = windowResolver.GetSnapshot();
            if (snapshot.Handle == IntPtr.Zero || !snapshot.IsVisible)
            {
                return ValueTask.FromResult(
                    WindowsNotificationResponse.Unavailable(
                        WindowsNotificationResponse.TrayWindowUnavailable));
            }

            lock (gate)
            {
                if (disposed)
                {
                    return ValueTask.FromResult(
                        WindowsNotificationResponse.Unavailable(
                            WindowsNotificationResponse.HostDisposed));
                }

                var icon = WindowsNative.LoadApplicationIcon();
                if (icon == IntPtr.Zero)
                {
                    return ValueTask.FromResult(
                        WindowsNative.ResponseFromLastError(
                            WindowsNotificationResponse.TrayUnavailable,
                            WindowsNotificationResponse.TrayBlocked,
                            WindowsNotificationResponse.TrayFailed));
                }

                if (iconRegistered &&
                    (registeredWindow != snapshot.Handle ||
                     registeredLogicalReminderId != request.LogicalReminderId))
                {
                    _ = WindowsNative.ShellNotifyIcon(
                        NimDelete,
                        CreateNotifyIconData(
                            registeredWindow,
                            icon,
                            registeredLogicalReminderId));
                    iconRegistered = false;
                    registeredWindow = IntPtr.Zero;
                    registeredLogicalReminderId = Guid.Empty;
                }

                // Toast is the single user-facing popup.  Keep TRAY as a
                // passive presence/health channel so one logical reminder
                // cannot surface as a second balloon after the Toast closes.
                var data = CreateNotifyIconData(
                    snapshot.Handle,
                    icon,
                    request.LogicalReminderId);
                var message = iconRegistered ? NimModify : NimAdd;
                if (!WindowsNative.ShellNotifyIcon(message, data))
                {
                    var firstFailure = WindowsNative.ResponseFromLastError(
                        WindowsNotificationResponse.TrayUnavailable,
                        WindowsNotificationResponse.TrayBlocked,
                        WindowsNotificationResponse.TrayFailed);
                    if (message == NimModify && firstFailure.Outcome == NotificationDeliveryOutcome.UNAVAILABLE)
                    {
                        iconRegistered = false;
                        registeredWindow = IntPtr.Zero;
                        data = CreateNotifyIconData(
                            snapshot.Handle,
                            icon,
                            request.LogicalReminderId);
                        if (WindowsNative.ShellNotifyIcon(NimAdd, data))
                        {
                            iconRegistered = true;
                        }
                        else
                        {
                            return ValueTask.FromResult(
                                WindowsNative.ResponseFromLastError(
                                    WindowsNotificationResponse.TrayUnavailable,
                                    WindowsNotificationResponse.TrayBlocked,
                                    WindowsNotificationResponse.TrayFailed));
                        }
                    }
                    else
                    {
                        return ValueTask.FromResult(firstFailure);
                    }
                }
                else
                {
                    iconRegistered = true;
                }

                registeredWindow = snapshot.Handle;
                registeredLogicalReminderId = request.LogicalReminderId;
                lastLogicalReplacementKey = request.LogicalReplacementKey;
                return ValueTask.FromResult(WindowsNotificationResponse.Delivered());
            }
        }
        catch (DllNotFoundException)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Unavailable(
                    WindowsNotificationResponse.TrayUnavailable));
        }
        catch (EntryPointNotFoundException)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Unavailable(
                    WindowsNotificationResponse.TrayUnavailable));
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Blocked(
                    WindowsNotificationResponse.TrayBlocked));
        }
        catch (Exception)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Failed(
                    WindowsNotificationResponse.TrayFailed));
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
            if (iconRegistered && registeredWindow != IntPtr.Zero)
            {
                var data = CreateNotifyIconData(
                    registeredWindow,
                    IntPtr.Zero,
                    registeredLogicalReminderId);
                _ = WindowsNative.ShellNotifyIcon(NimDelete, data);
            }

            iconRegistered = false;
            registeredWindow = IntPtr.Zero;
            registeredLogicalReminderId = Guid.Empty;
        }

        GC.SuppressFinalize(this);
    }

    private static WindowsNative.NotifyIconData CreateNotifyIconData(
        IntPtr windowHandle,
        IntPtr icon,
        Guid logicalReminderId)
    {
        return new WindowsNative.NotifyIconData
        {
            CbSize = (uint)Marshal.SizeOf<WindowsNative.NotifyIconData>(),
            HWnd = windowHandle,
            UId = NotifyIconId,
            UFlags = NotifyIconGuid | NotifyIconIcon | NotifyIconTip,
            HIcon = icon,
            SzTip = "ReminNote",
            SzInfo = string.Empty,
            UTimeoutOrVersion = 5_000,
            SzInfoTitle = string.Empty,
            DwInfoFlags = 0,
            GuidItem = logicalReminderId,
        };
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsSoundNotificationEffectSink : INotificationChannelEffectSink
{
    private static readonly IReadOnlyList<NotificationCapability> SoundCapabilities =
    [NotificationCapability.PRESENT];

    public static NotificationChannelStatus GetStatus(Instant observedAtUtc) =>
        WindowsNotificationResponse.Status(
            NotificationChannelId.Sound,
            SoundCapabilities,
            OperatingSystem.IsWindows()
                ? NotificationChannelHealth.HEALTHY
                : NotificationChannelHealth.UNAVAILABLE,
            observedAtUtc,
            OperatingSystem.IsWindows()
                ? "windows.sound.ready"
                : WindowsNotificationResponse.SoundUnavailable);

    public ValueTask<NotificationChannelDeliveryResponse> PresentAsync(
        NotificationChannelEffectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Unavailable(
                    WindowsNotificationResponse.SoundUnavailable));
        }

        try
        {
            System.Media.SystemSounds.Exclamation.Play();
            return ValueTask.FromResult(WindowsNotificationResponse.Delivered());
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Blocked(WindowsNotificationResponse.SoundBlocked));
        }
        catch (PlatformNotSupportedException)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Unavailable(WindowsNotificationResponse.SoundUnavailable));
        }
        catch (Exception)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Failed(WindowsNotificationResponse.SoundFailed));
        }
    }
}

/// <summary>
/// Schedules a real Windows waitable timer with fResume=true. The future
/// schedule instant is supplied separately from the recorded trigger instant,
/// and a replacement key owns at most one live timer registration.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWakeTimerNotificationEffectSink : INotificationChannelEffectSink, IDisposable
{
    private const uint TimerModifyState = 0x0002;
    private const uint Synchronize = 0x00100000;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSupported = 50;
    private const int ErrorFileNotFound = 2;
    private const int ErrorInvalidFunction = 1;
    private const int MaximumLiveTimers = 128;
    private const long WindowsEpochTicks = 116_444_736_000_000_000L;

    private static readonly IReadOnlyList<NotificationCapability> WakeTimerCapabilities =
    [
        NotificationCapability.PRESENT,
        NotificationCapability.WAKE,
    ];

    private readonly IClock clock;
    private readonly object gate = new();
    private readonly Dictionary<string, IntPtr> timers = new(StringComparer.Ordinal);
    private bool disposed;

    public WindowsWakeTimerNotificationEffectSink(IClock clock)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public int LiveTimerCount
    {
        get
        {
            lock (gate)
            {
                return timers.Count;
            }
        }
    }

    public NotificationChannelStatus GetStatus(Instant observedAtUtc)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WindowsNotificationResponse.Status(
                NotificationChannelId.WakeTimer,
                WakeTimerCapabilities,
                NotificationChannelHealth.UNAVAILABLE,
                observedAtUtc,
                WindowsNotificationResponse.WakeTimerUnavailable);
        }

        lock (gate)
        {
            if (disposed)
            {
                return WindowsNotificationResponse.Status(
                    NotificationChannelId.WakeTimer,
                    WakeTimerCapabilities,
                    NotificationChannelHealth.UNAVAILABLE,
                    observedAtUtc,
                    WindowsNotificationResponse.HostDisposed);
            }
        }

        IntPtr timer = IntPtr.Zero;
        try
        {
            timer = CreateTimer();
            if (timer == IntPtr.Zero)
            {
                return StatusFromWin32Error(observedAtUtc, Marshal.GetLastWin32Error());
            }

            var dueTime = -TimeSpan.FromMinutes(1).Ticks;
            Marshal.SetLastPInvokeError(0);
            if (!SetWaitableTimer(timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, true))
            {
                return StatusFromWin32Error(observedAtUtc, Marshal.GetLastWin32Error());
            }

            var setError = Marshal.GetLastWin32Error();
            if (setError == ErrorNotSupported)
            {
                _ = CancelWaitableTimer(timer);
                return WindowsNotificationResponse.Status(
                    NotificationChannelId.WakeTimer,
                    WakeTimerCapabilities,
                    NotificationChannelHealth.UNAVAILABLE,
                    observedAtUtc,
                    WindowsNotificationResponse.WakeTimerUnavailable);
            }

            if (!CancelWaitableTimer(timer))
            {
                return StatusFromWin32Error(observedAtUtc, Marshal.GetLastWin32Error());
            }

            return WindowsNotificationResponse.Status(
                NotificationChannelId.WakeTimer,
                WakeTimerCapabilities,
                NotificationChannelHealth.HEALTHY,
                observedAtUtc,
                "windows.wake_timer.ready");
        }
        catch (DllNotFoundException)
        {
            return WindowsNotificationResponse.Status(
                NotificationChannelId.WakeTimer,
                WakeTimerCapabilities,
                NotificationChannelHealth.UNAVAILABLE,
                observedAtUtc,
                WindowsNotificationResponse.WakeTimerUnavailable);
        }
        catch (EntryPointNotFoundException)
        {
            return WindowsNotificationResponse.Status(
                NotificationChannelId.WakeTimer,
                WakeTimerCapabilities,
                NotificationChannelHealth.UNAVAILABLE,
                observedAtUtc,
                WindowsNotificationResponse.WakeTimerUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return WindowsNotificationResponse.Status(
                NotificationChannelId.WakeTimer,
                WakeTimerCapabilities,
                NotificationChannelHealth.BLOCKED,
                observedAtUtc,
                WindowsNotificationResponse.WakeTimerBlocked);
        }
        catch (Exception)
        {
            return WindowsNotificationResponse.Status(
                NotificationChannelId.WakeTimer,
                WakeTimerCapabilities,
                NotificationChannelHealth.UNKNOWN,
                observedAtUtc,
                WindowsNotificationResponse.WakeTimerFailed);
        }
        finally
        {
            CloseTimer(timer);
        }
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
                WindowsNotificationResponse.Unavailable(
                    WindowsNotificationResponse.WakeTimerUnavailable));
        }

        if (request.ScheduledTriggerAtUtc is not { } scheduledAtUtc)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.NotAttempted(
                    WindowsNotificationResponse.WakeTimerScheduleMissing));
        }

        if (scheduledAtUtc <= clock.GetCurrentInstant())
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.NotAttempted(
                    WindowsNotificationResponse.WakeTimerSchedulePast));
        }

        if (!TryGetAbsoluteDueTime(scheduledAtUtc, out var dueTime))
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.NotAttempted(
                    WindowsNotificationResponse.WakeTimerSchedulePast));
        }

        try
        {
            return ScheduleTimer(request, dueTime, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Unavailable(
                    WindowsNotificationResponse.WakeTimerUnavailable));
        }
        catch (EntryPointNotFoundException)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Unavailable(
                    WindowsNotificationResponse.WakeTimerUnavailable));
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Blocked(
                    WindowsNotificationResponse.WakeTimerBlocked));
        }
        catch (Exception)
        {
            return ValueTask.FromResult(
                WindowsNotificationResponse.Failed(
                    WindowsNotificationResponse.WakeTimerFailed));
        }
    }

    private ValueTask<NotificationChannelDeliveryResponse> ScheduleTimer(
        NotificationChannelEffectRequest request,
        long dueTime,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (disposed)
            {
                return ValueTask.FromResult(
                    WindowsNotificationResponse.Unavailable(
                        WindowsNotificationResponse.HostDisposed));
            }

            if (!timers.ContainsKey(request.LogicalReplacementKey) &&
                timers.Count >= MaximumLiveTimers)
            {
                return ValueTask.FromResult(
                    WindowsNotificationResponse.NotAttempted(
                        WindowsNotificationResponse.WakeTimerCapacity));
            }
        }

        var timer = CreateTimer();
        var registered = false;
        try
        {
            if (timer == IntPtr.Zero)
            {
                return ValueTask.FromResult(
                    ResponseFromWin32Error(Marshal.GetLastWin32Error()));
            }

            Marshal.SetLastPInvokeError(0);
            if (!SetWaitableTimer(timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, true))
            {
                return ValueTask.FromResult(
                    ResponseFromWin32Error(Marshal.GetLastWin32Error()));
            }

            if (Marshal.GetLastWin32Error() == ErrorNotSupported)
            {
                _ = CancelWaitableTimer(timer);
                return ValueTask.FromResult(
                    WindowsNotificationResponse.Unavailable(
                        WindowsNotificationResponse.WakeTimerUnavailable));
            }

            cancellationToken.ThrowIfCancellationRequested();
            IntPtr oldTimer = IntPtr.Zero;
            lock (gate)
            {
                if (disposed)
                {
                    return ValueTask.FromResult(
                        WindowsNotificationResponse.Unavailable(
                            WindowsNotificationResponse.HostDisposed));
                }

                if (!timers.ContainsKey(request.LogicalReplacementKey) &&
                    timers.Count >= MaximumLiveTimers)
                {
                    return ValueTask.FromResult(
                        WindowsNotificationResponse.NotAttempted(
                            WindowsNotificationResponse.WakeTimerCapacity));
                }

                timers.TryGetValue(request.LogicalReplacementKey, out oldTimer);
                timers[request.LogicalReplacementKey] = timer;
                registered = true;
            }

            CloseTimer(oldTimer);
            return ValueTask.FromResult(WindowsNotificationResponse.Delivered());
        }
        finally
        {
            if (!registered)
            {
                CloseTimer(timer);
            }
        }
    }

    public void Dispose()
    {
        IntPtr[] liveTimers;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            liveTimers = timers.Values.ToArray();
            timers.Clear();
        }

        foreach (var timer in liveTimers)
        {
            CloseTimer(timer);
        }

        GC.SuppressFinalize(this);
    }

    private static IntPtr CreateTimer() =>
        CreateWaitableTimerExW(
            IntPtr.Zero,
            null,
            0,
            TimerModifyState | Synchronize);

    private static bool TryGetAbsoluteDueTime(Instant scheduledAtUtc, out long dueTime)
    {
        try
        {
            dueTime = checked(scheduledAtUtc.ToUnixTimeTicks() + WindowsEpochTicks);
            return dueTime > 0;
        }
        catch (OverflowException)
        {
            dueTime = 0;
            return false;
        }
    }

    private static NotificationChannelStatus StatusFromWin32Error(
        Instant observedAtUtc,
        int error) =>
        error == ErrorAccessDenied
            ? WindowsNotificationResponse.Status(
                NotificationChannelId.WakeTimer,
                WakeTimerCapabilities,
                NotificationChannelHealth.BLOCKED,
                observedAtUtc,
                WindowsNotificationResponse.WakeTimerBlocked)
            : WindowsNotificationResponse.Status(
                NotificationChannelId.WakeTimer,
                WakeTimerCapabilities,
                NotificationChannelHealth.UNAVAILABLE,
                observedAtUtc,
                error is ErrorNotSupported or ErrorInvalidFunction or ErrorFileNotFound
                    ? WindowsNotificationResponse.WakeTimerUnavailable
                    : WindowsNotificationResponse.WakeTimerFailed);

    private static NotificationChannelDeliveryResponse ResponseFromWin32Error(int error) =>
        error == ErrorAccessDenied
            ? WindowsNotificationResponse.Blocked(WindowsNotificationResponse.WakeTimerBlocked)
            : error is ErrorNotSupported or ErrorInvalidFunction or ErrorFileNotFound
                ? WindowsNotificationResponse.Unavailable(WindowsNotificationResponse.WakeTimerUnavailable)
                : WindowsNotificationResponse.Failed(WindowsNotificationResponse.WakeTimerFailed);

    private static void CloseTimer(IntPtr timer)
    {
        if (timer != IntPtr.Zero)
        {
            _ = CloseHandle(timer);
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr timerAttributes,
        [MarshalAs(UnmanagedType.LPWStr)] string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        IntPtr timer,
        ref long dueTime,
        int period,
        IntPtr completionRoutine,
        IntPtr completionRoutineArgument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(IntPtr timer);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

[SupportedOSPlatform("windows")]
internal static class WindowsNative
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSupported = 50;
    private const int ErrorFileNotFound = 2;

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr resource);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    public static IntPtr LoadApplicationIcon() =>
        LoadIconW(IntPtr.Zero, (IntPtr)32512);

    public static bool ShellNotifyIcon(uint message, NotifyIconData data) =>
        Shell_NotifyIconW(message, ref data);

    public static NotificationChannelDeliveryResponse ResponseFromLastError(
        string unavailableCode,
        string blockedCode,
        string failedCode)
    {
        var error = Marshal.GetLastWin32Error();
        return error == ErrorAccessDenied
            ? WindowsNotificationResponse.Blocked(blockedCode)
            : error is ErrorNotSupported or ErrorFileNotFound
                ? WindowsNotificationResponse.Unavailable(unavailableCode)
                : WindowsNotificationResponse.Failed(failedCode);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public uint CbSize;
        public IntPtr HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public IntPtr HIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SzTip;

        public uint DwState;
        public uint DwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string SzInfo;

        public uint UTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SzInfoTitle;

        public uint DwInfoFlags;
        public Guid GuidItem;
        public IntPtr HBalloonIcon;
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsRuntime
{
    private const int SOk = 0;
    private const int SFalse = 1;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private const int ErrorNotFound = unchecked((int)0x80070490);
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int RegDbEClassNotReg = unchecked((int)0x80040154);

    public static IntPtr GetVtableEntry(IntPtr instance, int slot)
    {
        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    public static int RoInitialize(uint initType) => RoInitializeNative(initType);

    public static void RoUninitialize() => RoUninitializeNative();

    public static int RoGetActivationFactory(
        IntPtr classId,
        ref Guid interfaceId,
        out IntPtr factory) =>
        RoGetActivationFactoryNative(classId, ref interfaceId, out factory);

    public static int RoActivateInstance(IntPtr classId, out IntPtr instance) =>
        RoActivateInstanceNative(classId, out instance);

    public static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new WindowsRuntimeCallException(operation, hresult);
        }
    }

    public static bool IsAccessDenied(Exception exception) =>
        GetHResult(exception) == EAccessDenied;

    public static bool IsUnavailable(Exception exception)
    {
        if (exception is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return true;
        }

        var hresult = GetHResult(exception);
        return hresult is ErrorFileNotFound or ErrorNotFound or ENoInterface or RegDbEClassNotReg;
    }

    private static int GetHResult(Exception exception) =>
        exception is COMException comException
            ? comException.HResult
            : Marshal.GetHRForException(exception);

    private sealed class WindowsRuntimeCallException : Exception
    {
        public WindowsRuntimeCallException(string operation, int hresult)
            : base(operation)
        {
            HResult = hresult;
        }
    }

    [DllImport("combase.dll", ExactSpelling = true, EntryPoint = "RoInitialize")]
    private static extern int RoInitializeNative(uint initType);

    [DllImport("combase.dll", ExactSpelling = true, EntryPoint = "RoUninitialize")]
    private static extern void RoUninitializeNative();

    [DllImport("combase.dll", ExactSpelling = true, EntryPoint = "RoGetActivationFactory")]
    private static extern int RoGetActivationFactoryNative(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, EntryPoint = "RoActivateInstance")]
    private static extern int RoActivateInstanceNative(
        IntPtr activatableClassId,
        out IntPtr instance);

    public static bool IsInitializedByThisScope(int hresult) =>
        hresult is SOk or SFalse;

    public static bool IsAlreadyInitialized(int hresult) => hresult == RpcEChangedMode;
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsRuntimeScope : IDisposable
{
    private readonly bool initialized;
    private bool disposed;

    private WindowsRuntimeScope(bool initialized)
    {
        this.initialized = initialized;
    }

    public static WindowsRuntimeScope Enter()
    {
        var hresult = WindowsRuntime.RoInitialize(1);
        if (WindowsRuntime.IsInitializedByThisScope(hresult))
        {
            return new WindowsRuntimeScope(initialized: true);
        }

        if (WindowsRuntime.IsAlreadyInitialized(hresult))
        {
            return new WindowsRuntimeScope(initialized: false);
        }

        WindowsRuntime.ThrowIfFailed(hresult, "RoInitialize");
        return new WindowsRuntimeScope(initialized: false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (initialized)
        {
            WindowsRuntime.RoUninitialize();
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsRuntimeString : IDisposable
{
    private bool disposed;

    private WindowsRuntimeString(IntPtr value)
    {
        Value = value;
    }

    public IntPtr Value { get; private set; }

    public static WindowsRuntimeString Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hresult = WindowsCreateString(value, (uint)value.Length, out var stringHandle);
        WindowsRuntime.ThrowIfFailed(hresult, "WindowsCreateString");
        return new WindowsRuntimeString(stringHandle);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (Value != IntPtr.Zero)
        {
            _ = WindowsDeleteString(Value);
            Value = IntPtr.Zero;
        }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out IntPtr stringHandle);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr stringHandle);
}

[SupportedOSPlatform("windows")]
internal sealed class ComReference : IDisposable
{
    private bool disposed;

    public ComReference(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            throw new ArgumentException("A COM reference cannot be null.", nameof(value));
        }

        Value = value;
    }

    public IntPtr Value { get; private set; }

    public ComReference QueryInterface(Guid interfaceId)
    {
        var result = IntPtr.Zero;
        var hresult = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
            WindowsRuntime.GetVtableEntry(Value, 0))(Value, ref interfaceId, out result);
        WindowsRuntime.ThrowIfFailed(hresult, "IUnknown.QueryInterface");
        return new ComReference(result);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (Value != IntPtr.Zero)
        {
            _ = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                WindowsRuntime.GetVtableEntry(Value, 2))(Value);
            Value = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(
        IntPtr self,
        ref Guid interfaceId,
        out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr self);
}
