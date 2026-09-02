using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Application;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Windows.Resources.Localization;

namespace ReminNote.Windows.Features.Reminders;

/// <summary>
/// Main-window Reminder Center. A complete read snapshot is swapped into the
/// collection in one operation; stale/unavailable states disable every write
/// command and never call the legacy Task application service.
/// </summary>
public sealed class ReminderCenterViewModel : ObservableObject, IDisposable
{
    private readonly IReminderQueryService queryService;
    private readonly IReminderCommandClient commandClient;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private ReminderSnapshotState snapshot = ReminderSnapshotState.Empty;
    private bool isOpen;
    private string interactionMessage = string.Empty;
    private int disposed;

    public ReminderCenterViewModel(
        IReminderQueryService queryService,
        IReminderCommandClient commandClient)
    {
        this.queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        this.commandClient = commandClient ?? throw new ArgumentNullException(nameof(commandClient));
        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        CloseCommand = new RelayCommand(Close);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public ObservableCollection<ReminderItemViewModel> Items { get; } = [];

    public IAsyncRelayCommand ToggleCommand { get; }

    public IRelayCommand CloseCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public bool IsOpen
    {
        get => isOpen;
        private set
        {
            if (SetProperty(ref isOpen, value))
            {
                OnPropertyChanged(nameof(ToggleLabel));
            }
        }
    }

    public string ToggleLabel => IsOpen
        ? UiText.ReminderCenterClose
        : UiText.ReminderCenterOpen;

    public int ReminderCount => Items.Count;

    public bool HasItems => Items.Count > 0;

    public bool HasNoItems => !HasItems;

    public bool HasEmptyState => !HasItems;

    public bool HasSnapshotIssue => SnapshotStatus is not ReminderSnapshotStatus.Fresh;

    public string EmptyStateLabel => SnapshotStatus switch
    {
        ReminderSnapshotStatus.Fresh => UiText.ReminderSnapshotEmpty,
        ReminderSnapshotStatus.Stale => UiText.Format(
            UiText.ReminderSnapshotStaleEmptyKey,
            SnapshotRevision?.ToString(CultureInfo.InvariantCulture) ?? "—"),
        ReminderSnapshotStatus.Unavailable => UiText.Format(
            UiText.ReminderSnapshotUnavailableEmptyKey,
            SnapshotStatusCode ?? "unknown"),
        _ => UiText.ReminderSnapshotUnavailableEmpty
    };

    public long? SnapshotRevision => snapshot.SnapshotRevision;

    public ReminderSnapshotStatus SnapshotStatus => snapshot.Status;

    public string? SnapshotStatusCode => snapshot.StatusCode;

    public bool HasSnapshot => snapshot.HasSnapshot;

    public bool IsFresh => SnapshotStatus == ReminderSnapshotStatus.Fresh;

    public bool ActionsEnabled => snapshot.CanWrite;

    public string StatusLabel => SnapshotStatus switch
    {
        ReminderSnapshotStatus.Fresh => UiText.Format(
            UiText.ReminderSnapshotFreshKey,
            SnapshotRevision ?? 0),
        ReminderSnapshotStatus.Stale => UiText.Format(
            UiText.ReminderSnapshotStaleKey,
            SnapshotRevision?.ToString(CultureInfo.InvariantCulture) ?? "—"),
        ReminderSnapshotStatus.Unavailable => UiText.Format(
            UiText.ReminderSnapshotUnavailableKey,
            SnapshotStatusCode ?? "unknown"),
        _ => SnapshotStatus.ToString()
    };

    public string InteractionMessage
    {
        get => interactionMessage;
        private set
        {
            if (SetProperty(ref interactionMessage, value))
            {
                OnPropertyChanged(nameof(HasInteractionMessage));
            }
        }
    }

    public bool HasInteractionMessage => !string.IsNullOrEmpty(InteractionMessage);

    /// <summary>
    /// Atomically applies the complete read result. An unavailable read keeps
    /// an existing list visible but marks the state unavailable; it never
    /// leaves a revision usable for a write command.
    /// </summary>
    public void ApplySnapshot(ReminderReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureNotDisposed();
        this.snapshot = this.snapshot.Apply(snapshot);
        if (snapshot.Status != ReminderSnapshotStatus.Unavailable || !this.snapshot.HasSnapshot)
        {
            ReplaceItems(this.snapshot.Items, this.snapshot.SnapshotRevision);
        }

        NotifySnapshotChanged();
    }

    public async System.Threading.Tasks.Task RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ReminderReadSnapshot snapshot;
            try
            {
                snapshot = await queryService
                    .GetAsync(ReminderQuery.ActiveOnly, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                snapshot = ReminderReadSnapshot.Unavailable(GetFailureCode(exception));
            }

            ApplySnapshot(snapshot);
            InteractionMessage = snapshot.Status == ReminderSnapshotStatus.Fresh
                ? string.Empty
                : UiText.Format(
                    UiText.ReminderSnapshotReadFailedKey,
                    snapshot.StatusCode ?? "unknown");
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            lifetimeCancellation.Cancel();
        }

        GC.SuppressFinalize(this);
    }

    private async System.Threading.Tasks.Task ToggleAsync()
    {
        if (IsOpen)
        {
            Close();
            return;
        }

        IsOpen = true;
        await RefreshAsync(lifetimeCancellation.Token).ConfigureAwait(true);
    }

    private void Close()
    {
        IsOpen = false;
    }

    private async System.Threading.Tasks.Task ExecuteActionAsync(
        ReminderItemViewModel item,
        ResolutionAction action,
        long? snoozeSeconds)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!TryGetActionRevision(item, out var revision))
        {
            SetActionUnavailable(item);
            return;
        }

        ReminderCommandResult result;
        try
        {
            result = await commandClient
                .ExecuteAsync(
                    new ReminderActionCommand(item.Model.InstanceId, action, revision, snoozeSeconds),
                    lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            result = new ReminderCommandResult(
                ReminderCommandOutcome.Unavailable,
                ErrorCode: GetFailureCode(exception));
        }

        await HandleCommandResultAsync(item, result).ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task MarkReadAsync(ReminderItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!TryGetActionRevision(item, out var revision))
        {
            SetActionUnavailable(item);
            return;
        }

        ReminderCommandResult result;
        try
        {
            result = await commandClient
                .MarkReadAsync(
                    new ReminderMarkReadCommand(item.Model.InstanceId, revision),
                    lifetimeCancellation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            result = new ReminderCommandResult(
                ReminderCommandOutcome.Unavailable,
                ErrorCode: GetFailureCode(exception));
        }

        await HandleCommandResultAsync(item, result).ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task HandleCommandResultAsync(
        ReminderItemViewModel item,
        ReminderCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Succeeded)
        {
            InteractionMessage = string.Empty;
            item.SetActionFeedback(null);
            await RefreshAsync(lifetimeCancellation.Token).ConfigureAwait(true);
            if (SnapshotStatus != ReminderSnapshotStatus.Fresh)
            {
                var refreshMessage = SnapshotStatus == ReminderSnapshotStatus.Stale
                    ? UiText.ReminderActionCommittedStale
                    : UiText.ReminderActionCommittedUnavailable;
                item.SetActionFeedback(refreshMessage);
                InteractionMessage = refreshMessage;
            }

            return;
        }

        var message = result.Outcome switch
        {
            ReminderCommandOutcome.Stale => UiText.ReminderActionStale,
            ReminderCommandOutcome.Unavailable => UiText.ReminderActionUnavailable,
            _ => UiText.ReminderActionRejected
        };
        var errorCode = result.ErrorCode ?? (result.Outcome switch
        {
            ReminderCommandOutcome.Stale => ProtocolErrorCodes.ExpectedRevisionMismatch,
            ReminderCommandOutcome.Unavailable => ProtocolErrorCodes.AgentUnavailable,
            _ => null
        });
        item.SetActionFeedback(message);
        InteractionMessage = errorCode is null
            ? message
            : $"{message} · {errorCode}";
        if (result.Outcome == ReminderCommandOutcome.Stale)
        {
            this.snapshot = this.snapshot.MarkStale(errorCode ?? ProtocolErrorCodes.ExpectedRevisionMismatch);
            NotifySnapshotChanged();
        }
        else if (result.Outcome == ReminderCommandOutcome.Unavailable)
        {
            this.snapshot = this.snapshot.MarkUnavailable(errorCode ?? ProtocolErrorCodes.AgentUnavailable);
            NotifySnapshotChanged();
        }
    }

    private void ReplaceItems(IReadOnlyList<ReminderReadModel> models, long? revision)
    {
        Items.Clear();
        foreach (var model in models)
        {
            var rowRevision = revision;
            Items.Add(new ReminderItemViewModel(
                model,
                () => ActionsEnabled && SnapshotRevision == rowRevision,
                ExecuteActionAsync,
                MarkReadAsync,
                rowRevision));
        }
    }

    private void NotifySnapshotChanged()
    {
        OnPropertyChanged(nameof(SnapshotRevision));
        OnPropertyChanged(nameof(SnapshotStatus));
        OnPropertyChanged(nameof(SnapshotStatusCode));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(IsFresh));
        OnPropertyChanged(nameof(HasSnapshotIssue));
        OnPropertyChanged(nameof(ActionsEnabled));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(EmptyStateLabel));
        OnPropertyChanged(nameof(HasEmptyState));
        OnPropertyChanged(nameof(ReminderCount));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        RefreshItemCommandStates();
    }

    private void RefreshItemCommandStates()
    {
        foreach (var item in Items)
        {
            item.RefreshCommandState();
        }
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(disposed != 0, this);

    private bool TryGetActionRevision(ReminderItemViewModel item, out long revision)
    {
        if (!ActionsEnabled ||
            item.SnapshotRevision is not { } rowRevision ||
            SnapshotRevision != rowRevision)
        {
            revision = default;
            return false;
        }

        revision = rowRevision;
        return true;
    }

    private void SetActionUnavailable(ReminderItemViewModel item)
    {
        var message = SnapshotStatus == ReminderSnapshotStatus.Stale
            ? UiText.ReminderActionStale
            : UiText.ReminderActionUnavailable;
        item.SetActionFeedback(message);
        InteractionMessage = message;
    }

    private static string GetFailureCode(Exception exception) => exception switch
    {
        ReminNote.Agent.Runtime.AgentCommandException agentException => agentException.Code,
        FileNotFoundException or DirectoryNotFoundException or IOException => ProtocolErrorCodes.StorageNotReady,
        _ => ProtocolErrorCodes.AgentUnavailable
    };

    private static bool IsFatal(Exception exception) => exception switch
    {
        OutOfMemoryException or StackOverflowException or AccessViolationException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsFatal),
        _ => exception.InnerException is not null && IsFatal(exception.InnerException)
    };
}
