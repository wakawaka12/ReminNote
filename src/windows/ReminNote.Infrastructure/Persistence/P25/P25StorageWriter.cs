using System.Data;
using Microsoft.Data.Sqlite;

namespace ReminNote.Infrastructure.Persistence.P25;

/// <summary>
/// Transactional storage adapter for P2.5. The caller supplies a bounded
/// domain adapter; this class owns receipt, revision and journal atomicity.
/// It is intentionally not registered in the current product host.
/// </summary>
public sealed class P25StorageWriter
{
    private readonly P25StorageStore store;

    internal P25StorageWriter(P25StorageStore store)
    {
        this.store = store;
    }

    public async ValueTask<P25WriteResult> ExecuteMutationAsync(
        P25CommandRequest request,
        P25MutationHandler handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        P25StorageValidation.ValidateRequest(request, store.ProfileScope);

        await store.WriterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preparation = await PrepareReceiptAsync(request, cancellationToken).ConfigureAwait(false);
            if (preparation.ImmediateResult is not null)
            {
                return preparation.ImmediateResult;
            }

            return await ExecuteDomainTransactionAsync(
                    request,
                    preparation.Receipt,
                    handler,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            store.WriterGate.Release();
        }
    }

    public async ValueTask<P25Receipt?> GetReceiptAsync(
        string actualUserSid,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        P25StorageValidation.ValidateCanonicalUuid(idempotencyKey, nameof(idempotencyKey));
        ValidateUserSid(actualUserSid);

        return await P25StorageSql.ReadReceiptAsync(
                store.Connection,
                actualUserSid,
                store.ProfileScope,
                idempotencyKey,
                transaction: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ReceiptPreparation> PrepareReceiptAsync(
        P25CommandRequest request,
        CancellationToken cancellationToken)
    {
        using var transaction = store.Connection.BeginTransaction(IsolationLevel.Serializable);
        var existing = await P25StorageSql.ReadReceiptAsync(
                store.Connection,
                request.ActualUserSid,
                request.ProfileScope,
                request.IdempotencyKey,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null && !MatchesRequest(existing, request))
        {
            transaction.Rollback();
            return new ReceiptPreparation(
                existing,
                BuildResult(
                    P25MutationOutcome.Rejected,
                    existing,
                    changed: existing.Changed,
                    committedRevision: existing.CommittedRevision,
                    errorCode: "ipc.idempotency.conflict",
                    replayed: false,
                    batchId: null,
                    receiptDurable: true));
        }

        var now = store.UtcNow().ToUniversalTime();
        if (existing is null)
        {
            await InsertPendingReceiptAsync(
                    request,
                    now,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);

            var pending = new P25Receipt(
                request.ActualUserSid,
                request.ProfileScope,
                request.IdempotencyKey,
                request.Operation,
                request.HashVersion,
                request.CanonicalPayloadHash.ToArray(),
                P25ReceiptStatus.Pending,
                Changed: false,
                CommittedRevision: null,
                ErrorCode: null,
                now,
                now,
                request.RequestId,
                request.RequestId,
                AttemptCount: 1,
                request.AgentInstanceId);
            transaction.Commit();
            return new ReceiptPreparation(pending, ImmediateResult: null);
        }

        var isReplayableTerminal = existing.Status is
            P25ReceiptStatus.Committed or
            P25ReceiptStatus.RejectedStale or
            P25ReceiptStatus.Rejected or
            P25ReceiptStatus.Cancelled;

        if (isReplayableTerminal)
        {
            var replay = existing with
            {
                LastRequestId = request.RequestId,
                UpdatedAtUtc = now,
                AttemptCount = existing.AttemptCount >= P25StorageLimits.MaxReceiptAttempts
                    ? P25StorageLimits.MaxReceiptAttempts
                    : existing.AttemptCount + 1,
                AgentInstanceId = request.AgentInstanceId ?? existing.AgentInstanceId
            };
            await UpdateReceiptAsync(replay, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();

            return new ReceiptPreparation(
                replay,
                BuildResult(
                    P25StorageSql.ToOutcome(replay.Status, replay.Changed, replayed: true),
                    replay,
                    replay.Changed,
                    replay.CommittedRevision,
                    replay.ErrorCode,
                    replayed: true,
                    batchId: null,
                    receiptDurable: true));
        }

        if (existing.AttemptCount >= P25StorageLimits.MaxReceiptAttempts)
        {
            transaction.Rollback();
            return new ReceiptPreparation(
                existing,
                BuildResult(
                    P25MutationOutcome.Rejected,
                    existing,
                    existing.Changed,
                    existing.CommittedRevision,
                    "storage.receipt_capacity",
                    replayed: false,
                    batchId: null,
                    receiptDurable: true));
        }

        var nextAttempt = existing.AttemptCount + 1;

        var updated = existing with
        {
            LastRequestId = request.RequestId,
            UpdatedAtUtc = now,
            AttemptCount = nextAttempt,
            AgentInstanceId = request.AgentInstanceId ?? existing.AgentInstanceId,
            Status = P25ReceiptStatus.Pending,
            Changed = false,
            CommittedRevision = null,
            ErrorCode = null
        };

        await UpdateReceiptAsync(updated, transaction, cancellationToken).ConfigureAwait(false);
        transaction.Commit();

        return new ReceiptPreparation(updated, ImmediateResult: null);
    }

    private async ValueTask<P25WriteResult> ExecuteDomainTransactionAsync(
        P25CommandRequest request,
        P25Receipt preparedReceipt,
        P25MutationHandler handler,
        CancellationToken cancellationToken)
    {
        SqliteTransaction? transaction = null;
        var commitAttempted = false;
        var commitCompleted = false;

        void RollbackAndDispose()
        {
            if (transaction is null)
            {
                return;
            }

            try
            {
                if (!commitCompleted)
                {
                    transaction.Rollback();
                }
            }
            catch
            {
                // A failed commit can leave SQLite's transaction state unknown;
                // rollback is best effort and must not hide the durable outcome.
            }
            finally
            {
                try
                {
                    transaction.Dispose();
                }
                catch
                {
                    // Disposal is also best effort after an ambiguous commit.
                }

                transaction = null;
            }
        }

        try
        {
            transaction = store.Connection.BeginTransaction(IsolationLevel.Serializable);
            var currentState = await ReadRevisionStateAsync(transaction, cancellationToken).ConfigureAwait(false);
            var receipt = await P25StorageSql.ReadReceiptAsync(
                    store.Connection,
                    request.ActualUserSid,
                    request.ProfileScope,
                    request.IdempotencyKey,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);

            if (receipt is null || !MatchesRequest(receipt, request))
            {
                throw new InvalidOperationException(
                    "The prepared P2.5 receipt was not available with the same identity.");
            }

            if (currentState.CurrentRevision != request.ExpectedRevision)
            {
                var stale = receipt with
                {
                    Status = P25ReceiptStatus.RejectedStale,
                    Changed = false,
                    CommittedRevision = null,
                    ErrorCode = "revision.expected_mismatch",
                    UpdatedAtUtc = store.UtcNow().ToUniversalTime()
                };
                await UpdateReceiptAsync(stale, transaction, cancellationToken).ConfigureAwait(false);
                commitAttempted = true;
                await store.CommitDomainTransactionAsync(transaction).ConfigureAwait(false);
                commitCompleted = true;
                return BuildResult(
                    P25MutationOutcome.Stale,
                    stale,
                    changed: false,
                    committedRevision: null,
                    stale.ErrorCode,
                    replayed: false,
                    batchId: null,
                    receiptDurable: true);
            }

            if (currentState.CurrentRevision == long.MaxValue)
            {
                RollbackAndDispose();
                return await FinalizeReceiptOnlyAsync(
                        request,
                        receipt,
                        P25ReceiptStatus.Rejected,
                        changed: false,
                        committedRevision: null,
                        errorCode: "storage.revision_overflow",
                        outcome: P25MutationOutcome.Rejected,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            var proposedRevision = checked(currentState.CurrentRevision + 1);
            var batchId = Guid.CreateVersion7().ToString("D");
            var context = new P25MutationContext(
                store.Connection,
                transaction,
                currentState.CurrentRevision,
                proposedRevision,
                batchId);
            var decision = await handler(context, cancellationToken).ConfigureAwait(false);
            decision.Validate();

            switch (decision.Kind)
            {
                case P25MutationDecisionKind.Changed:
                    foreach (var change in decision.Changes)
                    {
                        P25StorageValidation.ValidateJournalChange(change);
                    }

                    for (var ordinal = 0; ordinal < decision.Changes.Count; ordinal++)
                    {
                        await InsertJournalAsync(
                                request.ProfileScope,
                                proposedRevision,
                                ordinal,
                                batchId,
                                decision.Changes[ordinal],
                                store.UtcNow(),
                                transaction,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await UpdateRevisionStateAsync(
                            request.ProfileScope,
                            proposedRevision,
                            currentState.OldestAvailableRevision == 0
                                ? proposedRevision
                                : currentState.OldestAvailableRevision,
                            transaction,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var committed = receipt with
                    {
                        Status = P25ReceiptStatus.Committed,
                        Changed = true,
                        CommittedRevision = proposedRevision,
                        ErrorCode = null,
                        UpdatedAtUtc = store.UtcNow().ToUniversalTime()
                    };
                    await UpdateReceiptAsync(committed, transaction, cancellationToken).ConfigureAwait(false);
                    commitAttempted = true;
                    await store.CommitDomainTransactionAsync(transaction).ConfigureAwait(false);
                    commitCompleted = true;
                    return BuildResult(
                        P25MutationOutcome.Changed,
                        committed,
                        changed: true,
                        committedRevision: proposedRevision,
                        errorCode: null,
                        replayed: false,
                        batchId,
                        receiptDurable: true);

                case P25MutationDecisionKind.NoOp:
                    RollbackAndDispose();
                    return await FinalizeReceiptOnlyAsync(
                            request,
                            receipt,
                            P25ReceiptStatus.Committed,
                            changed: false,
                            committedRevision: currentState.CurrentRevision,
                            errorCode: null,
                            outcome: P25MutationOutcome.NoOp,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                case P25MutationDecisionKind.Rejected:
                    RollbackAndDispose();
                    return await FinalizeReceiptOnlyAsync(
                            request,
                            receipt,
                            P25ReceiptStatus.Rejected,
                            changed: false,
                            committedRevision: null,
                            decision.ErrorCode,
                            P25MutationOutcome.Rejected,
                            cancellationToken)
                        .ConfigureAwait(false);

                case P25MutationDecisionKind.Cancelled:
                    RollbackAndDispose();
                    return await FinalizeReceiptOnlyAsync(
                            request,
                            receipt,
                            P25ReceiptStatus.Cancelled,
                            changed: false,
                            committedRevision: null,
                            decision.ErrorCode,
                            P25MutationOutcome.Cancelled,
                            cancellationToken)
                        .ConfigureAwait(false);

                case P25MutationDecisionKind.TimedOut:
                    RollbackAndDispose();
                    return await FinalizeReceiptOnlyAsync(
                            request,
                            receipt,
                            P25ReceiptStatus.TimedOut,
                            changed: false,
                            committedRevision: null,
                            decision.ErrorCode,
                            P25MutationOutcome.TimedOut,
                            cancellationToken)
                        .ConfigureAwait(false);

                case P25MutationDecisionKind.Unknown:
                    RollbackAndDispose();
                    return await FinalizeReceiptOnlyAsync(
                            request,
                            receipt,
                            P25ReceiptStatus.Unknown,
                            changed: false,
                            committedRevision: null,
                            decision.ErrorCode,
                            P25MutationOutcome.Unknown,
                            cancellationToken)
                        .ConfigureAwait(false);

                default:
                    throw new InvalidOperationException("Unknown P2.5 mutation decision.");
            }
        }
        catch (OperationCanceledException)
        {
            RollbackAndDispose();
            if (commitAttempted)
            {
                return await ReconcileAmbiguousCommitAsync(
                        request,
                        preparedReceipt)
                    .ConfigureAwait(false);
            }

            return await FinalizeReceiptOnlyAsync(
                    request,
                    preparedReceipt,
                    P25ReceiptStatus.RolledBack,
                    changed: false,
                    committedRevision: null,
                    errorCode: "storage.transaction_failed",
                    outcome: P25MutationOutcome.RolledBack,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            RollbackAndDispose();
            if (commitAttempted)
            {
                return await ReconcileAmbiguousCommitAsync(
                        request,
                        preparedReceipt)
                    .ConfigureAwait(false);
            }

            return await FinalizeReceiptOnlyAsync(
                    request,
                    preparedReceipt,
                    P25ReceiptStatus.RolledBack,
                    changed: false,
                    committedRevision: null,
                    errorCode: "storage.transaction_failed",
                    outcome: P25MutationOutcome.RolledBack,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            RollbackAndDispose();
        }
    }

    private async ValueTask<P25WriteResult> ReconcileAmbiguousCommitAsync(
        P25CommandRequest request,
        P25Receipt preparedReceipt)
    {
        var observation = await ReadDurableMutationObservationAsync(request).ConfigureAwait(false);
        if (!observation.ReadCompleted ||
            observation.RevisionState is null ||
            observation.Receipt is null ||
            !MatchesRequest(observation.Receipt, request))
        {
            return BuildUnknownResult(preparedReceipt);
        }

        var receipt = observation.Receipt;
        var revisionState = observation.RevisionState;
        if (receipt.Status == P25ReceiptStatus.Committed &&
            receipt.CommittedRevision is { } committedRevision &&
            committedRevision <= revisionState.CurrentRevision &&
            (!receipt.Changed || observation.RowsAtCommittedRevision > 0))
        {
            return BuildResult(
                P25StorageSql.ToOutcome(receipt.Status, receipt.Changed, replayed: false),
                receipt,
                receipt.Changed,
                committedRevision,
                receipt.ErrorCode,
                replayed: false,
                batchId: null,
                receiptDurable: true);
        }

        if (receipt.Status == P25ReceiptStatus.Pending &&
            revisionState.CurrentRevision == request.ExpectedRevision &&
            observation.RowsAboveExpectedRevision == 0)
        {
            return await FinalizeReceiptOnlyAsync(
                    request,
                    receipt,
                    P25ReceiptStatus.RolledBack,
                    changed: false,
                    committedRevision: null,
                    errorCode: "storage.transaction_failed",
                    outcome: P25MutationOutcome.RolledBack,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (receipt.Status != P25ReceiptStatus.Committed &&
            receipt.Status != P25ReceiptStatus.Pending)
        {
            return BuildResult(
                P25StorageSql.ToOutcome(receipt.Status, receipt.Changed, replayed: false),
                receipt,
                receipt.Changed,
                receipt.CommittedRevision,
                receipt.ErrorCode,
                replayed: false,
                batchId: null,
                receiptDurable: true);
        }

        return BuildUnknownResult(receipt);
    }

    private async ValueTask<P25DurableMutationObservation> ReadDurableMutationObservationAsync(
        P25CommandRequest request)
    {
        try
        {
            using var transaction = store.Connection.BeginTransaction(
                IsolationLevel.Serializable,
                deferred: true);
            var revisionState = await ReadRevisionStateAsync(transaction, CancellationToken.None)
                .ConfigureAwait(false);
            if (!IsRevisionStateValid(revisionState))
            {
                return P25DurableMutationObservation.Incomplete;
            }

            var receipt = await P25StorageSql.ReadReceiptAsync(
                    store.Connection,
                    request.ActualUserSid,
                    request.ProfileScope,
                    request.IdempotencyKey,
                    transaction,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var journalEvidence = await ReadJournalEvidenceAsync(
                    transaction,
                    request.ExpectedRevision,
                    receipt?.CommittedRevision)
                .ConfigureAwait(false);
            transaction.Commit();
            return new P25DurableMutationObservation(
                true,
                revisionState,
                receipt,
                journalEvidence.RowsAtCommittedRevision,
                journalEvidence.RowsAboveExpectedRevision);
        }
        catch
        {
            return P25DurableMutationObservation.Incomplete;
        }
    }

    private async ValueTask<(long RowsAtCommittedRevision, long RowsAboveExpectedRevision)> ReadJournalEvidenceAsync(
        SqliteTransaction transaction,
        long expectedRevision,
        long? committedRevision)
    {
        await using var command = P25StorageSql.CreateCommand(
            store.Connection,
            """
            SELECT
                (
                    SELECT COUNT(*)
                    FROM change_journal
                    WHERE profile_scope = $profileScope
                      AND revision = $committedRevision
                ),
                (
                    SELECT COUNT(*)
                    FROM change_journal
                    WHERE profile_scope = $profileScope
                      AND revision > $expectedRevision
                );
            """,
            transaction);
        command.Parameters.AddWithValue("$profileScope", store.ProfileScope);
        command.Parameters.AddWithValue(
            "$committedRevision",
            (object?)committedRevision ?? -1L);
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
        if (!await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The P2.5 commit evidence query returned no row.");
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static P25WriteResult BuildUnknownResult(P25Receipt receipt)
    {
        var unknown = receipt with
        {
            Status = P25ReceiptStatus.Unknown,
            Changed = false,
            CommittedRevision = null,
            ErrorCode = "storage.transaction_failed"
        };
        return BuildResult(
            P25MutationOutcome.Unknown,
            unknown,
            changed: false,
            committedRevision: null,
            errorCode: unknown.ErrorCode,
            replayed: false,
            batchId: null,
            receiptDurable: false);
    }

    private async ValueTask<P25WriteResult> FinalizeReceiptOnlyAsync(
        P25CommandRequest request,
        P25Receipt knownReceipt,
        P25ReceiptStatus status,
        bool changed,
        long? committedRevision,
        string? errorCode,
        P25MutationOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var transaction = store.Connection.BeginTransaction(IsolationLevel.Serializable);
            var updated = knownReceipt with
            {
                Status = status,
                Changed = changed,
                CommittedRevision = committedRevision,
                ErrorCode = errorCode,
                UpdatedAtUtc = store.UtcNow().ToUniversalTime()
            };
            await UpdateReceiptAsync(updated, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return BuildResult(
                outcome,
                updated,
                changed,
                committedRevision,
                errorCode,
                replayed: false,
                batchId: null,
                receiptDurable: true);
        }
        catch
        {
            var transient = knownReceipt with
            {
                Status = P25ReceiptStatus.Unknown,
                Changed = false,
                CommittedRevision = null,
                ErrorCode = "storage.transaction_failed",
                UpdatedAtUtc = store.UtcNow().ToUniversalTime()
            };
            return BuildResult(
                P25MutationOutcome.Unknown,
                transient,
                changed: false,
                committedRevision: null,
                errorCode: transient.ErrorCode,
                replayed: false,
                batchId: null,
                receiptDurable: false);
        }
    }

    private async ValueTask<P25RevisionState> ReadRevisionStateAsync(
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = P25StorageSql.CreateCommand(
            store.Connection,
            """
            SELECT profile_scope, current_revision, oldest_available_revision
            FROM revision_state
            WHERE profile_scope = $profileScope;
            """,
            transaction);
        command.Parameters.AddWithValue("$profileScope", store.ProfileScope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The P2.5 profile revision state is missing.");
        }

        return new P25RevisionState(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    private async ValueTask InsertPendingReceiptAsync(
        P25CommandRequest request,
        DateTimeOffset now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = P25StorageSql.CreateCommand(
            store.Connection,
            """
            INSERT INTO command_receipt (
                actual_user_sid,
                profile_scope,
                idempotency_key,
                operation,
                hash_version,
                canonical_payload_hash,
                status,
                changed,
                committed_revision,
                error_code,
                first_accepted_at_utc,
                updated_at_utc,
                first_request_id,
                last_request_id,
                attempt_count,
                agent_instance_id
            )
            VALUES (
                $actualUserSid,
                $profileScope,
                $idempotencyKey,
                $operation,
                $hashVersion,
                $canonicalPayloadHash,
                'PENDING',
                0,
                NULL,
                NULL,
                $firstAcceptedAtUtc,
                $updatedAtUtc,
                $firstRequestId,
                $lastRequestId,
                1,
                $agentInstanceId
            );
            """,
            transaction);
        command.Parameters.AddWithValue("$actualUserSid", request.ActualUserSid);
        command.Parameters.AddWithValue("$profileScope", request.ProfileScope);
        command.Parameters.AddWithValue("$idempotencyKey", request.IdempotencyKey);
        command.Parameters.AddWithValue("$operation", request.Operation);
        command.Parameters.AddWithValue("$hashVersion", request.HashVersion);
        P25StorageSql.AddBlobParameter(command, "$canonicalPayloadHash", request.CanonicalPayloadHash);
        command.Parameters.AddWithValue("$firstAcceptedAtUtc", P25StorageSql.FormatUtc(now));
        command.Parameters.AddWithValue("$updatedAtUtc", P25StorageSql.FormatUtc(now));
        command.Parameters.AddWithValue("$firstRequestId", request.RequestId);
        command.Parameters.AddWithValue("$lastRequestId", request.RequestId);
        command.Parameters.AddWithValue("$agentInstanceId", (object?)request.AgentInstanceId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UpdateReceiptAsync(
        P25Receipt receipt,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = P25StorageSql.CreateCommand(
            store.Connection,
            """
            UPDATE command_receipt
            SET status = $status,
                changed = $changed,
                committed_revision = $committedRevision,
                error_code = $errorCode,
                updated_at_utc = $updatedAtUtc,
                last_request_id = $lastRequestId,
                attempt_count = $attemptCount,
                agent_instance_id = $agentInstanceId
            WHERE actual_user_sid = $actualUserSid
              AND profile_scope = $profileScope
              AND idempotency_key = $idempotencyKey;
            """,
            transaction);
        command.Parameters.AddWithValue("$status", P25StorageSql.ToStorageValue(receipt.Status));
        command.Parameters.AddWithValue("$changed", receipt.Changed ? 1 : 0);
        command.Parameters.AddWithValue(
            "$committedRevision",
            (object?)receipt.CommittedRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)receipt.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUtc", P25StorageSql.FormatUtc(receipt.UpdatedAtUtc));
        command.Parameters.AddWithValue("$lastRequestId", receipt.LastRequestId);
        command.Parameters.AddWithValue("$attemptCount", receipt.AttemptCount);
        command.Parameters.AddWithValue("$agentInstanceId", (object?)receipt.AgentInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$actualUserSid", receipt.ActualUserSid);
        command.Parameters.AddWithValue("$profileScope", receipt.ProfileScope);
        command.Parameters.AddWithValue("$idempotencyKey", receipt.IdempotencyKey);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("The P2.5 receipt update did not affect exactly one row.");
        }
    }

    private async ValueTask InsertJournalAsync(
        string profileScope,
        long revision,
        int ordinal,
        string batchId,
        P25JournalChange change,
        DateTimeOffset changedAtUtc,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = P25StorageSql.CreateCommand(
            store.Connection,
            """
            INSERT INTO change_journal (
                profile_scope,
                revision,
                change_ordinal,
                batch_id,
                entity_type,
                entity_id,
                change_kind,
                changed_at_utc
            )
            VALUES (
                $profileScope,
                $revision,
                $changeOrdinal,
                $batchId,
                $entityType,
                $entityId,
                $changeKind,
                $changedAtUtc
            );
            """,
            transaction);
        command.Parameters.AddWithValue("$profileScope", profileScope);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$changeOrdinal", ordinal);
        command.Parameters.AddWithValue("$batchId", batchId);
        command.Parameters.AddWithValue("$entityType", change.EntityType);
        command.Parameters.AddWithValue("$entityId", change.EntityId);
        command.Parameters.AddWithValue("$changeKind", change.ChangeKind);
        command.Parameters.AddWithValue("$changedAtUtc", P25StorageSql.FormatUtc(changedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UpdateRevisionStateAsync(
        string profileScope,
        long currentRevision,
        long oldestAvailableRevision,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = P25StorageSql.CreateCommand(
            store.Connection,
            """
            UPDATE revision_state
            SET current_revision = $currentRevision,
                oldest_available_revision = $oldestAvailableRevision
            WHERE profile_scope = $profileScope;
            """,
            transaction);
        command.Parameters.AddWithValue("$currentRevision", currentRevision);
        command.Parameters.AddWithValue("$oldestAvailableRevision", oldestAvailableRevision);
        command.Parameters.AddWithValue("$profileScope", profileScope);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("The P2.5 revision state update did not affect exactly one row.");
        }
    }

    private static bool MatchesRequest(P25Receipt receipt, P25CommandRequest request) =>
        receipt.Operation == request.Operation &&
        receipt.HashVersion == request.HashVersion &&
        receipt.CanonicalPayloadHash.AsSpan().SequenceEqual(request.CanonicalPayloadHash);

    private static bool IsRevisionStateValid(P25RevisionState state) =>
        state.CurrentRevision >= 0 &&
        state.OldestAvailableRevision >= 0 &&
        (state.CurrentRevision == 0
            ? state.OldestAvailableRevision == 0
            : state.OldestAvailableRevision > 0 &&
              state.OldestAvailableRevision <= state.CurrentRevision);

    private static P25WriteResult BuildResult(
        P25MutationOutcome outcome,
        P25Receipt receipt,
        bool changed,
        long? committedRevision,
        string? errorCode,
        bool replayed,
        string? batchId,
        bool receiptDurable) => new(
        outcome,
        receipt.Status,
        changed,
        committedRevision,
        errorCode,
        replayed,
        batchId,
        receipt,
        receiptDurable);

    private static void ValidateUserSid(string actualUserSid)
    {
        if (string.IsNullOrWhiteSpace(actualUserSid) || actualUserSid.Length > 256)
        {
            throw new ArgumentException("The user SID value must be bounded and non-empty.", nameof(actualUserSid));
        }
    }

    private sealed record P25DurableMutationObservation(
        bool ReadCompleted,
        P25RevisionState? RevisionState,
        P25Receipt? Receipt,
        long RowsAtCommittedRevision,
        long RowsAboveExpectedRevision)
    {
        public static P25DurableMutationObservation Incomplete { get; } = new(
            false,
            null,
            null,
            0,
            0);
    }

    private sealed record ReceiptPreparation(P25Receipt Receipt, P25WriteResult? ImmediateResult);
}
