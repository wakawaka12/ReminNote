using NodaTime;
using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Application;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Tasks;
using ReminNote.Core.Today;
using ReminNote.Infrastructure.Application;
using ReminNote.Widget.ViewModels;
using ReminNote.Windows.Features.Reminders;
using SystemTask = System.Threading.Tasks.Task;

namespace ReminNote.Tests;

public sealed class ReminderUiAndProtocolTests
{
    [Fact]
    public async SystemTask MainCenterUsesSnapshotRevisionForActionsAndRefreshesAfterSuccess()
    {
        var item = CreateItem("整理桌面资料");
        var query = new FakeReminderQueryService(ReminderReadSnapshot.Fresh(17, [item]));
        var commands = new FakeReminderCommandClient();
        using var center = new ReminderCenterViewModel(query, commands);

        await center.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReminderSnapshotStatus.Fresh, center.SnapshotStatus);
        Assert.Equal(17, center.SnapshotRevision);
        var row = Assert.Single(center.Items);
        Assert.True(row.DoneCommand.CanExecute(null));

        await row.DoneCommand.ExecuteAsync(null);

        var action = Assert.Single(commands.Actions);
        Assert.Equal(item.InstanceId, action.InstanceId);
        Assert.Equal(ResolutionAction.DONE, action.Action);
        Assert.Equal(17, action.ExpectedRevision);
        Assert.Equal(2, query.CallCount);
    }

    [Fact]
    public async SystemTask MainCenterOpensForToastActivationAndMarksAllFoundMembers()
    {
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        var first = CreateItem("摘要提醒 A", firstId);
        var second = CreateItem("摘要提醒 B", secondId);
        var query = new FakeReminderQueryService(
            ReminderReadSnapshot.Fresh(19, [first, second]));
        var commands = new FakeReminderCommandClient();
        using var center = new ReminderCenterViewModel(query, commands);

        await center.OpenForActivationAsync(
            [secondId, firstId],
            TestContext.Current.CancellationToken);

        Assert.True(center.IsOpen);
        Assert.Equal(2, center.ActivationRequestedCount);
        Assert.Equal(2, center.ActivationMatchCount);
        Assert.All(center.Items, item => Assert.True(item.IsActivationMatch));
        Assert.Contains("找到全部", center.InteractionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async SystemTask MainCenterRetainsLastModelAsUnavailableAndDisablesWrites()
    {
        var item = CreateItem("保留的提醒");
        var query = new FakeReminderQueryService(ReminderReadSnapshot.Fresh(23, [item]));
        var commands = new FakeReminderCommandClient();
        using var center = new ReminderCenterViewModel(query, commands);

        await center.RefreshAsync(TestContext.Current.CancellationToken);
        query.Current = ReminderReadSnapshot.Unavailable(ProtocolErrorCodes.AgentUnavailable);
        await center.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReminderSnapshotStatus.Unavailable, center.SnapshotStatus);
        Assert.True(center.HasSnapshot);
        Assert.Equal(23, center.SnapshotRevision);
        Assert.Equal("保留的提醒", Assert.Single(center.Items).Title);
        Assert.False(center.ActionsEnabled);
        Assert.False(center.Items[0].DoneCommand.CanExecute(null));

        await center.Items[0].DoneCommand.ExecuteAsync(null);
        Assert.Empty(commands.Actions);
    }

    [Fact]
    public async SystemTask MainCenterFailsClosedAfterAgentNotReadyCommand()
    {
        var query = new FakeReminderQueryService(
            ReminderReadSnapshot.Fresh(29, [CreateItem("等待 Agent 的提醒")]));
        var commands = new FakeReminderCommandClient
        {
            Result = new(
                ReminderCommandOutcome.Unavailable,
                ErrorCode: ProtocolErrorCodes.AgentNotReady)
        };
        using var center = new ReminderCenterViewModel(query, commands);

        await center.RefreshAsync(TestContext.Current.CancellationToken);
        await Assert.Single(center.Items).DoneCommand.ExecuteAsync(null);

        Assert.Equal(ReminderSnapshotStatus.Unavailable, center.SnapshotStatus);
        Assert.Equal(ProtocolErrorCodes.AgentNotReady, center.SnapshotStatusCode);
        Assert.False(center.ActionsEnabled);
    }

    [Fact]
    public async SystemTask MainCenterRoutesEveryResolutionActionThroughCommandClient()
    {
        foreach (var action in new[]
                 {
                     ResolutionAction.DONE,
                     ResolutionAction.SNOOZE,
                     ResolutionAction.SKIP,
                     ResolutionAction.IGNORE
                 })
        {
            var item = CreateItem($"{action} 提醒");
            var query = new FakeReminderQueryService(ReminderReadSnapshot.Fresh(37, [item]));
            var commands = new FakeReminderCommandClient();
            using var center = new ReminderCenterViewModel(query, commands);

            await center.RefreshAsync(TestContext.Current.CancellationToken);
            var row = Assert.Single(center.Items);

            var command = action switch
            {
                ResolutionAction.DONE => row.DoneCommand,
                ResolutionAction.SNOOZE => row.SnoozeCommand,
                ResolutionAction.SKIP => row.SkipCommand,
                ResolutionAction.IGNORE => row.IgnoreCommand,
                _ => throw new InvalidOperationException()
            };
            await command.ExecuteAsync(null);

            var sent = Assert.Single(commands.Actions);
            Assert.Equal(action, sent.Action);
            Assert.Equal(37, sent.ExpectedRevision);
        }
    }

    [Fact]
    public async SystemTask MainCenterRoutesMarkReadWithSnapshotRevisionThroughCommandClient()
    {
        var item = CreateItem("需要标记已读的提醒");
        var query = new FakeReminderQueryService(ReminderReadSnapshot.Fresh(41, [item]));
        var commands = new FakeReminderCommandClient();
        using var center = new ReminderCenterViewModel(query, commands);

        await center.RefreshAsync(TestContext.Current.CancellationToken);
        await Assert.Single(center.Items).MarkReadCommand.ExecuteAsync(null);

        var read = Assert.Single(commands.ReadCommands);
        Assert.Equal(item.InstanceId, read.InstanceId);
        Assert.Equal(41, read.ExpectedRevision);
        Assert.Empty(commands.Actions);
    }

    [Fact]
    public async SystemTask WidgetUsesQueryOnlyReminderSurfaceAndNoWriterFallback()
    {
        var item = CreateItem("Widget 提醒");
        var query = new FakeReminderQueryService(ReminderReadSnapshot.Fresh(31, [item]));
        var commands = new FakeReminderCommandClient();
        using var viewModel = new WidgetViewModel(
            new EmptyTodayQueryService(),
            new NoopTaskApplicationService(),
            query,
            commands,
            new FixedClock(Instant.FromUtc(2026, 9, 2, 8, 0)));

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReminderSnapshotStatus.Fresh, viewModel.ReminderSnapshotStatus);
        Assert.Equal(31, viewModel.ReminderSnapshotRevision);
        var row = Assert.Single(viewModel.ReminderItems);
        await row.SnoozeCommand.ExecuteAsync(null);

        var action = Assert.Single(commands.Actions);
        Assert.Equal(ResolutionAction.SNOOZE, action.Action);
        Assert.Equal(31, action.ExpectedRevision);
        Assert.Equal(ReminderItemViewModel.DefaultSnoozeSeconds, action.SnoozeSeconds);

        query.Current = ReminderReadSnapshot.Unavailable(ProtocolErrorCodes.AgentUnavailable);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReminderSnapshotStatus.Unavailable, viewModel.ReminderSnapshotStatus);
        Assert.True(viewModel.HasReminderSnapshot);
        Assert.False(viewModel.ReminderActionsEnabled);
    }

    [Fact]
    public void SnapshotRevisionAndLifecyclePayloadsFailClosedAtTheContractBoundary()
    {
        var item = CreateItem("合同校验提醒");

        var staleException = Assert.Throws<DomainValidationException>(() =>
            new ReminderReadSnapshot(
                snapshotRevision: null,
                items: [item],
                status: ReminderSnapshotStatus.Stale,
                statusCode: "test.stale"));
        Assert.Equal("reminder.snapshot.stale_revision_missing", staleException.Errors[0].Code);

        var unavailableException = Assert.Throws<DomainValidationException>(() =>
            new ReminderReadSnapshot(
                snapshotRevision: 7,
                items: [],
                status: ReminderSnapshotStatus.Unavailable,
                statusCode: "test.unavailable"));
        Assert.Equal("reminder.snapshot.unavailable_payload_invalid", unavailableException.Errors[0].Code);

        var resolvedException = Assert.Throws<DomainValidationException>(() =>
            new ReminderReadModel(
                item.InstanceId,
                item.ScheduleId,
                item.RuleId,
                item.OccurrenceId,
                item.TaskId,
                item.Title,
                item.Purpose,
                item.Priority,
                item.Pinned,
                item.TriggeredAtUtc,
                ReminderLifecycle.RESOLVED));
        Assert.Equal("reminder.read_model.resolution.missing", resolvedException.Errors[0].Code);
    }

    [Fact]
    public async SystemTask UnavailableReadOnIsolatedPathDoesNotCreateDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ReminNote-P3-06-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "isolated-read-only.sqlite");

        try
        {
            var profileScope = ProtocolProfileScope.Derive("sid-p3-06", databasePath);
            var service = new ReadOnlyReminderQueryService(databasePath, profileScope);

            var snapshot = await service.GetAsync(
                ReminderQuery.ActiveOnly,
                TestContext.Current.CancellationToken);

            Assert.Equal(ReminderSnapshotStatus.Unavailable, snapshot.Status);
            Assert.Equal(ProtocolErrorCodes.StorageNotReady, snapshot.StatusCode);
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ReminderResolvePayloadRoundTripsAndRetryKeepsLogicalIdentity()
    {
        var instanceId = ReminderInstanceId.New();
        var payload = ProtocolJson.CreateReminderResolvePayload(
            instanceId.ToString(),
            ResolutionAction.SNOOZE,
            ReminderItemViewModel.DefaultSnoozeSeconds);
        var request = ProtocolRequest.CreateMutationAttempt(
            ProtocolClientKinds.Widget,
            ProtocolIds.NewClientInstanceId(),
            DateTimeOffset.UtcNow,
            ProtocolLimits.DefaultMutationTimeoutMilliseconds,
            ProtocolOperations.ReminderResolve,
            payload,
            ProtocolIds.NewIdempotencyKey(),
            expectedRevision: 31);
        request.Validate();

        var wire = ProtocolJson.SerializeRequest(request);
        var decoded = ProtocolJson.DeserializeRequest(wire);
        var command = ProtocolJson.ReadReminderResolvePayload(decoded);
        var retry = request.CreateRetryAttempt(DateTimeOffset.UtcNow, request.TimeoutMs);

        Assert.Equal(instanceId.ToString(), command.InstanceId);
        Assert.Equal("SNOOZE", command.Action);
        Assert.Equal(ReminderItemViewModel.DefaultSnoozeSeconds, command.SnoozeSeconds);
        Assert.Equal(request.IdempotencyKey, retry.IdempotencyKey);
        Assert.Equal(request.ExpectedRevision, retry.ExpectedRevision);
        Assert.NotEqual(request.RequestId, retry.RequestId);
        Assert.Equal(request.Operation, retry.Operation);
    }

    [Fact]
    public void ReminderWireRejectsAnimeResolutionActions()
    {
        var instanceId = ReminderInstanceId.New();

        var exception = Assert.Throws<ProtocolContractException>(() =>
            ProtocolJson.CreateReminderResolvePayload(
                instanceId.ToString(),
                ResolutionAction.WATCHED));

        Assert.Equal(ProtocolErrorCodes.InvalidRequest, exception.Code);
    }

    [Fact]
    public void ReminderDomainErrorCodeFitsProtocolErrorAllowList()
    {
        var error = new ProtocolError(
            "reminder.instance.lifecycle.invalid",
            retryable: false,
            details: ProtocolJson.ParseObject("{}"));

        error.Validate();
    }

    private static ReminderReadModel CreateItem(string title, Guid? logicalReminderId = null)
    {
        var taskId = Guid.CreateVersion7();
        return new ReminderReadModel(
            ReminderInstanceId.New(),
            ReminderScheduleId.New(),
            ReminderRuleId.New(),
            OccurrenceId.From(taskId),
            TaskId.From(taskId),
            title,
            ReminderPurpose.TASK_CUSTOM,
            ReminderPriority.HIGH,
            pinned: true,
            Instant.FromUtc(2026, 9, 2, 8, 30),
            ReminderLifecycle.UNREAD,
            logicalReminderId: logicalReminderId);
    }

    private sealed class FakeReminderQueryService(ReminderReadSnapshot initial) : IReminderQueryService
    {
        public ReminderReadSnapshot Current { get; set; } = initial;

        public int CallCount { get; private set; }

        public ValueTask<ReminderReadSnapshot> GetAsync(
            ReminderQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(Current);
        }
    }

    private sealed class FakeReminderCommandClient : IReminderCommandClient
    {
        public List<ReminderActionCommand> Actions { get; } = [];

        public List<ReminderMarkReadCommand> ReadCommands { get; } = [];

        public ReminderCommandResult Result { get; set; } = new(ReminderCommandOutcome.Changed, CommittedRevision: 32);

        public ValueTask<ReminderCommandResult> ExecuteAsync(
            ReminderActionCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Actions.Add(command);
            return ValueTask.FromResult(Result);
        }

        public ValueTask<ReminderCommandResult> MarkReadAsync(
            ReminderMarkReadCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCommands.Add(command);
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private sealed class EmptyTodayQueryService : ITodayQueryService
    {
        public ValueTask<TodayReadModel> GetAsync(
            TodayQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new TodayReadModel(request.Now.InUtc().Date, []));
        }
    }

    private sealed class NoopTaskApplicationService : ITaskApplicationService
    {
        public ValueTask<TaskSnapshot> CreateAsync(
            CreateTaskCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<TaskSnapshot?> UpdateAsync(
            UpdateTaskCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<TaskSnapshot?> RecordResultAsync(
            RecordTaskResultCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(
            TaskId taskId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<TaskSnapshot?> ReorderAsync(
            ReorderTaskCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<TaskSnapshot?> ContinueAsync(
            ContinueTaskCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
