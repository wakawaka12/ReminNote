using System.IO;
using System.IO.Pipes;

namespace ReminNote.Widget.Startup;

internal static class WidgetInstanceIdentity
{
    public const string MutexName = @"Local\ReminNote.Widget";
    public const string PipeName = "ReminNote.Widget.Activation";
}

internal sealed class WidgetSingleInstanceCoordinator : IDisposable
{
    private const string ActivationMessage = "activate";
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly int _mutexOwnerThreadId;
    private bool _ownsMutex;
    private Task? _listenerTask;
    private bool _disposed;

    public WidgetSingleInstanceCoordinator(string mutexName, string pipeName)
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
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch (TimeoutException) when (attempt < 2)
            {
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
        try
        {
            _listenerTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
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
                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(cancellationToken) == ActivationMessage)
                {
                    activatePrimaryWindow();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
