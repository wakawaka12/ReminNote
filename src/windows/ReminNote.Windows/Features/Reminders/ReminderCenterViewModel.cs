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
    private bool isOpen;
    private bool hasSnapshot;
    private long? snapshotRevision;
    private ReminderSnapshotStatus snapshotStatus = ReminderSnapshotStatus.Unavailable;
    private string? snapshotStatusCode;
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

    public long? SnapshotRevision => snapshotRevision;

    public ReminderSnapshotStatus SnapshotStatus => snapshotStatus;

    public string? SnapshotStatusCode => snapshotStatusCode;

    public bool HasSnapshot => hasSnapshot;

    public bool IsFresh => SnapshotStatus == ReminderSnapshotStatus.Fresh;

    public bool ActionsEnabled => IsFresh && hasSnapshot && SnapshotRevision is not null;

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
    /// an existing list visible but marks it stale; a first unavailable read
    /// remains empty and explicitly unavailable.
    /// </summary>
    public void ApplySnapshot(ReminderReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureNotDisposed();

        if (snapshot.Status == ReminderSnapshotStatus.Unavailable)
        {
            snapshotStatusCode = snapshot.StatusCode;
            if (hasSnapshot)
            {
                snapshotStatus = ReminderSnapshotStatus.Stale;
            }
            else
            {
                snapshotStatus = ReminderSnapshotStatus.Unavailable;
                snapshotRevision = null;
                ReplaceItems(Array.Empty<ReminderReadModel>());
            }

            NotifySnapshotChanged();
            return;
        }

        snapshotStatus = snapshot.Status;
        snapshotStatusCode = snapshot.StatusCode;
        snapshotRevision = snapshot.SnapshotRevision;
        hasSnapshot = true;
        ReplaceItems(snapshot.Items);
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
            refreshGate.Dispose();
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
        await RefreshAsync().ConfigureAwait(true);
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
        if (!ActionsEnabled || SnapshotRevision is not { } revision)
        {
            item.SetActionFeedback(UiText.ReminderActionUnavailable);
            InteractionMessage = UiText.ReminderActionUnavailable;
            return;
        }

        ReminderCommandResult result;
        try
        {
            result = await commandClient
                .ExecuteAsync(
                    new ReminderActionCommand(item.Model.InstanceId, action, revision, snoozeSeconds),
                    CancellationToken.None)
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
        if (!ActionsEnabled || SnapshotRevision is not { } revision)
        {
            item.SetActionFeedback(UiText.ReminderActionUnavailable);
            InteractionMessage = UiText.ReminderActionUnavailable;
            return;
        }

        ReminderCommandResult result;
        try
        {
            result = await commandClient
                .MarkReadAsync(
                    new ReminderMarkReadCommand(item.Model.InstanceId, revision),
                    CancellationToken.None)
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
        if (result.Succeeded)
        {
            InteractionMessage = string.Empty;
            item.SetActionFeedback(null);
            await RefreshAsync().ConfigureAwait(true);
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
        if (result.Outcome is ReminderCommandOutcome.Stale or ReminderCommandOutcome.Unavailable)
        {
            snapshotStatus = ReminderSnapshotStatus.Stale;
            snapshotStatusCode = errorCode;
            NotifySnapshotChanged();
        }
    }

    private void ReplaceItems(IReadOnlyList<ReminderReadModel> models)
    {
        Items.Clear();
        foreach (var model in models)
        {
            Items.Add(new ReminderItemViewModel(
                model,
                () => ActionsEnabled,
                ExecuteActionAsync,
                MarkReadAsync));
        }
    }

    private void NotifySnapshotChanged()
    {
        OnPropertyChanged(nameof(SnapshotRevision));
        OnPropertyChanged(nameof(SnapshotStatus));
        OnPropertyChanged(nameof(SnapshotStatusCode));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(IsFresh));
        OnPropertyChanged(nameof(ActionsEnabled));
        OnPropertyChanged(nameof(StatusLabel));
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

    private static string GetFailureCode(Exception exception) => exception switch
    {
        ReminNote.Agent.Runtime.AgentCommandException agentException => agentException.Code,
        FileNotFoundException or DirectoryNotFoundException or IOException => "storage.not_ready",
        _ => "ipc.agent.unavailable"
    };

    private static bool IsFatal(Exception exception) => exception switch
    {
        OutOfMemoryException or StackOverflowException or AccessViolationException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsFatal),
        _ => exception.InnerException is not null && IsFatal(exception.InnerException)
    };
}
