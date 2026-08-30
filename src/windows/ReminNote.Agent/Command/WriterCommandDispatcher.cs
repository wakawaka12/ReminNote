using ReminNote.Agent.Writer;

namespace ReminNote.Agent.Command;

/// <summary>
/// Operation allow-list for the writer seam. It deliberately performs no
/// wire parsing, JSON serialization, or P2 domain mapping.
/// </summary>
internal sealed class WriterCommandDispatcher
{
    private readonly Dictionary<string, IWriterCommandExecutor> executors;

    public WriterCommandDispatcher(
        IEnumerable<KeyValuePair<string, IWriterCommandExecutor>> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);

        var map = new Dictionary<string, IWriterCommandExecutor>(StringComparer.Ordinal);
        foreach (var pair in executors)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key, nameof(executors));
            ArgumentNullException.ThrowIfNull(pair.Value, nameof(executors));
            if (!map.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    $"A writer executor is already registered for '{pair.Key}'.",
                    nameof(executors));
            }
        }

        this.executors = map;
    }

    public bool CanExecute(string operation) =>
        operation is not null && executors.ContainsKey(operation);

    public ValueTask<WriterExecutionResult> ExecuteAsync(
        WriterCommandRequest request,
        IWriterTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);

        if (!executors.TryGetValue(request.Operation, out var executor))
        {
            return ValueTask.FromResult(
                WriterExecutionResult.Rejected("ipc.request.invalid"));
        }

        return executor.ExecuteAsync(transaction, cancellationToken);
    }
}
