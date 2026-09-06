using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Notifications.Adapters;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.Reminders;

#pragma warning disable CA1707 // Slice directory and test names mirror the frozen plan.
namespace ReminNote.Tests.P3_04;

public sealed class NotificationDeliveryPersistenceTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 4, 8, 0);

    [Fact]
    public async Task AdditiveEfMigrationCreatesAttemptStreamWithoutChangingReminderCoreTables()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ReminNote-P3-04-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "profile.sqlite");

        try
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false,
                    ForeignKeys = true
                }.ToString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using var context = ReminNoteDatabase.CreateContext(connection);
            await context.Database.MigrateAsync(
                "20260902141656_P3ReminderPersistence",
                TestContext.Current.CancellationToken);
            var before = await ReadTablesAsync(
                connection,
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain("notification_delivery_attempt_events", before);
            Assert.Contains("reminder_rules", before);
            Assert.Contains("reminder_schedules", before);
            Assert.Contains("reminder_instances", before);

            await context.Database.MigrateAsync(
                "20260904090000_P304",
                TestContext.Current.CancellationToken);
            var after = await ReadTablesAsync(
                connection,
                TestContext.Current.CancellationToken);

            Assert.Contains("notification_delivery_attempt_events", after);
            Assert.True(before.IsSubsetOf(after));
            Assert.Equal(
                ["notification_delivery_attempt_events"],
                after.Except(before).OrderBy(name => name, StringComparer.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PendingAndOutcomeAreP25WriterTransactionsWithJournalAndReceipt()
    {
        using var database = await P304Database.CreateAsync();
        var request = NewRequest(NotificationChannelId.Toast);
        var before = await database.Store.ReadRevisionStateAsync(
            TestContext.Current.CancellationToken);

        var pending = await database.Attempts.AppendPendingAsync(
            request,
            maxAttempts: 3,
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryPendingDisposition.APPENDED, pending.Disposition);
        Assert.True(pending.Record.IsPending);
        Assert.Equal(before.CurrentRevision + 1, pending.Record.Revision);
        Assert.Equal(0, pending.Record.EventOrdinal);
        Assert.NotEqual(Guid.Empty, pending.Record.JournalBatchId);
        Assert.NotEqual(Guid.Empty, pending.Record.ReceiptId);
        Assert.Equal(
            pending.Record.AttemptId.ToString("D"),
            await database.Store.FindJournalEntityIdAsync(
                pending.Record.Revision,
                "notification_delivery_attempt",
                "pending",
                TestContext.Current.CancellationToken));

        var pendingReceipt = await database.Store.Writer.GetReceiptAsync(
            P304Database.UserSid,
            pending.Record.ReceiptId!.Value.ToString("D"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(pendingReceipt);
        Assert.Equal(P25ReceiptStatus.Committed, pendingReceipt!.Status);
        Assert.Equal(pending.Record.Revision, pendingReceipt.CommittedRevision);

        var outcome = await database.Attempts.AppendOutcomeAsync(
            pending.Record,
            new NotificationChannelDeliveryResponse(NotificationDeliveryOutcome.DELIVERED),
            retryable: false,
            Now + Duration.FromSeconds(1),
            nextAttemptAtUtc: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryAttemptState.DELIVERED, outcome.State);
        Assert.Equal(NotificationDeliveryOutcome.DELIVERED, outcome.Outcome);
        Assert.Equal(pending.Record.EventOrdinal + 1, outcome.EventOrdinal);
        Assert.Equal(pending.Record.Revision + 1, outcome.Revision);
        Assert.Equal(pending.Record.RequestId, outcome.RequestId);
        Assert.Equal(
            outcome.AttemptId.ToString("D"),
            await database.Store.FindJournalEntityIdAsync(
                outcome.Revision,
                "notification_delivery_attempt",
                "outcome",
                TestContext.Current.CancellationToken));
        Assert.Empty(await database.Attempts.ListPendingAsync(TestContext.Current.CancellationToken));
        Assert.Single(await database.Attempts.ListForInstanceAsync(
            request.CoreTrigger.InstanceId,
            TestContext.Current.CancellationToken));

        var eventCount = await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM notification_delivery_attempt_events WHERE profile_scope = $profileScope;",
            static reader => reader.GetInt64(0),
            command => command.Parameters.AddWithValue("$profileScope", P304Database.ProfileScope));
        Assert.Equal(2, eventCount);
    }

    [Fact]
    public async Task RetryUsesNewEffectIdentityKeepsLogicalKeyAndHonorsBackoffAndTerminalFailure()
    {
        using var database = await P304Database.CreateAsync();
        var request = NewRequest(NotificationChannelId.Toast);

        var first = await database.Attempts.AppendPendingAsync(
            request,
            maxAttempts: 3,
            Now,
            TestContext.Current.CancellationToken);
        var replay = await database.Attempts.AppendPendingAsync(
            request,
            maxAttempts: 3,
            Now + Duration.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationDeliveryPendingDisposition.REPLAYED, replay.Disposition);
        Assert.Equal(first.Record.AttemptId, replay.Record.AttemptId);
        Assert.Equal(1, replay.Record.AttemptNumber);

        var retry = request.CreateRetry();
        Assert.NotEqual(request.IdempotencyKey, retry.IdempotencyKey);
        Assert.NotEqual(request.RequestId, retry.RequestId);
        Assert.Equal(request.LogicalDeliveryKey, retry.LogicalDeliveryKey);
        Assert.Equal(request.CoreTrigger.InstanceId, retry.CoreTrigger.InstanceId);

        var failed = await database.Attempts.AppendOutcomeAsync(
            first.Record,
            new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.FAILED,
                "adapter.timeout"),
            retryable: true,
            Now + Duration.FromSeconds(1),
            Now + Duration.FromSeconds(6),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(NotificationDeliveryAttemptState.FAILED, failed.State);
        Assert.True(failed.Retryable);
        Assert.Equal(Now + Duration.FromSeconds(6), failed.NextAttemptAtUtc);

        var early = await database.Attempts.AppendPendingAsync(
            retry,
            maxAttempts: 3,
            Now + Duration.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(NotificationDeliveryPendingDisposition.DEFERRED, early.Disposition);
        Assert.Equal(failed.AttemptId, early.Record.AttemptId);

        var dueRetry = retry.CreateRetry();
        var second = await database.Attempts.AppendPendingAsync(
            dueRetry,
            maxAttempts: 3,
            Now + Duration.FromSeconds(6),
            TestContext.Current.CancellationToken);
        Assert.Equal(NotificationDeliveryPendingDisposition.APPENDED, second.Disposition);
        Assert.Equal(2, second.Record.AttemptNumber);
        Assert.NotEqual(first.Record.AttemptId, second.Record.AttemptId);
        Assert.Equal(request.CoreTrigger.LogicalReminderId, second.Record.LogicalReminderId);
        _ = await database.Attempts.AppendOutcomeAsync(
            second.Record,
            new NotificationChannelDeliveryResponse(NotificationDeliveryOutcome.DELIVERED),
            retryable: false,
            Now + Duration.FromSeconds(7),
            nextAttemptAtUtc: null,
            cancellationToken: TestContext.Current.CancellationToken);

        var trayRequest = NewRequest(NotificationChannelId.Tray);
        var trayPending = await database.Attempts.AppendPendingAsync(
            trayRequest,
            maxAttempts: 3,
            Now,
            TestContext.Current.CancellationToken);
        var terminal = await database.Attempts.AppendOutcomeAsync(
            trayPending.Record,
            new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.FAILED,
                NotificationErrorCodes.CapabilityMissing),
            retryable: false,
            Now + Duration.FromSeconds(1),
            nextAttemptAtUtc: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(terminal.Retryable);
        Assert.Empty(await database.Attempts.ListRecoverableAsync(
            Now + Duration.FromHours(1),
            TestContext.Current.CancellationToken));
        var terminalReplay = await database.Attempts.AppendPendingAsync(
            trayRequest.CreateRetry(),
            maxAttempts: 3,
            Now + Duration.FromHours(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(NotificationDeliveryPendingDisposition.TERMINAL, terminalReplay.Disposition);
        Assert.Equal(terminal.AttemptId, terminalReplay.Record.AttemptId);
    }

    [Fact]
    public async Task RestartRecoversPendingAttemptAndCompletesItWithoutNewCoreFact()
    {
        using var database = await P304Database.CreateAsync();
        var request = NewRequest(NotificationChannelId.Toast);
        NotificationDeliveryAttemptRecord pending;

        var appended = await database.Attempts.AppendPendingAsync(
            request,
            maxAttempts: 3,
            Now,
            TestContext.Current.CancellationToken);
        pending = appended.Record;
        await database.CloseStoreAsync();

        await database.ReopenStoreAsync();
        var recovered = await database.Attempts.ListRecoverableAsync(
            Now,
            TestContext.Current.CancellationToken);
        var recoveredPending = Assert.Single(recovered);
        Assert.Equal(pending.AttemptId, recoveredPending.AttemptId);
        Assert.Equal(pending.IdempotencyKey, recoveredPending.IdempotencyKey);
        Assert.True(recoveredPending.IsPending);

        var channel = new RecordingChannel(
            NotificationChannelId.Toast,
            new NotificationChannelDeliveryResponse(NotificationDeliveryOutcome.DELIVERED));
        await using var dispatcher = new ReminNote.Agent.Notifications.NotificationDeliveryDispatcher(
            new NotificationChannelCatalog([channel]),
            database.Attempts,
            new AllowAllNotificationPresentationPolicy(),
            new FixedClock(Now));

        var results = await dispatcher.RecoverAsync(TestContext.Current.CancellationToken);
        var result = Assert.Single(results);
        Assert.Equal(NotificationDeliveryOutcome.DELIVERED, result.Outcome);
        Assert.True(result.ChannelInvoked);
        Assert.Equal(1, channel.DeliveryCount);
        Assert.Single(channel.Requests);
        Assert.Equal(pending.IdempotencyKey, channel.Requests[0].IdempotencyKey);
        Assert.NotEqual(pending.RequestId, channel.Requests[0].RequestId);
        Assert.Equal(pending.InstanceId, result.CoreTrigger.InstanceId);
        Assert.Equal(pending.AttemptId, result.Attempt!.AttemptId);
        Assert.Equal(1, result.Attempt.EventOrdinal);
        Assert.Equal(channel.Requests[0].RequestId, result.Attempt.RequestId);
        Assert.Empty(await database.Attempts.ListRecoverableAsync(
            Now,
            TestContext.Current.CancellationToken));

    }

    [Fact]
    public async Task QuietHoursRecoveryAggregatesQueuedNormalRemindersIntoOneEffect()
    {
        using var database = await P304Database.CreateAsync();
        var quietStart = Instant.FromUtc(2026, 9, 4, 15, 0); // 23:00 Asia/Shanghai
        var quietEnd = Instant.FromUtc(2026, 9, 4, 23, 0); // 07:00 Asia/Shanghai
        var policy = new ReminNote.Agent.Notifications.QuietHoursNotificationPresentationPolicy(
            new ReminNote.Core.Reminders.Policy.QuietHoursPolicy(
                [new ReminNote.Core.Reminders.Policy.QuietHoursWindow(
                    new LocalTime(22, 0),
                    new LocalTime(7, 0))]),
            DateTimeZoneProviders.Tzdb["Asia/Shanghai"]);
        var channel = new RecordingChannel(
            NotificationChannelId.Toast,
            new NotificationChannelDeliveryResponse(NotificationDeliveryOutcome.DELIVERED));

        await using (var quietDispatcher = new ReminNote.Agent.Notifications.NotificationDeliveryDispatcher(
                         new NotificationChannelCatalog([channel]),
                         database.Attempts,
                         policy,
                         new FixedClock(quietStart)))
        {
            var first = await quietDispatcher.DispatchAsync(
                NewRequest(NotificationChannelId.Toast, 0, NotificationPriority.NORMAL, pinned: false),
                TestContext.Current.CancellationToken);
            var second = await quietDispatcher.DispatchAsync(
                NewRequest(NotificationChannelId.Toast, 1, NotificationPriority.NORMAL, pinned: false),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                NotificationErrorCodes.PolicySummaryQueued,
                first.Attempt!.ErrorCode);
            Assert.Equal(quietEnd, first.Attempt.NextAttemptAtUtc);
            Assert.Equal(
                NotificationErrorCodes.PolicySummaryQueued,
                second.Attempt!.ErrorCode);
            Assert.Equal(0, channel.DeliveryCount);
        }

        await using var recoveryDispatcher = new ReminNote.Agent.Notifications.NotificationDeliveryDispatcher(
            new NotificationChannelCatalog([channel]),
            database.Attempts,
            policy,
            new FixedClock(quietEnd));
        var recovered = await recoveryDispatcher.RecoverAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, recovered.Count);
        Assert.Equal(1, channel.DeliveryCount);
        Assert.Single(recovered, result => result.Outcome == NotificationDeliveryOutcome.DELIVERED);
        var summaryRequest = Assert.Single(channel.Requests);
        Assert.NotNull(summaryRequest.Summary);
        Assert.Equal(2, summaryRequest.Summary!.Count);
        Assert.Equal(
            recovered
                .Where(result => result.Attempt?.ErrorCode == NotificationErrorCodes.PolicySummaryAggregated)
                .Select(result => result.CoreTrigger.LogicalReminderId)
                .Append(summaryRequest.CoreTrigger.LogicalReminderId)
                .OrderBy(id => id.ToString("D"), StringComparer.Ordinal),
            summaryRequest.Summary.LogicalReminderIds
                .OrderBy(id => id.ToString("D"), StringComparer.Ordinal));
        var aggregated = Assert.Single(
            recovered,
            result => result.Attempt?.ErrorCode == NotificationErrorCodes.PolicySummaryAggregated);
        Assert.Equal(NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS, aggregated.Outcome);
        Assert.False(aggregated.Attempt!.Retryable);
        Assert.Empty(await database.Attempts.ListRecoverableAsync(
            quietEnd,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QuietHoursSummaryFailureDefersEveryMemberAndRetriesAsOneSummary()
    {
        using var database = await P304Database.CreateAsync();
        var quietStart = Instant.FromUtc(2026, 9, 4, 15, 0);
        var quietEnd = Instant.FromUtc(2026, 9, 4, 23, 0);
        var policy = new ReminNote.Agent.Notifications.QuietHoursNotificationPresentationPolicy(
            new ReminNote.Core.Reminders.Policy.QuietHoursPolicy(
                [new ReminNote.Core.Reminders.Policy.QuietHoursWindow(
                    new LocalTime(22, 0),
                    new LocalTime(7, 0))]),
            DateTimeZoneProviders.Tzdb["Asia/Shanghai"]);
        var channel = new RecordingChannel(
            NotificationChannelId.Toast,
            new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.FAILED,
                NotificationErrorCodes.DeliveryFailed));

        await using (var quietDispatcher = new ReminNote.Agent.Notifications.NotificationDeliveryDispatcher(
                         new NotificationChannelCatalog([channel]),
                         database.Attempts,
                         policy,
                         new FixedClock(quietStart)))
        {
            _ = await quietDispatcher.DispatchAsync(
                NewRequest(NotificationChannelId.Toast, 0, NotificationPriority.NORMAL, pinned: false),
                TestContext.Current.CancellationToken);
            _ = await quietDispatcher.DispatchAsync(
                NewRequest(NotificationChannelId.Toast, 1, NotificationPriority.NORMAL, pinned: false),
                TestContext.Current.CancellationToken);
        }

        await using var firstRecovery = new ReminNote.Agent.Notifications.NotificationDeliveryDispatcher(
            new NotificationChannelCatalog([channel]),
            database.Attempts,
            policy,
            new FixedClock(quietEnd));
        var failedRecovery = await firstRecovery.RecoverAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, failedRecovery.Count);
        Assert.Equal(1, channel.DeliveryCount);
        var failedRepresentative = Assert.Single(
            failedRecovery,
            result => result.ChannelInvoked);
        Assert.Equal(NotificationDeliveryOutcome.FAILED, failedRepresentative.Outcome);
        Assert.True(failedRepresentative.Attempt!.Retryable);
        Assert.True(
            NotificationErrorCodes.TryParsePolicySummaryRetryCode(
                failedRepresentative.Attempt.ErrorCode,
                out var summaryWindowEndUnixSeconds));
        Assert.Equal(quietEnd.ToUnixTimeSeconds(), summaryWindowEndUnixSeconds);
        var retryAtUtc = failedRepresentative.Attempt.NextAttemptAtUtc!.Value;
        Assert.Empty(await database.Attempts.ListRecoverableAsync(
            quietEnd,
            TestContext.Current.CancellationToken));

        channel.Response = new NotificationChannelDeliveryResponse(
            NotificationDeliveryOutcome.DELIVERED);
        await using var secondRecovery = new ReminNote.Agent.Notifications.NotificationDeliveryDispatcher(
            new NotificationChannelCatalog([channel]),
            database.Attempts,
            policy,
            new FixedClock(retryAtUtc));
        var retried = await secondRecovery.RecoverAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, retried.Count);
        Assert.Equal(2, channel.DeliveryCount);
        var retriedSummaryRequest = Assert.Single(
            channel.Requests,
            request => request.RequestId != failedRepresentative.Attempt!.RequestId);
        Assert.NotNull(retriedSummaryRequest.Summary);
        Assert.Equal(2, retriedSummaryRequest.Summary!.Count);
        Assert.Single(
            retried,
            result => result.Outcome == NotificationDeliveryOutcome.DELIVERED);
        Assert.Single(
            retried,
            result => result.Attempt?.ErrorCode == NotificationErrorCodes.PolicySummaryAggregated);
        Assert.Empty(await database.Attempts.ListRecoverableAsync(
            retryAtUtc,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(129)]
    public async Task QuietHoursSummarySplitsAtTheBoundedMemberLimitWithoutDroppingMembers(
        int memberCount)
    {
        using var database = await P304Database.CreateAsync();
        var quietStart = Instant.FromUtc(2026, 9, 4, 15, 0);
        var quietEnd = Instant.FromUtc(2026, 9, 4, 23, 0);
        var policy = CreateQuietHoursPolicy();
        var channel = new RecordingChannel(
            NotificationChannelId.Toast,
            new NotificationChannelDeliveryResponse(NotificationDeliveryOutcome.DELIVERED));
        var requests = Enumerable
            .Range(0, memberCount)
            .Select(index => NewUniqueRequest(NotificationChannelId.Toast, index))
            .ToArray();
        var expectedEffects = (memberCount + NotificationContractLimits.MaxSummaryMemberCount - 1) /
            NotificationContractLimits.MaxSummaryMemberCount;

        await using (var quietDispatcher = new ReminNote.Agent.Notifications.NotificationDeliveryDispatcher(
                         new NotificationChannelCatalog([channel]),
                         database.Attempts,
                         policy,
                         new FixedClock(quietStart)))
        {
            foreach (var request in requests)
            {
                var queued = await quietDispatcher.DispatchAsync(
                    request,
                    TestContext.Current.CancellationToken);
                Assert.Equal(
                    NotificationErrorCodes.PolicySummaryQueued,
                    queued.Attempt!.ErrorCode);
            }
        }

        await using var recoveryDispatcher = new ReminNote.Agent.Notifications.NotificationDeliveryDispatcher(
            new NotificationChannelCatalog([channel]),
            database.Attempts,
            policy,
            new FixedClock(quietEnd));
        var recovered = await recoveryDispatcher.RecoverAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(memberCount, recovered.Count);
        Assert.Equal(expectedEffects, channel.DeliveryCount);
        Assert.All(
            channel.Requests,
            request => Assert.True(
                request.Summary is null ||
                request.Summary.Count <= NotificationContractLimits.MaxSummaryMemberCount));

        var represented = channel.Requests
            .SelectMany(request => request.Summary?.LogicalReminderIds ??
                [request.CoreTrigger.LogicalReminderId])
            .ToHashSet();
        Assert.Equal(
            requests.Select(request => request.CoreTrigger.LogicalReminderId).ToHashSet(),
            represented);
        Assert.Empty(await database.Attempts.ListRecoverableAsync(
            quietEnd,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QuietHoursSummaryFailureAtTheLimitDefersAndRetriesTheTail()
    {
        const int memberCount = 65;
        using var database = await P304Database.CreateAsync();
        var quietStart = Instant.FromUtc(2026, 9, 4, 15, 0);
        var quietEnd = Instant.FromUtc(2026, 9, 4, 23, 0);
        var policy = CreateQuietHoursPolicy();
        var channel = new RecordingChannel(
            NotificationChannelId.Toast,
            new NotificationChannelDeliveryResponse(
                NotificationDeliveryOutcome.FAILED,
                NotificationErrorCodes.DeliveryFailed));
        var requests = Enumerable
            .Range(0, memberCount)
            .Select(index => NewUniqueRequest(NotificationChannelId.Toast, index + 10_000))
            .ToArray();

        await using (var quietDispatcher = new ReminNote.Agent.Notifications.NotificationDeliveryDispatcher(
                         new NotificationChannelCatalog([channel]),
                         database.Attempts,
                         policy,
                         new FixedClock(quietStart)))
        {
            foreach (var request in requests)
            {
                _ = await quietDispatcher.DispatchAsync(
                    request,
                    TestContext.Current.CancellationToken);
            }
        }

        await using var firstRecovery = new ReminNote.Agent.Notifications.NotificationDeliveryDispatcher(
            new NotificationChannelCatalog([channel]),
            database.Attempts,
            policy,
            new FixedClock(quietEnd));
        var failed = await firstRecovery.RecoverAsync(
            TestContext.Current.CancellationToken);
        var failedRepresentative = Assert.Single(failed, result => result.ChannelInvoked);
        Assert.Equal(NotificationDeliveryOutcome.FAILED, failedRepresentative.Outcome);
        Assert.Equal(1, channel.DeliveryCount);
        Assert.NotNull(channel.Requests[0].Summary);
        Assert.Equal(NotificationContractLimits.MaxSummaryMemberCount, channel.Requests[0].Summary!.Count);
        var retryAtUtc = failedRepresentative.Attempt!.NextAttemptAtUtc!.Value;
        Assert.Empty(await database.Attempts.ListRecoverableAsync(
            quietEnd,
            TestContext.Current.CancellationToken));

        channel.Response = new NotificationChannelDeliveryResponse(
            NotificationDeliveryOutcome.DELIVERED);
        await using var secondRecovery = new ReminNote.Agent.Notifications.NotificationDeliveryDispatcher(
            new NotificationChannelCatalog([channel]),
            database.Attempts,
            policy,
            new FixedClock(retryAtUtc));
        var retried = await secondRecovery.RecoverAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(memberCount, retried.Count);
        Assert.Equal(3, channel.DeliveryCount);
        var represented = channel.Requests
            .Skip(1)
            .SelectMany(request => request.Summary?.LogicalReminderIds ??
                [request.CoreTrigger.LogicalReminderId])
            .ToHashSet();
        var allRepresented = channel.Requests[0].Summary!.LogicalReminderIds
            .Concat(represented)
            .ToHashSet();
        Assert.Equal(
            requests.Select(request => request.CoreTrigger.LogicalReminderId).ToHashSet(),
            allRepresented);
        Assert.Contains(
            requests[^1].CoreTrigger.LogicalReminderId,
            allRepresented);
        Assert.Empty(await database.Attempts.ListRecoverableAsync(
            retryAtUtc,
            TestContext.Current.CancellationToken));
    }

    private static ReminNote.Agent.Notifications.QuietHoursNotificationPresentationPolicy CreateQuietHoursPolicy() =>
        new(
            new ReminNote.Core.Reminders.Policy.QuietHoursPolicy(
                [new ReminNote.Core.Reminders.Policy.QuietHoursWindow(
                    new LocalTime(22, 0),
                    new LocalTime(7, 0))]),
            DateTimeZoneProviders.Tzdb["Asia/Shanghai"]);

    private static NotificationDeliveryRequest NewUniqueRequest(
        NotificationChannelId channelId,
        int seed)
    {
        _ = seed;
        var core = new NotificationTriggerFact(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            attemptOrdinal: 1,
            NotificationPurposeSnapshot.TaskStart,
            NotificationPriority.NORMAL,
            pinnedSnapshot: false,
            Now,
            Now - Duration.FromMinutes(1));
        return new NotificationDeliveryRequest(
            core,
            channelId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7());
    }

    private static NotificationDeliveryRequest NewRequest(
        NotificationChannelId channelId,
        int idOffset = 0,
        NotificationPriority priority = NotificationPriority.HIGH,
        bool pinned = true)
    {
        var offset = idOffset * 20;
        var core = new NotificationTriggerFact(
            Id(1 + offset),
            Id(2 + offset),
            Id(3 + offset),
            Id(4 + offset),
            Id(5 + offset),
            attemptOrdinal: 1,
            NotificationPurposeSnapshot.TaskStart,
            priority,
            pinnedSnapshot: pinned,
            Now,
            Now - Duration.FromMinutes(1));
        return new NotificationDeliveryRequest(
            core,
            channelId,
            Id(12 + offset),
            Id((channelId == NotificationChannelId.Toast ? 13 : 23) + offset),
            Id((channelId == NotificationChannelId.Toast ? 14 : 24) + offset));
    }

    private static Guid Id(int suffix) =>
        Guid.Parse($"0191f6a4-3b25-7c12-8d34-56789abcde{suffix:X2}");

    private static async Task<HashSet<string>> ReadTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private sealed class RecordingChannel(
        NotificationChannelId channelId,
        NotificationChannelDeliveryResponse response) : INotificationChannel
    {
        private readonly NotificationChannelStatus status = new(
            channelId,
            [NotificationCapability.PRESENT],
            NotificationChannelHealth.HEALTHY,
            Now);

        public List<NotificationDeliveryRequest> Requests { get; } = [];

        public NotificationChannelDeliveryResponse Response { get; set; } = response;

        public int DeliveryCount => Requests.Count;

        public NotificationChannelId ChannelId => channelId;

        public NotificationChannelStatus GetStatus() => status;

        public ValueTask<NotificationChannelDeliveryResponse> DeliverAsync(
            NotificationDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(Response);
        }
    }

    private sealed class P304Database : IDisposable
    {
        public const string ProfileScope = "p304-test-profile";

        public const string UserSid = "S-1-5-21-1000-1000-1000-1000";

        private P304Database(
            string root,
            string databasePath,
            P25StorageStore store,
            SqliteNotificationDeliveryAttemptStore attempts)
        {
            Root = root;
            DatabasePath = databasePath;
            Store = store;
            Attempts = attempts;
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public P25StorageStore Store { get; private set; }

        public SqliteNotificationDeliveryAttemptStore Attempts { get; private set; }

        public static async ValueTask<P304Database> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ReminNote-P3-04-durable-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "profile.sqlite");
            try
            {
                var store = await OpenStoreAsync(databasePath).ConfigureAwait(false);
                var attempts = new SqliteNotificationDeliveryAttemptStore(store, UserSid);
                return new P304Database(root, databasePath, store, attempts);
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                throw;
            }
        }

        public async ValueTask<T> ExecuteScalarAsync<T>(
            string sql,
            Func<SqliteDataReader, T> read,
            Action<SqliteCommand>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(read);
            await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            configure?.Invoke(command);
            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            return read(reader);
        }

        public async ValueTask CloseStoreAsync()
        {
            await Store.DisposeAsync().ConfigureAwait(false);
        }

        public async ValueTask ReopenStoreAsync()
        {
            Store = await OpenStoreAsync(DatabasePath).ConfigureAwait(false);
            Attempts = new SqliteNotificationDeliveryAttemptStore(Store, UserSid);
        }

        public void Dispose()
        {
            Store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static async ValueTask<P25StorageStore> OpenStoreAsync(string databasePath)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true
            };
            var connection = new SqliteConnection(builder.ConnectionString);
            P25StorageStore? store = null;
            try
            {
                store = await P25StorageStore.OpenAsync(
                        connection,
                        ProfileScope,
                        requireWal: true,
                        cancellationToken: TestContext.Current.CancellationToken)
                    .ConfigureAwait(false);
                var attempts = new SqliteNotificationDeliveryAttemptStore(store, UserSid);
                await attempts.InitializeSchemaForFixtureAsync(
                        TestContext.Current.CancellationToken)
                    .ConfigureAwait(false);
                return store;
            }
            catch
            {
                if (store is not null)
                {
                    await store.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }

        private async ValueTask<SqliteConnection> OpenConnectionAsync()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true
            };
            var connection = new SqliteConnection(builder.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            return connection;
        }
    }
}

#pragma warning restore CA1707
