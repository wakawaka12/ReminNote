using ReminNote.Core.Transport;

namespace ReminNote.Tests;

public sealed class ReconnectPolicyTests
{
    [Fact]
    public void DefaultPolicyHasOnlyTheFiniteFrozenBackoffSequence()
    {
        var policy = TransportReconnectPolicy.Default;

        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(
            new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5)
            },
            policy.BackoffDelays);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelayBeforeAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelayBeforeAttempt(3));
    }

    [Fact]
    public async Task ExecutorRetriesTransientFailuresOnlyWithinTheConfiguredBudget()
    {
        var attempts = 0;
        var policy = new TransportReconnectPolicy
        {
            MaxAttempts = 3,
            BackoffDelays = new[] { TimeSpan.Zero, TimeSpan.Zero }
        };
        var executor = new TransportReconnectExecutor(policy);

        var result = await executor.ExecuteAsync(
            (attempt, _) =>
            {
                attempts++;
                return attempt < 3
                    ? ValueTask.FromException<int>(new IOException("transient"))
                    : ValueTask.FromResult(17);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(17, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExhaustionIsReportedAsTransportIoWithoutInventingAProtocolErrorCode()
    {
        var attempts = 0;
        var policy = new TransportReconnectPolicy
        {
            MaxAttempts = 2,
            BackoffDelays = new[] { TimeSpan.Zero }
        };
        var executor = new TransportReconnectExecutor(policy);

        var exception = await Assert.ThrowsAsync<TransportFailureException>(
            () => executor.ExecuteAsync<int>(
                (_, _) =>
                {
                    attempts++;
                    return ValueTask.FromException<int>(new IOException("transient"));
                },
                cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(2, attempts);
        Assert.Equal(TransportFailureKind.Io, exception.Kind);
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task CallerCancellationStopsBeforeAnAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var attempts = 0;
        var executor = new TransportReconnectExecutor(
            new TransportReconnectPolicy
            {
                MaxAttempts = 1,
                BackoffDelays = Array.Empty<TimeSpan>()
            });

        #pragma warning disable xUnit1051
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(
                (_, _) =>
                {
                    attempts++;
                    return ValueTask.FromResult(true);
                },
                cancellationToken: cancellation.Token).AsTask());
        #pragma warning restore xUnit1051

        Assert.Equal(0, attempts);
    }
}
