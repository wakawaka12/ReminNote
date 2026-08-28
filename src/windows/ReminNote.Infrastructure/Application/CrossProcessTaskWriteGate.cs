using ReminNote.Core.Application;

namespace ReminNote.Infrastructure.Application;

/// <summary>
/// Temporary P2 cross-process semaphore for the complete local read-modify-write
/// operation. A semaphore is used instead of a thread-affine Mutex because the
/// application lease spans awaits and may be disposed by another continuation
/// thread. P2.5 replaces this with Agent command serialization.
/// </summary>
public sealed class CrossProcessTaskWriteGate : ITaskWriteGate, IDisposable
{
    public const string DefaultName = "Local\\ReminNote.P2.TaskWrite";

    private readonly Semaphore semaphore;
    private readonly int timeoutMilliseconds;
    private int disposed;

    public CrossProcessTaskWriteGate(
        string name = DefaultName,
        int timeoutMilliseconds = 5_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);

        semaphore = new Semaphore(1, 1, name);
        this.timeoutMilliseconds = timeoutMilliseconds;
    }

    public IDisposable Enter(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var waitResult = cancellationToken.CanBeCanceled
            ? WaitHandle.WaitAny([semaphore, cancellationToken.WaitHandle], timeoutMilliseconds)
            : semaphore.WaitOne(timeoutMilliseconds)
                ? 0
                : WaitHandle.WaitTimeout;

        if (waitResult == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (waitResult != 0)
        {
            throw new TaskWriteGateBusyException();
        }

        return new Lease(semaphore);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            semaphore.Dispose();
        }
    }

    private sealed class Lease(Semaphore semaphore) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
