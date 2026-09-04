using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Reminders.Policy;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.Reminders;

namespace ReminNote.Agent.Scheduling;

/// <summary>
/// Production adapter for the P3 scheduler seam. Reads are performed through
/// a SQLite read-only/query-only connection; every state transition goes
/// through the already-open P2.5 writer and its revision/journal/receipt
/// transaction. The adapter deliberately keeps no timer or schedule cache.
/// </summary>
public sealed class AgentReminderSchedulerStore : IReminderSchedulerStore, IAsyncDisposable
{
    private const string DueOperation = "agent.reminder.due";

    private readonly P25StorageStore writerStore;
    private readonly string databasePath;
    private readonly string actualUserSid;
    private readonly string agentInstanceId;
    private int disposed;

    public AgentReminderSchedulerStore(
        P25StorageStore writerStore,
        string databasePath,
        string actualUserSid,
        Guid agentInstanceId)
    {
        this.writerStore = writerStore ?? throw new ArgumentNullException(nameof(writerStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualUserSid);
        if (actualUserSid.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(actualUserSid));
        }

        if (agentInstanceId == Guid.Empty)
        {
            throw new ArgumentException("The Agent instance id cannot be empty.", nameof(agentInstanceId));
        }

        this.databasePath = Path.GetFullPath(databasePath);
        this.actualUserSid = actualUserSid;
        this.agentInstanceId = agentInstanceId.ToString("D");
    }

    public string DatabasePath => databasePath;

    public string ProfileScope => writerStore.ProfileScope;

    public async ValueTask<IReadOnlyList<ReminderPendingSchedule>> ReadPendingAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        await using var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        await using var context = CreateContext(connection);
        using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: true);
        context.Database.UseTransaction(transaction);

        var schedules = await context.ReminderSchedules
            .AsNoTracking()
            .Where(schedule => schedule.State == ScheduleState.PENDING)
            .OrderBy(schedule => schedule.TriggerAtUtc)
            .ThenBy(schedule => schedule.ScheduleRevision)
            .ThenBy(schedule => schedule.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var rules = await LoadRulesAsync(context, schedules, cancellationToken).ConfigureAwait(false);
        var result = schedules
            .Select(schedule => new ReminderPendingSchedule(
                schedule.ToDomain(),
                rules.TryGetValue(schedule.RuleId, out var rule) ? rule : null))
            .ToArray();

        transaction.Commit();
        return result;
    }

    public async ValueTask<IReminderDueTransaction?> BeginDueTransactionAsync(
        ReminderScheduleId scheduleId,
        Instant observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var snapshot = await ReadDueSnapshotAsync(
                scheduleId,
                observedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return snapshot is null
            ? null
            : new AgentReminderDueTransaction(this, snapshot);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        return ValueTask.CompletedTask;
    }

    private async ValueTask<ReminderDueCommitResult> CommitDueAsync(
        DueSnapshot snapshot,
        ReminderDueMutation mutation,
        CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(mutation);

        var expectedRevision = (await writerStore
                .ReadRevisionStateAsync(cancellationToken)
            .ConfigureAwait(false)).CurrentRevision;
        var request = CreateDueRequest(snapshot.Schedule.Id, expectedRevision);
        var observation = new CommitObservation();
        var write = await writerStore.Writer.ExecuteMutationAsync(
                request,
                (context, token) => ApplyDueMutationAsync(
                    context,
                    snapshot,
                    mutation,
                    observation,
                    token),
                cancellationToken)
            .ConfigureAwait(false);

        if (write.Outcome == P25MutationOutcome.Changed && observation.Result is not null)
        {
            return observation.Result;
        }

        var durable = await ReadDurableStateAsync(
                snapshot.Schedule.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (write.Outcome == P25MutationOutcome.Replayed)
        {
            return ReplayResult(mutation, durable);
        }

        if (write.Outcome is P25MutationOutcome.Stale or P25MutationOutcome.NoOp)
        {
            return new ReminderDueCommitResult(
                ReminderDueCommitDisposition.NOT_DUE,
                durable.Schedule?.State ?? ScheduleState.CANCELLED,
                durable.Instance,
                durable.DerivedSchedule,
                write.ErrorCode ?? ReminderSchedulerCodes.NotClaimed);
        }

        return new ReminderDueCommitResult(
            ReminderDueCommitDisposition.REJECTED,
            durable.Schedule?.State ?? ScheduleState.CANCELLED,
            null,
            null,
            write.ErrorCode ?? ReminderSchedulerCodes.CommitRejected);
    }

    private static async ValueTask<P25MutationDecision> ApplyDueMutationAsync(
        P25MutationContext mutationContext,
        DueSnapshot originalSnapshot,
        ReminderDueMutation mutation,
        CommitObservation observation,
        CancellationToken cancellationToken)
    {
        await using var context = ReminNoteDatabase.CreateContext(mutationContext.Connection);
        context.Database.UseTransaction(mutationContext.Transaction);

        var scheduleEntity = await context.ReminderSchedules
            .SingleOrDefaultAsync(
                schedule => schedule.Id == originalSnapshot.Schedule.Id.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (scheduleEntity is null ||
            scheduleEntity.State != ScheduleState.PENDING ||
            scheduleEntity.TriggerAtUtc > originalSnapshot.ObservedAtUtc)
        {
            observation.Result = new ReminderDueCommitResult(
                ReminderDueCommitDisposition.NOT_DUE,
                scheduleEntity?.State ?? ScheduleState.CANCELLED,
                null,
                null,
                ReminderSchedulerCodes.NotClaimed);
            return P25MutationDecision.NoOp();
        }

        var currentSchedule = scheduleEntity.ToDomain();
        var ruleEntity = await context.ReminderRules
            .SingleOrDefaultAsync(
                rule => rule.Id == scheduleEntity.RuleId,
                cancellationToken)
            .ConfigureAwait(false);
        var taskEntity = ruleEntity?.TargetKind == ReminderTargetKind.TASK_INSTANCE
            ? await context.Tasks
                .SingleOrDefaultAsync(
                    task => task.Id == ruleEntity.TargetId,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        var existingForSchedule = await context.ReminderInstances
            .SingleOrDefaultAsync(
                instance => instance.ScheduleId == currentSchedule.Id.Value,
                cancellationToken)
            .ConfigureAwait(false);

        // Re-check all facts that can invalidate a scan result while the
        // P2.5 writer gate is held. The scan is only a hint; this branch is the
        // durable truth used for the final transition.
        if (ruleEntity is null)
        {
            return await CommitTerminalAsync(
                    context,
                    scheduleEntity,
                    currentSchedule,
                    mutation.AtUtc,
                    ScheduleStateReason.TASK_DELETED,
                    ReminderSchedulerCodes.RuleMissing,
                    ScheduleState.CANCELLED,
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (ruleEntity.TargetKind != ReminderTargetKind.TASK_INSTANCE ||
            ruleEntity.OccurrenceId != currentSchedule.OccurrenceId.Value ||
            ruleEntity.Id != currentSchedule.RuleId.Value)
        {
            return await CommitTerminalAsync(
                    context,
                    scheduleEntity,
                    currentSchedule,
                    mutation.AtUtc,
                    ScheduleStateReason.TASK_DELETED,
                    ReminderSchedulerCodes.TargetMismatch,
                    ScheduleState.CANCELLED,
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (ruleEntity.RuleRevision != currentSchedule.RuleRevision)
        {
            return await CommitTerminalAsync(
                    context,
                    scheduleEntity,
                    currentSchedule,
                    mutation.AtUtc,
                    ScheduleStateReason.RULE_REBUILT,
                    ReminderSchedulerCodes.RuleRevisionStale,
                    ScheduleState.CANCELLED,
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!ruleEntity.Enabled)
        {
            return await CommitTerminalAsync(
                    context,
                    scheduleEntity,
                    currentSchedule,
                    mutation.AtUtc,
                    ScheduleStateReason.RULE_DISABLED,
                    ReminderSchedulerCodes.RuleDisabled,
                    ScheduleState.CANCELLED,
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (taskEntity is null)
        {
            return await CommitTerminalAsync(
                    context,
                    scheduleEntity,
                    currentSchedule,
                    mutation.AtUtc,
                    ScheduleStateReason.TASK_DELETED,
                    ReminderSchedulerCodes.TargetMissing,
                    ScheduleState.CANCELLED,
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (taskEntity.Result is not null)
        {
            return await CommitTerminalAsync(
                    context,
                    scheduleEntity,
                    currentSchedule,
                    mutation.AtUtc,
                    ScheduleStateReason.TASK_RESULT_RECORDED,
                    ReminderPolicyCodes.RecoveryTaskResultRecorded,
                    ScheduleState.CANCELLED,
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var duplicate = existingForSchedule;
        if (duplicate is null && mutation.Instance is not null)
        {
            duplicate = await context.ReminderInstances
                .SingleOrDefaultAsync(
                    instance =>
                        instance.LogicalReminderId == currentSchedule.LogicalReminderId.Value &&
                        instance.AttemptOrdinal == mutation.Instance.AttemptOrdinal,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (mutation.Kind == ReminderDueMutationKind.TRIGGER && duplicate is not null)
        {
            currentSchedule.Consume(mutation.AtUtc);
            scheduleEntity.Apply(currentSchedule);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            observation.Result = new ReminderDueCommitResult(
                ReminderDueCommitDisposition.REPLAYED,
                currentSchedule.State,
                duplicate.ToDomain(),
                null,
                ReminderPolicyCodes.RecoveryDuplicate);
            return P25MutationDecision.Changed(
                [new P25JournalChange(
                    "reminder_schedule",
                    currentSchedule.Id.ToString(),
                    "consumed_duplicate")]);
        }

        if (mutation.Kind == ReminderDueMutationKind.CONSUME_DUPLICATE)
        {
            currentSchedule.Consume(mutation.AtUtc);
            scheduleEntity.Apply(currentSchedule);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            observation.Result = new ReminderDueCommitResult(
                ReminderDueCommitDisposition.COMMITTED,
                currentSchedule.State,
                duplicate?.ToDomain(),
                null,
                mutation.ReasonCode);
            return P25MutationDecision.Changed(
                [new P25JournalChange(
                    "reminder_schedule",
                    currentSchedule.Id.ToString(),
                    "consumed_duplicate")]);
        }

        if (mutation.Kind == ReminderDueMutationKind.CANCEL)
        {
            return await CommitTerminalAsync(
                    context,
                    scheduleEntity,
                    currentSchedule,
                    mutation.AtUtc,
                    mutation.TerminalReason,
                    mutation.ReasonCode,
                    ScheduleState.CANCELLED,
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (mutation.Kind == ReminderDueMutationKind.EXPIRE)
        {
            return await CommitTerminalAsync(
                    context,
                    scheduleEntity,
                    currentSchedule,
                    mutation.AtUtc,
                    mutation.TerminalReason,
                    mutation.ReasonCode,
                    ScheduleState.EXPIRED,
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (mutation.Kind != ReminderDueMutationKind.TRIGGER || mutation.Instance is null)
        {
            observation.Result = new ReminderDueCommitResult(
                ReminderDueCommitDisposition.REJECTED,
                currentSchedule.State,
                null,
                null,
                "reminder.scheduler.mutation_invalid");
            return P25MutationDecision.Rejected("reminder.scheduler.mutation_invalid");
        }

        var instance = ReminderInstance.CreateFromSchedule(
            currentSchedule,
            mutation.Instance.AttemptOrdinal,
            mutation.Instance.TriggeredAtUtc,
            mutation.Instance.Id);
        currentSchedule.Consume(mutation.AtUtc);
        scheduleEntity.Apply(currentSchedule);
        context.ReminderInstances.Add(ReminderInstanceEntity.FromDomain(instance));

        ReminderSchedule? derivedSchedule = null;
        if (mutation.DerivedSchedule is not null)
        {
            var requestedDerived = mutation.DerivedSchedule;
            if (requestedDerived.OriginScheduleId != currentSchedule.Id ||
                requestedDerived.RuleId != currentSchedule.RuleId ||
                requestedDerived.OccurrenceId != currentSchedule.OccurrenceId ||
                requestedDerived.ScheduleRevision <= currentSchedule.ScheduleRevision)
            {
                observation.Result = new ReminderDueCommitResult(
                    ReminderDueCommitDisposition.REJECTED,
                    currentSchedule.State,
                    null,
                    null,
                    "reminder.scheduler.derived_invalid");
                return P25MutationDecision.Rejected("reminder.scheduler.derived_invalid");
            }

            derivedSchedule = ReminderSchedule.CreateDerived(
                currentSchedule,
                requestedDerived.Cause,
                requestedDerived.ScheduleRevision,
                requestedDerived.TriggerAtUtc,
                requestedDerived.CreatedAtUtc,
                requestedDerived.TimeZoneId,
                requestedDerived.Id);
            context.ReminderSchedules.Add(ReminderScheduleEntity.FromDomain(derivedSchedule));
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        observation.Result = new ReminderDueCommitResult(
            ReminderDueCommitDisposition.COMMITTED,
            currentSchedule.State,
            instance,
            derivedSchedule,
            mutation.ReasonCode);
        var changes = new List<P25JournalChange>
        {
            new("reminder_schedule", currentSchedule.Id.ToString(), "consumed"),
            new("reminder_instance", instance.Id.ToString(), "triggered")
        };
        if (derivedSchedule is not null)
        {
            changes.Add(new(
                "reminder_schedule",
                derivedSchedule.Id.ToString(),
                "scheduled"));
        }

        return P25MutationDecision.Changed(changes);
    }

    private static async ValueTask<P25MutationDecision> CommitTerminalAsync(
        ReminNoteDbContext context,
        ReminderScheduleEntity scheduleEntity,
        ReminderSchedule schedule,
        Instant atUtc,
        ScheduleStateReason reason,
        string reasonCode,
        ScheduleState expectedState,
        CommitObservation observation,
        CancellationToken cancellationToken)
    {
        if (expectedState == ScheduleState.CANCELLED)
        {
            schedule.Cancel(reason, atUtc);
        }
        else if (expectedState == ScheduleState.EXPIRED)
        {
            schedule.Expire(reason, atUtc);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(expectedState));
        }

        scheduleEntity.Apply(schedule);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        observation.Result = new ReminderDueCommitResult(
            ReminderDueCommitDisposition.COMMITTED,
            schedule.State,
            null,
            null,
            reasonCode);
        var changeKind = expectedState == ScheduleState.CANCELLED ? "cancelled" : "expired";
        return P25MutationDecision.Changed(
            [new P25JournalChange(
                "reminder_schedule",
                schedule.Id.ToString(),
                changeKind)]);
    }

    private async ValueTask<DueSnapshot?> ReadDueSnapshotAsync(
        ReminderScheduleId scheduleId,
        Instant observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        await using var context = CreateContext(connection);
        using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: true);
        context.Database.UseTransaction(transaction);

        var scheduleEntity = await context.ReminderSchedules
            .AsNoTracking()
            .SingleOrDefaultAsync(
                schedule => schedule.Id == scheduleId.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (scheduleEntity is null || scheduleEntity.State != ScheduleState.PENDING)
        {
            transaction.Commit();
            return null;
        }

        var ruleEntity = await context.ReminderRules
            .AsNoTracking()
            .SingleOrDefaultAsync(
                rule => rule.Id == scheduleEntity.RuleId,
                cancellationToken)
            .ConfigureAwait(false);
        var taskEntity = ruleEntity?.TargetKind == ReminderTargetKind.TASK_INSTANCE
            ? await context.Tasks
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    task => task.Id == ruleEntity.TargetId,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        var existingEntity = await context.ReminderInstances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                instance => instance.ScheduleId == scheduleEntity.Id,
                cancellationToken)
            .ConfigureAwait(false);
        var attemptOrdinals = await context.ReminderInstances
            .AsNoTracking()
            .Where(instance => instance.LogicalReminderId == scheduleEntity.LogicalReminderId)
            .Select(instance => instance.AttemptOrdinal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var scheduleRevisions = await context.ReminderSchedules
            .AsNoTracking()
            .Where(schedule =>
                schedule.RuleId == scheduleEntity.RuleId &&
                schedule.OccurrenceId == scheduleEntity.OccurrenceId)
            .Select(schedule => schedule.ScheduleRevision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var schedule = scheduleEntity.ToDomain();
        var rule = ruleEntity?.ToDomain();
        var target = BuildTarget(ruleEntity, taskEntity, schedule.TimeZoneId);
        var nextAttemptOrdinal = attemptOrdinals.Count == 0
            ? 1
            : checked(attemptOrdinals.Max() + 1);
        var nextScheduleRevision = scheduleRevisions.Count == 0
            ? 1
            : checked(scheduleRevisions.Max() + 1);

        transaction.Commit();
        return new DueSnapshot(
            schedule,
            rule,
            target,
            existingEntity?.ToDomain(),
            nextAttemptOrdinal,
            nextScheduleRevision,
            observedAtUtc);
    }

    private async ValueTask<DurableState> ReadDurableStateAsync(
        ReminderScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        await using var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        await using var context = CreateContext(connection);
        using var transaction = connection.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: true);
        context.Database.UseTransaction(transaction);
        var schedule = await context.ReminderSchedules
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == scheduleId.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (schedule is null)
        {
            transaction.Commit();
            return new DurableState(null, null, null);
        }

        var instance = await context.ReminderInstances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ScheduleId == scheduleId.Value,
                cancellationToken)
            .ConfigureAwait(false);
        var derived = await context.ReminderSchedules
            .AsNoTracking()
            .Where(item => item.OriginScheduleId == scheduleId.Value)
            .OrderByDescending(item => item.ScheduleRevision)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
        return new DurableState(
            schedule.ToDomain(),
            instance?.ToDomain(),
            derived?.ToDomain());
    }

    private static ReminderDueCommitResult ReplayResult(
        ReminderDueMutation mutation,
        DurableState durable)
    {
        if (durable.Schedule is null)
        {
            return new(
                ReminderDueCommitDisposition.REJECTED,
                ScheduleState.CANCELLED,
                null,
                null,
                "reminder.scheduler.schedule_missing");
        }

        return new(
            ReminderDueCommitDisposition.REPLAYED,
            durable.Schedule.State,
            durable.Instance,
            durable.DerivedSchedule,
            mutation.ReasonCode);
    }

    private P25CommandRequest CreateDueRequest(
        ReminderScheduleId scheduleId,
        long expectedRevision)
    {
        var identity = string.Join(
            "|",
            DueOperation,
            ProfileScope,
            scheduleId.ToString(),
            expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var identityHash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        if (identityHash.AsSpan(0, 16).SequenceEqual(Guid.Empty.ToByteArray()))
        {
            identityHash[0] = 1;
        }

        return new P25CommandRequest(
            actualUserSid,
            ProfileScope,
            new Guid(identityHash.AsSpan(0, 16)).ToString("D"),
            DueOperation,
            P25StorageLimits.CanonicalHashVersion,
            SHA256.HashData(Encoding.UTF8.GetBytes("payload|" + identity)),
            expectedRevision,
            Guid.CreateVersion7().ToString("D"),
            agentInstanceId);
    }

    private static async ValueTask<Dictionary<Guid, ReminderRule>> LoadRulesAsync(
        ReminNoteDbContext context,
        IReadOnlyList<ReminderScheduleEntity> schedules,
        CancellationToken cancellationToken)
    {
        var ids = schedules
            .Select(schedule => schedule.RuleId)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var entities = await context.ReminderRules
            .AsNoTracking()
            .Where(rule => ids.Contains(rule.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return entities.ToDictionary(rule => rule.Id, rule => rule.ToDomain());
    }

    private static ReminderTargetState? BuildTarget(
        ReminderRuleEntity? rule,
        TaskEntity? task,
        string? timeZoneId)
    {
        if (rule is null || task is null || rule.TargetKind != ReminderTargetKind.TASK_INSTANCE)
        {
            return null;
        }

        return new ReminderTargetState(
            rule.TargetId,
            OccurrenceId.From(rule.OccurrenceId),
            ComputeTaskStartAtUtc(task, timeZoneId),
            OccurrenceCancelled: false,
            TaskResultRecorded: task.Result is not null,
            CustomReminderMeaningful: true);
    }

    private static Instant? ComputeTaskStartAtUtc(TaskEntity task, string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || task.TimeType == TaskTimeType.ANYTIME)
        {
            return null;
        }

        var localTime = task.TimeType switch
        {
            TaskTimeType.TIME => task.TimePoint,
            TaskTimeType.RANGE => task.RangeStart,
            _ => null
        };
        if (localTime is null)
        {
            return null;
        }

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId);
        return zone is null
            ? null
            : zone.AtLeniently(task.LocalDate.At(localTime.Value)).ToInstant();
    }

    private static ReminNoteDbContext CreateContext(SqliteConnection connection) =>
        ReminNoteDatabase.CreateContext(connection);

    private void EnsureNotDisposed() =>
        ObjectDisposedException.ThrowIf(disposed != 0, this);

    private sealed record DueSnapshot(
        ReminderSchedule Schedule,
        ReminderRule? Rule,
        ReminderTargetState? Target,
        ReminderInstance? ExistingInstance,
        int NextAttemptOrdinal,
        long NextScheduleRevision,
        Instant ObservedAtUtc);

    private sealed record DurableState(
        ReminderSchedule? Schedule,
        ReminderInstance? Instance,
        ReminderSchedule? DerivedSchedule);

    private sealed class CommitObservation
    {
        public ReminderDueCommitResult? Result { get; set; }
    }

    private sealed class AgentReminderDueTransaction(
        AgentReminderSchedulerStore store,
        DueSnapshot snapshot) : IReminderDueTransaction
    {
        private bool completed;

        public ReminderSchedule Schedule => snapshot.Schedule;

        public ReminderRule? Rule => snapshot.Rule;

        public ReminderTargetState? Target => snapshot.Target;

        public ReminderInstance? ExistingInstance => snapshot.ExistingInstance;

        public int NextAttemptOrdinal => snapshot.NextAttemptOrdinal;

        public long NextScheduleRevision => snapshot.NextScheduleRevision;

        public async ValueTask<ReminderDueCommitResult> CommitAsync(
            ReminderDueMutation mutation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            EnsureOpen();
            try
            {
                return await store.CommitDueAsync(snapshot, mutation, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                completed = true;
            }
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            completed = true;
            return ValueTask.CompletedTask;
        }

        private void EnsureOpen()
        {
            if (completed)
            {
                throw new InvalidOperationException("The reminder due transaction is already closed.");
            }
        }
    }
}
