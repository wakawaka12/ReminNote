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

    private static Guid Id(int suffix) =>
        Guid.Parse($"0191f6a4-3b25-7c12-8d34-56789abcde{suffix:X2}");
}
