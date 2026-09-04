using ReminNote.Agent.Notifications;
using ReminNote.Agent.Scheduling;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Notifications.Adapters;
using ReminNote.Core.Reminders.Policy;
using ReminNote.Infrastructure.Persistence.Reminders;
using ReminNote.Tests.P25Storage;

#pragma warning disable CA1707 // Slice directory and test names mirror the frozen plan.
namespace ReminNote.Tests.P3_04;

public sealed class NotificationDeliveryRuntimeTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 4, 8, 0);

    [Fact]
    public void CompositeCatalogCombinesWindowsAndWidgetHostChannels()
    {
        var windows = new NotificationChannelCatalog(
        [
            NewChannel(NotificationChannelId.Toast),
            NewChannel(NotificationChannelId.Tray),
            NewChannel(NotificationChannelId.Sound),
            NewChannel(NotificationChannelId.WakeTimer)
        ]);
        var widget = new NotificationChannelCatalog(
            [NewChannel(NotificationChannelId.Widget)]);

        var composite = new CompositeNotificationChannelCatalog([windows, widget]);

        Assert.Equal(
            NotificationChannels.P3.OrderBy(channel => channel.Value),
            composite.ChannelIds.OrderBy(channel => channel.Value));
        Assert.All(NotificationChannels.P3, channel =>
            Assert.True(composite.TryGet(channel, out _)));
    }

    [Fact]
    public async Task SchedulerCommitPrecedesChannelAndRetryNeverCreatesAnotherInstance()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        var attemptStore = new SqliteNotificationDeliveryAttemptStore(
            fixture.Store,
            P25StorageFixture.UserSid);
        await attemptStore.InitializeSchemaForFixtureAsync(
            TestContext.Current.CancellationToken);

        var schedulerStore = new CommitTrackingSchedulerStore(Now);
        var channel = new RecordingChannel(
            NotificationChannelId.Toast,
            new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.FAILED,
                "adapter.timeout"),
            () => schedulerStore.CoreCommitCompleted);
        var catalog = new NotificationChannelCatalog([channel]);
        var clock = new MutableClock(Now);
        await using var scheduler = new ReminderScheduler(
            schedulerStore,
            clock,
            identityGenerator: new FixedIdentityGenerator(110, 120));
        await using var dispatcher = new NotificationDeliveryDispatcher(
            catalog,
            attemptStore,
            new AllowAllNotificationPresentationPolicy(),
            clock,
            new NotificationRetryPolicy(
                maxAttempts: 3,
                initialBackoff: TimeSpan.FromSeconds(5),
                maximumBackoff: TimeSpan.FromSeconds(30)));
        await using var runtime = new ReminderNotificationRuntime(
            scheduler,
            dispatcher,
            new CatalogNotificationChannelSelector(catalog, [NotificationChannelId.Toast]));

        var first = await runtime.RunDueCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, schedulerStore.CommitCount);
        Assert.Equal(1, schedulerStore.InstanceCount);
        Assert.True(schedulerStore.CoreCommitCompleted);
        Assert.Single(first.Deliveries);
        Assert.True(first.Deliveries[0].CoreTriggerWasRecorded);
        Assert.Equal(NotificationDeliveryOutcome.FAILED, first.Deliveries[0].Outcome);
        Assert.True(first.Deliveries[0].Attempt!.Retryable);
        Assert.Equal(1, channel.DeliveryCount);
        Assert.Equal(channel.Requests[0].RequestId, first.Deliveries[0].Attempt!.RequestId);
        Assert.All(channel.Requests, _ => Assert.True(schedulerStore.CoreCommitCompleted));

        var originalInstanceId = first.Deliveries[0].CoreTrigger.InstanceId;
        var originalAttemptId = first.Deliveries[0].Attempt!.AttemptId;
        clock.Current = Now + Duration.FromSeconds(6);
        channel.Response = new NotificationChannelDeliveryResponse(
            NotificationDeliveryOutcome.DELIVERED);

        var recovery = await runtime.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, schedulerStore.CommitCount);
        Assert.Equal(1, schedulerStore.InstanceCount);
        Assert.Single(recovery.Deliveries);
        Assert.Equal(NotificationDeliveryOutcome.DELIVERED, recovery.Deliveries[0].Outcome);
        Assert.Equal(originalInstanceId, recovery.Deliveries[0].CoreTrigger.InstanceId);
        Assert.Equal(2, recovery.Deliveries[0].Attempt!.AttemptNumber);
        Assert.NotEqual(originalAttemptId, recovery.Deliveries[0].Attempt!.AttemptId);
        Assert.Equal(2, channel.DeliveryCount);
        Assert.Equal(channel.Requests[1].RequestId, recovery.Deliveries[0].Attempt!.RequestId);
        Assert.Equal(
            2,
            (await attemptStore.ListForInstanceAsync(
                originalInstanceId,
                TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task RecoveryReconcilesCommittedTriggerWhenProcessStoppedBeforeAttemptAppend()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        var attemptStore = new SqliteNotificationDeliveryAttemptStore(
            fixture.Store,
            P25StorageFixture.UserSid);
        await attemptStore.InitializeSchemaForFixtureAsync(
            TestContext.Current.CancellationToken);

        var schedulerStore = new CommitTrackingSchedulerStore(Now);
        var clock = new MutableClock(Now);
        await using var scheduler = new ReminderScheduler(
            schedulerStore,
            clock,
            identityGenerator: new FixedIdentityGenerator(130, 140));
        var schedulerRun = await scheduler.RunDueCycleAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var committedTrigger = Assert.Single(schedulerRun.DueResults).TriggerFact;
        Assert.NotNull(committedTrigger);
        Assert.Equal(1, schedulerStore.CommitCount);
        Assert.Equal(1, schedulerStore.InstanceCount);

        var channel = new RecordingChannel(
            NotificationChannelId.Toast,
            new NotificationChannelDeliveryResponse(NotificationDeliveryOutcome.DELIVERED));
        var catalog = new NotificationChannelCatalog([channel]);
        await using var dispatcher = new NotificationDeliveryDispatcher(
            catalog,
            attemptStore,
            new AllowAllNotificationPresentationPolicy(),
            clock);
        await using var runtime = new ReminderNotificationRuntime(
            scheduler,
            dispatcher,
            new CatalogNotificationChannelSelector(catalog, [NotificationChannelId.Toast]),
            new FixedCommittedTriggerSource(committedTrigger!));

        var recovery = await runtime.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Single(recovery.Deliveries);
        Assert.Equal(NotificationDeliveryOutcome.DELIVERED, recovery.Deliveries[0].Outcome);
        Assert.Equal(1, channel.DeliveryCount);
        Assert.Equal(1, schedulerStore.CommitCount);
        Assert.Equal(1, schedulerStore.InstanceCount);
        Assert.Single(await attemptStore.ListForInstanceAsync(
            committedTrigger!.InstanceId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonRetryableChannelFailureIsTerminalAndSafeModeAvoidsWritesAndEffects()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        var attemptStore = new SqliteNotificationDeliveryAttemptStore(
            fixture.Store,
            P25StorageFixture.UserSid);
        await attemptStore.InitializeSchemaForFixtureAsync(
            TestContext.Current.CancellationToken);

        var channel = new RecordingChannel(
            NotificationChannelId.Toast,
            new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.FAILED,
                NotificationErrorCodes.CapabilityMissing));
        var catalog = new NotificationChannelCatalog([channel]);
        var gate = new NotificationDeliverySafetyGate();
        await using var dispatcher = new NotificationDeliveryDispatcher(
            catalog,
            attemptStore,
            new AllowAllNotificationPresentationPolicy(),
            new MutableClock(Now),
            safetyGate: gate);
        var request = NewRequest(NotificationChannelId.Toast);

        var terminal = await dispatcher.DispatchAsync(
            request,
            TestContext.Current.CancellationToken);
        Assert.Equal(NotificationDurableDispatchDisposition.APPENDED, terminal.Disposition);
        Assert.False(terminal.Attempt!.Retryable);
        Assert.Equal(1, channel.DeliveryCount);
        Assert.Empty(await attemptStore.ListRecoverableAsync(
            Now + Duration.FromHours(1),
            TestContext.Current.CancellationToken));

        var terminalRetry = await dispatcher.DispatchAsync(
            request.CreateRetry(),
            TestContext.Current.CancellationToken);
        Assert.Equal(NotificationDurableDispatchDisposition.TERMINAL, terminalRetry.Disposition);
        Assert.Equal(1, channel.DeliveryCount);

        gate.Enter("notification.safe_mode");
        var safeModeRequest = NewRequest(NotificationChannelId.Tray);
        var safeMode = await dispatcher.DispatchAsync(
            safeModeRequest,
            TestContext.Current.CancellationToken);
        Assert.True(safeMode.WasSkippedBySafeMode);
        Assert.False(safeMode.ChannelInvoked);
        Assert.Null(safeMode.Attempt);
        Assert.Equal(1, channel.DeliveryCount);
        Assert.Null(await attemptStore.FindCurrentAsync(
            safeModeRequest.CoreTrigger.InstanceId,
            safeModeRequest.CoreTrigger.LogicalReminderId,
            safeModeRequest.ChannelId,
            TestContext.Current.CancellationToken));
    }

    private static NotificationDeliveryRequest NewRequest(NotificationChannelId channelId)
    {
        var core = new NotificationTriggerFact(
            Id(1),
            Id(2),
            Id(3),
            Id(4),
            Id(5),
            attemptOrdinal: 1,
            NotificationPurposeSnapshot.TaskStart,
            NotificationPriority.NORMAL,
            pinnedSnapshot: false,
            Now,
            Now - Duration.FromMinutes(1));
        return new NotificationDeliveryRequest(
            core,
            channelId,
            Id(12),
            Id(channelId == NotificationChannelId.Toast ? 13 : 23),
            Id(channelId == NotificationChannelId.Toast ? 14 : 24));
    }

    private static Guid Id(int suffix) =>
        Guid.Parse($"0191f6a4-3b25-7c12-8d34-56789abcde{suffix:X2}");

    private static RecordingChannel NewChannel(NotificationChannelId channelId) =>
        new(
            channelId,
            new NotificationChannelDeliveryResponse(NotificationDeliveryOutcome.DELIVERED));

    private sealed class MutableClock(Instant now) : IClock
    {
        public Instant Current { get; set; } = now;

        public Instant GetCurrentInstant() => Current;
    }

    private sealed class FixedIdentityGenerator(int instanceSuffix, int scheduleSuffix)
        : IReminderIdentityGenerator
    {
        private int nextInstanceSuffix = instanceSuffix;
        private int nextScheduleSuffix = scheduleSuffix;

        public ReminderInstanceId NewInstanceId() =>
            ReminderInstanceId.From(Id(nextInstanceSuffix++));

        public ReminderScheduleId NewScheduleId() =>
            ReminderScheduleId.From(Id(nextScheduleSuffix++));
    }

    private sealed class RecordingChannel(
        NotificationChannelId channelId,
        NotificationChannelDeliveryResponse response,
        Func<bool>? canInvoke = null) : INotificationChannel
    {
        private readonly NotificationChannelStatus status = new(
            channelId,
            [NotificationCapability.PRESENT],
            NotificationChannelHealth.HEALTHY,
            Now);

        public NotificationChannelDeliveryResponse Response { get; set; } = response;

        public List<NotificationDeliveryRequest> Requests { get; } = [];

        public int DeliveryCount => Requests.Count;

        public NotificationChannelId ChannelId => channelId;

        public NotificationChannelStatus GetStatus() => status;

        public ValueTask<NotificationChannelDeliveryResponse> DeliverAsync(
            NotificationDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (canInvoke is not null && !canInvoke())
            {
                throw new InvalidOperationException("The core trigger was not committed before delivery.");
            }

            Requests.Add(request);
            return ValueTask.FromResult(Response);
        }
    }

    private sealed class FixedCommittedTriggerSource(
        NotificationTriggerFact committedTrigger)
        : INotificationCommittedTriggerRecoverySource
    {
        public ValueTask<IReadOnlyList<NotificationTriggerFact>> ListCommittedTriggerFactsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<NotificationTriggerFact>>([committedTrigger]);
        }
    }

    private sealed class CommitTrackingSchedulerStore
        : IReminderSchedulerStore
    {
        private readonly ReminderRule rule;
        private readonly ReminderTargetState target;
        private readonly object sync = new();
        private ReminderSchedule schedule;
        private ReminderInstance? instance;

        public CommitTrackingSchedulerStore(Instant now)
        {
            var taskId = Id(101);
            rule = ReminderRule.CreateForTask(
                taskId,
                ReminderPurpose.TASK_CUSTOM,
                ReminderTiming.AbsoluteUtc(now - Duration.FromSeconds(1)),
                ReminderPriority.NORMAL,
                pinned: false,
                RepeatPolicy.Disabled,
                WakePolicy.DEFAULT,
                enabled: true,
                now - Duration.FromMinutes(1));
            schedule = ReminderSchedule.CreateFromRule(
                rule,
                LogicalReminderId.From(Id(102)),
                scheduleRevision: 1,
                triggerAtUtc: now - Duration.FromSeconds(1),
                createdAtUtc: now - Duration.FromMinutes(1),
                timeZoneId: "Asia/Shanghai",
                id: ReminderScheduleId.From(Id(103)));
            target = new ReminderTargetState(
                taskId,
                rule.OccurrenceId,
                TaskStartAtUtc: null,
                OccurrenceCancelled: false,
                TaskResultRecorded: false,
                CustomReminderMeaningful: true);
        }

        public int BeginCount { get; private set; }

        public int CommitCount { get; private set; }

        public bool CoreCommitCompleted { get; private set; }

        public int InstanceCount => instance is null ? 0 : 1;

        public ValueTask<IReadOnlyList<ReminderPendingSchedule>> ReadPendingAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                IReadOnlyList<ReminderPendingSchedule> result = schedule.IsPending
                    ? [new ReminderPendingSchedule(schedule, rule)]
                    : [];
                return ValueTask.FromResult(result);
            }
        }

        public ValueTask<IReminderDueTransaction?> BeginDueTransactionAsync(
            ReminderScheduleId scheduleId,
            Instant observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _ = observedAtUtc;
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (scheduleId != schedule.Id || !schedule.IsPending)
                {
                    return ValueTask.FromResult<IReminderDueTransaction?>(null);
                }

                BeginCount++;
                return ValueTask.FromResult<IReminderDueTransaction?>(
                    new FakeDueTransaction(this));
            }
        }

        private ReminderDueCommitResult Commit(ReminderDueMutation mutation)
        {
            lock (sync)
            {
                if (!schedule.IsPending)
                {
                    return new(
                        ReminderDueCommitDisposition.NOT_DUE,
                        schedule.State,
                        instance,
                        null,
                        ReminderSchedulerCodes.NotClaimed);
                }

                if (mutation.Kind != ReminderDueMutationKind.TRIGGER || mutation.Instance is null)
                {
                    throw new InvalidOperationException("The P3-04 fixture expects one trigger mutation.");
                }

                schedule.Consume(mutation.AtUtc);
                instance = mutation.Instance;
                CommitCount++;
                CoreCommitCompleted = true;
                return new(
                    ReminderDueCommitDisposition.COMMITTED,
                    schedule.State,
                    instance,
                    null,
                    mutation.ReasonCode);
            }
        }

        private sealed class FakeDueTransaction(CommitTrackingSchedulerStore owner)
            : IReminderDueTransaction
        {
            private bool completed;

            public ReminderSchedule Schedule => owner.schedule;

            public ReminderRule? Rule => owner.rule;

            public ReminderTargetState? Target => owner.target;

            public ReminderInstance? ExistingInstance => owner.instance;

            public int NextAttemptOrdinal => owner.instance is null
                ? 1
                : checked(owner.instance.AttemptOrdinal + 1);

            public long NextScheduleRevision => owner.schedule.ScheduleRevision + 1;

            public ValueTask<ReminderDueCommitResult> CommitAsync(
                ReminderDueMutation mutation,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completed)
                {
                    throw new InvalidOperationException("Due transaction is already closed.");
                }

                completed = true;
                return ValueTask.FromResult(owner.Commit(mutation));
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                completed = true;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

#pragma warning restore CA1707
