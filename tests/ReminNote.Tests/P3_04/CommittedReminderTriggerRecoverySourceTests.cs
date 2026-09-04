using System.Globalization;
using Microsoft.Data.Sqlite;
using NodaTime;
using ReminNote.Agent.Notifications;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.Reminders;
using ReminNote.Tests.P25Storage;

#pragma warning disable CA1707 // Slice directory and test names mirror the frozen plan.
namespace ReminNote.Tests.P3_04;

public sealed class CommittedReminderTriggerRecoverySourceTests
{
    private static readonly Instant TriggeredAt = Instant.FromUtc(2026, 9, 4, 8, 0);

    [Fact]
    public async Task ReadsCommittedInstancesAndSkipsInstancesWithTerminalAttempts()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        var attemptStore = new SqliteNotificationDeliveryAttemptStore(
            fixture.Store,
            P25StorageFixture.UserSid);
        await attemptStore.InitializeSchemaForFixtureAsync(
            TestContext.Current.CancellationToken);
        await fixture.ExecuteSqlAsync(
            """
            CREATE TABLE reminder_instances (
                id TEXT NOT NULL PRIMARY KEY,
                schedule_id TEXT NOT NULL,
                rule_id TEXT NOT NULL,
                occurrence_id TEXT NOT NULL,
                logical_reminder_id TEXT NOT NULL,
                attempt_ordinal INTEGER NOT NULL,
                purpose_snapshot TEXT NOT NULL,
                priority_snapshot TEXT NOT NULL,
                pinned_snapshot INTEGER NOT NULL,
                triggered_at_utc TEXT NOT NULL,
                lifecycle TEXT NOT NULL,
                read_at_utc TEXT NULL,
                resolved_at_utc TEXT NULL,
                resolution_action TEXT NULL
            );
            """);

        var instanceId = Id(1);
        var trigger = NewTrigger(instanceId);
        await fixture.ExecuteSqlAsync(
            """
            INSERT INTO reminder_instances (
                id, schedule_id, rule_id, occurrence_id, logical_reminder_id,
                attempt_ordinal, purpose_snapshot, priority_snapshot,
                pinned_snapshot, triggered_at_utc, lifecycle,
                read_at_utc, resolved_at_utc, resolution_action)
            VALUES (
                $id, $scheduleId, $ruleId, $occurrenceId, $logicalReminderId,
                1, 'TASK_START', 'HIGH', 1, $triggeredAtUtc, 'UNREAD',
                NULL, NULL, NULL);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", instanceId.ToString("D"));
                command.Parameters.AddWithValue("$scheduleId", Id(2).ToString("D"));
                command.Parameters.AddWithValue("$ruleId", Id(3).ToString("D"));
                command.Parameters.AddWithValue("$occurrenceId", Id(4).ToString("D"));
                command.Parameters.AddWithValue("$logicalReminderId", Id(5).ToString("D"));
                command.Parameters.AddWithValue(
                    "$triggeredAtUtc",
                    TriggeredAt.ToString(
                        "uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'",
                        CultureInfo.InvariantCulture));
            });

        var source = new AgentCommittedReminderTriggerRecoverySource(
            fixture.DatabasePath,
            attemptStore,
            [NotificationChannelId.Toast, NotificationChannelId.Widget]);
        var recovered = await source.ListCommittedTriggerFactsAsync(
            TestContext.Current.CancellationToken);

        var recoveredTrigger = Assert.Single(recovered);
        Assert.Equal(trigger.InstanceId, recoveredTrigger.InstanceId);
        Assert.Equal(trigger.LogicalReminderId, recoveredTrigger.LogicalReminderId);
        Assert.Equal(NotificationPriority.HIGH, recoveredTrigger.PrioritySnapshot);

        await AppendDeliveredAsync(
            attemptStore,
            recoveredTrigger,
            NotificationChannelId.Toast,
            20);
        await AppendDeliveredAsync(
            attemptStore,
            recoveredTrigger,
            NotificationChannelId.Widget,
            30);

        Assert.Empty(await source.ListCommittedTriggerFactsAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MissingHostBridgeCatalogIsKnownButFailClosed()
    {
        var catalog = AgentNotificationChannelComposition.CreateFailClosed(
            SystemClock.Instance);

        Assert.Equal(
            NotificationChannels.P3.OrderBy(channel => channel.Value),
            catalog.ChannelIds.OrderBy(channel => channel.Value));
        foreach (var channelId in NotificationChannels.P3)
        {
            Assert.True(catalog.TryGet(channelId, out var channel));
            var status = channel.GetStatus();
            Assert.Equal(channelId, status.ChannelId);
            Assert.Equal(NotificationChannelHealth.UNAVAILABLE, status.Health);
            Assert.Equal(NotificationErrorCodes.ChannelUnavailable, status.HealthCode);
        }
    }

    private static async ValueTask AppendDeliveredAsync(
        SqliteNotificationDeliveryAttemptStore attemptStore,
        NotificationTriggerFact trigger,
        NotificationChannelId channelId,
        int suffix)
    {
        var request = new NotificationDeliveryRequest(
            trigger,
            channelId,
            Id(suffix),
            Id(suffix + 1),
            Id(suffix + 2));
        var attempt = NotificationDeliveryAttempt.From(
            request,
            Id(suffix + 3),
            TriggeredAt,
            new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.DELIVERED));
        var result = await attemptStore.AppendAsync(
            attempt,
            TestContext.Current.CancellationToken);
        Assert.Equal(NotificationAttemptRecordDisposition.APPENDED, result.Disposition);
    }

    private static NotificationTriggerFact NewTrigger(Guid instanceId) =>
        new(
            instanceId,
            Id(2),
            Id(3),
            Id(4),
            Id(5),
            attemptOrdinal: 1,
            NotificationPurposeSnapshot.TaskStart,
            NotificationPriority.HIGH,
            pinnedSnapshot: true,
            TriggeredAt);

    private static Guid Id(int suffix) =>
        Guid.Parse($"0191f6a4-3b25-7c12-8d34-56789abcde{suffix:X2}");
}

#pragma warning restore CA1707
