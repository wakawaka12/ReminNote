namespace ReminNote.Agent.Writer;

/// <summary>
/// Small transaction boundary helper for the non-runnable writer seam.
/// Commits are intentionally non-cancellable once the caller has crossed the
/// supplied commit gate; this models the frozen commit-before-response rule.
/// </summary>
internal sealed class WriterTransactionAdapter
{
    private readonly IWriterPersistence persistence;

    public WriterTransactionAdapter(IWriterPersistence persistence)
    {
        this.persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    public async ValueTask<T> RunAsync<T>(
        WriterTransactionKind kind,
        Func<IWriterTransaction, CancellationToken, ValueTask<T>> operation,
        Func<bool>? tryEnterCommit = null,
        CancellationToken operationCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var transaction = await persistence
            .BeginTransactionAsync(kind, CancellationToken.None)
            .ConfigureAwait(false);

        try
        {
            var result = await operation(transaction, operationCancellationToken)
                .ConfigureAwait(false);

            if (tryEnterCommit is not null && !tryEnterCommit())
            {
                throw new WriterCommitCancelledException();
            }

            // Once the actor marks the commit boundary, a caller cancellation
            // cannot turn a durable commit into a second domain attempt.
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (Exception transactionException)
        {
            Exception? recoveryException = null;
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                recoveryException = rollbackException;
            }

            try
            {
                transaction.DiscardDirtyState();
            }
            catch (Exception discardException)
            {
                recoveryException ??= discardException;
            }

            if (recoveryException is not null)
            {
                throw new WriterTransactionRecoveryException(
                    transactionException,
                    recoveryException);
            }

            throw;
        }
    }
}
