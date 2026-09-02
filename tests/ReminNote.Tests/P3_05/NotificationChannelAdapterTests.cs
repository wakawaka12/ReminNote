using System.Text;
using NodaTime;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Notifications.Adapters;

#pragma warning disable CA1707 // Slice directory names mirror the frozen plan.
namespace ReminNote.Tests.P3_05;

public sealed class NotificationChannelAdapterTests
{
    private static readonly Instant Now = Instant.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void CapabilityMatrixCoversFrozenChannelsAndKeepsTaskbarUnderTray()
    {
        Assert.Equal(
            [
                NotificationCapability.PRESENT,
                NotificationCapability.REPLACE_LOGICAL_REMINDER,
                NotificationCapability.READ_ON_CLOSE,
            ],
            NotificationChannelCapabilityMatrix.Toast);
        Assert.Equal(
            [
                NotificationCapability.PRESENT,
                NotificationCapability.REPLACE_LOGICAL_REMINDER,
            ],
            NotificationChannelCapabilityMatrix.Tray);
        Assert.Equal(
            [
                NotificationCapability.PRESENT,
                NotificationCapability.REPLACE_LOGICAL_REMINDER,
                NotificationCapability.READ_ON_CLOSE,
            ],
            NotificationChannelCapabilityMatrix.Widget);
        Assert.Equal(
            [NotificationCapability.PRESENT],
            NotificationChannelCapabilityMatrix.Sound);
        Assert.Equal(
            [NotificationCapability.PRESENT, NotificationCapability.WAKE],
            NotificationChannelCapabilityMatrix.WakeTimer);

        var taskbar = new TaskbarFlashNotificationChannel(
            FixedSource(NotificationChannelId.Tray),
            new RecordingEffectSink(),
            new FixedClock(Now));

        Assert.Equal(NotificationChannelId.Tray, taskbar.ChannelId);
        Assert.Equal(NotificationSurfaceKind.TASKBAR_FLASH, taskbar.SurfaceKind);
        Assert.Throws<NotificationContractException>(() =>
            NotificationChannelCapabilityMatrix.For(NotificationChannelId.Parse("TASKBAR")));
    }

    [Fact]
    public async Task HealthyAdapterForwardsTriggerSnapshotAndLogicalReplacementKey()
    {
        var sink = new RecordingEffectSink();
        var adapter = new WindowsToastNotificationChannel(
            FixedSource(NotificationChannelId.Toast),
            sink,
            new FixedClock(Now));
        var request = NewRequest(
            NotificationChannelId.Toast,
            NotificationPriority.HIGH,
            pinned: true);

        var response = await adapter.DeliverAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryOutcome.DELIVERED, response.Outcome);
        var effect = Assert.Single(sink.Requests);
        Assert.Equal(NotificationSurfaceKind.TOAST, effect.SurfaceKind);
        Assert.Equal(request.CoreTrigger, effect.DeliveryRequest.CoreTrigger);
        Assert.Equal(request.CorrelationId, effect.CorrelationId);
        Assert.Equal(request.IdempotencyKey, effect.IdempotencyKey);
        Assert.Equal(request.CoreTrigger.LogicalReminderId, effect.LogicalReminderId);
        Assert.Equal(
            request.CoreTrigger.LogicalReminderId.ToString("D"),
            effect.LogicalReplacementKey);
        Assert.Equal(NotificationPriority.HIGH, effect.PrioritySnapshot);
        Assert.True(effect.PinnedSnapshot);
    }

    [Fact]
    public async Task WakeTimerForwardsScheduledInstantSeparatelyFromRecordedTrigger()
    {
        var sink = new RecordingEffectSink();
        var adapter = new WakeTimerNotificationChannel(
            FixedSource(NotificationChannelId.WakeTimer),
            sink,
            new FixedClock(Now));
        var scheduledAtUtc = Now + Duration.FromMinutes(10);
        var request = NewRequest(
            NotificationChannelId.WakeTimer,
            scheduledTriggerAtUtc: scheduledAtUtc);

        var response = await adapter.DeliverAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryOutcome.DELIVERED, response.Outcome);
        var effect = Assert.Single(sink.Requests);
        Assert.Equal(Now, effect.TriggeredAtUtc);
        Assert.Equal(scheduledAtUtc, effect.ScheduledTriggerAtUtc);
        Assert.Equal(
            request.CoreTrigger.LogicalReminderId.ToString("D"),
            effect.LogicalReplacementKey);
    }

    [Fact]
    public async Task EveryFrozenAdapterForwardsToItsOwnInjectedSurface()
    {
        var fixedClock = new FixedClock(Now);
        var cases = new[]
        {
            CreateCase(NotificationChannelId.Toast, NotificationSurfaceKind.TOAST, fixedClock),
            CreateCase(NotificationChannelId.Tray, NotificationSurfaceKind.TRAY, fixedClock),
            CreateCase(NotificationChannelId.Tray, NotificationSurfaceKind.TASKBAR_FLASH, fixedClock),
            CreateCase(NotificationChannelId.Widget, NotificationSurfaceKind.WIDGET, fixedClock),
            CreateCase(NotificationChannelId.Sound, NotificationSurfaceKind.SOUND, fixedClock),
            CreateCase(NotificationChannelId.WakeTimer, NotificationSurfaceKind.WAKE_TIMER, fixedClock),
        };

        foreach (var (adapter, surfaceKind, sink) in cases)
        {
            var response = await adapter.DeliverAsync(
                NewRequest(adapter.ChannelId),
                TestContext.Current.CancellationToken);

            Assert.Equal(NotificationDeliveryOutcome.DELIVERED, response.Outcome);
            Assert.Equal(surfaceKind, Assert.Single(sink.Requests).SurfaceKind);
        }
    }

    [Theory]
    [InlineData("BLOCKED", "BLOCKED", "notification.channel.blocked")]
    [InlineData("UNAVAILABLE", "UNAVAILABLE", "notification.channel.unavailable")]
    [InlineData("UNKNOWN", "UNAVAILABLE", "notification.channel.unavailable")]
    public async Task BlockedOrUnavailableHealthNeverCallsExternalSink(
        string healthText,
        string outcomeText,
        string expectedErrorCode)
    {
        var sink = new RecordingEffectSink();
        var adapter = new WindowsToastNotificationChannel(
            FixedSource(
                NotificationChannelId.Toast,
                Enum.Parse<NotificationChannelHealth>(healthText)),
            sink,
            new FixedClock(Now));

        var response = await adapter.DeliverAsync(
            NewRequest(NotificationChannelId.Toast),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            Enum.Parse<NotificationDeliveryOutcome>(outcomeText),
            response.Outcome);
        Assert.Equal(expectedErrorCode, response.ErrorCode);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task MissingPresentCapabilityIsNotAttemptedAndDoesNotBecomeSuccess()
    {
        var sink = new RecordingEffectSink();
        var status = new NotificationChannelStatus(
            NotificationChannelId.Widget,
            [],
            NotificationChannelHealth.HEALTHY,
            Now);
        var adapter = new WidgetNotificationChannel(
            new FixedNotificationChannelStatusSource(status),
            sink,
            new FixedClock(Now));

        var response = await adapter.DeliverAsync(
            NewRequest(NotificationChannelId.Widget),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryOutcome.NOT_ATTEMPTED, response.Outcome);
        Assert.Equal(NotificationErrorCodes.CapabilityMissing, response.ErrorCode);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task WakeTimerRequiresWakeCapabilityInAdditionToPresent()
    {
        var sink = new RecordingEffectSink();
        var status = new NotificationChannelStatus(
            NotificationChannelId.WakeTimer,
            [NotificationCapability.PRESENT],
            NotificationChannelHealth.HEALTHY,
            Now);
        var adapter = new WakeTimerNotificationChannel(
            new FixedNotificationChannelStatusSource(status),
            sink,
            new FixedClock(Now));

        var response = await adapter.DeliverAsync(
            NewRequest(NotificationChannelId.WakeTimer),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryOutcome.NOT_ATTEMPTED, response.Outcome);
        Assert.Equal(NotificationErrorCodes.CapabilityMissing, response.ErrorCode);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public void StatusCannotAdvertiseCapabilityOutsideItsAdapterMatrix()
    {
        var status = new NotificationChannelStatus(
            NotificationChannelId.Sound,
            [NotificationCapability.PRESENT, NotificationCapability.WAKE],
            NotificationChannelHealth.HEALTHY,
            Now);
        var adapter = new SoundNotificationChannel(
            new FixedNotificationChannelStatusSource(status),
            new RecordingEffectSink(),
            new FixedClock(Now));

        var exception = Assert.Throws<NotificationContractException>(adapter.GetStatus);

        Assert.Equal(NotificationErrorCodes.SerializationInvalid, exception.Code);
    }

    [Fact]
    public async Task FailedHealthProbeBecomesUnknownAndIsRecordedAsUnavailable()
    {
        var sink = new RecordingEffectSink();
        var source = new DelegateNotificationChannelStatusSource(
            () => throw new InvalidOperationException("test probe failure"));
        var adapter = new SoundNotificationChannel(
            source,
            sink,
            new FixedClock(Now));

        var status = adapter.GetStatus();
        var response = await adapter.DeliverAsync(
            NewRequest(NotificationChannelId.Sound),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationChannelHealth.UNKNOWN, status.Health);
        Assert.Equal(NotificationErrorCodes.ChannelUnavailable, status.HealthCode);
        Assert.Equal(NotificationDeliveryOutcome.UNAVAILABLE, response.Outcome);
        Assert.Equal(NotificationErrorCodes.ChannelUnavailable, response.ErrorCode);
        Assert.Empty(sink.Requests);
    }

    [Fact]
    public async Task ExternalFailureIsAChannelFailureAndKeepsCoreSnapshotIntact()
    {
        var sink = new RecordingEffectSink
        {
            ExceptionToThrow = new InvalidOperationException("unverified OS effect")
        };
        var adapter = new SoundNotificationChannel(
            FixedSource(NotificationChannelId.Sound),
            sink,
            new FixedClock(Now));
        var request = NewRequest(NotificationChannelId.Sound);

        var response = await adapter.DeliverAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryOutcome.FAILED, response.Outcome);
        Assert.Equal(NotificationErrorCodes.DeliveryFailed, response.ErrorCode);
        Assert.Equal(request.CoreTrigger, sink.Requests.Single().DeliveryRequest.CoreTrigger);
    }

    [Fact]
    public async Task CancellationIsAStableFailedChannelFact()
    {
        var sink = new RecordingEffectSink
        {
            ExceptionToThrow = new OperationCanceledException("test cancellation")
        };
        var adapter = new WindowsToastNotificationChannel(
            FixedSource(NotificationChannelId.Toast),
            sink,
            new FixedClock(Now));

        var response = await adapter.DeliverAsync(
            NewRequest(NotificationChannelId.Toast),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryOutcome.FAILED, response.Outcome);
        Assert.Equal(NotificationErrorCodes.DeliveryCancelled, response.ErrorCode);
    }

    [Fact]
    public async Task QuietHoursSuppressionComesFromCoordinatorAndLeavesSinkUntouched()
    {
        using var isolated = new IsolatedArtifact();
        var sink = new RecordingEffectSink();
        var adapter = new WindowsToastNotificationChannel(
            FixedSource(NotificationChannelId.Toast),
            sink,
            new FixedClock(Now));
        var request = NewRequest(NotificationChannelId.Toast);
        var store = new FileDeliveryAttemptStore(isolated.Root);
        var coordinator = new NotificationDeliveryCoordinator(
            new NotificationChannelCatalog([adapter]),
            store,
            new SuppressingPresentationPolicy(),
            new FixedClock(Now));

        var result = await coordinator.DispatchAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.True(result.CoreTriggerWasRecorded);
        Assert.Equal(request.CoreTrigger, result.CoreTrigger);
        Assert.Equal(
            NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS,
            result.Outcome);
        Assert.Equal(
            NotificationErrorCodes.PolicySuppressedQuietHours,
            result.Attempt!.ErrorCode);
        Assert.Empty(sink.Requests);
        Assert.Single(await store.ListForInstanceAsync(
            request.CoreTrigger.InstanceId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CoordinatorReplayUsesSameAttemptAndDoesNotRepeatExternalEffect()
    {
        using var isolated = new IsolatedArtifact();
        var sink = new RecordingEffectSink();
        var adapter = new WidgetNotificationChannel(
            FixedSource(NotificationChannelId.Widget),
            sink,
            new FixedClock(Now));
        var request = NewRequest(NotificationChannelId.Widget);
        var store = new FileDeliveryAttemptStore(isolated.Root);
        var coordinator = new NotificationDeliveryCoordinator(
            new NotificationChannelCatalog([adapter]),
            store,
            new AllowAllNotificationPresentationPolicy(),
            new FixedClock(Now));

        var first = await coordinator.DispatchAsync(
            request,
            TestContext.Current.CancellationToken);
        var replay = await coordinator.DispatchAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryOutcome.DELIVERED, first.Outcome);
        Assert.Equal(first.Attempt, replay.Attempt);
        Assert.True(replay.WasDuplicate);
        Assert.Single(sink.Requests);
        Assert.Single(await store.ListForInstanceAsync(
            request.CoreTrigger.InstanceId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CatalogRejectsDuplicateFrozenChannelIdentity()
    {
        var first = new SoundNotificationChannel(
            FixedSource(NotificationChannelId.Sound),
            new RecordingEffectSink(),
            new FixedClock(Now));
        var second = new SoundNotificationChannel(
            FixedSource(NotificationChannelId.Sound),
            new RecordingEffectSink(),
            new FixedClock(Now));

        var exception = Assert.Throws<NotificationContractException>(() =>
            new NotificationChannelCatalog([first, second]));

        Assert.Equal(NotificationErrorCodes.SerializationInvalid, exception.Code);
    }

    private static NotificationChannelStatus FixedStatus(
        NotificationChannelId channelId,
        NotificationChannelHealth health = NotificationChannelHealth.HEALTHY,
        IReadOnlyList<NotificationCapability>? capabilities = null) =>
        new(
            channelId,
            capabilities ?? NotificationChannelCapabilityMatrix.For(channelId),
            health,
            Now,
            health switch
            {
                NotificationChannelHealth.BLOCKED => NotificationErrorCodes.ChannelBlocked,
                NotificationChannelHealth.UNAVAILABLE or NotificationChannelHealth.UNKNOWN =>
                    NotificationErrorCodes.ChannelUnavailable,
                _ => null,
            });

    private static FixedNotificationChannelStatusSource FixedSource(
        NotificationChannelId channelId,
        NotificationChannelHealth health = NotificationChannelHealth.HEALTHY,
        IReadOnlyList<NotificationCapability>? capabilities = null) =>
        new(FixedStatus(channelId, health, capabilities));

    private static (NotificationChannelAdapter Adapter, NotificationSurfaceKind SurfaceKind, RecordingEffectSink Sink)
        CreateCase(
            NotificationChannelId channelId,
            NotificationSurfaceKind surfaceKind,
            FixedClock clock)
    {
        var sink = new RecordingEffectSink();
        NotificationChannelAdapter adapter = (channelId.Value, surfaceKind) switch
        {
            ("TOAST", NotificationSurfaceKind.TOAST) => new WindowsToastNotificationChannel(
                FixedSource(channelId),
                sink,
                clock),
            ("TRAY", NotificationSurfaceKind.TRAY) => new TrayNotificationChannel(
                FixedSource(channelId),
                sink,
                clock),
            ("TRAY", NotificationSurfaceKind.TASKBAR_FLASH) => new TaskbarFlashNotificationChannel(
                FixedSource(channelId),
                sink,
                clock),
            ("WIDGET", NotificationSurfaceKind.WIDGET) => new WidgetNotificationChannel(
                FixedSource(channelId),
                sink,
                clock),
            ("SOUND", NotificationSurfaceKind.SOUND) => new SoundNotificationChannel(
                FixedSource(channelId),
                sink,
                clock),
            ("WAKE_TIMER", NotificationSurfaceKind.WAKE_TIMER) => new WakeTimerNotificationChannel(
                FixedSource(channelId),
                sink,
                clock),
            _ => throw new InvalidOperationException(
                $"No adapter test case exists for '{channelId.Value}/{surfaceKind}'."),
        };

        return (adapter, surfaceKind, sink);
    }

    private static NotificationDeliveryRequest NewRequest(
        NotificationChannelId channelId,
        NotificationPriority priority = NotificationPriority.NORMAL,
        bool pinned = false,
        Instant? scheduledTriggerAtUtc = null) =>
        new(
            new NotificationTriggerFact(
                Id("0191f6a4-3b25-7c12-8d34-56789abcde01"),
                Id("0191f6a4-3b25-7c12-8d34-56789abcde02"),
                Id("0191f6a4-3b25-7c12-8d34-56789abcde03"),
                Id("0191f6a4-3b25-7c12-8d34-56789abcde04"),
                Id("0191f6a4-3b25-7c12-8d34-56789abcde05"),
                1,
                NotificationPurposeSnapshot.TaskStart,
                priority,
                pinned,
                Now,
                scheduledTriggerAtUtc),
            channelId,
            Id("0191f6a4-3b25-7c12-8d34-56789abcde12"),
            Id("0191f6a4-3b25-7c12-8d34-56789abcde13"));

    private static Guid Id(string text) => Guid.Parse(text);

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private sealed class RecordingEffectSink : INotificationChannelEffectSink
    {
        public List<NotificationChannelEffectRequest> Requests { get; } = [];

        public Exception? ExceptionToThrow { get; init; }

        public ValueTask<NotificationChannelDeliveryResponse> PresentAsync(
            NotificationChannelEffectRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (ExceptionToThrow is { } exception)
            {
                throw exception;
            }

            return ValueTask.FromResult(
                new NotificationChannelDeliveryResponse(
                    NotificationDeliveryOutcome.DELIVERED));
        }
    }

    private sealed class SuppressingPresentationPolicy : INotificationPresentationPolicy
    {
        public NotificationPresentationDecision Evaluate(
            NotificationPresentationContext context) =>
            NotificationPresentationDecision.SuppressedQuietHours(
                NotificationErrorCodes.PolicySuppressedQuietHours);
    }

    private sealed class IsolatedArtifact : IDisposable
    {
        public IsolatedArtifact()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "ReminNote.P3_05",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FileDeliveryAttemptStore : INotificationDeliveryAttemptStore
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        private readonly Dictionary<DeliveryKey, NotificationDeliveryAttempt> attempts = [];
        private readonly List<NotificationDeliveryAttempt> ordered = [];

        public FileDeliveryAttemptStore(string root)
        {
            Directory.CreateDirectory(root);
            AttemptFilePath = Path.Combine(root, "delivery-attempts.jsonl");
            if (!File.Exists(AttemptFilePath))
            {
                return;
            }

            foreach (var line in File.ReadLines(AttemptFilePath, Utf8NoBom))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var attempt = NotificationContractJson.DeserializeDeliveryAttempt(line);
                var key = DeliveryKey.For(attempt);
                attempts[key] = attempt;
                ordered.Add(attempt);
            }
        }

        public string AttemptFilePath { get; }

        public ValueTask<NotificationDeliveryAttempt?> FindAsync(
            Guid instanceId,
            NotificationChannelId channelId,
            Guid idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            attempts.TryGetValue(
                new DeliveryKey(instanceId, channelId, idempotencyKey),
                out var attempt);
            return ValueTask.FromResult(attempt);
        }

        public ValueTask<NotificationDeliveryAppendResult> AppendAsync(
            NotificationDeliveryAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            var key = DeliveryKey.For(attempt);
            if (attempts.TryGetValue(key, out var existing))
            {
                return ValueTask.FromResult(
                    NotificationDeliveryAppendResult.Replayed(existing));
            }

            attempts.Add(key, attempt);
            ordered.Add(attempt);
            File.AppendAllText(
                AttemptFilePath,
                NotificationContractJson.SerializeDeliveryAttemptText(attempt) +
                Environment.NewLine,
                Utf8NoBom);
            return ValueTask.FromResult(
                NotificationDeliveryAppendResult.Appended(attempt));
        }

        public ValueTask<IReadOnlyList<NotificationDeliveryAttempt>> ListForInstanceAsync(
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<NotificationDeliveryAttempt>>(
                ordered.Where(attempt => attempt.InstanceId == instanceId).ToArray());

        private readonly record struct DeliveryKey(
            Guid InstanceId,
            NotificationChannelId ChannelId,
            Guid IdempotencyKey)
        {
            public static DeliveryKey For(NotificationDeliveryAttempt attempt) =>
                new(attempt.InstanceId, attempt.ChannelId, attempt.IdempotencyKey);
        }
    }
}

#pragma warning restore CA1707
