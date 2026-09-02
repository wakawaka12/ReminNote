using NodaTime;
using ReminNote.Agent.Scheduling;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Reminders.Policy;

#pragma warning disable CA1707 // Slice directory and test names mirror the frozen plan.
namespace ReminNote.Tests.P3_03;

public sealed class ReminderSchedulerTests
{
    private static readonly Instant CreatedAt = Instant.FromUtc(2026, 9, 2, 8, 0);
    private static readonly Instant EvaluatedAt = Instant.FromUtc(2026, 9, 2, 9, 0);

    [Fact]
    public async Task DueCycleConsumesEveryDueRowAndRecomputesNearestPendingWakeup()
    {
        using var root = new IsolatedTempRoot();
        var store = new InMemorySchedulerStore(root.Path);
        var clock = new MutableClock(EvaluatedAt);

        var first = AddTaskSchedule(
            store,
            10,
            ReminderPurpose.TASK_START,
            EvaluatedAt - Duration.FromMinutes(20));
        var second = AddTaskSchedule(
            store,
            30,
            ReminderPurpose.TASK_CUSTOM,
            EvaluatedAt - Duration.FromMinutes(10));
        var future = AddTaskSchedule(
            store,
            50,
            ReminderPurpose.TASK_RANGE_END,
            EvaluatedAt + Duration.FromMinutes(30));

        await using var scheduler = new ReminderScheduler(
            store,
            clock,
            identityGenerator: new FixedIdentityGenerator(200, 220));

        var run = await scheduler.RunDueCycleAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(run.IsRecovery);
        Assert.Equal(2, run.ProcessedCount);
        Assert.Equal(2, run.TriggeredCount);
        Assert.Equal(future.Schedule.TriggerAtUtc, run.NextWakeupUtc);
        Assert.Equal(ScheduleState.CONSUMED, first.Schedule.State);
        Assert.Equal(ScheduleState.CONSUMED, second.Schedule.State);
        Assert.Equal(ScheduleState.PENDING, future.Schedule.State);
        Assert.Equal(2, store.InstanceCount);
        Assert.All(run.DueResults, result =>
        {
            Assert.Equal(ReminderDueResultKind.TRIGGERED, result.Kind);
            Assert.Equal(ReminderSchedulerCodes.DueTriggered, result.ReasonCode);
            Assert.True(result.ShouldDispatch);
            Assert.NotNull(result.Instance);
            Assert.NotNull(result.TriggerFact);
            Assert.Equal(result.Instance!.Id.Value, result.TriggerFact!.InstanceId);
        });
        Assert.True(File.Exists(Path.Combine(root.Path, "scheduler-fake.log")));
    }

    [Fact]
    public async Task ConcurrentScanAndFreshSchedulerRecoveryDoNotDuplicateCoreInstance()
    {
        using var root = new IsolatedTempRoot();
        var store = new InMemorySchedulerStore(root.Path);
        var clock = new MutableClock(EvaluatedAt);
        var fixture = AddTaskSchedule(
            store,
            70,
            ReminderPurpose.TASK_START,
            EvaluatedAt - Duration.FromMinutes(5));
        var identityGenerator = new FixedIdentityGenerator(201, 221);

        await using (var scheduler = new ReminderScheduler(store, clock, identityGenerator: identityGenerator))
        {
            var runs = await Task.WhenAll(
                scheduler.RunDueCycleAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask(),
                scheduler.RunDueCycleAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(1, runs.Sum(run => run.ProcessedCount));
            Assert.Equal(1, runs.Sum(run => run.TriggeredCount));
        }

        await using var restartedScheduler = new ReminderScheduler(
            store,
            clock,
            identityGenerator: identityGenerator);
        var recovery = await restartedScheduler.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.True(recovery.IsRecovery);
        Assert.Empty(recovery.DueResults);
        Assert.Equal(1, store.InstanceCount);
        Assert.Equal(ScheduleState.CONSUMED, fixture.Schedule.State);
        Assert.Equal(1, store.CommitCount);
        Assert.Equal(1, store.BeginCount);
    }

    [Fact]
    public async Task PendingScheduleWithExistingInstanceIsConsumedAsDuplicate()
    {
        using var root = new IsolatedTempRoot();
        var store = new InMemorySchedulerStore(root.Path);
        var clock = new MutableClock(EvaluatedAt);
        var fixture = AddTaskSchedule(
            store,
            80,
            ReminderPurpose.TASK_START,
            EvaluatedAt - Duration.FromMinutes(5));
        var existing = ReminderInstance.CreateFromSchedule(
            fixture.Schedule,
            attemptOrdinal: 1,
            triggeredAtUtc: EvaluatedAt - Duration.FromMinutes(4),
            id: ReminderInstanceId.From(Id(240)));
        store.SeedInstance(fixture.Schedule.Id, existing);

        await using var scheduler = new ReminderScheduler(
            store,
            clock,
            identityGenerator: new FixedIdentityGenerator(207, 227));

        var result = Assert.Single(
            (await scheduler.RunDueCycleAsync(cancellationToken: TestContext.Current.CancellationToken)).DueResults);

        Assert.Equal(ReminderDueResultKind.DUPLICATE, result.Kind);
        Assert.Equal(ReminderPolicyCodes.RecoveryDuplicate, result.ReasonCode);
        Assert.Equal(existing.Id, result.Instance!.Id);
        Assert.False(result.ShouldDispatch);
        Assert.Null(result.TriggerFact);
        Assert.Equal(ScheduleState.CONSUMED, fixture.Schedule.State);
        Assert.Equal(1, store.InstanceCount);
    }

    [Fact]
    public async Task RecoveryExpiresObsoletePreStartAndTriggersNormalTaskStartSummary()
    {
        using var root = new IsolatedTempRoot();
        var store = new InMemorySchedulerStore(root.Path);
        var clock = new MutableClock(EvaluatedAt);

        var preStart = AddTaskSchedule(
            store,
            90,
            ReminderPurpose.TASK_PRE_START,
            EvaluatedAt - Duration.FromMinutes(40),
            taskStartAtUtc: EvaluatedAt - Duration.FromMinutes(5));
        var taskStart = AddTaskSchedule(
            store,
            110,
            ReminderPurpose.TASK_START,
            EvaluatedAt - Duration.FromMinutes(30),
            taskStartAtUtc: EvaluatedAt - Duration.FromMinutes(5));

        await using var scheduler = new ReminderScheduler(
            store,
            clock,
            identityGenerator: new FixedIdentityGenerator(202, 222));

        var run = await scheduler.RecoverAsync(TestContext.Current.CancellationToken);

        var expired = Assert.Single(run.DueResults, result => result.ScheduleId == preStart.Schedule.Id);
        Assert.Equal(ReminderDueResultKind.EXPIRED, expired.Kind);
        Assert.Equal(ScheduleState.EXPIRED, expired.FinalScheduleState);
        Assert.Equal(ReminderPolicyCodes.RecoveryPreStartObsolete, expired.ReasonCode);
        Assert.Null(expired.Instance);
        Assert.Null(expired.TriggerFact);

        var summarized = Assert.Single(run.DueResults, result => result.ScheduleId == taskStart.Schedule.Id);
        Assert.Equal(ReminderDueResultKind.TRIGGERED, summarized.Kind);
        Assert.Equal(ReminderPolicyCodes.RecoveryStartSummary, summarized.ReasonCode);
        Assert.True(summarized.ShouldDispatch);
        Assert.NotNull(summarized.TriggerFact);
        Assert.Equal(ScheduleState.EXPIRED, preStart.Schedule.State);
        Assert.Equal(ScheduleState.CONSUMED, taskStart.Schedule.State);
        Assert.Equal(1, store.InstanceCount);
    }

    [Fact]
    public async Task TaskResultRecordedCancelsDueScheduleWithoutCreatingCoreInstance()
    {
        using var root = new IsolatedTempRoot();
        var store = new InMemorySchedulerStore(root.Path);
        var fixture = AddTaskSchedule(
            store,
            130,
            ReminderPurpose.TASK_CUSTOM,
            EvaluatedAt - Duration.FromMinutes(2),
            taskResultRecorded: true);

        await using var scheduler = new ReminderScheduler(
            store,
            new MutableClock(EvaluatedAt),
            identityGenerator: new FixedIdentityGenerator(203, 223));

        var run = await scheduler.RunDueCycleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = Assert.Single(run.DueResults);

        Assert.Equal(ReminderDueResultKind.CANCELLED, result.Kind);
        Assert.Equal(ReminderPolicyCodes.RecoveryTaskResultRecorded, result.ReasonCode);
        Assert.Equal(ScheduleState.CANCELLED, result.FinalScheduleState);
        Assert.Equal(ScheduleState.CANCELLED, fixture.Schedule.State);
        Assert.Null(result.Instance);
        Assert.False(result.ShouldDispatch);
        Assert.Equal(0, store.InstanceCount);
    }

    [Fact]
    public async Task RepeatCreatesDurableDerivedScheduleWithMonotonicOrdinalAndRevision()
    {
        using var root = new IsolatedTempRoot();
        var store = new InMemorySchedulerStore(root.Path);
        var clock = new MutableClock(EvaluatedAt);
        var fixture = AddTaskSchedule(
            store,
            150,
            ReminderPurpose.TASK_START,
            EvaluatedAt - Duration.FromSeconds(10),
            repeatPolicy: new RepeatPolicy(enabled: true, intervalSeconds: 60, maxCount: 3));
        var identityGenerator = new FixedIdentityGenerator(204, 224);

        await using var scheduler = new ReminderScheduler(store, clock, identityGenerator: identityGenerator);
        var firstRun = await scheduler.RunDueCycleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var firstResult = Assert.Single(firstRun.DueResults);
        Assert.NotNull(firstResult.Instance);
        var firstInstance = firstResult.Instance!;
        Assert.NotNull(firstResult.DerivedSchedule);
        var derived = firstResult.DerivedSchedule!;

        Assert.Equal(1, firstInstance.AttemptOrdinal);
        Assert.Equal(ScheduleCause.REPEAT, derived.Cause);
        Assert.Equal(fixture.Schedule.Id, derived.OriginScheduleId);
        Assert.Equal(fixture.Schedule.ScheduleRevision + 1, derived.ScheduleRevision);
        Assert.Equal(fixture.Schedule.TriggerAtUtc + Duration.FromSeconds(60), derived.TriggerAtUtc);
        Assert.Equal(ScheduleState.PENDING, derived.State);

        clock.Current = derived.TriggerAtUtc;
        var secondRun = await scheduler.RunDueCycleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var secondResult = Assert.Single(secondRun.DueResults);
        Assert.NotNull(secondResult.Instance);
        var secondInstance = secondResult.Instance!;

        Assert.Equal(ReminderDueResultKind.TRIGGERED, secondResult.Kind);
        Assert.Equal(2, secondInstance.AttemptOrdinal);
        Assert.Equal(2, store.InstanceCount);
        Assert.NotNull(secondResult.DerivedSchedule);
        Assert.Equal(3, secondResult.DerivedSchedule!.ScheduleRevision);
        Assert.Equal(fixture.Schedule.TriggerAtUtc + Duration.FromSeconds(120), secondResult.DerivedSchedule.TriggerAtUtc);
    }

    [Fact]
    public async Task RecoveryDrainsAlreadyDueRepeatChainWithinOneCycleButHonorsMaxCount()
    {
        using var root = new IsolatedTempRoot();
        var store = new InMemorySchedulerStore(root.Path);
        var fixture = AddTaskSchedule(
            store,
            20,
            ReminderPurpose.TASK_START,
            EvaluatedAt - Duration.FromMinutes(5),
            repeatPolicy: new RepeatPolicy(enabled: true, intervalSeconds: 60, maxCount: 3));

        await using var scheduler = new ReminderScheduler(
            store,
            new MutableClock(EvaluatedAt),
            identityGenerator: new FixedIdentityGenerator(208, 228));

        var run = await scheduler.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, run.ProcessedCount);
        Assert.Equal(3, run.TriggeredCount);
        Assert.Equal(3, store.InstanceCount);
        Assert.Null(run.NextWakeupUtc);
        Assert.Equal(ScheduleState.CONSUMED, fixture.Schedule.State);
    }

    [Fact]
    public async Task WakeupSelectionSeparatesInProcessTimerFromOsWakePermission()
    {
        using var root = new IsolatedTempRoot();
        var store = new InMemorySchedulerStore(root.Path);
        var clock = new MutableClock(EvaluatedAt);
        var noWake = AddTaskSchedule(
            store,
            170,
            ReminderPurpose.TASK_CUSTOM,
            EvaluatedAt + Duration.FromMinutes(10),
            wakePolicy: WakePolicy.NO);
        var yesWake = AddTaskSchedule(
            store,
            190,
            ReminderPurpose.TASK_CUSTOM,
            EvaluatedAt + Duration.FromMinutes(20),
            wakePolicy: WakePolicy.YES);

        await using var scheduler = new ReminderScheduler(store, clock);

        var inProcessWakeup = await scheduler.FindNextWakeupAsync(TestContext.Current.CancellationToken);
        var osWakeup = await scheduler.FindNearestWakeupAsync(
            new WakeProfile(DefaultAllowsWake: true, SafeMode: false),
            new WakeCapability(OsSupportsWake: true, Healthy: true),
            TestContext.Current.CancellationToken);
        var deniedWakeup = await scheduler.FindNearestWakeupAsync(
            new WakeProfile(DefaultAllowsWake: false, SafeMode: true),
            new WakeCapability(OsSupportsWake: true, Healthy: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(noWake.Schedule.TriggerAtUtc, inProcessWakeup);
        Assert.Equal(yesWake.Schedule.TriggerAtUtc, osWakeup);
        Assert.Null(deniedWakeup);
    }

    [Fact]
    public async Task FailedDueCommitRollsBackWithoutLosingPendingTruth()
    {
        using var root = new IsolatedTempRoot();
        var store = new InMemorySchedulerStore(root.Path) { FailNextCommit = true };
        var clock = new MutableClock(EvaluatedAt);
        var fixture = AddTaskSchedule(
            store,
            210,
            ReminderPurpose.TASK_START,
            EvaluatedAt - Duration.FromMinutes(3));

        await using var scheduler = new ReminderScheduler(
            store,
            clock,
            identityGenerator: new FixedIdentityGenerator(205, 225));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scheduler.RunDueCycleAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ScheduleState.PENDING, fixture.Schedule.State);
        Assert.Equal(0, store.InstanceCount);
        Assert.Equal(1, store.RollbackCount);

        var retry = await scheduler.RunDueCycleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ReminderDueResultKind.TRIGGERED, Assert.Single(retry.DueResults).Kind);
        Assert.Equal(1, store.InstanceCount);
    }

    [Fact]
    public async Task MissingRuleIsClosedAsDeletedInsteadOfBeingTriggered()
    {
        using var root = new IsolatedTempRoot();
        var store = new InMemorySchedulerStore(root.Path);
        var taskId = Id(230);
        var rule = ReminderRule.CreateForTask(
            taskId,
            ReminderPurpose.TASK_START,
            ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
            ReminderPriority.NORMAL,
            pinned: false,
            RepeatPolicy.Disabled,
            WakePolicy.DEFAULT,
            enabled: true,
            CreatedAt);
        var schedule = ReminderSchedule.CreateFromRule(
            rule,
            LogicalReminderId.From(Id(231)),
            scheduleRevision: 1,
            triggerAtUtc: EvaluatedAt - Duration.FromMinutes(1),
            createdAtUtc: CreatedAt,
            id: ReminderScheduleId.From(Id(232)));
        store.Add(
            schedule,
            rule: null,
            new ReminderTargetState(taskId, OccurrenceId.From(taskId), null, false, false, true));

        await using var scheduler = new ReminderScheduler(
            store,
            new MutableClock(EvaluatedAt),
            identityGenerator: new FixedIdentityGenerator(206, 226));

        var result = Assert.Single((await scheduler.RunDueCycleAsync(cancellationToken: TestContext.Current.CancellationToken)).DueResults);

        Assert.Equal(ReminderDueResultKind.CANCELLED, result.Kind);
        Assert.Equal(ReminderSchedulerCodes.RuleMissing, result.ReasonCode);
        Assert.Equal(ScheduleState.CANCELLED, schedule.State);
        Assert.Equal(ScheduleStateReason.TASK_DELETED, schedule.TerminalReason);
        Assert.Equal(0, store.InstanceCount);
    }

    private static ScheduledReminder AddTaskSchedule(
        InMemorySchedulerStore store,
        int suffix,
        ReminderPurpose purpose,
        Instant triggerAtUtc,
        Instant? taskStartAtUtc = null,
        bool occurrenceCancelled = false,
        bool taskResultRecorded = false,
        bool customReminderMeaningful = true,
        ReminderPriority priority = ReminderPriority.NORMAL,
        bool pinned = false,
        RepeatPolicy? repeatPolicy = null,
        WakePolicy wakePolicy = WakePolicy.DEFAULT,
        bool enabled = true)
    {
        var taskId = Id(suffix);
        var rule = ReminderRule.CreateForTask(
            taskId,
            purpose,
            TimingFor(purpose, triggerAtUtc),
            priority,
            pinned,
            repeatPolicy ?? RepeatPolicy.Disabled,
            wakePolicy,
            enabled,
            CreatedAt);
        var schedule = ReminderSchedule.CreateFromRule(
            rule,
            LogicalReminderId.From(Id(suffix + 1)),
            scheduleRevision: 1,
            triggerAtUtc,
            CreatedAt,
            timeZoneId: "Asia/Shanghai",
            id: ReminderScheduleId.From(Id(suffix + 2)));
        var target = new ReminderTargetState(
            taskId,
            OccurrenceId.From(taskId),
            taskStartAtUtc,
            occurrenceCancelled,
            taskResultRecorded,
            customReminderMeaningful);
        store.Add(schedule, rule, target);
        return new ScheduledReminder(rule, schedule, target);
    }

    private static ReminderTiming TimingFor(ReminderPurpose purpose, Instant triggerAtUtc) => purpose switch
    {
        ReminderPurpose.TASK_CUSTOM => ReminderTiming.AbsoluteUtc(triggerAtUtc),
        ReminderPurpose.TASK_PRE_START => ReminderTiming.Relative(ReminderAnchor.TASK_TIME, -60),
        ReminderPurpose.TASK_START => ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
        ReminderPurpose.TASK_RANGE_END => ReminderTiming.Relative(ReminderAnchor.RANGE_END, -60),
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };

    private static Guid Id(int suffix) =>
        Guid.Parse($"0191f6a4-3b25-7c12-8d34-56789abcde{suffix:X2}");

    private sealed record ScheduledReminder(
        ReminderRule Rule,
        ReminderSchedule Schedule,
        ReminderTargetState Target);

    private sealed class MutableClock(Instant current) : IClock
    {
        public Instant Current { get; set; } = current;

        public Instant GetCurrentInstant() => Current;
    }

    private sealed class FixedIdentityGenerator(int firstInstanceSuffix, int firstScheduleSuffix)
        : IReminderIdentityGenerator
    {
        private int nextInstanceSuffix = firstInstanceSuffix;
        private int nextScheduleSuffix = firstScheduleSuffix;

        public ReminderInstanceId NewInstanceId() =>
            ReminderInstanceId.From(Id(nextInstanceSuffix++));

        public ReminderScheduleId NewScheduleId() =>
            ReminderScheduleId.From(Id(nextScheduleSuffix++));
    }

    private sealed class InMemorySchedulerStore
        : IReminderSchedulerStore
    {
        private readonly object sync = new();
        private readonly Dictionary<ReminderScheduleId, Entry> entries = new();
        private readonly string root;

        public InMemorySchedulerStore(string root)
        {
            this.root = root;
        }

        public bool FailNextCommit { get; set; }

        public int BeginCount { get; private set; }

        public int CommitCount { get; private set; }

        public int RollbackCount { get; private set; }

        public int InstanceCount
        {
            get
            {
                lock (sync)
                {
                    return entries.Values.Count(entry => entry.Instance is not null);
                }
            }
        }

        public void Add(
            ReminderSchedule schedule,
            ReminderRule? rule,
            ReminderTargetState? target,
            ReminderInstance? instance = null)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            lock (sync)
            {
                if (entries.ContainsKey(schedule.Id))
                {
                    throw new InvalidOperationException("Duplicate fake schedule id.");
                }

                _ = new ReminderPendingSchedule(schedule, rule);
                entries.Add(schedule.Id, new Entry(schedule, rule, target, instance));
            }
        }

        public void SeedInstance(ReminderScheduleId scheduleId, ReminderInstance instance)
        {
            ArgumentNullException.ThrowIfNull(instance);
            lock (sync)
            {
                if (!entries.TryGetValue(scheduleId, out var entry))
                {
                    throw new InvalidOperationException("Unknown fake schedule id.");
                }

                entry.Instance = instance;
            }
        }

        public ValueTask<IReadOnlyList<ReminderPendingSchedule>> ReadPendingAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                IReadOnlyList<ReminderPendingSchedule> result = entries.Values
                    .Where(entry => entry.Schedule.IsPending)
                    .OrderBy(entry => entry.Schedule.TriggerAtUtc)
                    .Select(entry => new ReminderPendingSchedule(entry.Schedule, entry.Rule))
                    .ToArray();
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
                if (!entries.TryGetValue(scheduleId, out var entry) || !entry.Schedule.IsPending)
                {
                    return ValueTask.FromResult<IReminderDueTransaction?>(null);
                }

                BeginCount++;
                return ValueTask.FromResult<IReminderDueTransaction?>(
                    new FakeDueTransaction(this, entry));
            }
        }

        private ReminderDueCommitResult Commit(
            Entry entry,
            ReminderDueMutation mutation)
        {
            lock (sync)
            {
                if (FailNextCommit)
                {
                    FailNextCommit = false;
                    // Model the existing writer adapter's rule: once commit
                    // is entered, the transaction adapter performs its own
                    // confirmed rollback/reconciliation.
                    Rollback();
                    throw new InvalidOperationException("Injected fake commit failure.");
                }

                if (!entry.Schedule.IsPending)
                {
                    return new(
                        ReminderDueCommitDisposition.NOT_DUE,
                        entry.Schedule.State,
                        entry.Instance,
                        null,
                        ReminderSchedulerCodes.NotClaimed);
                }

                if (mutation.Kind == ReminderDueMutationKind.TRIGGER)
                {
                    ArgumentNullException.ThrowIfNull(mutation.Instance);
                    var duplicate = entries.Values
                        .Select(candidate => candidate.Instance)
                        .FirstOrDefault(instance =>
                            instance is not null &&
                            instance.LogicalReminderId == entry.Schedule.LogicalReminderId &&
                            instance.AttemptOrdinal == mutation.Instance.AttemptOrdinal);
                    if (duplicate is not null)
                    {
                        entry.Schedule.Consume(mutation.AtUtc);
                        entry.Instance = duplicate;
                        CommitCount++;
                        AppendAudit("REPLAYED");
                        return new(
                            ReminderDueCommitDisposition.REPLAYED,
                            entry.Schedule.State,
                            duplicate,
                            null,
                            ReminderPolicyCodes.RecoveryDuplicate);
                    }

                    entry.Schedule.Consume(mutation.AtUtc);
                    entry.Instance = mutation.Instance;
                    if (mutation.DerivedSchedule is not null)
                    {
                        AddLocked(
                            mutation.DerivedSchedule,
                            entry.Rule,
                            entry.Target);
                    }

                    CommitCount++;
                    AppendAudit("COMMITTED_TRIGGER");
                    return new(
                        ReminderDueCommitDisposition.COMMITTED,
                        entry.Schedule.State,
                        entry.Instance,
                        mutation.DerivedSchedule,
                        mutation.ReasonCode);
                }

                switch (mutation.Kind)
                {
                    case ReminderDueMutationKind.CONSUME_DUPLICATE:
                        entry.Schedule.Consume(mutation.AtUtc);
                        break;
                    case ReminderDueMutationKind.CANCEL:
                        entry.Schedule.Cancel(mutation.TerminalReason, mutation.AtUtc);
                        break;
                    case ReminderDueMutationKind.EXPIRE:
                        entry.Schedule.Expire(mutation.TerminalReason, mutation.AtUtc);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mutation));
                }

                CommitCount++;
                AppendAudit(mutation.Kind.ToString());
                return new(
                    ReminderDueCommitDisposition.COMMITTED,
                    entry.Schedule.State,
                    entry.Instance,
                    null,
                    mutation.ReasonCode);
            }
        }

        private int NextAttemptOrdinal(Entry entry)
        {
            lock (sync)
            {
                var max = entries.Values
                    .Select(candidate => candidate.Instance)
                    .Where(instance =>
                        instance is not null &&
                        instance.LogicalReminderId == entry.Schedule.LogicalReminderId)
                    .Select(instance => instance!.AttemptOrdinal)
                    .DefaultIfEmpty(0)
                    .Max();
                return checked(max + 1);
            }
        }

        private long NextScheduleRevision(Entry entry)
        {
            lock (sync)
            {
                var max = entries.Values
                    .Where(candidate =>
                        candidate.Schedule.RuleId == entry.Schedule.RuleId &&
                        candidate.Schedule.OccurrenceId == entry.Schedule.OccurrenceId)
                    .Select(candidate => candidate.Schedule.ScheduleRevision)
                    .DefaultIfEmpty(0)
                    .Max();
                return checked(max + 1);
            }
        }

        private void Rollback()
        {
            lock (sync)
            {
                RollbackCount++;
                AppendAudit("ROLLBACK");
            }
        }

        private void AddLocked(
            ReminderSchedule schedule,
            ReminderRule? rule,
            ReminderTargetState? target)
        {
            if (entries.ContainsKey(schedule.Id))
            {
                throw new InvalidOperationException("Duplicate fake derived schedule id.");
            }

            entries.Add(schedule.Id, new Entry(schedule, rule, target, null));
        }

        private void AppendAudit(string operation)
        {
            File.AppendAllText(
                Path.Combine(root, "scheduler-fake.log"),
                operation + Environment.NewLine);
        }

        private sealed class Entry(
            ReminderSchedule schedule,
            ReminderRule? rule,
            ReminderTargetState? target,
            ReminderInstance? instance)
        {
            public ReminderSchedule Schedule { get; } = schedule;

            public ReminderRule? Rule { get; } = rule;

            public ReminderTargetState? Target { get; } = target;

            public ReminderInstance? Instance { get; set; } = instance;
        }

        private sealed class FakeDueTransaction(
            InMemorySchedulerStore store,
            Entry entry) : IReminderDueTransaction
        {
            private bool completed;

            public ReminderSchedule Schedule => entry.Schedule;

            public ReminderRule? Rule => entry.Rule;

            public ReminderTargetState? Target => entry.Target;

            public ReminderInstance? ExistingInstance { get; } = entry.Instance;

            public int NextAttemptOrdinal { get; } = store.NextAttemptOrdinal(entry);

            public long NextScheduleRevision { get; } = store.NextScheduleRevision(entry);

            public ValueTask<ReminderDueCommitResult> CommitAsync(
                ReminderDueMutation mutation,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(mutation);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOpen();
                var result = store.Commit(entry, mutation);
                completed = true;
                return ValueTask.FromResult(result);
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!completed)
                {
                    completed = true;
                    store.Rollback();
                }

                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private void EnsureOpen()
            {
                if (completed)
                {
                    throw new InvalidOperationException("Fake due transaction is already closed.");
                }
            }
        }
    }

    private sealed class IsolatedTempRoot : IDisposable
    {
        public IsolatedTempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ReminNote-P3-03-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "isolated.marker"), "P3-03 fake root");
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
}

#pragma warning restore CA1707
