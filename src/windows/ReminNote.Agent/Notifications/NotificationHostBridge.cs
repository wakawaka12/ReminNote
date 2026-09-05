using System.Buffers;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NodaTime;
using NodaTime.Text;
using ReminNote.Agent.Transport;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Notifications.Adapters;

namespace ReminNote.Agent.Notifications;

/// <summary>
/// Which desktop process owns the OS effect sink. The endpoints are separate
/// so a missing Main host cannot accidentally acknowledge a Widget delivery.
/// </summary>
public enum NotificationHostBridgeKind
{
    Main,
    Widget,
}

/// <summary>
/// Agent-side effect sink for the real Main/Widget host boundary. A failed
/// connection is reported as UNAVAILABLE; it is never converted to success.
/// The sink carries only validated IDs and snapshots, never display text.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NotificationHostBridgeEffectSink : INotificationChannelEffectSink
{
    private readonly string pipeName;
    private readonly NotificationSurfaceKind surfaceKind;
    private readonly Action<bool> observeAvailability;

    public NotificationHostBridgeEffectSink(
        string profileScope,
        NotificationHostBridgeKind hostKind,
        NotificationSurfaceKind surfaceKind,
        Action<bool>? observeAvailability = null)
    {
        ProtocolProfileScope.Validate(profileScope);
        if (!Enum.IsDefined(surfaceKind))
        {
            throw new ArgumentOutOfRangeException(nameof(surfaceKind));
        }

        this.pipeName = ProtocolPipeNames.NotificationHost(profileScope, hostKind.ToString());
        this.surfaceKind = surfaceKind;
        this.observeAvailability = observeAvailability ?? (_ => { });
    }

    public async ValueTask<NotificationChannelDeliveryResponse> PresentAsync(
        NotificationChannelEffectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            observeAvailability(false);
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.UNAVAILABLE,
                NotificationErrorCodes.ChannelUnavailable);
        }

        try
        {
            var requestJson = NotificationHostBridgeProtocol.SerializeRequest(request, surfaceKind);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromMilliseconds(750));
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            await client.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            await NotificationHostBridgeProtocol.WriteLineAsync(
                    client,
                    requestJson,
                    connectTimeout.Token)
                .ConfigureAwait(false);
            var responseJson = await NotificationHostBridgeProtocol.ReadLineAsync(
                    client,
                    connectTimeout.Token)
                .ConfigureAwait(false);
            if (responseJson is null)
            {
                throw new IOException("Notification host closed the bridge before returning a response.");
            }

            var response = NotificationHostBridgeProtocol.ParseResponse(responseJson);
            observeAvailability(response.Outcome == NotificationDeliveryOutcome.DELIVERED);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            observeAvailability(false);
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.UNAVAILABLE,
                NotificationErrorCodes.ChannelUnavailable);
        }
        catch (Exception exception) when (
            exception is IOException or
            TimeoutException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            observeAvailability(false);
            return new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.UNAVAILABLE,
                exception is UnauthorizedAccessException
                    ? NotificationErrorCodes.ChannelBlocked
                    : NotificationErrorCodes.ChannelUnavailable);
        }
    }
}

/// <summary>
/// Explicit Agent-to-host channel composition. The bridge-backed catalog is
/// usable before either UI process starts and then fails closed until a host
/// accepts a request. This keeps the core trigger/attempt durable while the
/// host lifecycle remains independent of the Agent writer.
/// </summary>
public static class AgentNotificationChannelComposition
{
    [SupportedOSPlatform("windows")]
    public static NotificationChannelCatalog CreateForProfile(
        string profileScope,
        IClock clock)
    {
        ProtocolProfileScope.Validate(profileScope);
        ArgumentNullException.ThrowIfNull(clock);

        return new NotificationChannelCatalog(
        [
            CreateChannel(
                NotificationChannelId.Toast,
                NotificationSurfaceKind.TOAST,
                NotificationHostBridgeKind.Main,
                profileScope,
                clock),
            CreateChannel(
                NotificationChannelId.Tray,
                NotificationSurfaceKind.TRAY,
                NotificationHostBridgeKind.Main,
                profileScope,
                clock),
            CreateChannel(
                NotificationChannelId.Widget,
                NotificationSurfaceKind.WIDGET,
                NotificationHostBridgeKind.Widget,
                profileScope,
                clock),
            CreateChannel(
                NotificationChannelId.Sound,
                NotificationSurfaceKind.SOUND,
                NotificationHostBridgeKind.Main,
                profileScope,
                clock),
            CreateChannel(
                NotificationChannelId.WakeTimer,
                NotificationSurfaceKind.WAKE_TIMER,
                NotificationHostBridgeKind.Main,
                profileScope,
                clock),
        ]);
    }

    /// <summary>
    /// Safe test/development composition. Production always calls
    /// <see cref="CreateForProfile"/> so a host bridge can be reached.
    /// </summary>
    public static NotificationChannelCatalog CreateFailClosed(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new NotificationChannelCatalog(
        [
            CreateUnavailable(NotificationChannelId.Toast, NotificationSurfaceKind.TOAST, clock),
            CreateUnavailable(NotificationChannelId.Tray, NotificationSurfaceKind.TRAY, clock),
            CreateUnavailable(NotificationChannelId.Widget, NotificationSurfaceKind.WIDGET, clock),
            CreateUnavailable(NotificationChannelId.Sound, NotificationSurfaceKind.SOUND, clock),
            CreateUnavailable(NotificationChannelId.WakeTimer, NotificationSurfaceKind.WAKE_TIMER, clock),
        ]);
    }

    [SupportedOSPlatform("windows")]
    private static INotificationChannel CreateChannel(
        NotificationChannelId channelId,
        NotificationSurfaceKind surfaceKind,
        NotificationHostBridgeKind hostKind,
        string profileScope,
        IClock clock)
    {
        var availability = new BridgeAvailability();
        var sink = new NotificationHostBridgeEffectSink(
            profileScope,
            hostKind,
            surfaceKind,
            availability.Observe);
        var statusSource = new DelegateNotificationChannelStatusSource(() =>
        {
            var health = availability.GetHealth();
            var code = availability.GetHealthCode();
            return new NotificationChannelStatus(
                channelId,
                NotificationChannelCapabilityMatrix.For(channelId),
                health,
                clock.GetCurrentInstant(),
                code);
        });

        return channelId.Value switch
        {
            "TOAST" => new WindowsToastNotificationChannel(statusSource, sink, clock),
            "TRAY" => new TrayNotificationChannel(statusSource, sink, clock),
            "WIDGET" => new WidgetNotificationChannel(statusSource, sink, clock),
            "SOUND" => new SoundNotificationChannel(statusSource, sink, clock),
            "WAKE_TIMER" => new WakeTimerNotificationChannel(statusSource, sink, clock),
            _ => throw new ArgumentOutOfRangeException(nameof(channelId)),
        };
    }

    private static INotificationChannel CreateUnavailable(
        NotificationChannelId channelId,
        NotificationSurfaceKind surfaceKind,
        IClock clock) =>
        channelId.Value switch
        {
            "TOAST" => new WindowsToastNotificationChannel(
                StatusSource(channelId, clock),
                new UnavailableNotificationChannelEffectSink(),
                clock),
            "TRAY" => new TrayNotificationChannel(
                StatusSource(channelId, clock),
                new UnavailableNotificationChannelEffectSink(),
                clock),
            "WIDGET" => new WidgetNotificationChannel(
                StatusSource(channelId, clock),
                new UnavailableNotificationChannelEffectSink(),
                clock),
            "SOUND" => new SoundNotificationChannel(
                StatusSource(channelId, clock),
                new UnavailableNotificationChannelEffectSink(),
                clock),
            "WAKE_TIMER" => new WakeTimerNotificationChannel(
                StatusSource(channelId, clock),
                new UnavailableNotificationChannelEffectSink(),
                clock),
            _ => throw new ArgumentOutOfRangeException(nameof(channelId)),
        };

    private static DelegateNotificationChannelStatusSource StatusSource(
        NotificationChannelId channelId,
        IClock clock) =>
        new(() => NotificationChannelAdapterDefaults.SafeUnavailableStatus(
            channelId,
            clock.GetCurrentInstant(),
            NotificationErrorCodes.ChannelUnavailable));

    private sealed class BridgeAvailability
    {
        private const long RetryAfterMilliseconds = 5_000;
        private int state;
        private long failureAtMilliseconds;

        public void Observe(bool delivered)
        {
            if (delivered)
            {
                Interlocked.Exchange(ref failureAtMilliseconds, 0);
                Volatile.Write(ref state, 1);
                return;
            }

            Interlocked.Exchange(ref failureAtMilliseconds, Environment.TickCount64);
            Volatile.Write(ref state, -1);
        }

        public NotificationChannelHealth GetHealth()
        {
            var currentState = Volatile.Read(ref state);
            if (currentState == -1)
            {
                var failedAt = Interlocked.Read(ref failureAtMilliseconds);
                if (failedAt > 0 && Environment.TickCount64 - failedAt >= RetryAfterMilliseconds)
                {
                    // Allow the next delivery attempt to probe a host that may
                    // have started after the previous connection failure.
                    return NotificationChannelHealth.DEGRADED;
                }
            }

            return currentState switch
            {
                1 => NotificationChannelHealth.HEALTHY,
                -1 => NotificationChannelHealth.UNAVAILABLE,
                _ => NotificationChannelHealth.DEGRADED,
            };
        }

        public string GetHealthCode()
        {
            var currentState = Volatile.Read(ref state);
            if (currentState == -1)
            {
                var failedAt = Interlocked.Read(ref failureAtMilliseconds);
                if (failedAt > 0 && Environment.TickCount64 - failedAt >= RetryAfterMilliseconds)
                {
                    return "notification.host_bridge.retrying";
                }
            }

            return currentState switch
            {
                1 => "notification.host_bridge.ready",
                -1 => NotificationErrorCodes.ChannelUnavailable,
                _ => "notification.host_bridge.not_probed",
            };
        }
    }
}

/// <summary>
/// Host-side lifecycle object. Main and Widget each own one instance and
/// dispose it with their WPF application. The server invokes only the host's
/// already-registered catalog, so all native effects remain UI-owned.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NotificationHostBridgeServer : IAsyncDisposable, IDisposable
{
    private readonly string pipeName;
    private readonly INotificationChannelCatalog catalog;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task acceptLoop;
    private int disposed;

    private NotificationHostBridgeServer(
        string profileScope,
        NotificationHostBridgeKind hostKind,
        INotificationChannelCatalog catalog)
    {
        ProtocolProfileScope.Validate(profileScope);
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        pipeName = ProtocolPipeNames.NotificationHost(profileScope, hostKind.ToString());
        acceptLoop = Task.Run(() => RunAsync(lifetime.Token));
    }

    public static NotificationHostBridgeServer Start(
        string profileScope,
        NotificationHostBridgeKind hostKind,
        INotificationChannelCatalog catalog)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Notification host bridges require Windows named pipes.");
        }

        return new NotificationHostBridgeServer(profileScope, hostKind, catalog);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        try
        {
            await acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = CreateServerStream();
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var requestLine = await NotificationHostBridgeProtocol
                    .ReadLineAsync(server, cancellationToken)
                    .ConfigureAwait(false);
                if (requestLine is null)
                {
                    continue;
                }

                var response = await DispatchAsync(requestLine, cancellationToken).ConfigureAwait(false);
                await NotificationHostBridgeProtocol
                    .WriteLineAsync(server, NotificationHostBridgeProtocol.SerializeResponse(response), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                JsonException)
            {
                // A malformed/unavailable host request is an unavailable
                // channel fact; the Agent remains alive for later requests.
            }
        }
    }

    private async ValueTask<NotificationHostBridgeResponse> DispatchAsync(
        string requestLine,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = NotificationHostBridgeProtocol.ParseRequest(requestLine);
            if (!catalog.TryGet(request.DeliveryRequest.ChannelId, out var channel))
            {
                return NotificationHostBridgeResponse.Unavailable(NotificationErrorCodes.ChannelMissing);
            }

            var response = await channel
                .DeliverAsync(request.DeliveryRequest, cancellationToken)
                .ConfigureAwait(false);
            return new NotificationHostBridgeResponse(response.Outcome, response.ErrorCode);
        }
        catch (NotificationContractException)
        {
            return NotificationHostBridgeResponse.Unavailable(NotificationErrorCodes.SerializationInvalid);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return NotificationHostBridgeResponse.Unavailable(NotificationErrorCodes.ChannelUnavailable);
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        var security = NamedPipeAcl.CreateBusinessSecurity(WindowsUserIdentity.GetCurrentSid());
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            16_384,
            16_384,
            security,
            HandleInheritability.None,
            additionalAccessRights: 0);
    }
}

internal sealed record NotificationHostBridgeResponse(
    NotificationDeliveryOutcome Outcome,
    string? ErrorCode)
{
    public static NotificationHostBridgeResponse Unavailable(string code) =>
        new(NotificationDeliveryOutcome.UNAVAILABLE, code);
}

internal static class NotificationHostBridgeProtocol
{
    private const int MaxFrameBytes = 16_384;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly InstantPattern InstantPattern =
        InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss.FFFFFFFFF'Z'");

    public static string SerializeRequest(
        NotificationChannelEffectRequest request,
        NotificationSurfaceKind surfaceKind)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trigger = request.DeliveryRequest.CoreTrigger;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("channelId", request.ChannelId.Value);
            writer.WriteString("surfaceKind", surfaceKind.ToString());
            writer.WriteString("requestId", request.DeliveryRequest.RequestId.ToString("D"));
            writer.WriteString("correlationId", request.CorrelationId.ToString("D"));
            writer.WriteString("idempotencyKey", request.IdempotencyKey.ToString("D"));
            writer.WriteString("instanceId", trigger.InstanceId.ToString("D"));
            writer.WriteString("scheduleId", trigger.ScheduleId.ToString("D"));
            writer.WriteString("ruleId", trigger.RuleId.ToString("D"));
            writer.WriteString("occurrenceId", trigger.OccurrenceId.ToString("D"));
            writer.WriteString("logicalReminderId", trigger.LogicalReminderId.ToString("D"));
            writer.WriteNumber("attemptOrdinal", trigger.AttemptOrdinal);
            writer.WriteString("purpose", trigger.PurposeSnapshot.Value);
            writer.WriteString("priority", trigger.PrioritySnapshot.ToString());
            writer.WriteBoolean("pinned", trigger.PinnedSnapshot);
            writer.WriteString("triggeredAtUtc", InstantPattern.Format(trigger.TriggeredAtUtc));
            if (trigger.ScheduledTriggerAtUtc is { } scheduled)
            {
                writer.WriteString("scheduledTriggerAtUtc", InstantPattern.Format(scheduled));
            }

            writer.WriteEndObject();
        }

        return Utf8.GetString(stream.ToArray());
    }

    public static NotificationHostBridgeRequest ParseRequest(string json)
    {
        if (Utf8.GetByteCount(json) > MaxFrameBytes)
        {
            throw new NotificationContractException(
                NotificationErrorCodes.SerializationTooLarge,
                "Notification host bridge request is too large.");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new NotificationContractException(
                NotificationErrorCodes.SerializationInvalid,
                "Notification host bridge request must be an object.");
        }

        var channelId = NotificationChannelId.Parse(RequireString(root, "channelId"));
        NotificationChannels.RequireKnown(channelId);
        var surfaceKind = ParseEnum<NotificationSurfaceKind>(root, "surfaceKind");
        var trigger = new NotificationTriggerFact(
            ParseGuid(root, "instanceId"),
            ParseGuid(root, "scheduleId"),
            ParseGuid(root, "ruleId"),
            ParseGuid(root, "occurrenceId"),
            ParseGuid(root, "logicalReminderId"),
            RequireInt32(root, "attemptOrdinal"),
            new NotificationPurposeSnapshot(RequireString(root, "purpose")),
            ParseEnum<NotificationPriority>(root, "priority"),
            RequireBoolean(root, "pinned"),
            ParseInstant(root, "triggeredAtUtc"),
            ParseOptionalInstant(root, "scheduledTriggerAtUtc"));
        var delivery = new NotificationDeliveryRequest(
            trigger,
            channelId,
            ParseGuid(root, "correlationId"),
            ParseGuid(root, "idempotencyKey"),
            ParseGuid(root, "requestId"));
        return new NotificationHostBridgeRequest(
            new NotificationChannelEffectRequest(delivery, surfaceKind));
    }

    public static string SerializeResponse(NotificationHostBridgeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("outcome", response.Outcome.ToString());
            if (response.ErrorCode is null)
            {
                writer.WriteNull("errorCode");
            }
            else
            {
                writer.WriteString("errorCode", response.ErrorCode);
            }

            writer.WriteEndObject();
        }

        return Utf8.GetString(stream.ToArray());
    }

    public static NotificationChannelDeliveryResponse ParseResponse(string json)
    {
        if (Utf8.GetByteCount(json) > MaxFrameBytes)
        {
            throw new IOException("Notification host bridge response is too large.");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var outcome = ParseEnum<NotificationDeliveryOutcome>(root, "outcome");
        string? errorCode = null;
        if (root.TryGetProperty("errorCode", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            errorCode = RequireString(error, "errorCode");
        }

        return new NotificationChannelDeliveryResponse(outcome, errorCode);
    }

    public static async ValueTask WriteLineAsync(
        Stream stream,
        string value,
        CancellationToken cancellationToken)
    {
        var bytes = Utf8.GetBytes(value + "\n");
        if (bytes.Length > MaxFrameBytes)
        {
            throw new IOException("Notification host bridge frame is too large.");
        }

        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<string?> ReadLineAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var one = new byte[1];
        while (buffer.WrittenCount < MaxFrameBytes)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.WrittenCount == 0
                    ? null
                    : throw new IOException("Notification host bridge sent a truncated frame.");
            }

            if (one[0] == (byte)'\n')
            {
                return Utf8.GetString(buffer.WrittenSpan);
            }

            buffer.Write(one);
        }

        throw new IOException("Notification host bridge frame is too large.");
    }

    private static string RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text)
        {
            throw new NotificationContractException(
                NotificationErrorCodes.SerializationInvalid,
                $"Notification host bridge field '{name}' must be a string.",
                name);
        }

        return text;
    }

    private static bool RequireBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new NotificationContractException(
                NotificationErrorCodes.SerializationInvalid,
                $"Notification host bridge field '{name}' must be boolean.",
                name);
        }

        return value.GetBoolean();
    }

    private static int RequireInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
        {
            throw new NotificationContractException(
                NotificationErrorCodes.SerializationInvalid,
                $"Notification host bridge field '{name}' must be Int32.",
                name);
        }

        return result;
    }

    private static Guid ParseGuid(JsonElement root, string name)
    {
        var text = RequireString(root, name);
        if (!Guid.TryParse(text, out var value))
        {
            throw new NotificationContractException(
                NotificationErrorCodes.SerializationInvalid,
                $"Notification host bridge field '{name}' is not a UUID.",
                name);
        }

        return value;
    }

    private static T ParseEnum<T>(JsonElement root, string name)
        where T : struct, Enum
    {
        var text = RequireString(root, name);
        if (!Enum.TryParse<T>(text, false, out var value) || !Enum.IsDefined(value))
        {
            throw new NotificationContractException(
                NotificationErrorCodes.SerializationInvalid,
                $"Notification host bridge field '{name}' is not supported.",
                name);
        }

        return value;
    }

    private static Instant ParseInstant(JsonElement root, string name)
    {
        var text = RequireString(root, name);
        var parse = InstantPattern.Parse(text);
        if (!parse.Success)
        {
            throw new NotificationContractException(
                NotificationErrorCodes.InvalidTimestamp,
                $"Notification host bridge field '{name}' is not a canonical UTC instant.",
                name);
        }

        return parse.Value;
    }

    private static Instant? ParseOptionalInstant(JsonElement root, string name) =>
        root.TryGetProperty(name, out _) ? ParseInstant(root, name) : null;
}

internal sealed record NotificationHostBridgeRequest(
    NotificationChannelEffectRequest EffectRequest)
{
    public NotificationDeliveryRequest DeliveryRequest => EffectRequest.DeliveryRequest;
}
