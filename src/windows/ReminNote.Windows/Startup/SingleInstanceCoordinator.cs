using System.IO;
using System.IO.Pipes;

namespace ReminNote.Windows.Startup;

internal static class MainInstanceIdentity
{
    public const string MutexName = @"Local\ReminNote.Windows.Main";
    public const string PipeName = "ReminNote.Windows.Main.Activation";
}

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string ActivationMessage = "activate";
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _listenerGate = new();
    private readonly int _mutexOwnerThreadId;
    private bool _ownsMutex;
    private Task? _listenerTask;
    private NamedPipeServerStream? _activeServer;
    private bool _disposed;

    public SingleInstanceCoordinator(string mutexName, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        _pipeName = pipeName;
        _mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        _mutexOwnerThreadId = _ownsMutex ? Environment.CurrentManagedThreadId : -1;
    }

    public bool IsPrimary => _ownsMutex;

    public void StartListener(Action activatePrimaryWindow)
    {
        ArgumentNullException.ThrowIfNull(activatePrimaryWindow);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary instance can listen for activation.");
        }

        _listenerTask = ListenForActivationAsync(activatePrimaryWindow, _cancellation.Token);
    }

    public bool TryActivateExisting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsPrimary)
        {
            return false;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
                client.Connect(250);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(ActivationMessage);
                return true;
            }
            catch (IOException)
            {
                if (attempt == 2)
                {
                    return false;
                }

                Thread.Sleep(50);
            }
            catch (TimeoutException)
            {
                if (attempt == 2)
                {
                    return false;
                }

                Thread.Sleep(50);
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        lock (_listenerGate)
        {
            _activeServer?.Dispose();
        }

        try
        {
            _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutdown must continue if the listener was interrupted by a
            // closed pipe while the application is exiting.
        }

        // Mutex ownership is thread-affine. The WPF app disposes this on its
        // startup/UI thread; avoid throwing if a host disposes from elsewhere.
        if (_ownsMutex && Environment.CurrentManagedThreadId == _mutexOwnerThreadId)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }

    private async Task ListenForActivationAsync(Action activatePrimaryWindow, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                lock (_listenerGate)
                {
                    _activeServer = server;
                }

                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    using var reader = new StreamReader(server);
                    if (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) == ActivationMessage)
                    {
                        activatePrimaryWindow();
                    }
                }
                finally
                {
                    lock (_listenerGate)
                    {
                        if (ReferenceEquals(_activeServer, server))
                        {
                            _activeServer = null;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
