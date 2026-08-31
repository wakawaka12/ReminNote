#if P25_AGENT_HARNESS

#pragma warning disable xUnit1030 // Internal actor tests deliberately control continuation behavior.
#pragma warning disable xUnit1051 // Cancellation paths are part of the seam under test.

using ReminNote.Agent.Command;
using ReminNote.Agent.Transport;
using ReminNote.Agent.Writer;
using ReminNote.Core.Protocol;
using ReminNote.Core.Transport;
using Xunit;

namespace ReminNote.P25Agent.Harness;

public sealed class P25AgentWriterTests
{
    [Fact]
    public void ProtocolAdapterKeepsAttemptAndLogicalIdentitySeparate()
    {
        const string userSid = "S-1-5-21-100-200-300-400";
        var profileScope = ProtocolProfileScope.Derive(
            userSid,
            @"C:\data\reminnote.sqlite");
        var request = ProtocolRequest.CreateMutationAttempt(
            ProtocolClientKinds.Widget,
            ProtocolIds.NewClientInstanceId(),
            DateTimeOffset.UtcNow,
            timeoutMs: 5_000,
            ProtocolOperations.TaskRename,
            ProtocolJson.ParseObject(
                "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"title\":\"new\"}"),
            ProtocolIds.NewIdempotencyKey(),
            expectedRevision: 0);

        var first = WriterCommandRequest.FromProtocolRequest(request, userSid, profileScope);
        var retryRequest = request.CreateRetryAttempt(DateTimeOffset.UtcNow, timeoutMs: 2_000);
        var retry = WriterCommandRequest.FromProtocolRequest(retryRequest, userSid, profileScope);

        Assert(first.RequestId == request.RequestId, "adapter must preserve the wire attempt ID");
        Assert(retry.RequestId == retryRequest.RequestId, "retry adapter must preserve its new attempt ID");
        Assert(first.RequestId != retry.RequestId, "each retry must use a new RequestId");
        Assert(first.IdempotencyKey == retry.IdempotencyKey, "retry must preserve idempotency key");
        Assert(first.HasSameHash(retry.CanonicalPayloadHash.Span), "retry must preserve canonical hash");
        Assert(first.ActualUserSid == userSid, "adapter must bind the observed user SID");
        Assert(first.ProfileScope == profileScope, "adapter must bind the resolved profile scope");
        Assert(first.Payload is { } payload && payload.GetProperty("title").GetString() == "new", "payload must cross the adapter");
    }

    [Fact]
    public Task DispatcherRejectsUnknownOperationAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = new DelegateExecutor(
            (_, _) => ValueTask.FromResult(WriterExecutionResult.Changed(
                [new WriterChange("task", "task-1", "create")])));
        var dispatcher = CreateDispatcher("command.test", executor);

        Assert(dispatcher.CanExecute("command.test"), "registered operation should be executable");
        Assert(!dispatcher.CanExecute("command.unknown"), "unknown operation should not be executable");

        return RunWithActorAsync(
            new SingleProfileWriterActor("profile-a", persistence, dispatcher),
            async actor =>
            {
                var response = await actor.ExecuteAsync(
                        Command("command.unknown", "key-unknown"))
                    .ConfigureAwait(false);

                Assert(!response.Ok, "unknown operation must fail");
                AssertEqual("ipc.request.invalid", response.ErrorCode, "unknown operation error code");
                AssertEqual(0, executor.Calls, "unknown operation must not invoke executor");
                AssertEqual(0, persistence.ReceiptCount, "unknown operation must not create a receipt");
            });
    }

    [Fact]
    public async Task QueueIsSerialAndBoundedAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = new BlockingFirstExecutor();
        var dispatcher = CreateDispatcher("command.test", executor);
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            dispatcher,
            queueCapacity: 1);

        var firstTask = actor.ExecuteAsync(Command("command.test", "key-1", expectedRevision: 0)).AsTask();
        await executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var secondTask = actor.ExecuteAsync(Command("command.test", "key-2", expectedRevision: 1)).AsTask();
        var overloaded = await actor
            .ExecuteAsync(Command("command.test", "key-3", expectedRevision: 2))
            .ConfigureAwait(false);
        var callsBeforeRelease = executor.Calls;
        var pending = await actor.GetStatusAsync("key-1").ConfigureAwait(false);

        executor.ReleaseFirst.TrySetResult(true);
        var first = await firstTask.ConfigureAwait(false);
        var second = await secondTask.ConfigureAwait(false);

        Assert(!overloaded.Ok, "third command should be rejected when active plus queue fill capacity");
        AssertEqual("ipc.request.overloaded", overloaded.ErrorCode, "overload error code");
        AssertEqual(1, callsBeforeRelease, "the first executor is blocked before it returns");
        AssertEqual(WriterOutcome.Pending, pending.Outcome, "in-flight status should remain pending");
        AssertEqual(WriterReceiptStatus.Pending, pending.ReceiptStatus, "in-flight receipt status");
        Assert(first.Ok && first.Outcome == WriterOutcome.Changed, "first command should commit");
        Assert(second.Ok && second.Outcome == WriterOutcome.Changed, "second queued command should commit after fresh revision");
        AssertEqual(2L, persistence.CurrentRevision, "serial commands should allocate revisions in order");
        AssertEqual(1, executor.MaxConcurrency, "a profile actor must execute one domain handler at a time");
        AssertEqual(2, executor.Calls, "overloaded command must not execute");
        AssertEqual(0, persistence.ReceiptCountFor("key-3"), "overloaded command must not create a receipt");
    }

    [Fact]
    public async Task ExpectedRevisionIsCheckedInsideTransactionAsync()
    {
        var persistence = new InMemoryWriterPersistence { CurrentRevision = 7 };
        var executor = new DelegateExecutor(
            (_, _) => ValueTask.FromResult(WriterExecutionResult.Changed(
                [new WriterChange("task", "task-stale", "update")])));
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));

        var response = await actor
            .ExecuteAsync(Command("command.test", "key-stale", expectedRevision: 6))
            .ConfigureAwait(false);

        Assert(!response.Ok, "stale command must fail");
        AssertEqual(WriterOutcome.Stale, response.Outcome, "stale outcome");
        AssertEqual("revision.expected_mismatch", response.ErrorCode, "stale error code");
        AssertEqual(7L, response.CurrentRevision, "stale response should expose current revision to this internal seam");
        AssertEqual(1, persistence.RevisionReadCount, "revision must be read in the writer transaction");
        AssertEqual(0, executor.Calls, "stale command must not execute domain logic");
        AssertEqual(7L, persistence.CurrentRevision, "stale command must not advance revision");

        var status = await actor.GetStatusAsync("key-stale").ConfigureAwait(false);
        AssertEqual(WriterReceiptStatus.RejectedStale, status.ReceiptStatus, "stale receipt status");
    }

    [Fact]
    public async Task SameKeySameHashReplaysAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = ChangedExecutor("task-replay", "create");
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));
        var command = Command("command.test", "key-replay", expectedRevision: 0, hashByte: 4);

        var first = await actor.ExecuteAsync(command).ConfigureAwait(false);
        var replay = await actor.ExecuteAsync(command).ConfigureAwait(false);

        Assert(first.Ok && first.Outcome == WriterOutcome.Changed, "first command should commit");
        Assert(replay.Ok && replay.Replayed, "same key/hash should be marked replayed");
        AssertEqual(WriterOutcome.Replayed, replay.Outcome, "replay outcome");
        AssertEqual(1, executor.Calls, "replay must not rerun domain executor");
        AssertEqual(1L, persistence.CurrentRevision, "replay must not allocate another revision");
    }

    [Fact]
    public async Task ActiveSameKeySameHashSharesWorkAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = new BlockingFirstExecutor();
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));
        var command = Command("command.test", "key-active-replay", expectedRevision: 0);

        var firstTask = actor.ExecuteAsync(command).AsTask();
        await executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var duplicateTask = actor.ExecuteAsync(command).AsTask();

        executor.ReleaseFirst.TrySetResult(true);
        var first = await firstTask.ConfigureAwait(false);
        var duplicate = await duplicateTask.ConfigureAwait(false);

        Assert(first.Ok && first.Outcome == WriterOutcome.Changed, "original active command should commit");
        Assert(duplicate.Ok && duplicate.Replayed, "active same key/hash should share the completion");
        AssertEqual(WriterOutcome.Replayed, duplicate.Outcome, "active replay outcome");
        AssertEqual(1, executor.Calls, "active replay must not rerun domain executor");
        AssertEqual(1L, persistence.CurrentRevision, "active replay must not allocate another revision");
    }

    [Fact]
    public async Task SameKeyDifferentHashConflictsAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = ChangedExecutor("task-conflict", "create");
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));

        var first = await actor
            .ExecuteAsync(Command("command.test", "key-conflict", expectedRevision: 0, hashByte: 5))
            .ConfigureAwait(false);
        var conflict = await actor
            .ExecuteAsync(Command("command.test", "key-conflict", expectedRevision: 0, hashByte: 6))
            .ConfigureAwait(false);

        Assert(first.Ok, "first command should commit");
        Assert(!conflict.Ok, "different hash must fail");
        AssertEqual("ipc.idempotency.conflict", conflict.ErrorCode, "conflict error code");
        AssertEqual(1, executor.Calls, "conflict must not rerun domain executor");
        AssertEqual(1L, persistence.CurrentRevision, "conflict must not change revision");
        var status = await actor.GetStatusAsync("key-conflict").ConfigureAwait(false);
        AssertEqual(WriterReceiptStatus.Committed, status.ReceiptStatus, "conflict must preserve first receipt");
    }

    [Fact]
    public async Task NoOpDoesNotAdvanceRevisionAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = new DelegateExecutor(
            (_, _) => ValueTask.FromResult(WriterExecutionResult.NoOp()));
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));
        var command = Command("command.test", "key-noop", expectedRevision: 0);

        var response = await actor.ExecuteAsync(command).ConfigureAwait(false);
        var replay = await actor.ExecuteAsync(command).ConfigureAwait(false);
        var status = await actor.GetStatusAsync(command.IdempotencyKey).ConfigureAwait(false);

        Assert(response.Ok && response.Outcome == WriterOutcome.NoOp, "no-op should be successful");
        Assert(response.Changed == false, "no-op changed flag");
        AssertEqual(0L, response.CommittedRevision, "no-op uses current revision as reconciliation position");
        AssertEqual(0L, persistence.CurrentRevision, "no-op must not allocate a revision");
        Assert(replay.Replayed, "no-op should be replayable");
        AssertEqual(WriterReceiptStatus.Committed, status.ReceiptStatus, "no-op receipt status");
        AssertEqual(0, persistence.CommittedBatchCount, "no-op must not persist a domain batch");
        AssertEqual(1, executor.Calls, "no-op replay must not rerun executor");
    }

    [Fact]
    public async Task RejectedDoesNotAdvanceRevisionAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = new DelegateExecutor(
            (_, _) => ValueTask.FromResult(WriterExecutionResult.Rejected("task.domain.rejected")));
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));
        var command = Command("command.test", "key-rejected", expectedRevision: 0);

        var response = await actor.ExecuteAsync(command).ConfigureAwait(false);
        var replay = await actor.ExecuteAsync(command).ConfigureAwait(false);
        var status = await actor.GetStatusAsync(command.IdempotencyKey).ConfigureAwait(false);

        Assert(!response.Ok && response.Outcome == WriterOutcome.Rejected, "domain rejection should fail");
        AssertEqual("task.domain.rejected", response.ErrorCode, "domain error code must remain specific");
        AssertEqual(0L, persistence.CurrentRevision, "rejection must not allocate a revision");
        Assert(replay.Replayed, "rejection should be replayable");
        AssertEqual(WriterReceiptStatus.Rejected, status.ReceiptStatus, "rejected receipt status");
        AssertEqual(1, executor.Calls, "rejected replay must not rerun executor");
    }

    [Fact]
    public async Task RollbackDiscardsDirtyStateAsync()
    {
        var persistence = new InMemoryWriterPersistence { FailDomainCommitCount = 1 };
        var executor = ChangedExecutor("task-rollback", "update");
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));
        var command = Command("command.test", "key-rollback", expectedRevision: 0);

        var rolledBack = await actor.ExecuteAsync(command).ConfigureAwait(false);
        Assert(!rolledBack.Ok, "injected transaction failure should fail");
        AssertEqual(WriterOutcome.RolledBack, rolledBack.Outcome, "rollback outcome");
        AssertEqual(WriterReceiptStatus.RolledBack, rolledBack.ReceiptStatus, "rollback receipt status");
        AssertEqual(0L, persistence.CurrentRevision, "rollback must not advance revision");
        AssertEqual(0, persistence.CommittedBatchCount, "rollback must not commit domain changes");
        Assert(persistence.DirtyTrackerDiscardCount >= 1, "failed transaction must discard dirty state");

        var retried = await actor.ExecuteAsync(command).ConfigureAwait(false);
        Assert(retried.Ok && retried.Outcome == WriterOutcome.Changed, "rollback key may retry once with same hash");
        AssertEqual(1L, persistence.CurrentRevision, "successful retry revision");
        AssertEqual(2, executor.Calls, "retry should execute exactly once after the failure");

        var replay = await actor.ExecuteAsync(command).ConfigureAwait(false);
        Assert(replay.Replayed, "successful retry should become terminal replay");
        AssertEqual(2, executor.Calls, "terminal replay must not execute again");
    }

    [Fact]
    public async Task CommitExceptionAfterDurableApplyReconcilesAsCommittedAsync()
    {
        var persistence = new InMemoryWriterPersistence { FailAfterDomainApplyCount = 1 };
        var executor = ChangedExecutor("task-ambiguous", "update");
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));
        var command = Command("command.test", "key-ambiguous", expectedRevision: 0);

        var response = await actor.ExecuteAsync(command).ConfigureAwait(false);
        var receipt = persistence.FindReceipt(command.IdempotencyKey);
        var replay = await actor.ExecuteAsync(command).ConfigureAwait(false);

        Assert(response.Ok, "a post-commit exception must reconcile the durable result");
        AssertEqual(WriterOutcome.Changed, response.Outcome, "ambiguous commit outcome");
        AssertEqual(WriterReceiptStatus.Committed, response.ReceiptStatus, "ambiguous commit receipt");
        AssertEqual(1L, response.CommittedRevision, "ambiguous commit revision");
        AssertEqual(WriterReceiptStatus.Committed, receipt?.Status, "durable receipt must remain committed");
        AssertEqual(1L, persistence.CurrentRevision, "ambiguous commit must allocate one revision");
        AssertEqual(1, persistence.CommittedBatchCount, "ambiguous commit must persist one domain batch");
        Assert(replay.Replayed, "a reconciled command must replay without a second domain attempt");
        AssertEqual(1, executor.Calls, "ambiguous commit must not rerun the executor");
    }

    [Fact]
    public async Task CancelBeforeCommitRollsBackAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = new BlockingFirstExecutor();
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));
        var command = Command("command.test", "key-cancel", expectedRevision: 0);

        var task = actor.ExecuteAsync(command).AsTask();
        await executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var cancel = await actor.CancelAsync(command.IdempotencyKey).ConfigureAwait(false);
        AssertEqual(WriterCancelOutcome.Requested, cancel.Outcome, "pre-commit cancel should be accepted");

        executor.ReleaseFirst.TrySetResult(true);
        var response = await task.ConfigureAwait(false);

        Assert(!response.Ok && response.Outcome == WriterOutcome.Cancelled, "pre-commit cancel outcome");
        AssertEqual(WriterReceiptStatus.Cancelled, response.ReceiptStatus, "cancelled receipt status");
        AssertEqual(0L, persistence.CurrentRevision, "cancel before commit must not advance revision");
        AssertEqual(0, persistence.CommittedBatchCount, "cancel before commit must not commit domain changes");
        Assert(persistence.DirtyTrackerDiscardCount >= 1, "cancelled transaction must discard dirty state");

        var replay = await actor.ExecuteAsync(command).ConfigureAwait(false);
        Assert(replay.Replayed, "cancelled terminal receipt should replay");
        AssertEqual(1, executor.Calls, "replay of cancellation must not rerun domain");
    }

    [Fact]
    public async Task CancelQueuedPendingReceiptFinalizesCancellationAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = new BlockingFirstExecutor();
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor),
            queueCapacity: 1);
        var firstCommand = Command("command.test", "key-cancel-first", expectedRevision: 0);
        var queuedCommand = Command("command.test", "key-cancel-queued", expectedRevision: 1);
        persistence.SeedReceipt(WriterReceipt.Pending(
            queuedCommand.IdempotencyKey,
            queuedCommand.Operation,
            queuedCommand.CanonicalPayloadHash));

        var firstTask = actor.ExecuteAsync(firstCommand).AsTask();
        await executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var queuedTask = actor.ExecuteAsync(queuedCommand).AsTask();
        var cancel = await actor.CancelAsync(queuedCommand.IdempotencyKey).ConfigureAwait(false);

        executor.ReleaseFirst.TrySetResult(true);
        var first = await firstTask.ConfigureAwait(false);
        var queued = await queuedTask.ConfigureAwait(false);

        AssertEqual(WriterCancelOutcome.Requested, cancel.Outcome, "queued pending cancel should be accepted");
        Assert(first.Ok, "the preceding command should commit");
        Assert(!queued.Ok && queued.Outcome == WriterOutcome.Cancelled, "queued pending cancel outcome");
        AssertEqual(WriterReceiptStatus.Cancelled, queued.ReceiptStatus, "queued receipt should be finalized");
        AssertEqual(1L, persistence.CurrentRevision, "queued cancel must not allocate another revision");
        AssertEqual(1, executor.Calls, "cancelled queued command must not enter domain executor");
    }

    [Fact]
    public async Task CancelOrphanedPendingReceiptIsActorOrderedAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = new BlockingFirstExecutor();
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor),
            queueCapacity: 1);
        var firstCommand = Command("command.test", "key-cancel-order-first", expectedRevision: 0);
        var orphanedCommand = Command("command.test", "key-cancel-order-orphan", expectedRevision: 1);
        persistence.SeedReceipt(WriterReceipt.Pending(
            orphanedCommand.IdempotencyKey,
            orphanedCommand.Operation,
            orphanedCommand.CanonicalPayloadHash));

        var firstTask = actor.ExecuteAsync(firstCommand).AsTask();
        await executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // There is no active WorkItem for this key. Cancellation must still
        // enter the same actor queue and conditionally finalize the durable
        // pending receipt after the preceding command's commit.
        var cancelTask = actor.CancelAsync(orphanedCommand.IdempotencyKey).AsTask();

        executor.ReleaseFirst.TrySetResult(true);
        var first = await firstTask.ConfigureAwait(false);
        var cancel = await cancelTask.ConfigureAwait(false);

        Assert(first.Ok, "the preceding command should commit");
        AssertEqual(WriterCancelOutcome.Requested, cancel.Outcome, "orphaned pending cancel should be accepted");
        AssertEqual(WriterReceiptStatus.Cancelled, cancel.ReceiptStatus, "orphaned receipt should be finalized");
        AssertEqual(1L, persistence.CurrentRevision, "queued cancellation must preserve preceding revision");

        var replay = await actor.ExecuteAsync(orphanedCommand).ConfigureAwait(false);
        Assert(!replay.Ok && replay.Replayed, "cancelled orphaned receipt should replay without execution");
        AssertEqual(WriterReceiptStatus.Cancelled, replay.ReceiptStatus, "replay should observe cancellation");
        AssertEqual(1, executor.Calls, "orphaned cancellation must not enter domain executor");
    }

    [Fact]
    public async Task PostCommitCancelAndResponseLossDoNotReplayDomainAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = ChangedExecutor("task-response-loss", "update");
        persistence.BlockNextDomainCommit();
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));
        var command = Command("command.test", "key-response-loss", expectedRevision: 0);
        using var responseCancellation = new CancellationTokenSource();
        var responseTask = actor.ExecuteAsync(command, responseCancellation.Token).AsTask();

        await persistence.DomainCommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var cancel = await actor.CancelAsync(command.IdempotencyKey).ConfigureAwait(false);
        AssertEqual(
            WriterCancelOutcome.AlreadyAtCommitBoundary,
            cancel.Outcome,
            "post-commit cancellation must not cancel the durable transaction");

        responseCancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
                () => responseTask,
                "response loss should cancel only the caller wait")
            .ConfigureAwait(false);

        persistence.ReleaseDomainCommit.TrySetResult(true);
        await WaitUntilAsync(
                () => persistence.CurrentRevision == 1,
                "the accepted commit should complete after response loss")
            .ConfigureAwait(false);

        var status = await actor.GetStatusAsync(command.IdempotencyKey).ConfigureAwait(false);
        AssertEqual(WriterReceiptStatus.Committed, status.ReceiptStatus, "response loss status reconciliation");
        AssertEqual(1, executor.Calls, "response loss must not rerun domain");
        AssertEqual(1, persistence.CommittedBatchCount, "response loss must not duplicate domain commit");

        var replay = await actor.ExecuteAsync(command).ConfigureAwait(false);
        Assert(replay.Replayed, "original key/hash should reconcile as replay");
        AssertEqual(1, executor.Calls, "reconcile replay must not rerun domain");
    }

    [Fact]
    public async Task TimeoutIsDurableAndRetryableOnceAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = new SequenceExecutor(
            (_, _) => ValueTask.FromException<WriterExecutionResult>(
                new WriterCommandTimeoutException()),
            (_, _) => ValueTask.FromResult(WriterExecutionResult.Changed(
                [new WriterChange("task", "task-timeout", "create")])));
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));
        var command = Command("command.test", "key-timeout", expectedRevision: 0);

        var timedOut = await actor.ExecuteAsync(command).ConfigureAwait(false);
        Assert(!timedOut.Ok && timedOut.Outcome == WriterOutcome.Timeout, "timeout outcome");
        AssertEqual(WriterReceiptStatus.TimedOut, timedOut.ReceiptStatus, "timeout receipt status");
        AssertEqual(0L, persistence.CurrentRevision, "timeout before commit must not advance revision");

        var retried = await actor.ExecuteAsync(command).ConfigureAwait(false);
        Assert(retried.Ok && retried.Outcome == WriterOutcome.Changed, "timeout may retry once with the same key/hash");
        AssertEqual(1L, persistence.CurrentRevision, "successful timeout retry revision");
        AssertEqual(2, executor.Calls, "timeout retry count");
    }

    [Fact]
    public async Task UnknownIsDurableAndRetryBoundedAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = new SequenceExecutor(
            (_, _) => ValueTask.FromException<WriterExecutionResult>(
                new WriterCommandUnknownException()),
            (_, _) => ValueTask.FromException<WriterExecutionResult>(
                new WriterCommandUnknownException()));
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));
        var command = Command("command.test", "key-unknown", expectedRevision: 0);

        var first = await actor.ExecuteAsync(command).ConfigureAwait(false);
        var second = await actor.ExecuteAsync(command).ConfigureAwait(false);
        var third = await actor.ExecuteAsync(command).ConfigureAwait(false);

        Assert(!first.Ok && first.Outcome == WriterOutcome.Unknown, "first unknown outcome");
        Assert(!second.Ok && second.Outcome == WriterOutcome.Unknown, "second unknown outcome");
        Assert(second.Replayed == false, "second attempt is the one allowed recovery attempt");
        Assert(third.Replayed, "third same key/hash should only replay bounded unknown");
        AssertEqual(WriterReceiptStatus.Unknown, third.ReceiptStatus, "unknown receipt status");
        AssertEqual(2, executor.Calls, "unknown recovery must not loop forever");
        AssertEqual(0L, persistence.CurrentRevision, "unknown attempts must not fabricate a revision");
    }

    [Fact]
    public async Task OrphanedPendingReceiptRecoversAfterActorRestartAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = ChangedExecutor("task-recovered", "update");
        var command = Command("command.test", "key-orphaned-pending", expectedRevision: 0);
        persistence.SeedReceipt(WriterReceipt.Pending(
            command.IdempotencyKey,
            command.Operation,
            command.CanonicalPayloadHash));

        // A new actor over the same durable seam represents the restart case;
        // this does not claim a production Agent restart or real database run.
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));

        var response = await actor.ExecuteAsync(command).ConfigureAwait(false);
        var receipt = persistence.FindReceipt(command.IdempotencyKey);

        Assert(response.Ok && response.Outcome == WriterOutcome.Changed, "orphaned pending receipt should recover once");
        AssertEqual(1, executor.Calls, "orphaned pending recovery should execute once");
        AssertEqual(1L, persistence.CurrentRevision, "recovered pending command should allocate one revision");
        Assert(receipt is not null, "recovered receipt should remain durable");
        AssertEqual(WriterReceiptStatus.Committed, receipt!.Status, "recovered receipt status");
        AssertEqual(2, receipt.AttemptCount, "recovery should consume one bounded retry attempt");
    }

    [Fact]
    public async Task OrphanedPendingReceiptDoesNotHangPastRecoveryBudgetAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = ChangedExecutor("task-never-run", "update");
        var command = Command("command.test", "key-orphaned-pending-budget", expectedRevision: 0);
        persistence.SeedReceipt(WriterReceipt.Pending(
            command.IdempotencyKey,
            command.Operation,
            command.CanonicalPayloadHash,
            attemptCount: 2));
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.test", executor));

        var unknown = await actor.ExecuteAsync(command).ConfigureAwait(false);
        var replay = await actor.ExecuteAsync(command).ConfigureAwait(false);
        var receipt = persistence.FindReceipt(command.IdempotencyKey);

        Assert(!unknown.Ok && unknown.Outcome == WriterOutcome.Unknown, "exhausted orphaned pending should become unknown");
        Assert(!replay.Ok && replay.Replayed, "bounded unknown should be replayable without hanging");
        AssertEqual(WriterReceiptStatus.Unknown, receipt?.Status, "exhausted pending receipt status");
        AssertEqual(0, executor.Calls, "exhausted orphaned pending must not execute domain logic");
        AssertEqual(0L, persistence.CurrentRevision, "exhausted orphaned pending must not fabricate a revision");
    }

    [Fact]
    public async Task ReorderUsesOneTransactionAndRevisionAsync()
    {
        var persistence = new InMemoryWriterPersistence();
        var executor = new DelegateExecutor(
            (_, _) => ValueTask.FromResult(WriterExecutionResult.Changed(
                [
                    new WriterChange("task", "task-a", "sort_order"),
                    new WriterChange("task", "task-b", "sort_order")
                ])));
        await using var actor = new SingleProfileWriterActor(
            "profile-a",
            persistence,
            CreateDispatcher("command.task.reorder", executor));

        var response = await actor
            .ExecuteAsync(Command("command.task.reorder", "key-reorder", expectedRevision: 0))
            .ConfigureAwait(false);

        Assert(response.Ok && response.Outcome == WriterOutcome.Changed, "reorder should commit");
        AssertEqual(1L, response.CommittedRevision, "reorder committed revision");
        AssertEqual(1L, persistence.CurrentRevision, "reorder should allocate one revision");
        AssertEqual(1, persistence.CommittedBatchCount, "reorder should have one committed domain batch");
        AssertEqual(2, persistence.CommittedBatches[0].Count, "reorder batch should contain both rows");
        AssertEqual(1, persistence.DomainTransactionCount, "reorder should use one domain transaction");
        AssertEqual(1, persistence.CommitCountFor(WriterTransactionKind.Domain), "reorder should commit once");
    }

    private static WriterCommandDispatcher CreateDispatcher(
        string operation,
        IWriterCommandExecutor executor) =>
        new([new KeyValuePair<string, IWriterCommandExecutor>(operation, executor)]);

    private static WriterCommandRequest Command(
        string operation,
        string key,
        long expectedRevision = 0,
        byte hashByte = 1) =>
        new(operation, key, Enumerable.Repeat(hashByte, 32).ToArray(), expectedRevision);

    private static DelegateExecutor ChangedExecutor(string taskId, string changeKind) =>
        new((_, _) => ValueTask.FromResult(WriterExecutionResult.Changed(
            [new WriterChange("task", taskId, changeKind)])));

    private static async Task RunWithActorAsync(
        SingleProfileWriterActor actor,
        Func<SingleProfileWriterActor, Task> test)
    {
        await using (actor.ConfigureAwait(false))
        {
            await test(actor).ConfigureAwait(false);
        }
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        string message,
        int timeoutMilliseconds = 5_000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new InvalidOperationException(message);
            }

            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message}; expected '{expected}', actual '{actual}'.");
        }
    }

    private sealed class DelegateExecutor : IWriterCommandExecutor
    {
        private readonly Func<IWriterTransaction, CancellationToken, ValueTask<WriterExecutionResult>> execute;
        private int active;
        private int calls;
        private int maxConcurrency;

        public DelegateExecutor(
            Func<IWriterTransaction, CancellationToken, ValueTask<WriterExecutionResult>> execute)
        {
            this.execute = execute;
        }

        public int Calls => Volatile.Read(ref calls);

        public int MaxConcurrency => Volatile.Read(ref maxConcurrency);

        public async ValueTask<WriterExecutionResult> ExecuteAsync(
            IWriterTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            var current = Interlocked.Increment(ref active);
            UpdateMaxConcurrency(current);
            try
            {
                return await execute(transaction, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        private void UpdateMaxConcurrency(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref maxConcurrency);
                if (observed >= current ||
                    Interlocked.CompareExchange(ref maxConcurrency, current, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    private sealed class BlockingFirstExecutor : IWriterCommandExecutor
    {
        private int calls;
        private int active;

        public BlockingFirstExecutor()
        {
            FirstStarted = NewSignal();
            ReleaseFirst = NewSignal();
        }

        public TaskCompletionSource<bool> FirstStarted { get; }

        public TaskCompletionSource<bool> ReleaseFirst { get; }

        public int Calls => Volatile.Read(ref calls);

        public int MaxConcurrency => Volatile.Read(ref maxConcurrency);

        private int maxConcurrency;

        public async ValueTask<WriterExecutionResult> ExecuteAsync(
            IWriterTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref calls);
            var current = Interlocked.Increment(ref active);
            while (true)
            {
                var observed = Volatile.Read(ref maxConcurrency);
                if (observed >= current ||
                    Interlocked.CompareExchange(ref maxConcurrency, current, observed) == observed)
                {
                    break;
                }
            }
            try
            {
                if (call == 1)
                {
                    FirstStarted.TrySetResult(true);
                    await ReleaseFirst.Task.ConfigureAwait(false);
                }

                return WriterExecutionResult.Changed(
                    [new WriterChange("task", $"task-{call}", "update")]);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    private sealed class SequenceExecutor : IWriterCommandExecutor
    {
        private readonly Queue<Func<IWriterTransaction, CancellationToken, ValueTask<WriterExecutionResult>>> steps;
        private readonly object gate = new();

        public SequenceExecutor(
            params Func<IWriterTransaction, CancellationToken, ValueTask<WriterExecutionResult>>[] steps)
        {
            this.steps = new Queue<Func<IWriterTransaction, CancellationToken, ValueTask<WriterExecutionResult>>>(steps);
        }

        public int Calls { get; private set; }

        public ValueTask<WriterExecutionResult> ExecuteAsync(
            IWriterTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            Func<IWriterTransaction, CancellationToken, ValueTask<WriterExecutionResult>> step;
            lock (gate)
            {
                Calls++;
                step = steps.Count == 0
                    ? throw new InvalidOperationException("No executor step remains.")
                    : steps.Dequeue();
            }

            return step(transaction, cancellationToken);
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class P25AgentTransportTests
{
    [Fact]
    public async Task InFlightOverflowReturnsBoundedProtocolErrorAsync()
    {
        var agentInstanceId = Guid.Parse("019b2b36-4444-7abc-8def-0123456789af");
        var hello = ProtocolRequest.CreateReadAttempt(
            ProtocolClientKinds.Widget,
            ProtocolIds.NewClientInstanceId(),
            DateTimeOffset.UtcNow,
            timeoutMs: 2_000,
            ProtocolOperations.SessionHello,
            ProtocolJson.CreateHelloPayload(
                new ProtocolHelloPayload(
                    [ProtocolVersion.Current],
                    [],
                    [])));
        var firstCommand = ProtocolRequest.CreateMutationAttempt(
            ProtocolClientKinds.Widget,
            ProtocolIds.NewClientInstanceId(),
            DateTimeOffset.UtcNow,
            timeoutMs: 5_000,
            ProtocolOperations.TaskRename,
            ProtocolJson.ParseObject(
                "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"title\":\"first\"}"),
            ProtocolIds.NewIdempotencyKey(),
            expectedRevision: 0);
        var secondCommand = ProtocolRequest.CreateMutationAttempt(
            ProtocolClientKinds.Widget,
            ProtocolIds.NewClientInstanceId(),
            DateTimeOffset.UtcNow,
            timeoutMs: 5_000,
            ProtocolOperations.TaskRename,
            ProtocolJson.ParseObject(
                "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ac\",\"title\":\"second\"}"),
            ProtocolIds.NewIdempotencyKey(),
            expectedRevision: 0);
        var helloResponse = new ProtocolResponse(
            ProtocolVersion.Current,
            hello.RequestId,
            ProtocolOperations.SessionHello,
            agentInstanceId.ToString("D"),
            serverRevision: 7,
            ok: true,
            replayed: false,
            ProtocolOutcomes.NoOp,
            committedRevision: null,
            ProtocolJson.ParseObject("{}"),
            error: null);
        var firstResponse = new ProtocolResponse(
            ProtocolVersion.Current,
            firstCommand.RequestId,
            ProtocolOperations.TaskRename,
            agentInstanceId.ToString("D"),
            serverRevision: 7,
            ok: true,
            replayed: false,
            ProtocolOutcomes.NoOp,
            committedRevision: 7,
            ProtocolJson.ParseObject("{}"),
            error: null);
        var codec = new ScriptedFrameCodec(
            ProtocolJson.SerializeRequest(hello),
            ProtocolJson.SerializeRequest(firstCommand),
            ProtocolJson.SerializeRequest(secondCommand),
            null);
        var dispatcher = new BlockingTransportDispatcher(
            codec,
            ProtocolJson.SerializeResponse(firstResponse));
        var release = NewSignal();
        dispatcher.Release = release;

        await using var session = new NamedPipeTransportSession(
            new NoOpPayloadValidator(),
            new ReadyHandshake(ProtocolJson.SerializeResponse(helloResponse)),
            dispatcher,
            new ProtocolOverloadResponder(agentInstanceId, () => 7),
            new TransportLimits(new HarnessTransportLimitSource { MaxInFlightRequests = 1 }),
            codec);

        var runTask = session.RunAsync(
            new MemoryStream(),
            new TransportPeerIdentity("S-1-5-21-100-200-300-400", "p1-test"))
            .AsTask();
        await dispatcher.Started.Task.ConfigureAwait(false);
        await codec.SecondWrite.Task.ConfigureAwait(false);
        release.TrySetResult(true);
        await runTask.ConfigureAwait(false);

        var overload = ProtocolJson.DeserializeResponse(codec.Writes[1]);
        Assert.Equal(secondCommand.RequestId, overload.RequestId);
        Assert.Equal(ProtocolOperations.TaskRename, overload.Operation);
        Assert.False(overload.Ok);
        Assert.Equal(ProtocolOutcomes.Rejected, overload.Outcome);
        Assert.NotNull(overload.Error);
        Assert.Equal(ProtocolErrorCodes.Overloaded, overload.Error!.Code);
        Assert.True(overload.Error.Retryable);
        Assert.Null(overload.Payload);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class NoOpPayloadValidator : ITransportPayloadValidator
    {
        public void Validate(ReadOnlyMemory<byte> frame) => Assert.NotEmpty(frame.ToArray());
    }

    private sealed class ReadyHandshake : ITransportHandshake
    {
        private readonly ReadOnlyMemory<byte> response;

        public ReadyHandshake(ReadOnlyMemory<byte> response)
        {
            this.response = response;
        }

        public ValueTask<TransportHandshakeResult> HandleAsync(
            ReadOnlyMemory<byte> firstOrPendingFrame,
            TransportPeerIdentity peer,
            CancellationToken cancellationToken)
        {
            _ = firstOrPendingFrame;
            _ = peer;
            _ = cancellationToken;
            return ValueTask.FromResult(new TransportHandshakeResult(
                Ready: true,
                CloseConnection: false,
                ResponseFrame: response));
        }
    }

    private sealed class BlockingTransportDispatcher : ITransportRequestDispatcher
    {
        private readonly ScriptedFrameCodec codec;
        private readonly ReadOnlyMemory<byte> response;

        public BlockingTransportDispatcher(
            ScriptedFrameCodec codec,
            ReadOnlyMemory<byte> response)
        {
            this.codec = codec;
            this.response = response;
        }

        public TaskCompletionSource<bool> Started { get; } = NewSignal();

        public TaskCompletionSource<bool>? Release { get; set; }

        public async ValueTask<ReadOnlyMemory<byte>> DispatchAsync(
            ReadOnlyMemory<byte> requestFrame,
            TransportPeerIdentity peer)
        {
            _ = codec;
            _ = requestFrame;
            _ = peer;
            Started.TrySetResult(true);
            await Release!.Task.ConfigureAwait(false);
            return response;
        }
    }

    private sealed class ScriptedFrameCodec : ITransportFrameCodec
    {
        private readonly Queue<byte[]?> frames;
        private readonly object gate = new();

        public ScriptedFrameCodec(params byte[]?[] frames)
        {
            this.frames = new Queue<byte[]?>(frames);
        }

        public List<byte[]> Writes { get; } = [];

        public TaskCompletionSource<bool> SecondWrite { get; } = NewSignal();

        public ValueTask<byte[]?> ReadAsync(
            Stream stream,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            _ = stream;
            _ = timeout;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(frames.Dequeue());
        }

        public ValueTask WriteAsync(
            Stream stream,
            ReadOnlyMemory<byte> frame,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            _ = stream;
            _ = timeout;
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                Writes.Add(frame.ToArray());
                if (Writes.Count == 2)
                {
                    SecondWrite.TrySetResult(true);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class HarnessTransportLimitSource : ITransportLimitSource
    {
        public int MaxFrameBytes { get; } = ProtocolLimits.MaxFrameBytes;

        public int MaxCommandPayloadBytes { get; } = ProtocolLimits.MaxWriteCommandPayloadBytes;

        public int MaxEventPayloadBytes { get; } = ProtocolLimits.MaxEventPayloadBytes;

        public int MaxErrorDetailsBytes { get; } = ProtocolLimits.MaxErrorDetailsBytes;

        public int MaxSuccessPayloadBytes { get; } = ProtocolLimits.MaxSuccessPayloadBytes;

        public int MaxJsonDepth { get; } = ProtocolLimits.MaxJsonNestingDepth;

        public int MaxInFlightRequests { get; init; } = ProtocolLimits.MaxInFlightPerConnection;

        public int MaxQueuedRequests { get; } = ProtocolLimits.MaxQueuedRequestsPerProfile;

        public TimeSpan ConnectDeadline { get; } = TimeSpan.FromMilliseconds(ProtocolLimits.ConnectDeadlineMilliseconds);

        public TimeSpan HelloQueryStatusDeadline { get; } = TimeSpan.FromMilliseconds(ProtocolLimits.ReadDeadlineMilliseconds);

        public TimeSpan DefaultMutationTimeout { get; } = TimeSpan.FromMilliseconds(ProtocolLimits.DefaultMutationTimeoutMilliseconds);

        public TimeSpan CancelTimeout { get; } = TimeSpan.FromSeconds(1);

        public TimeSpan AbsoluteRequestDeadline { get; } = TimeSpan.FromMilliseconds(ProtocolLimits.MaxRequestDeadlineMilliseconds);
    }
}

internal sealed class InMemoryWriterPersistence : IWriterPersistence
{
    private readonly object gate = new();
    private readonly Dictionary<string, WriterReceipt> receipts = new(StringComparer.Ordinal);
    private readonly List<FakeTransaction> transactions = [];
    private int failPersistDomainCount;
    private int failStoreReceiptCount;
    private int failDomainCommitCount;
    private int failAfterDomainApplyCount;
    private int blockNextDomainCommit;

    private long currentRevision;

    public long CurrentRevision
    {
        get => Interlocked.Read(ref currentRevision);
        set => Interlocked.Exchange(ref currentRevision, value);
    }

    public int FailPersistDomainCount
    {
        get => Volatile.Read(ref failPersistDomainCount);
        set => Volatile.Write(ref failPersistDomainCount, value);
    }

    public int FailStoreReceiptCount
    {
        get => Volatile.Read(ref failStoreReceiptCount);
        set => Volatile.Write(ref failStoreReceiptCount, value);
    }

    public int FailDomainCommitCount
    {
        get => Volatile.Read(ref failDomainCommitCount);
        set => Volatile.Write(ref failDomainCommitCount, value);
    }

    public int FailAfterDomainApplyCount
    {
        get => Volatile.Read(ref failAfterDomainApplyCount);
        set => Volatile.Write(ref failAfterDomainApplyCount, value);
    }

    public int ReceiptCount
    {
        get
        {
            lock (gate)
            {
                return receipts.Count;
            }
        }
    }

    public int DomainTransactionCount => CountTransactions(WriterTransactionKind.Domain);

    public int CommittedBatchCount
    {
        get
        {
            lock (gate)
            {
                return transactions.Count(transaction => transaction.CommittedDomainChanges is not null);
            }
        }
    }

    public int DirtyTrackerDiscardCount
    {
        get
        {
            lock (gate)
            {
                return transactions.Sum(transaction => transaction.DiscardDirtyStateCount);
            }
        }
    }

    public int RevisionReadCount
    {
        get
        {
            lock (gate)
            {
                return transactions.Sum(transaction => transaction.RevisionReadCount);
            }
        }
    }

    public List<IReadOnlyList<WriterChange>> CommittedBatches
    {
        get
        {
            lock (gate)
            {
                return transactions
                    .Where(transaction => transaction.CommittedDomainChanges is not null)
                    .Select(transaction => transaction.CommittedDomainChanges!)
                    .ToList();
            }
        }
    }

    public TaskCompletionSource<bool> DomainCommitStarted { get; private set; } = NewSignal();

    public TaskCompletionSource<bool> ReleaseDomainCommit { get; private set; } = NewSignal();

    public ValueTask<IWriterTransaction> BeginTransactionAsync(
        WriterTransactionKind kind,
        CancellationToken cancellationToken = default)
    {
        var transaction = new FakeTransaction(this, kind);
        lock (gate)
        {
            transactions.Add(transaction);
        }

        return ValueTask.FromResult<IWriterTransaction>(transaction);
    }

    public ValueTask<WriterReceipt?> ReadReceiptAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return ValueTask.FromResult(
                receipts.TryGetValue(idempotencyKey, out var receipt)
                    ? Clone(receipt)
                    : null);
        }
    }

    public int ReceiptCountFor(string idempotencyKey)
    {
        lock (gate)
        {
            return receipts.ContainsKey(idempotencyKey) ? 1 : 0;
        }
    }

    public void SeedReceipt(WriterReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        lock (gate)
        {
            receipts[receipt.IdempotencyKey] = Clone(receipt);
        }
    }

    public int CommitCountFor(WriterTransactionKind kind)
    {
        lock (gate)
        {
            return transactions.Count(transaction =>
                transaction.Kind == kind && transaction.CommitCount > 0);
        }
    }

    public void BlockNextDomainCommit()
    {
        DomainCommitStarted = NewSignal();
        ReleaseDomainCommit = NewSignal();
        Interlocked.Exchange(ref blockNextDomainCommit, 1);
    }

    internal WriterReceipt? FindReceipt(string idempotencyKey)
    {
        lock (gate)
        {
            return receipts.TryGetValue(idempotencyKey, out var receipt)
                ? Clone(receipt)
                : null;
        }
    }

    private long ReadCurrentRevision(FakeTransaction transaction)
    {
        lock (gate)
        {
            transaction.RevisionReadCount++;
            return CurrentRevision;
        }
    }

    internal bool ConsumeFailPersistDomain() =>
        Consume(ref failPersistDomainCount);

    internal bool ConsumeFailAfterDomainApply() =>
        Consume(ref failAfterDomainApplyCount);

    internal bool ConsumeFailStoreReceipt() =>
        Consume(ref failStoreReceiptCount);

    private bool ConsumeFailDomainCommit() =>
        Consume(ref failDomainCommitCount);

    internal bool ConsumeBlockNextDomainCommit() =>
        Consume(ref blockNextDomainCommit);

    private void Apply(FakeTransaction transaction)
    {
        lock (gate)
        {
            foreach (var receipt in transaction.StagedReceipts.Values)
            {
                receipts[receipt.IdempotencyKey] = Clone(receipt);
            }

            if (transaction.StagedDomainChanges is not null)
            {
                CurrentRevision = transaction.StagedRevision;
                transaction.CommittedDomainChanges = transaction.StagedDomainChanges.ToArray();
            }
        }
    }

    private int CountTransactions(WriterTransactionKind kind)
    {
        lock (gate)
        {
            return transactions.Count(transaction => transaction.Kind == kind);
        }
    }

    private static bool Consume(ref int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref value);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref value, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    private static WriterReceipt Clone(WriterReceipt receipt) =>
        new(
            receipt.IdempotencyKey,
            receipt.Operation,
            receipt.CanonicalPayloadHash,
            receipt.Status,
            receipt.Changed,
            receipt.CommittedRevision,
            receipt.ErrorCode,
            receipt.AttemptCount);

    private sealed class FakeTransaction : IWriterTransaction
    {
        private readonly InMemoryWriterPersistence persistence;

        public FakeTransaction(
            InMemoryWriterPersistence persistence,
            WriterTransactionKind kind)
        {
            this.persistence = persistence;
            Kind = kind;
        }

        public WriterTransactionKind Kind { get; }

        public Dictionary<string, WriterReceipt> StagedReceipts { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<WriterChange>? StagedDomainChanges { get; private set; }

        public long StagedRevision { get; private set; }

        public IReadOnlyList<WriterChange>? CommittedDomainChanges { get; set; }

        public int RevisionReadCount { get; set; }

        public int DiscardDirtyStateCount { get; private set; }

        public int CommitCount { get; private set; }

        public async ValueTask<WriterReceipt?> FindReceiptAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StagedReceipts.TryGetValue(idempotencyKey, out var staged)
                ? Clone(staged)
                : persistence.FindReceipt(idempotencyKey);
        }

        public ValueTask StoreReceiptAsync(
            WriterReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (persistence.ConsumeFailStoreReceipt())
            {
                throw new InvalidOperationException("Injected receipt store failure.");
            }

            StagedReceipts[receipt.IdempotencyKey] = Clone(receipt);
            return ValueTask.CompletedTask;
        }

        public ValueTask<long> ReadCurrentRevisionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(persistence.ReadCurrentRevision(this));
        }

        public ValueTask PersistDomainChangesAsync(
            IReadOnlyList<WriterChange> changes,
            long committedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (persistence.ConsumeFailPersistDomain())
            {
                throw new InvalidOperationException("Injected domain persist failure.");
            }

            StagedDomainChanges = changes.ToArray();
            StagedRevision = committedRevision;
            return ValueTask.CompletedTask;
        }

        public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCount++;

            if (Kind == WriterTransactionKind.Domain &&
                persistence.ConsumeBlockNextDomainCommit())
            {
                persistence.DomainCommitStarted.TrySetResult(true);
                await persistence.ReleaseDomainCommit.Task.ConfigureAwait(false);
            }

            if (Kind == WriterTransactionKind.Domain &&
                persistence.ConsumeFailDomainCommit())
            {
                throw new WriterCommitRolledBackException(
                    new InvalidOperationException("Injected commit failure."));
            }

            persistence.Apply(this);

            if (Kind == WriterTransactionKind.Domain &&
                persistence.ConsumeFailAfterDomainApply())
            {
                throw new InvalidOperationException("Injected post-commit failure.");
            }
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void DiscardDirtyState()
        {
            DiscardDirtyStateCount++;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

#pragma warning restore xUnit1051
#pragma warning restore xUnit1030

#endif
