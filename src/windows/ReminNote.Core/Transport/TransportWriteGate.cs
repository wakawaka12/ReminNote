namespace ReminNote.Core.Transport;

/// <summary>
/// Serializes complete transport-frame writes on one duplex connection.
/// The operation must include the entire length prefix, payload and flush so
/// concurrent responses cannot interleave their framing bytes.
/// </summary>
public sealed class TransportWriteGate : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int disposed;

    /// <summary>
    /// Runs one complete frame write while holding the connection write gate.
    /// </summary>
    public async ValueTask WriteAsync(
        Func<ValueTask> writeOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeOperation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writeOperation().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
