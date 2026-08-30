using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using ReminNote.Infrastructure.Persistence.P25;

namespace ReminNote.Tests.P25Storage;

public sealed class P25StorageConsistencyTests
{
    [Fact]
    public async Task FreshProfileStartsAtZeroAndReadConnectionIsWalQueryOnly()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        await using var reader = await P25StorageReader.OpenReadOnlyAsync(
            fixture.DatabasePath,
            P25StorageFixture.ProfileScope,
            TestContext.Current.CancellationToken);

        var state = await reader.ReadRevisionStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, state.CurrentRevision);
        Assert.Equal(0, state.OldestAvailableRevision);

        var empty = await reader.GetChangesAsync(0, 1, TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.Success, empty.Outcome);
        Assert.Equal(0, empty.SnapshotUpperBound);
        Assert.Equal(0, empty.ToInclusive);
        Assert.Empty(empty.Batches);

        await using var connection = await P25ReadOnlyConnectionFactory.OpenAsync(
            fixture.DatabasePath,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, await ExecuteScalarLongAsync(connection, "PRAGMA query_only;"));
        Assert.Equal(1, await ExecuteScalarLongAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("wal", await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task ChangedTaskAndHistoryFixtureShareOneRevisionAndBatchId()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        var taskId = "task-" + Guid.CreateVersion7().ToString("N");
        var historyId = "history-" + Guid.CreateVersion7().ToString("N");
        var request = CreateRequest(expectedRevision: 0);

        var result = await fixture.Store.Writer.ExecuteMutationAsync(
            request,
            async (context, cancellationToken) =>
            {
                await using (var taskCommand = context.CreateCommand(
                    """
                    INSERT INTO p25_fixture_task (task_id, state, revision, batch_id)
                    VALUES ($taskId, 'created', $revision, $batchId);
                    """))
                {
                    taskCommand.Parameters.AddWithValue("$taskId", taskId);
                    taskCommand.Parameters.AddWithValue("$revision", context.ProposedRevision);
                    taskCommand.Parameters.AddWithValue("$batchId", context.ProposedBatchId);
                    await taskCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var historyCommand = context.CreateCommand(
                    """
                    INSERT INTO p25_fixture_history (history_id, task_id, revision, batch_id)
                    VALUES ($historyId, $taskId, $revision, $batchId);
                    """))
                {
                    historyCommand.Parameters.AddWithValue("$historyId", historyId);
                    historyCommand.Parameters.AddWithValue("$taskId", taskId);
                    historyCommand.Parameters.AddWithValue("$revision", context.ProposedRevision);
                    historyCommand.Parameters.AddWithValue("$batchId", context.ProposedBatchId);
                    await historyCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                return P25MutationDecision.Changed(
                [
                    new P25JournalChange("Task", taskId, "created"),
                    new P25JournalChange("TaskHistory", historyId, "created")
                ]);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(P25MutationOutcome.Changed, result.Outcome);
        Assert.Equal(P25ReceiptStatus.Committed, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(1, result.CommittedRevision);
        Assert.NotNull(result.BatchId);
        Assert.True(result.ReceiptDurable);
        Assert.NotNull(result.Receipt);

        var taskRow = await fixture.ExecuteScalarAsync(
            "SELECT revision, batch_id FROM p25_fixture_task WHERE task_id = $taskId;",
            static reader => (reader.GetInt64(0), reader.GetString(1)),
            command => command.Parameters.AddWithValue("$taskId", taskId));
        var historyRow = await fixture.ExecuteScalarAsync(
            "SELECT revision, batch_id FROM p25_fixture_history WHERE history_id = $historyId;",
            static reader => (reader.GetInt64(0), reader.GetString(1)),
            command => command.Parameters.AddWithValue("$historyId", historyId));
        Assert.Equal((1L, result.BatchId!), taskRow);
        Assert.Equal((1L, result.BatchId!), historyRow);

        await using var reader = await P25StorageReader.OpenReadOnlyAsync(
            fixture.DatabasePath,
            P25StorageFixture.ProfileScope,
            TestContext.Current.CancellationToken);
        var page = await reader.GetChangesAsync(0, 128, TestContext.Current.CancellationToken);
        var batch = Assert.Single(page.Batches);
        Assert.Equal(1, batch.Revision);
        Assert.Equal(result.BatchId, batch.BatchId);
        Assert.Equal(2, batch.Changes.Count);
        Assert.Collection(
            batch.Changes,
            taskChange => Assert.Equal("Task", taskChange.EntityType),
            historyChange => Assert.Equal("TaskHistory", historyChange.EntityType));
    }

    [Fact]
    public async Task NoOpStaleRejectedAndReceiptOnlyPathsDoNotAdvanceRevision()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        var changed = await WriteFixtureChangeAsync(fixture, expectedRevision: 0);
        Assert.Equal(1, changed.CommittedRevision);

        var noOpRequest = CreateRequest(1);
        var noOp = await fixture.Store.Writer.ExecuteMutationAsync(
            noOpRequest,
            static (_, _) => ValueTask.FromResult(P25MutationDecision.NoOp()),
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.NoOp, noOp.Outcome);
        Assert.Equal(P25ReceiptStatus.Committed, noOp.Status);
        Assert.False(noOp.Changed);
        Assert.Equal(1, noOp.CommittedRevision);

        var noOpHandlerCalled = false;
        var noOpReplay = await fixture.Store.Writer.ExecuteMutationAsync(
            noOpRequest with { RequestId = NewUuid() },
            (_, _) =>
            {
                noOpHandlerCalled = true;
                return ValueTask.FromResult(P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "must-not-run", "created")]));
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.NoOp, noOpReplay.Outcome);
        Assert.Equal(P25ReceiptStatus.Committed, noOpReplay.Status);
        Assert.False(noOpReplay.Changed);
        Assert.True(noOpReplay.Replayed);
        Assert.Equal(1, noOpReplay.CommittedRevision);
        Assert.False(noOpHandlerCalled);

        var invoked = false;
        var stale = await fixture.Store.Writer.ExecuteMutationAsync(
            CreateRequest(0),
            (_, _) =>
            {
                invoked = true;
                return ValueTask.FromResult(P25MutationDecision.NoOp());
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Stale, stale.Outcome);
        Assert.Equal(P25ReceiptStatus.RejectedStale, stale.Status);
        Assert.Equal("revision.expected_mismatch", stale.ErrorCode);
        Assert.False(invoked);

        var rejected = await fixture.Store.Writer.ExecuteMutationAsync(
            CreateRequest(1),
            static (_, _) => ValueTask.FromResult(P25MutationDecision.Rejected("task.fixture.rejected")),
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Rejected, rejected.Outcome);
        Assert.Equal(P25ReceiptStatus.Rejected, rejected.Status);
        Assert.Equal("task.fixture.rejected", rejected.ErrorCode);

        await using var reader = await P25StorageReader.OpenReadOnlyAsync(
            fixture.DatabasePath,
            P25StorageFixture.ProfileScope,
            TestContext.Current.CancellationToken);
        var state = await reader.ReadRevisionStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, state.CurrentRevision);
        Assert.Equal(1, await fixture.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM change_journal;",
            static reader => reader.GetInt64(0)));
    }

    [Fact]
    public async Task ReplayUsesReceiptAndDifferentHashConflictsWithoutSecondWrite()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        var request = CreateRequest(0);
        var calls = 0;
        var first = await fixture.Store.Writer.ExecuteMutationAsync(
            request,
            async (context, cancellationToken) =>
            {
                calls++;
                await InsertFixtureTaskAsync(context, "replay-task", cancellationToken);
                return P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "replay-task", "created")]);
            },
            TestContext.Current.CancellationToken);

        var replay = await fixture.Store.Writer.ExecuteMutationAsync(
            request with { RequestId = NewUuid() },
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(P25MutationDecision.NoOp());
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(P25MutationOutcome.Replayed, replay.Outcome);
        Assert.True(replay.Replayed);
        Assert.Equal(P25ReceiptStatus.Committed, replay.Status);
        Assert.Equal(first.CommittedRevision, replay.CommittedRevision);
        Assert.Equal(1, calls);
        Assert.NotNull(replay.Receipt);
        Assert.Equal(2, replay.Receipt!.AttemptCount);

        var conflict = await fixture.Store.Writer.ExecuteMutationAsync(
            request with
            {
                RequestId = NewUuid(),
                CanonicalPayloadHash = SHA256.HashData(Encoding.UTF8.GetBytes("different-payload"))
            },
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(P25MutationDecision.NoOp());
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(P25MutationOutcome.Rejected, conflict.Outcome);
        Assert.Equal("ipc.idempotency.conflict", conflict.ErrorCode);
        Assert.Equal(P25ReceiptStatus.Committed, conflict.Status);
        Assert.Equal(1, calls);
        Assert.NotNull(conflict.Receipt);
        Assert.Equal(2, conflict.Receipt!.AttemptCount);
    }

    [Fact]
    public async Task RollbackLeavesNoHalfWriteAndTheSameKeyCanRetry()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        var request = CreateRequest(0);
        var calls = 0;

        var failed = await fixture.Store.Writer.ExecuteMutationAsync(
            request,
            async (context, cancellationToken) =>
            {
                calls++;
                await InsertFixtureTaskAsync(context, "rollback-task", cancellationToken);
                if (calls == 1)
                {
                    throw new InvalidOperationException("injected fixture failure");
                }

                return P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "rollback-task", "created")]);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(P25MutationOutcome.RolledBack, failed.Outcome);
        Assert.Equal(P25ReceiptStatus.RolledBack, failed.Status);
        Assert.True(failed.ReceiptDurable);
        Assert.Equal(0, await fixture.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM p25_fixture_task;",
            static reader => reader.GetInt64(0)));
        Assert.Equal(0, await fixture.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM change_journal;",
            static reader => reader.GetInt64(0)));

        var retry = await fixture.Store.Writer.ExecuteMutationAsync(
            request with { RequestId = NewUuid() },
            async (context, cancellationToken) =>
            {
                calls++;
                await InsertFixtureTaskAsync(context, "rollback-task", cancellationToken);
                return P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "rollback-task", "created")]);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(P25MutationOutcome.Changed, retry.Outcome);
        Assert.Equal(1, retry.CommittedRevision);
        Assert.Equal(2, calls);
        Assert.Equal(1, await fixture.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM p25_fixture_task;",
            static reader => reader.GetInt64(0)));
    }

    [Fact]
    public async Task AmbiguousCommitReconcilesDurableCommittedFactWithoutDowngrade()
    {
        var injectAmbiguousFailure = true;
        Func<ValueTask> afterCommit = () =>
        {
            if (injectAmbiguousFailure)
            {
                injectAmbiguousFailure = false;
                throw new InvalidOperationException("injected post-commit failure");
            }

            return ValueTask.CompletedTask;
        };
        await using var fixture = await P25StorageFixture.CreateAsync(afterCommit);
        var request = CreateRequest(0);
        var handlerCalls = 0;

        var result = await fixture.Store.Writer.ExecuteMutationAsync(
            request,
            async (context, cancellationToken) =>
            {
                handlerCalls++;
                await InsertFixtureTaskAsync(context, "ambiguous-task", cancellationToken);
                return P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "ambiguous-task", "created")]);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(P25MutationOutcome.Changed, result.Outcome);
        Assert.Equal(P25ReceiptStatus.Committed, result.Status);
        Assert.True(result.Changed);
        Assert.False(result.Replayed);
        Assert.Equal(1, result.CommittedRevision);
        Assert.True(result.ReceiptDurable);
        Assert.Equal(1, handlerCalls);
        Assert.Equal(1, await fixture.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM p25_fixture_task WHERE task_id = 'ambiguous-task';",
            static reader => reader.GetInt64(0)));
        Assert.Equal(1, await fixture.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM change_journal WHERE revision = 1;",
            static reader => reader.GetInt64(0)));
        Assert.Equal(1, await fixture.ExecuteScalarAsync(
            "SELECT current_revision FROM revision_state WHERE profile_scope = $profileScope;",
            static reader => reader.GetInt64(0),
            command => command.Parameters.AddWithValue(
                "$profileScope",
                P25StorageFixture.ProfileScope)));

        var durableReceipt = await fixture.Store.Writer.GetReceiptAsync(
            P25StorageFixture.UserSid,
            request.IdempotencyKey,
            TestContext.Current.CancellationToken);
        Assert.NotNull(durableReceipt);
        Assert.Equal(P25ReceiptStatus.Committed, durableReceipt!.Status);
        Assert.True(durableReceipt.Changed);
        Assert.Equal(1, durableReceipt.CommittedRevision);

        var replay = await fixture.Store.Writer.ExecuteMutationAsync(
            request with { RequestId = NewUuid() },
            (_, _) =>
            {
                handlerCalls++;
                return ValueTask.FromResult(P25MutationDecision.NoOp());
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Replayed, replay.Outcome);
        Assert.Equal(P25ReceiptStatus.Committed, replay.Status);
        Assert.True(replay.Changed);
        Assert.Equal(1, replay.CommittedRevision);
        Assert.True(replay.Replayed);
        Assert.Equal(1, handlerCalls);
    }

    [Fact]
    public async Task CancelTimeoutAndUnknownReceiptsHaveExplicitReplayRules()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();

        var cancelledRequest = CreateRequest(0);
        var cancelled = await fixture.Store.Writer.ExecuteMutationAsync(
            cancelledRequest,
            static (_, _) => ValueTask.FromResult(P25MutationDecision.Cancelled()),
            TestContext.Current.CancellationToken);
        Assert.Equal(P25ReceiptStatus.Cancelled, cancelled.Status);
        Assert.Equal(P25MutationOutcome.Cancelled, cancelled.Outcome);

        var cancelHandlerCalled = false;
        var cancelledReplay = await fixture.Store.Writer.ExecuteMutationAsync(
            cancelledRequest with { RequestId = NewUuid() },
            (_, _) =>
            {
                cancelHandlerCalled = true;
                return ValueTask.FromResult(P25MutationDecision.NoOp());
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Replayed, cancelledReplay.Outcome);
        Assert.Equal(P25ReceiptStatus.Cancelled, cancelledReplay.Status);
        Assert.False(cancelHandlerCalled);

        var timeoutRequest = CreateRequest(0);
        var timedOut = await fixture.Store.Writer.ExecuteMutationAsync(
            timeoutRequest,
            static (_, _) => ValueTask.FromResult(P25MutationDecision.TimedOut()),
            TestContext.Current.CancellationToken);
        Assert.Equal(P25ReceiptStatus.TimedOut, timedOut.Status);
        Assert.Equal(P25MutationOutcome.TimedOut, timedOut.Outcome);

        var timeoutRetry = await fixture.Store.Writer.ExecuteMutationAsync(
            timeoutRequest with { RequestId = NewUuid() },
            async (context, cancellationToken) =>
            {
                await InsertFixtureTaskAsync(context, "timeout-retry", cancellationToken);
                return P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "timeout-retry", "created")]);
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Changed, timeoutRetry.Outcome);
        Assert.Equal(1, timeoutRetry.CommittedRevision);

        var unknownRequest = CreateRequest(1);
        var unknown = await fixture.Store.Writer.ExecuteMutationAsync(
            unknownRequest,
            static (_, _) => ValueTask.FromResult(P25MutationDecision.Unknown()),
            TestContext.Current.CancellationToken);
        Assert.Equal(P25ReceiptStatus.Unknown, unknown.Status);
        Assert.Equal(P25MutationOutcome.Unknown, unknown.Outcome);

        var unknownRetry = await fixture.Store.Writer.ExecuteMutationAsync(
            unknownRequest with { RequestId = NewUuid() },
            async (context, cancellationToken) =>
            {
                await InsertFixtureTaskAsync(context, "unknown-retry", cancellationToken);
                return P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "unknown-retry", "created")]);
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Changed, unknownRetry.Outcome);
        Assert.Equal(2, unknownRetry.CommittedRevision);
    }

    [Fact]
    public async Task RecoveryStatesAllowOnlyOneRetryThenReturnStableExhaustedResult()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();

        var timeoutRequest = CreateRequest(0);
        var timeoutCalls = 0;
        var timeoutFirst = await fixture.Store.Writer.ExecuteMutationAsync(
            timeoutRequest,
            (_, _) =>
            {
                timeoutCalls++;
                return ValueTask.FromResult(P25MutationDecision.TimedOut());
            },
            TestContext.Current.CancellationToken);
        var timeoutRetry = await fixture.Store.Writer.ExecuteMutationAsync(
            timeoutRequest with { RequestId = NewUuid() },
            (_, _) =>
            {
                timeoutCalls++;
                return ValueTask.FromResult(P25MutationDecision.TimedOut());
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(P25ReceiptStatus.TimedOut, timeoutFirst.Status);
        Assert.Equal(P25ReceiptStatus.TimedOut, timeoutRetry.Status);
        Assert.Equal(2, timeoutRetry.Receipt!.AttemptCount);

        var timeoutExhausted = await fixture.Store.Writer.ExecuteMutationAsync(
            timeoutRequest with { RequestId = NewUuid() },
            (_, _) =>
            {
                timeoutCalls++;
                return ValueTask.FromResult(P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "must-not-run", "created")]));
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Rejected, timeoutExhausted.Outcome);
        Assert.Equal(P25ReceiptStatus.TimedOut, timeoutExhausted.Status);
        Assert.Equal("storage.receipt_capacity", timeoutExhausted.ErrorCode);
        Assert.False(timeoutExhausted.Changed);
        Assert.Equal(2, timeoutExhausted.Receipt!.AttemptCount);
        Assert.Equal(2, timeoutCalls);

        var timeoutExhaustedRepeat = await fixture.Store.Writer.ExecuteMutationAsync(
            timeoutRequest with { RequestId = NewUuid() },
            (_, _) =>
            {
                timeoutCalls++;
                return ValueTask.FromResult(P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "must-not-run-again", "created")]));
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Rejected, timeoutExhaustedRepeat.Outcome);
        Assert.Equal(P25ReceiptStatus.TimedOut, timeoutExhaustedRepeat.Status);
        Assert.Equal("storage.receipt_capacity", timeoutExhaustedRepeat.ErrorCode);
        Assert.Equal(2, timeoutExhaustedRepeat.Receipt!.AttemptCount);
        Assert.Equal(2, timeoutCalls);

        var rollbackRequest = CreateRequest(0);
        var rollbackCalls = 0;
        for (var attempt = 0; attempt < P25StorageLimits.MaxReceiptAttempts; attempt++)
        {
            var rollback = await fixture.Store.Writer.ExecuteMutationAsync(
                rollbackRequest with { RequestId = NewUuid() },
                (_, _) =>
                {
                    rollbackCalls++;
                    throw new InvalidOperationException("injected recovery failure");
                },
                TestContext.Current.CancellationToken);
            Assert.Equal(P25MutationOutcome.RolledBack, rollback.Outcome);
            Assert.Equal(P25ReceiptStatus.RolledBack, rollback.Status);
        }

        var rollbackExhausted = await fixture.Store.Writer.ExecuteMutationAsync(
            rollbackRequest with { RequestId = NewUuid() },
            (_, _) =>
            {
                rollbackCalls++;
                return ValueTask.FromResult(P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "must-not-run", "created")]));
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Rejected, rollbackExhausted.Outcome);
        Assert.Equal(P25ReceiptStatus.RolledBack, rollbackExhausted.Status);
        Assert.Equal("storage.receipt_capacity", rollbackExhausted.ErrorCode);
        Assert.Equal(2, rollbackExhausted.Receipt!.AttemptCount);
        Assert.Equal(2, rollbackCalls);

        var unknownRequest = CreateRequest(0);
        var unknownCalls = 0;
        for (var attempt = 0; attempt < P25StorageLimits.MaxReceiptAttempts; attempt++)
        {
            var unknown = await fixture.Store.Writer.ExecuteMutationAsync(
                unknownRequest with { RequestId = NewUuid() },
                (_, _) =>
                {
                    unknownCalls++;
                    return ValueTask.FromResult(P25MutationDecision.Unknown());
                },
                TestContext.Current.CancellationToken);
            Assert.Equal(P25MutationOutcome.Unknown, unknown.Outcome);
            Assert.Equal(P25ReceiptStatus.Unknown, unknown.Status);
        }

        var unknownExhausted = await fixture.Store.Writer.ExecuteMutationAsync(
            unknownRequest with { RequestId = NewUuid() },
            (_, _) =>
            {
                unknownCalls++;
                return ValueTask.FromResult(P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "must-not-run", "created")]));
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Rejected, unknownExhausted.Outcome);
        Assert.Equal(P25ReceiptStatus.Unknown, unknownExhausted.Status);
        Assert.Equal("storage.receipt_capacity", unknownExhausted.ErrorCode);
        Assert.Equal(2, unknownExhausted.Receipt!.AttemptCount);
        Assert.Equal(2, unknownCalls);
        Assert.Equal(0, await fixture.ExecuteScalarAsync(
            "SELECT current_revision FROM revision_state WHERE profile_scope = $profileScope;",
            static reader => reader.GetInt64(0),
            command => command.Parameters.AddWithValue("$profileScope", P25StorageFixture.ProfileScope)));
    }

    [Fact]
    public async Task OversizedRevisionBatchReturnsStableErrorWithoutSplitting()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        var largeChanges = new List<P25JournalChange>();
        var entityType = new string('t', 128);
        var entityId = new string('i', 240);
        var changeKind = new string('k', 64);
        for (var index = 0; index < 600; index++)
        {
            largeChanges.Add(new P25JournalChange(entityType, entityId, changeKind));
        }

        var result = await fixture.Store.Writer.ExecuteMutationAsync(
            CreateRequest(0),
            (_, _) => ValueTask.FromResult(P25MutationDecision.Changed(largeChanges)),
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Changed, result.Outcome);
        Assert.Equal(1, result.CommittedRevision);

        await using var reader = await P25StorageReader.OpenReadOnlyAsync(
            fixture.DatabasePath,
            P25StorageFixture.ProfileScope,
            TestContext.Current.CancellationToken);
        var page = await reader.GetChangesAsync(0, 1, TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.BatchTooLarge, page.Outcome);
        Assert.Equal("revision.batch_too_large", page.ErrorCode);
        Assert.Empty(page.Batches);
        Assert.Equal(0, page.ToInclusive);
        Assert.False(page.HasMore);

        var repeat = await reader.GetChangesAsync(0, 1, TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.BatchTooLarge, repeat.Outcome);
        Assert.Equal(page.ErrorCode, repeat.ErrorCode);
        Assert.Empty(repeat.Batches);
    }

    [Fact]
    public async Task ChangesPaginationUsesUpperBoundAndNeverSplitsABatch()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        var first = await WriteFixtureChangeAsync(fixture, 0, changeCount: 2);
        var second = await WriteFixtureChangeAsync(fixture, 1);
        var third = await WriteFixtureChangeAsync(fixture, 2);
        Assert.Equal(3, third.CommittedRevision);

        await using var reader = await P25StorageReader.OpenReadOnlyAsync(
            fixture.DatabasePath,
            P25StorageFixture.ProfileScope,
            TestContext.Current.CancellationToken);
        var page1 = await reader.GetChangesAsync(0, 1, TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.Success, page1.Outcome);
        Assert.Equal(3, page1.SnapshotUpperBound);
        Assert.Equal(1, page1.ToInclusive);
        Assert.True(page1.HasMore);
        Assert.Single(page1.Batches);
        Assert.Equal(2, page1.Batches[0].Changes.Count);

        var page2 = await reader.GetChangesAsync(
            page1.ToInclusive,
            1,
            TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.Success, page2.Outcome);
        Assert.Equal(2, page2.ToInclusive);
        Assert.True(page2.HasMore);
        Assert.Single(page2.Batches);

        var page3 = await reader.GetChangesAsync(
            page2.ToInclusive,
            1,
            TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.Success, page3.Outcome);
        Assert.Equal(3, page3.ToInclusive);
        Assert.False(page3.HasMore);

        var current = await reader.GetChangesAsync(3, 128, TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.Success, current.Outcome);
        Assert.Empty(current.Batches);
        Assert.Equal(3, current.ToInclusive);

        var ahead = await reader.GetChangesAsync(4, 128, TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.Ahead, ahead.Outcome);
        Assert.Equal("revision.ahead", ahead.ErrorCode);

        var invalidZero = await reader.GetChangesAsync(0, 0, TestContext.Current.CancellationToken);
        var invalidLarge = await reader.GetChangesAsync(0, 129, TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.InvalidRequest, invalidZero.Outcome);
        Assert.Equal(P25ChangesOutcome.InvalidRequest, invalidLarge.Outcome);
        Assert.Equal(1, first.CommittedRevision);
        Assert.Equal(2, second.CommittedRevision);
    }

    [Fact]
    public async Task ChangesDetectOldestGapInteriorGapAndBrokenBatchOrdinal()
    {
        await using (var interiorFixture = await P25StorageFixture.CreateAsync())
        {
            await WriteFixtureChangeAsync(interiorFixture, 0);
            await WriteFixtureChangeAsync(interiorFixture, 1);
            await WriteFixtureChangeAsync(interiorFixture, 2);
            await interiorFixture.ExecuteSqlAsync("DELETE FROM change_journal WHERE revision = 2;");

            await using var reader = await P25StorageReader.OpenReadOnlyAsync(
                interiorFixture.DatabasePath,
                P25StorageFixture.ProfileScope,
                TestContext.Current.CancellationToken);
            var page = await reader.GetChangesAsync(0, 128, TestContext.Current.CancellationToken);
            Assert.Equal(P25ChangesOutcome.Gap, page.Outcome);
            Assert.True(page.FullRefreshRequired);
            Assert.Equal("revision.gap", page.ErrorCode);
        }

        await using (var oldestFixture = await P25StorageFixture.CreateAsync())
        {
            await WriteFixtureChangeAsync(oldestFixture, 0);
            await WriteFixtureChangeAsync(oldestFixture, 1);
            await WriteFixtureChangeAsync(oldestFixture, 2);
            await oldestFixture.ExecuteSqlAsync(
                """
                DELETE FROM change_journal WHERE revision = 1;
                UPDATE revision_state SET oldest_available_revision = 2
                WHERE profile_scope = $profileScope;
                """,
                command => command.Parameters.AddWithValue(
                    "$profileScope",
                    P25StorageFixture.ProfileScope));

            await using var reader = await P25StorageReader.OpenReadOnlyAsync(
                oldestFixture.DatabasePath,
                P25StorageFixture.ProfileScope,
                TestContext.Current.CancellationToken);
            var gap = await reader.GetChangesAsync(0, 128, TestContext.Current.CancellationToken);
            Assert.Equal(P25ChangesOutcome.Gap, gap.Outcome);
            Assert.True(gap.FullRefreshRequired);

            var retained = await reader.GetChangesAsync(1, 128, TestContext.Current.CancellationToken);
            Assert.Equal(P25ChangesOutcome.Success, retained.Outcome);
            Assert.Equal(3, retained.ToInclusive);
            Assert.Equal(2, retained.Batches.Count);
        }

        await using (var batchFixture = await P25StorageFixture.CreateAsync())
        {
            await WriteFixtureChangeAsync(batchFixture, 0, changeCount: 2);
            await batchFixture.ExecuteSqlAsync(
                "DELETE FROM change_journal WHERE revision = 1 AND change_ordinal = 0;");

            await using var reader = await P25StorageReader.OpenReadOnlyAsync(
                batchFixture.DatabasePath,
                P25StorageFixture.ProfileScope,
                TestContext.Current.CancellationToken);
            var brokenBatch = await reader.GetChangesAsync(0, 128, TestContext.Current.CancellationToken);
            Assert.Equal(P25ChangesOutcome.IntegrityFailed, brokenBatch.Outcome);
            Assert.True(brokenBatch.FullRefreshRequired);
            Assert.Equal("storage.integrity_failed", brokenBatch.ErrorCode);
        }
    }

    [Fact]
    public async Task ReadSnapshotKeepsModelAndRevisionOnOneWalBoundaryAndApplyIsAtomic()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        await WriteFixtureChangeAsync(fixture, 0, entityId: "snapshot-task", state: "before");
        await using var reader = await P25StorageReader.OpenReadOnlyAsync(
            fixture.DatabasePath,
            P25StorageFixture.ProfileScope,
            TestContext.Current.CancellationToken);
        var snapshotStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var snapshotTask = reader.ReadSnapshotAsync(async (context, cancellationToken) =>
        {
            var before = await ReadFixtureStateAsync(context, "snapshot-task", cancellationToken);
            snapshotStarted.SetResult(true);
            await releaseSnapshot.Task.WaitAsync(cancellationToken);
            var after = await ReadFixtureStateAsync(context, "snapshot-task", cancellationToken);
            return new SnapshotValue(after, context.SnapshotRevision, before);
        }, TestContext.Current.CancellationToken).AsTask();

        await Task.WhenAny(snapshotStarted.Task, snapshotTask).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        if (snapshotTask.IsFaulted)
        {
            await snapshotTask;
        }

        await snapshotStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var update = await fixture.Store.Writer.ExecuteMutationAsync(
            CreateRequest(1),
            async (context, cancellationToken) =>
            {
                await using var command = context.CreateCommand(
                    """
                    UPDATE p25_fixture_task
                    SET state = 'after', revision = $revision, batch_id = $batchId
                    WHERE task_id = 'snapshot-task';
                    """);
                command.Parameters.AddWithValue("$revision", context.ProposedRevision);
                command.Parameters.AddWithValue("$batchId", context.ProposedBatchId);
                await command.ExecuteNonQueryAsync(cancellationToken);
                return P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "snapshot-task", "updated")]);
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(2, update.CommittedRevision);
        releaseSnapshot.SetResult(true);

        var snapshot = await snapshotTask;
        Assert.Equal(1, snapshot.SnapshotRevision);
        Assert.Equal("before", snapshot.Value.Value);
        Assert.Equal("before", snapshot.Value.ValueObservedBeforeRelease);

        var model = new P25AtomicReadModel<SnapshotValue>(
            new SnapshotValue("old", 0, "old"));
        Assert.False(model.TryApply(snapshot, static _ => throw new InvalidOperationException("apply failure")));
        var afterFailure = model.Read();
        Assert.Equal("old", afterFailure.Value.Value);
        Assert.Equal(0, afterFailure.LastSeenRevision);
        Assert.True(model.TryApply(snapshot, static value => value));
        var afterSuccess = model.Read();
        Assert.Equal("before", afterSuccess.Value.Value);
        Assert.Equal(1, afterSuccess.LastSeenRevision);
        Assert.False(model.TryApply(new P25ReadSnapshot<SnapshotValue>(0, new("older", 0, "older")), static value => value));
    }

    [Fact]
    public async Task ReadOnlyConnectionRejectsDataAndSchemaWrites()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        await using var connection = await P25ReadOnlyConnectionFactory.OpenAsync(
            fixture.DatabasePath,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO p25_fixture_task (task_id, state, revision, batch_id) VALUES ('blocked', 'x', 0, 'b');";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE blocked_table (id INTEGER);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 9;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });

        var missingPath = Path.Combine(fixture.Root, "does-not-exist.sqlite");
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => P25ReadOnlyConnectionFactory
                .OpenAsync(missingPath, TestContext.Current.CancellationToken)
                .AsTask());
    }

    [Fact]
    public async Task Signed64MaximumIsReadableButCannotAllocateAnotherRevision()
    {
        await using var fixture = await P25StorageFixture.CreateAsync();
        var changedAtUtc = DateTimeOffset.UtcNow.ToString(
            "O",
            System.Globalization.CultureInfo.InvariantCulture);
        await fixture.ExecuteSqlAsync(
            """
            UPDATE revision_state
            SET current_revision = $maxRevision,
                oldest_available_revision = $maxRevision
            WHERE profile_scope = $profileScope;
            INSERT INTO change_journal (
                profile_scope, revision, change_ordinal, batch_id,
                entity_type, entity_id, change_kind, changed_at_utc
            )
            VALUES (
                $profileScope, $maxRevision, 0, 'max-batch',
                'Task', 'max-task', 'fixture', $changedAtUtc
            );
            """,
            command =>
            {
                command.Parameters.AddWithValue("$profileScope", P25StorageFixture.ProfileScope);
                command.Parameters.AddWithValue("$maxRevision", long.MaxValue);
                command.Parameters.AddWithValue("$changedAtUtc", changedAtUtc);
            });

        await using var reader = await P25StorageReader.OpenReadOnlyAsync(
            fixture.DatabasePath,
            P25StorageFixture.ProfileScope,
            TestContext.Current.CancellationToken);
        var state = await reader.ReadRevisionStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(long.MaxValue, state.CurrentRevision);
        var last = await reader.GetChangesAsync(
            long.MaxValue - 1,
            1,
            TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.Success, last.Outcome);
        Assert.Equal(long.MaxValue, last.ToInclusive);
        Assert.Single(last.Batches);

        var atEnd = await reader.GetChangesAsync(long.MaxValue, 1, TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.Success, atEnd.Outcome);
        Assert.Empty(atEnd.Batches);

        var invalid = await reader.GetChangesAsync(long.MinValue, 1, TestContext.Current.CancellationToken);
        Assert.Equal(P25ChangesOutcome.InvalidRequest, invalid.Outcome);

        var handlerCalled = false;
        var overflow = await fixture.Store.Writer.ExecuteMutationAsync(
            CreateRequest(long.MaxValue),
            (_, _) =>
            {
                handlerCalled = true;
                return ValueTask.FromResult(P25MutationDecision.Changed(
                    [new P25JournalChange("Task", "should-not-run", "created")]));
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(P25MutationOutcome.Rejected, overflow.Outcome);
        Assert.Equal(P25ReceiptStatus.Rejected, overflow.Status);
        Assert.Equal("storage.revision_overflow", overflow.ErrorCode);
        Assert.False(handlerCalled);
        Assert.Equal(1, await fixture.ExecuteScalarAsync(
            "SELECT COUNT(*) FROM change_journal;",
            static read => read.GetInt64(0)));
    }

    private static async ValueTask<P25WriteResult> WriteFixtureChangeAsync(
        P25StorageFixture fixture,
        long expectedRevision,
        string? entityId = null,
        string state = "created",
        int changeCount = 1)
    {
        entityId ??= "task-" + Guid.CreateVersion7().ToString("N");
        var request = CreateRequest(expectedRevision);
        return await fixture.Store.Writer.ExecuteMutationAsync(
            request,
            async (context, cancellationToken) =>
            {
                await InsertFixtureTaskAsync(context, entityId, cancellationToken, state);
                var changes = new List<P25JournalChange>
                {
                    new("Task", entityId, "created")
                };
                for (var index = 1; index < changeCount; index++)
                {
                    changes.Add(new("TaskHistory", entityId + "-history-" + index, "created"));
                }

                return P25MutationDecision.Changed(changes);
            });
    }

    private static async ValueTask InsertFixtureTaskAsync(
        P25MutationContext context,
        string taskId,
        CancellationToken cancellationToken,
        string state = "created")
    {
        await using var command = context.CreateCommand(
            """
            INSERT INTO p25_fixture_task (task_id, state, revision, batch_id)
            VALUES ($taskId, $state, $revision, $batchId);
            """);
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$revision", context.ProposedRevision);
        command.Parameters.AddWithValue("$batchId", context.ProposedBatchId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<string> ReadFixtureStateAsync(
        P25SnapshotContext context,
        string taskId,
        CancellationToken cancellationToken)
    {
        await using var command = context.CreateCommand(
            "SELECT state FROM p25_fixture_task WHERE task_id = $taskId;");
        command.Parameters.AddWithValue("$taskId", taskId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Assert.IsType<string>(value);
    }

    private static P25CommandRequest CreateRequest(
        long expectedRevision,
        string? idempotencyKey = null,
        string? requestId = null,
        byte[]? hash = null,
        string operation = "command.task.fixture")
    {
        idempotencyKey ??= NewUuid();
        requestId ??= NewUuid();
        hash ??= SHA256.HashData(
            Encoding.UTF8.GetBytes($"{operation}:{expectedRevision}:{idempotencyKey}"));
        return new P25CommandRequest(
            P25StorageFixture.UserSid,
            P25StorageFixture.ProfileScope,
            idempotencyKey,
            operation,
            P25StorageLimits.CanonicalHashVersion,
            hash,
            expectedRevision,
            requestId,
            AgentInstanceId: NewUuid());
    }

    private static string NewUuid() => Guid.CreateVersion7().ToString("D");

    private static async ValueTask<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null
            ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record SnapshotValue(
        string Value,
        long RevisionReadAtSnapshot,
        string ValueObservedBeforeRelease);
}
