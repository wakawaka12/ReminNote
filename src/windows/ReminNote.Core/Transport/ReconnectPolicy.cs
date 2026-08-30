namespace ReminNote.Core.Transport;

/// <summary>
/// Finite reconnect budget for a transport connection. It never creates or
/// changes an application idempotency key; replay/reconciliation belongs to
/// the shared command contract and its client adapter.
/// </summary>
public sealed record TransportReconnectPolicy
{
    public int MaxAttempts { get; init; } = 3;

    public IReadOnlyList<TimeSpan> BackoffDelays { get; init; } =
        Array.AsReadOnly(new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5)
        });

    public static TransportReconnectPolicy Default { get; } = new();

    public void Validate()
    {
        if (MaxAttempts is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        }

        ArgumentNullException.ThrowIfNull(BackoffDelays);
        if (BackoffDelays.Count < MaxAttempts - 1 || BackoffDelays.Any(delay => delay < TimeSpan.Zero))
        {
            throw new ArgumentException(
                "A reconnect policy must provide a finite non-negative delay for each retry.",
                nameof(BackoffDelays));
        }
    }

    public TimeSpan GetDelayBeforeAttempt(int attempt)
    {
        Validate();
        if (attempt < 2 || attempt > MaxAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        return BackoffDelays[attempt - 2];
    }
}

public sealed class TransportReconnectExecutor
{
    private readonly TransportReconnectPolicy policy;
    private readonly TimeProvider timeProvider;

    public TransportReconnectExecutor(
        TransportReconnectPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        this.policy = policy ?? TransportReconnectPolicy.Default;
        this.policy.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<T> ExecuteAsync<T>(
        Func<int, CancellationToken, ValueTask<T>> connectAttempt,
        Func<Exception, bool>? isTransient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectAttempt);
        isTransient ??= IsTransientFailure;

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await connectAttempt(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (isTransient(exception) && attempt < policy.MaxAttempts)
            {
                lastFailure = exception;
                await Task.Delay(
                    policy.GetDelayBeforeAttempt(attempt + 1),
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (isTransient(exception))
            {
                lastFailure = exception;
            }
        }

        throw new TransportFailureException(
            TransportFailureKind.Io,
            "The finite transport reconnect budget was exhausted.",
            innerException: lastFailure);
    }

    private static bool IsTransientFailure(Exception exception) =>
        exception is IOException or TimeoutException or TransportDeadlineExceededException;
}
