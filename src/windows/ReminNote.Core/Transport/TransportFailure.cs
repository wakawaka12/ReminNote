namespace ReminNote.Core.Transport;

/// <summary>
/// Transport-local failure categories. The P2.5-01 contract owns wire error
/// codes; this seam intentionally does not define or duplicate them.
/// </summary>
public enum TransportFailureKind
{
    InvalidFrame,
    FrameTooLarge,
    InvalidJson,
    EndOfStream,
    DeadlineExpired,
    Cancelled,
    Closed,
    Io
}

public class TransportFailureException : IOException
{
    public TransportFailureException(
        TransportFailureKind kind,
        string message = "The transport operation failed.",
        bool connectionMustClose = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        ConnectionMustClose = connectionMustClose;
    }

    public TransportFailureKind Kind { get; }

    public bool ConnectionMustClose { get; }
}

public sealed class TransportDeadlineExceededException : TransportFailureException
{
    public TransportDeadlineExceededException()
        : base(TransportFailureKind.DeadlineExpired)
    {
    }
}
