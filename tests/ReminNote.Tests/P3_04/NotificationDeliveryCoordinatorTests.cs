using System.Text;
using NodaTime;
using ReminNote.Core.Reminders.Notifications;

#pragma warning disable CA1707 // Slice directory names mirror the frozen plan.
namespace ReminNote.Tests.P3_04;

public sealed class NotificationDeliveryCoordinatorTests
{
    private static readonly Instant Now = Instant.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public async Task BlockedAndUnavailableChannelsKeepTheCoreTriggerFact()
    {
        using var isolated = new IsolatedNotificationRoot();
        var status = new NotificationChannelStatus(
            NotificationChannelId.Toast,
            [NotificationCapability.PRESENT],
            NotificationChannelHealth.BLOCKED,
            Now,
            NotificationErrorCodes.ChannelBlocked);
        var channel = new FakeNotificationChannel(status, NotificationDeliveryOutcome.DELIVERED);
        var catalog = new FakeNotificationChannelCatalog(channel);
        var store = new FileDeliveryAttemptStore(isolated.Path);
        var coordinator = NewCoordinator(catalog, store);
        var request = NewRequest(NotificationChannelId.Toast);

        var blocked = await coordinator.DispatchAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.True(blocked.CoreTriggerWasRecorded);
        Assert.True(blocked.ChannelWasBlockedOrUnavailable);
        Assert.Equal(NotificationDeliveryOutcome.BLOCKED, blocked.Outcome);
        Assert.Equal(NotificationErrorCodes.ChannelBlocked, blocked.Attempt!.ErrorCode);
        Assert.Equal(0, channel.DeliveryCount);
        Assert.Single(await store.ListForInstanceAsync(
            request.CoreTrigger.InstanceId,
            TestContext.Current.CancellationToken));

        var unavailableChannel = new FakeNotificationChannel(
            new NotificationChannelStatus(
                NotificationChannelId.Toast,
                [NotificationCapability.PRESENT],
                NotificationChannelHealth.UNAVAILABLE,
                Now,
                "toast.os_unavailable"),
            NotificationDeliveryOutcome.DELIVERED);
        var unavailableStore = new FileDeliveryAttemptStore(
            System.IO.Path.Combine(isolated.Path, "unavailable"));
        var unavailable = await NewCoordinator(
                new FakeNotificationChannelCatalog(unavailableChannel),
                unavailableStore)
            .DispatchAsync(
                NewRequest(NotificationChannelId.Toast),
                TestContext.Current.CancellationToken);

        Assert.True(unavailable.CoreTriggerWasRecorded);
        Assert.True(unavailable.ChannelWasBlockedOrUnavailable);
        Assert.Equal(NotificationDeliveryOutcome.UNAVAILABLE, unavailable.Outcome);
        Assert.Equal("toast.os_unavailable", unavailable.Attempt!.ErrorCode);
        Assert.Equal(0, unavailableChannel.DeliveryCount);
    }

    [Fact]
    public async Task MissingCapabilityIsNotReportedAsSuccessfulPresentation()
    {
        using var isolated = new IsolatedNotificationRoot();
        var channel = new FakeNotificationChannel(
            new NotificationChannelStatus(
                NotificationChannelId.Toast,
                [NotificationCapability.READ_ON_CLOSE],
                NotificationChannelHealth.HEALTHY,
                Now),
            NotificationDeliveryOutcome.DELIVERED);
        var store = new FileDeliveryAttemptStore(isolated.Path);
        var result = await NewCoordinator(
                new FakeNotificationChannelCatalog(channel),
                store)
            .DispatchAsync(
                NewRequest(NotificationChannelId.Toast),
                TestContext.Current.CancellationToken);

        Assert.True(result.CoreTriggerWasRecorded);
        Assert.Equal(NotificationDeliveryOutcome.NOT_ATTEMPTED, result.Outcome);
        Assert.Equal(NotificationErrorCodes.CapabilityMissing, result.Attempt!.ErrorCode);
        Assert.False(result.ChannelWasBlockedOrUnavailable);
        Assert.Equal(0, channel.DeliveryCount);
    }

    [Fact]
    public async Task SuccessFailureRetryAndDuplicateAreAppendOnly()
    {
        using var isolated = new IsolatedNotificationRoot();
        var channel = new FakeNotificationChannel(
            new NotificationChannelStatus(
                NotificationChannelId.Toast,
                [NotificationCapability.PRESENT],
                NotificationChannelHealth.HEALTHY,
                Now),
            NotificationDeliveryOutcome.DELIVERED);
        var store = new FileDeliveryAttemptStore(isolated.Path);
        var coordinator = NewCoordinator(new FakeNotificationChannelCatalog(channel), store);
        var request = NewRequest(NotificationChannelId.Toast);

        var first = await coordinator.DispatchAsync(
            request,
            TestContext.Current.CancellationToken);
        var duplicate = await coordinator.DispatchAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryOutcome.DELIVERED, first.Outcome);
        Assert.Equal(NotificationAttemptRecordDisposition.APPENDED, first.RecordDisposition);
        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(NotificationAttemptRecordDisposition.REPLAYED, duplicate.RecordDisposition);
        Assert.Equal(first.Attempt, duplicate.Attempt);
        Assert.Equal(1, channel.DeliveryCount);
        Assert.Single(await store.ListForInstanceAsync(
            request.CoreTrigger.InstanceId,
            TestContext.Current.CancellationToken));

        channel.Response = new NotificationChannelDeliveryResponse(
            NotificationDeliveryOutcome.FAILED,
            "adapter.timeout");
        var retry = await coordinator.DispatchAsync(
            new NotificationDeliveryRequest(
                request.CoreTrigger,
                request.ChannelId,
                request.CorrelationId,
                Id("0191f6a4-3b25-7c12-8d34-56789abcde14")),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryOutcome.FAILED, retry.Outcome);
        Assert.Equal("adapter.timeout", retry.Attempt!.ErrorCode);
        Assert.Equal(2, channel.DeliveryCount);
        Assert.Equal(
            2,
            (await store.ListForInstanceAsync(
                request.CoreTrigger.InstanceId,
                TestContext.Current.CancellationToken)).Count);
        Assert.Equal(2, File.ReadAllLines(store.AttemptFilePath, Encoding.UTF8).Length);
    }

    [Fact]
    public async Task PolicySuppressionIsASeparateAppendOnlyOutcome()
    {
        using var isolated = new IsolatedNotificationRoot();
        var channel = new FakeNotificationChannel(
            new NotificationChannelStatus(
                NotificationChannelId.Toast,
                [NotificationCapability.PRESENT],
                NotificationChannelHealth.HEALTHY,
                Now),
            NotificationDeliveryOutcome.DELIVERED);
        var store = new FileDeliveryAttemptStore(isolated.Path);
        var coordinator = new NotificationDeliveryCoordinator(
            new FakeNotificationChannelCatalog(channel),
            store,
            new SuppressPolicy(),
            new FixedClock(Now));

        var result = await coordinator.DispatchAsync(
            NewRequest(NotificationChannelId.Toast),
            TestContext.Current.CancellationToken);

        Assert.True(result.CoreTriggerWasRecorded);
        Assert.Equal(NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS, result.Outcome);
        Assert.Equal(NotificationErrorCodes.PolicySuppressedQuietHours, result.Attempt!.ErrorCode);
        Assert.Equal(0, channel.DeliveryCount);
    }

    [Fact]
    public async Task RestartReplaysPersistedAttemptWithoutCallingChannelAgain()
    {
        using var isolated = new IsolatedNotificationRoot();
        var firstChannel = new FakeNotificationChannel(
            new NotificationChannelStatus(
                NotificationChannelId.Toast,
                [NotificationCapability.PRESENT],
                NotificationChannelHealth.HEALTHY,
                Now),
            NotificationDeliveryOutcome.DELIVERED);
        var request = NewRequest(NotificationChannelId.Toast);
        var firstStore = new FileDeliveryAttemptStore(isolated.Path);
        var first = await NewCoordinator(
                new FakeNotificationChannelCatalog(firstChannel),
                firstStore)
            .DispatchAsync(request, TestContext.Current.CancellationToken);

        var restartedChannel = new FakeNotificationChannel(
            new NotificationChannelStatus(
                NotificationChannelId.Toast,
                [NotificationCapability.PRESENT],
                NotificationChannelHealth.HEALTHY,
                Now),
            NotificationDeliveryOutcome.FAILED);
        var restartedStore = new FileDeliveryAttemptStore(isolated.Path);
        var replay = await NewCoordinator(
                new FakeNotificationChannelCatalog(restartedChannel),
                restartedStore)
            .DispatchAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(first.Attempt, replay.Attempt);
        Assert.True(replay.WasDuplicate);
        Assert.Equal(0, restartedChannel.DeliveryCount);
        Assert.Single(await restartedStore.ListForInstanceAsync(
            request.CoreTrigger.InstanceId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReusingAnIdempotencyKeyForDifferentIntentIsRejected()
    {
        using var isolated = new IsolatedNotificationRoot();
        var channel = new FakeNotificationChannel(
            new NotificationChannelStatus(
                NotificationChannelId.Toast,
                [NotificationCapability.PRESENT],
                NotificationChannelHealth.HEALTHY,
                Now),
            NotificationDeliveryOutcome.DELIVERED);
        var store = new FileDeliveryAttemptStore(isolated.Path);
        var coordinator = NewCoordinator(new FakeNotificationChannelCatalog(channel), store);
        var request = NewRequest(NotificationChannelId.Toast);
        await coordinator.DispatchAsync(request, TestContext.Current.CancellationToken);

        var conflictingCore = new NotificationTriggerFact(
            request.CoreTrigger.InstanceId,
            request.CoreTrigger.ScheduleId,
            request.CoreTrigger.RuleId,
            request.CoreTrigger.OccurrenceId,
            request.CoreTrigger.LogicalReminderId,
            request.CoreTrigger.AttemptOrdinal,
            NotificationPurposeSnapshot.TaskRangeEnd,
            request.CoreTrigger.PrioritySnapshot,
            request.CoreTrigger.PinnedSnapshot,
            request.CoreTrigger.TriggeredAtUtc);
        var conflictingRequest = new NotificationDeliveryRequest(
            conflictingCore,
            request.ChannelId,
            request.CorrelationId,
            request.IdempotencyKey);

        var exception = await Assert.ThrowsAsync<NotificationContractException>(async () =>
            await coordinator.DispatchAsync(
                conflictingRequest,
                TestContext.Current.CancellationToken));
        Assert.Equal(NotificationErrorCodes.IdempotencyConflict, exception.Code);
        Assert.Equal(1, channel.DeliveryCount);
        Assert.Single(await store.ListForInstanceAsync(
            request.CoreTrigger.InstanceId,
            TestContext.Current.CancellationToken));
    }

    private static NotificationDeliveryCoordinator NewCoordinator(
        INotificationChannelCatalog catalog,
        INotificationDeliveryAttemptStore store) =>
        new(catalog, store, new AllowAllNotificationPresentationPolicy(), new FixedClock(Now));

    private static NotificationDeliveryRequest NewRequest(NotificationChannelId channelId)
    {
        var core = new NotificationTriggerFact(
            Id("0191f6a4-3b25-7c12-8d34-56789abcde01"),
            Id("0191f6a4-3b25-7c12-8d34-56789abcde02"),
            Id("0191f6a4-3b25-7c12-8d34-56789abcde03"),
            Id("0191f6a4-3b25-7c12-8d34-56789abcde04"),
            Id("0191f6a4-3b25-7c12-8d34-56789abcde05"),
            1,
            NotificationPurposeSnapshot.TaskStart,
            NotificationPriority.NORMAL,
            false,
            Now);
        return new NotificationDeliveryRequest(
            core,
            channelId,
            Id("0191f6a4-3b25-7c12-8d34-56789abcde12"),
            Id("0191f6a4-3b25-7c12-8d34-56789abcde13"));
    }

    private static Guid Id(string text) => Guid.Parse(text);

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private sealed class FakeNotificationChannel(
        NotificationChannelStatus status,
        NotificationDeliveryOutcome initialOutcome) : INotificationChannel
    {
        public NotificationChannelStatus Status { get; } = status;

        public NotificationDeliveryResponseState ResponseState { get; } =
            new(initialOutcome);

        public NotificationChannelDeliveryResponse Response
        {
            get => ResponseState.Response;
            set => ResponseState.Response = value;
        }

        public int DeliveryCount { get; private set; }

        public NotificationChannelId ChannelId => Status.ChannelId;

        public NotificationChannelStatus GetStatus() => Status;

        public ValueTask<NotificationChannelDeliveryResponse> DeliverAsync(
            NotificationDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            DeliveryCount++;
            return ValueTask.FromResult(Response);
        }
    }

    private sealed class NotificationDeliveryResponseState(NotificationDeliveryOutcome outcome)
    {
        public NotificationChannelDeliveryResponse Response { get; set; } =
            new(outcome);
    }

    private sealed class FakeNotificationChannelCatalog(FakeNotificationChannel channel)
        : INotificationChannelCatalog
    {
        public bool TryGet(NotificationChannelId channelId, out INotificationChannel result)
        {
            if (channelId == channel.ChannelId)
            {
                result = channel;
                return true;
            }

            result = null!;
            return false;
        }
    }

    private sealed class SuppressPolicy : INotificationPresentationPolicy
    {
        public NotificationPresentationDecision Evaluate(NotificationPresentationContext context) =>
            NotificationPresentationDecision.SuppressedQuietHours(
                NotificationErrorCodes.PolicySuppressedQuietHours);
    }

    private sealed class IsolatedNotificationRoot : IDisposable
    {
        public IsolatedNotificationRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ReminNote.P3_04",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FileDeliveryAttemptStore : INotificationDeliveryAttemptStore
    {
        private readonly Dictionary<DeliveryKey, NotificationDeliveryAttempt> attempts = [];
        private readonly List<NotificationDeliveryAttempt> ordered = [];

        public FileDeliveryAttemptStore(string root)
        {
            Directory.CreateDirectory(root);
            AttemptFilePath = System.IO.Path.Combine(root, "delivery-attempts.jsonl");
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
            attempts.TryGetValue(new DeliveryKey(instanceId, channelId, idempotencyKey), out var attempt);
            return ValueTask.FromResult(attempt);
        }

        public ValueTask<NotificationDeliveryAppendResult> AppendAsync(
            NotificationDeliveryAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            var key = DeliveryKey.For(attempt);
            if (attempts.TryGetValue(key, out var existing))
            {
                return ValueTask.FromResult(NotificationDeliveryAppendResult.Replayed(existing));
            }

            attempts.Add(key, attempt);
            ordered.Add(attempt);
            File.AppendAllText(
                AttemptFilePath,
                NotificationContractJson.SerializeDeliveryAttemptText(attempt) + Environment.NewLine,
                Utf8NoBom);
            return ValueTask.FromResult(NotificationDeliveryAppendResult.Appended(attempt));
        }

        public ValueTask<IReadOnlyList<NotificationDeliveryAttempt>> ListForInstanceAsync(
            Guid instanceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<NotificationDeliveryAttempt>>(
                ordered.Where(attempt => attempt.InstanceId == instanceId).ToArray());

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

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
