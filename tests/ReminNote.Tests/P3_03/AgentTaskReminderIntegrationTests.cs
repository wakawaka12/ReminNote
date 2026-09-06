using Microsoft.EntityFrameworkCore;
using NodaTime;
using ReminNote.Agent.Runtime;
using ReminNote.Agent.Transport;
using ReminNote.Core.Application;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.P25;
using SystemTask = System.Threading.Tasks.Task;

namespace ReminNote.Tests.P3_03;

/// <summary>
/// Exercises the public wire command through the Agent dispatcher. The test
/// starts from a migrated empty database and never seeds ReminderRule or
/// ReminderSchedule rows; those rows must be materialized by the same command
/// path used by Main/Widget.
/// </summary>
public sealed class AgentTaskReminderIntegrationTests
{
    private const string UserSid = "S-1-5-21-100-200-300-400";
    [Fact]
    public async SystemTask PublicTaskCreateCommandMaterializesRuleAndFirstSchedule()
    {
        using var database = new SqliteTestDatabase(fileBacked: true);
        database.Migrate();
        var databasePath = database.DatabasePath!;
        var profileScope = ProtocolProfileScope.Derive(UserSid, databasePath);
        await P25StorageSchema.EnsureProfileAsync(
                database.Connection,
                profileScope,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var store = await P25StorageStore.OpenReadyAsync(
                database.Connection,
                profileScope,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var reminder = new ReminderRuleOptions(
            ReminderPurpose.TASK_START,
            ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
            ReminderPriority.HIGH,
            Pinned: true,
            WakePolicy: WakePolicy.YES);
        var request = ProtocolRequest.CreateMutationAttempt(
            ProtocolClientKinds.Main,
            ProtocolIds.NewClientInstanceId(),
            DateTimeOffset.UtcNow,
            ProtocolLimits.DefaultMutationTimeoutMilliseconds,
            ProtocolOperations.TaskCreate,
            ProtocolJson.CreateTaskCreatePayload(
                "IPC reminder creation",
                TimeSpec.At(new LocalDate(2030, 1, 1), new LocalTime(10, 0)),
                reminder),
            ProtocolIds.NewIdempotencyKey(),
            expectedRevision: 0);
        var dispatcher = new AgentProtocolDispatcher(
            store,
            profileScope,
            databasePath,
            Id(100));

        var frame = ProtocolJson.SerializeRequest(request);
        var responseFrame = await dispatcher.DispatchAsync(
                frame,
                new TransportPeerIdentity(UserSid, profileScope))
            .ConfigureAwait(true);
        var response = ProtocolJson.DeserializeResponse(responseFrame.Span);

        Assert.True(
            response.Ok,
            $"{response.Error?.Code}: {response.Error?.HumanMessage ?? response.Outcome}");
        Assert.Equal(ProtocolOutcomes.Changed, response.Outcome);
        Assert.NotNull(response.Payload);
        var taskId = Guid.Parse(response.Payload!.Value.GetProperty("taskId").GetString()!);

        await using var context = database.CreateContext();
        var task = await context.Tasks.SingleAsync(
            value => value.Id == taskId,
            TestContext.Current.CancellationToken);
        var rule = await context.ReminderRules.SingleAsync(
            value => value.TargetId == taskId,
            TestContext.Current.CancellationToken);
        var schedule = await context.ReminderSchedules.SingleAsync(
            value => value.OccurrenceId == taskId,
            TestContext.Current.CancellationToken);

        Assert.Equal(taskId, rule.TargetId);
        Assert.Equal(taskId, rule.OccurrenceId);
        Assert.Equal(rule.Id, schedule.RuleId);
        Assert.Equal(rule.RuleRevision, schedule.RuleRevision);
        Assert.Equal(ScheduleState.PENDING, schedule.State);
        Assert.Equal(ReminderPriority.HIGH, schedule.PrioritySnapshot);
        Assert.True(schedule.PinnedSnapshot);

        var replayFrame = await dispatcher.DispatchAsync(
                frame,
                new TransportPeerIdentity(UserSid, profileScope))
            .ConfigureAwait(true);
        var replay = ProtocolJson.DeserializeResponse(replayFrame.Span);
        Assert.True(replay.Ok);
        Assert.True(replay.Replayed);
        Assert.Equal(ProtocolOutcomes.Replayed, replay.Outcome);
        Assert.Equal(
            1,
            await context.ReminderRules.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            1,
            await context.ReminderSchedules.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async SystemTask ReminderRuleUpsertUsesExplicitCreateAndUpdateSemantics()
    {
        using var database = new SqliteTestDatabase(fileBacked: true);
        database.Migrate();
        var databasePath = database.DatabasePath!;
        var profileScope = ProtocolProfileScope.Derive(UserSid, databasePath);
        await P25StorageSchema.EnsureProfileAsync(
                database.Connection,
                profileScope,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var store = await P25StorageStore.OpenReadyAsync(
                database.Connection,
                profileScope,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var dispatcher = new AgentProtocolDispatcher(
            store,
            profileScope,
            databasePath,
            Id(200));
        var clientId = ProtocolIds.NewClientInstanceId();

        var initial = await DispatchAsync(
                dispatcher,
                profileScope,
                ProtocolRequest.CreateMutationAttempt(
                    ProtocolClientKinds.Main,
                    clientId,
                    DateTimeOffset.UtcNow,
                    ProtocolLimits.DefaultMutationTimeoutMilliseconds,
                    ProtocolOperations.TaskCreate,
                    ProtocolJson.CreateTaskCreatePayload(
                        "显式规则语义测试",
                        TimeSpec.At(new LocalDate(2030, 1, 1), new LocalTime(10, 0)),
                        new ReminderRuleOptions(
                            ReminderPurpose.TASK_START,
                            ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0))),
                    ProtocolIds.NewIdempotencyKey(),
                    expectedRevision: 0))
            .ConfigureAwait(true);
        Assert.True(initial.Ok, initial.Error?.Code);
        var taskId = Guid.Parse(initial.Payload!.Value.GetProperty("taskId").GetString()!);
        var currentRevision = initial.CommittedRevision!.Value;

        var createOptions = new ReminderRuleOptions(
            ReminderPurpose.TASK_PRE_START,
            ReminderTiming.Relative(ReminderAnchor.TASK_TIME, -300),
            ReminderPriority.HIGH,
            Pinned: true);
        var createPayload = ProtocolJson.CreateReminderRuleUpsertPayload(taskId.ToString("D"), createOptions);
        Assert.Equal(ReminderRuleUpsertModes.Create, createPayload.GetProperty("mode").GetString());
        Assert.False(createPayload.TryGetProperty("ruleId", out _));
        var created = await DispatchAsync(
                dispatcher,
                profileScope,
                ProtocolRequest.CreateMutationAttempt(
                    ProtocolClientKinds.Main,
                    clientId,
                    DateTimeOffset.UtcNow,
                    ProtocolLimits.DefaultMutationTimeoutMilliseconds,
                    ProtocolOperations.ReminderRuleUpsert,
                    createPayload,
                    ProtocolIds.NewIdempotencyKey(),
                    currentRevision))
            .ConfigureAwait(true);
        Assert.True(created.Ok, created.Error?.Code);
        Assert.True(created.Payload.HasValue);
        var createdResponsePayload = created.Payload!.Value;
        Assert.True(createdResponsePayload.TryGetProperty("ruleId", out var createdRuleIdValue));
        Assert.True(ReminderRuleId.TryParse(createdRuleIdValue.GetString(), out _));
        currentRevision = created.CommittedRevision!.Value;

        await using var context = database.CreateContext();
        var rules = await context.ReminderRules
            .AsNoTracking()
            .Where(value => value.TargetId == taskId)
            .OrderBy(value => value.CreatedAtUtc)
            .ThenBy(value => value.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rules.Length);
        var createdRule = Assert.Single(rules, value => value.Purpose == ReminderPurpose.TASK_PRE_START);
        var updateOptions = createOptions with { Priority = ReminderPriority.LOW, Pinned = false };
        var updatePayload = ProtocolJson.CreateReminderRuleUpsertPayload(
            taskId.ToString("D"),
            updateOptions,
            createdRule.Id.ToString("D"));
        Assert.Equal(ReminderRuleUpsertModes.Update, updatePayload.GetProperty("mode").GetString());
        Assert.Equal(createdRule.Id.ToString("D"), updatePayload.GetProperty("ruleId").GetString());
        var updated = await DispatchAsync(
                dispatcher,
                profileScope,
                ProtocolRequest.CreateMutationAttempt(
                    ProtocolClientKinds.Main,
                    clientId,
                    DateTimeOffset.UtcNow,
                    ProtocolLimits.DefaultMutationTimeoutMilliseconds,
                    ProtocolOperations.ReminderRuleUpsert,
                    updatePayload,
                    ProtocolIds.NewIdempotencyKey(),
                    currentRevision))
            .ConfigureAwait(true);
        Assert.True(updated.Ok, updated.Error?.Code);
        Assert.Equal(createdRule.Id.ToString("D"), updated.Payload!.Value.GetProperty("ruleId").GetString());

        var updatedRule = await context.ReminderRules.AsNoTracking().SingleAsync(
            value => value.Id == createdRule.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(ReminderPriority.LOW, updatedRule.Priority);
        Assert.False(updatedRule.Pinned);
        Assert.Equal(2, updatedRule.RuleRevision);
        var ruleCountBeforeMissingUpdate = await context.ReminderRules.CountAsync(TestContext.Current.CancellationToken);

        var missingRuleId = Guid.CreateVersion7().ToString("D");
        var missingUpdate = ProtocolJson.CreateReminderRuleUpsertPayload(
            taskId.ToString("D"),
            updateOptions,
            missingRuleId);
        var rejected = await DispatchAsync(
                dispatcher,
                profileScope,
                ProtocolRequest.CreateMutationAttempt(
                    ProtocolClientKinds.Main,
                    clientId,
                    DateTimeOffset.UtcNow,
                    ProtocolLimits.DefaultMutationTimeoutMilliseconds,
                    ProtocolOperations.ReminderRuleUpsert,
                    missingUpdate,
                    ProtocolIds.NewIdempotencyKey(),
                    updated.CommittedRevision!.Value))
            .ConfigureAwait(true);
        Assert.False(rejected.Ok);
        Assert.Equal(ProtocolErrorCodes.NotFound, rejected.Error?.Code);
        Assert.Equal(ruleCountBeforeMissingUpdate, await context.ReminderRules.CountAsync(TestContext.Current.CancellationToken));
    }

    private static async System.Threading.Tasks.Task<ProtocolResponse> DispatchAsync(
        AgentProtocolDispatcher dispatcher,
        string profileScope,
        ProtocolRequest request)
    {
        var frame = ProtocolJson.SerializeRequest(request);
        var responseFrame = await dispatcher.DispatchAsync(
                frame,
                new TransportPeerIdentity(UserSid, profileScope))
            .ConfigureAwait(true);
        return ProtocolJson.DeserializeResponse(responseFrame.Span);
    }

    private static Guid Id(int suffix) =>
        Guid.Parse($"0191f6a4-3b25-7c12-8d34-56789abcde{suffix:X2}");
}
