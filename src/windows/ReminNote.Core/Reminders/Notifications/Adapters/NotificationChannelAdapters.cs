using System.Collections.ObjectModel;
using System.Globalization;
using NodaTime;

#pragma warning disable CA1707 // Frozen channel/effect codes use uppercase underscore names.
namespace ReminNote.Core.Reminders.Notifications.Adapters;

/// <summary>
/// The external effect requested by a channel adapter. TASKBAR_FLASH is a
/// presentation effect of the frozen TRAY channel; it is deliberately not a
/// new persisted channel identity.
/// </summary>
public enum NotificationSurfaceKind
{
    TOAST,
    TRAY,
    WIDGET,
    TASKBAR_FLASH,
    SOUND,
    WAKE_TIMER,
}

/// <summary>
/// The capability matrix for the P3 channel adapters. Capabilities describe
/// what an adapter can do; health is supplied separately by its status source.
/// </summary>
public static class NotificationChannelCapabilityMatrix
{
    public static IReadOnlyList<NotificationCapability> Toast { get; } =
        ReadOnly(
            NotificationCapability.PRESENT,
            NotificationCapability.REPLACE_LOGICAL_REMINDER,
            NotificationCapability.READ_ON_CLOSE);

    public static IReadOnlyList<NotificationCapability> Tray { get; } =
        ReadOnly(
            NotificationCapability.PRESENT,
            NotificationCapability.REPLACE_LOGICAL_REMINDER);

    public static IReadOnlyList<NotificationCapability> Widget { get; } =
        ReadOnly(
            NotificationCapability.PRESENT,
            NotificationCapability.REPLACE_LOGICAL_REMINDER,
            NotificationCapability.READ_ON_CLOSE);

    public static IReadOnlyList<NotificationCapability> Sound { get; } =
        ReadOnly(NotificationCapability.PRESENT);

    // WAKE_TIMER is an external effect channel, so PRESENT means that the
    // effect can be handed to the injected sink. WAKE is its product
    // capability; the P3-04 coordinator still gets its common PRESENT gate.
    public static IReadOnlyList<NotificationCapability> WakeTimer { get; } =
        ReadOnly(NotificationCapability.PRESENT, NotificationCapability.WAKE);

    public static IReadOnlyList<NotificationCapability> For(
        NotificationChannelId channelId)
    {
        NotificationChannels.RequireKnown(channelId);
        return channelId.Value switch
        {
            "TOAST" => Toast,
            "TRAY" => Tray,
            "WIDGET" => Widget,
            "SOUND" => Sound,
            "WAKE_TIMER" => WakeTimer,
            _ => throw new InvalidOperationException(
                $"No capability matrix exists for '{channelId.Value}'."),
        };
    }

    private static ReadOnlyCollection<NotificationCapability> ReadOnly(
        params NotificationCapability[] capabilities) =>
        new ReadOnlyCollection<NotificationCapability>(capabilities);
}

/// <summary>
/// Supplies a fresh health snapshot when the host wants to probe its external
/// surface. The source performs no probing by itself; the host owns that I/O.
/// </summary>
public interface INotificationChannelStatusSource
{
    NotificationChannelStatus GetStatus();
}

public sealed class FixedNotificationChannelStatusSource : INotificationChannelStatusSource
{
    public FixedNotificationChannelStatusSource(NotificationChannelStatus status)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public NotificationChannelStatus Status { get; }

    public NotificationChannelStatus GetStatus() => Status;
}

public sealed class DelegateNotificationChannelStatusSource : INotificationChannelStatusSource
{
    private readonly Func<NotificationChannelStatus> getStatus;

    public DelegateNotificationChannelStatusSource(
        Func<NotificationChannelStatus> getStatus)
    {
        this.getStatus = getStatus ?? throw new ArgumentNullException(nameof(getStatus));
    }

    public NotificationChannelStatus GetStatus() =>
        getStatus() ?? throw new InvalidOperationException(
            "The notification channel status source returned null.");
}

/// <summary>
/// A safe default status for a channel whose OS capability has not been
/// verified. It deliberately advertises the adapter's shape but reports
/// UNAVAILABLE, so dispatch cannot be mistaken for a successful presentation.
/// </summary>
public static class NotificationChannelAdapterDefaults
{
    public static NotificationChannelStatus SafeUnavailableStatus(
        NotificationChannelId channelId,
        Instant observedAtUtc,
        string? healthCode = null)
    {
        var code = healthCode ?? NotificationErrorCodes.ChannelUnavailable;
        return new NotificationChannelStatus(
            channelId,
            NotificationChannelCapabilityMatrix.For(channelId),
            NotificationChannelHealth.UNAVAILABLE,
            observedAtUtc,
            code);
    }
}

/// <summary>
/// A bounded, text-free request to an external surface. Display text belongs
/// to the host/read-model layer and is intentionally absent here.
/// </summary>
public sealed record NotificationChannelEffectRequest
{
    public NotificationChannelEffectRequest(
        NotificationDeliveryRequest deliveryRequest,
        NotificationSurfaceKind surfaceKind)
    {
        DeliveryRequest = deliveryRequest ?? throw new ArgumentNullException(nameof(deliveryRequest));
        if (!Enum.IsDefined(surfaceKind))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "Notification surface kind is not supported.",
                nameof(surfaceKind));
        }

        if (surfaceKind == NotificationSurfaceKind.TASKBAR_FLASH &&
            deliveryRequest.ChannelId != NotificationChannelId.Tray)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.ChannelIdentityMismatch,
                "Taskbar flash is a TRAY presentation effect.",
                nameof(surfaceKind));
        }

        SurfaceKind = surfaceKind;
    }

    public NotificationDeliveryRequest DeliveryRequest { get; }

    public NotificationSurfaceKind SurfaceKind { get; }

    public NotificationChannelId ChannelId => DeliveryRequest.ChannelId;

    public Guid InstanceId => DeliveryRequest.CoreTrigger.InstanceId;

    public Guid LogicalReminderId => DeliveryRequest.CoreTrigger.LogicalReminderId;

    public Guid CorrelationId => DeliveryRequest.CorrelationId;

    public Guid IdempotencyKey => DeliveryRequest.IdempotencyKey;

    public NotificationPurposeSnapshot PurposeSnapshot =>
        DeliveryRequest.CoreTrigger.PurposeSnapshot;

    public NotificationPriority PrioritySnapshot =>
        DeliveryRequest.CoreTrigger.PrioritySnapshot;

    public bool PinnedSnapshot => DeliveryRequest.CoreTrigger.PinnedSnapshot;

    public Instant TriggeredAtUtc => DeliveryRequest.CoreTrigger.TriggeredAtUtc;

    /// <summary>
    /// Stable replace/update key for Toast, Tray and Widget presenters. It is
    /// the logical reminder identity, never a window handle or process ID.
    /// </summary>
    public string LogicalReplacementKey =>
        LogicalReminderId.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>
/// Host-owned effect port. A production host can later implement this with a
/// Windows API, while tests and safe startup use a fake or unavailable sink.
/// </summary>
public interface INotificationChannelEffectSink
{
    ValueTask<NotificationChannelDeliveryResponse> PresentAsync(
        NotificationChannelEffectRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A deterministic sink for tests and unverified environments. It performs no
/// OS call and cannot report a false delivery.
/// </summary>
public sealed class UnavailableNotificationChannelEffectSink : INotificationChannelEffectSink
{
    public ValueTask<NotificationChannelDeliveryResponse> PresentAsync(
        NotificationChannelEffectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(
            new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.UNAVAILABLE,
                NotificationErrorCodes.ChannelUnavailable));
    }
}

/// <summary>
/// Small injection helper for a fake or a host-owned presenter. It keeps the
/// adapter independent from WPF, WinRT and audio APIs.
/// </summary>
public sealed class DelegateNotificationChannelEffectSink : INotificationChannelEffectSink
{
    private readonly Func<
        NotificationChannelEffectRequest,
        CancellationToken,
        ValueTask<NotificationChannelDeliveryResponse>> present;

    public DelegateNotificationChannelEffectSink(
        Func<
            NotificationChannelEffectRequest,
            CancellationToken,
            ValueTask<NotificationChannelDeliveryResponse>> present)
    {
        this.present = present ?? throw new ArgumentNullException(nameof(present));
    }

    public ValueTask<NotificationChannelDeliveryResponse> PresentAsync(
        NotificationChannelEffectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return present(request, cancellationToken);
    }
}

/// <summary>
/// Common fail-closed implementation for every frozen P3 channel. It checks
/// the adapter's identity and current health before invoking the injected
/// external effect sink. It never writes Rule, Schedule or Instance facts.
/// </summary>
public abstract class NotificationChannelAdapter : INotificationChannel
{
    private readonly INotificationChannelStatusSource statusSource;
    private readonly INotificationChannelEffectSink effectSink;
    private readonly IClock clock;

    protected NotificationChannelAdapter(
        NotificationChannelId channelId,
        NotificationSurfaceKind surfaceKind,
        IReadOnlyList<NotificationCapability> expectedCapabilities,
        INotificationChannelStatusSource statusSource,
        INotificationChannelEffectSink effectSink,
        IClock clock)
    {
        ChannelId = NotificationChannels.RequireKnown(channelId);
        if (!Enum.IsDefined(surfaceKind))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "Notification surface kind is not supported.",
                nameof(surfaceKind));
        }

        ArgumentNullException.ThrowIfNull(expectedCapabilities);
        if (expectedCapabilities.Count == 0 ||
            expectedCapabilities.Count > NotificationContractLimits.MaxCapabilityCount ||
            expectedCapabilities.Distinct().Count() != expectedCapabilities.Count ||
            expectedCapabilities.Any(capability => !Enum.IsDefined(capability)))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "Adapter capabilities must be a bounded list of distinct known values.",
                nameof(expectedCapabilities));
        }

        this.statusSource = statusSource ?? throw new ArgumentNullException(nameof(statusSource));
        this.effectSink = effectSink ?? throw new ArgumentNullException(nameof(effectSink));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        SurfaceKind = surfaceKind;
        ExpectedCapabilities = new ReadOnlyCollection<NotificationCapability>(
            expectedCapabilities.ToArray());
    }

    public NotificationChannelId ChannelId { get; }

    public NotificationSurfaceKind SurfaceKind { get; }

    public IReadOnlyList<NotificationCapability> ExpectedCapabilities { get; }

    public NotificationChannelStatus GetStatus()
    {
        NotificationChannelStatus status;
        try
        {
            status = statusSource.GetStatus() ?? throw new InvalidOperationException(
                "The notification channel status source returned null.");
        }
        catch (NotificationContractException)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed probe is an unavailable channel, not a healthy one.
            // PRESENT is retained so the coordinator records UNAVAILABLE
            // rather than misclassifying the known adapter as missing.
            return new NotificationChannelStatus(
                ChannelId,
                ExpectedCapabilities,
                NotificationChannelHealth.UNKNOWN,
                clock.GetCurrentInstant(),
                NotificationErrorCodes.ChannelUnavailable);
        }

        if (status.ChannelId != ChannelId)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.ChannelIdentityMismatch,
                "The channel status source returned a different channel ID.",
                nameof(status));
        }

        if (status.Capabilities.Any(capability => !ExpectedCapabilities.Contains(capability)))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "The channel status advertises a capability outside the adapter matrix.",
                nameof(status));
        }

        return status;
    }

    public async ValueTask<NotificationChannelDeliveryResponse> DeliverAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChannelId != ChannelId)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.ChannelIdentityMismatch,
                "The delivery request targets a different channel.",
                nameof(request));
        }

        var status = GetStatus();
        var hasRequiredCapabilities =
            status.Supports(NotificationCapability.PRESENT) &&
            (ChannelId != NotificationChannelId.WakeTimer ||
             status.Supports(NotificationCapability.WAKE));
        if (!hasRequiredCapabilities)
        {
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.NOT_ATTEMPTED,
                NotificationErrorCodes.CapabilityMissing);
        }

        if (status.Health == NotificationChannelHealth.BLOCKED)
        {
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.BLOCKED,
                status.HealthCode ?? NotificationErrorCodes.ChannelBlocked);
        }

        if (status.Health is NotificationChannelHealth.UNAVAILABLE or NotificationChannelHealth.UNKNOWN)
        {
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.UNAVAILABLE,
                status.HealthCode ?? NotificationErrorCodes.ChannelUnavailable);
        }

        if (status.Health is not (NotificationChannelHealth.HEALTHY or NotificationChannelHealth.DEGRADED))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "Channel health cannot be dispatched.",
                nameof(status));
        }

        try
        {
            var response = await effectSink.PresentAsync(
                new NotificationChannelEffectRequest(request, SurfaceKind),
                cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                throw new InvalidOperationException(
                    "The notification effect sink returned null.");
            }

            if (response.Outcome == NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS)
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.DeliveryOutcomeInvalid,
                    "Quiet Hours suppression must be decided by the presentation policy.",
                    nameof(response));
            }

            return WithDefaultErrorCode(response);
        }
        catch (NotificationContractException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.FAILED,
                NotificationErrorCodes.DeliveryCancelled);
        }
        catch (Exception)
        {
            // External presentation errors are channel facts. They do not
            // erase the already-recorded core trigger.
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.FAILED,
                NotificationErrorCodes.DeliveryFailed);
        }
    }

    private static NotificationChannelDeliveryResponse WithDefaultErrorCode(
        NotificationChannelDeliveryResponse response) =>
        response.ErrorCode is not null || response.Outcome == NotificationDeliveryOutcome.DELIVERED
            ? response
            : new NotificationChannelDeliveryResponse(
                response.Outcome,
                response.Outcome switch
                {
                    NotificationDeliveryOutcome.BLOCKED => NotificationErrorCodes.ChannelBlocked,
                    NotificationDeliveryOutcome.UNAVAILABLE => NotificationErrorCodes.ChannelUnavailable,
                    NotificationDeliveryOutcome.NOT_ATTEMPTED => NotificationErrorCodes.CapabilityMissing,
                    NotificationDeliveryOutcome.FAILED => NotificationErrorCodes.DeliveryFailed,
                    _ => NotificationErrorCodes.DeliveryFailed,
                });
}

public sealed class WindowsToastNotificationChannel : NotificationChannelAdapter
{
    public WindowsToastNotificationChannel(
        INotificationChannelStatusSource statusSource,
        INotificationChannelEffectSink effectSink,
        IClock clock)
        : base(
            NotificationChannelId.Toast,
            NotificationSurfaceKind.TOAST,
            NotificationChannelCapabilityMatrix.Toast,
            statusSource,
            effectSink,
            clock)
    {
    }
}

/// <summary>
/// Tray owns the frozen TRAY channel. Taskbar flash is an optional separate
/// effect and can use <see cref="TaskbarFlashNotificationChannel"/> when a
/// host wants that presentation mode.
/// </summary>
public sealed class TrayNotificationChannel : NotificationChannelAdapter
{
    public TrayNotificationChannel(
        INotificationChannelStatusSource statusSource,
        INotificationChannelEffectSink effectSink,
        IClock clock)
        : base(
            NotificationChannelId.Tray,
            NotificationSurfaceKind.TRAY,
            NotificationChannelCapabilityMatrix.Tray,
            statusSource,
            effectSink,
            clock)
    {
    }
}

/// <summary>
/// Taskbar flash intentionally shares the TRAY channel identity. It is a
/// replaceable adapter choice, not an additional persisted delivery channel.
/// </summary>
public sealed class TaskbarFlashNotificationChannel : NotificationChannelAdapter
{
    public TaskbarFlashNotificationChannel(
        INotificationChannelStatusSource statusSource,
        INotificationChannelEffectSink effectSink,
        IClock clock)
        : base(
            NotificationChannelId.Tray,
            NotificationSurfaceKind.TASKBAR_FLASH,
            NotificationChannelCapabilityMatrix.Tray,
            statusSource,
            effectSink,
            clock)
    {
    }
}

public sealed class WidgetNotificationChannel : NotificationChannelAdapter
{
    public WidgetNotificationChannel(
        INotificationChannelStatusSource statusSource,
        INotificationChannelEffectSink effectSink,
        IClock clock)
        : base(
            NotificationChannelId.Widget,
            NotificationSurfaceKind.WIDGET,
            NotificationChannelCapabilityMatrix.Widget,
            statusSource,
            effectSink,
            clock)
    {
    }
}

public sealed class SoundNotificationChannel : NotificationChannelAdapter
{
    public SoundNotificationChannel(
        INotificationChannelStatusSource statusSource,
        INotificationChannelEffectSink effectSink,
        IClock clock)
        : base(
            NotificationChannelId.Sound,
            NotificationSurfaceKind.SOUND,
            NotificationChannelCapabilityMatrix.Sound,
            statusSource,
            effectSink,
            clock)
    {
    }
}

public sealed class WakeTimerNotificationChannel : NotificationChannelAdapter
{
    public WakeTimerNotificationChannel(
        INotificationChannelStatusSource statusSource,
        INotificationChannelEffectSink effectSink,
        IClock clock)
        : base(
            NotificationChannelId.WakeTimer,
            NotificationSurfaceKind.WAKE_TIMER,
            NotificationChannelCapabilityMatrix.WakeTimer,
            statusSource,
            effectSink,
            clock)
    {
    }
}

/// <summary>
/// Validated catalog for host composition. It is intentionally a read-only
/// lookup and contains no Agent, SQLite or WPF behavior.
/// </summary>
public sealed class NotificationChannelCatalog : INotificationChannelCatalog
{
    private readonly ReadOnlyDictionary<NotificationChannelId, INotificationChannel> channels;
    private readonly IReadOnlyCollection<INotificationChannel> orderedChannels;

    public NotificationChannelCatalog(IEnumerable<INotificationChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var map = new Dictionary<NotificationChannelId, INotificationChannel>();
        foreach (var channel in channels)
        {
            ArgumentNullException.ThrowIfNull(channel);
            var channelId = NotificationChannels.RequireKnown(channel.ChannelId);
            if (!map.TryAdd(channelId, channel))
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.SerializationInvalid,
                    $"Channel '{channelId.Value}' was registered more than once.",
                    nameof(channels));
            }
        }

        this.channels = new ReadOnlyDictionary<NotificationChannelId, INotificationChannel>(map);
        orderedChannels = map.Values.ToArray();
    }

    public IReadOnlyCollection<INotificationChannel> Channels => orderedChannels;

    public bool TryGet(NotificationChannelId channelId, out INotificationChannel channel) =>
        channels.TryGetValue(channelId, out channel!);
}

#pragma warning restore CA1707
