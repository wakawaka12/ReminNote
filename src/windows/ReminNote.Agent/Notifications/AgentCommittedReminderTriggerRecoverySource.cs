using Microsoft.EntityFrameworkCore;
using ReminNote.Agent.Scheduling;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.Reminders;

namespace ReminNote.Agent.Notifications;

/// <summary>
/// Read-only reconciliation source for the crash window between the P3-03
/// due transaction commit and the P3-04 PENDING append. It reads only durable
/// ReminderInstance rows and consults the durable attempt projection before
/// returning a trigger; it never opens a writer or creates a core instance.
/// </summary>
public sealed class AgentCommittedReminderTriggerRecoverySource
    : INotificationCommittedTriggerRecoverySource
{
    private readonly string databasePath;
    private readonly INotificationDeliveryAttemptJournalStore attemptStore;
    private readonly IReadOnlyList<NotificationChannelId> channelIds;

    public AgentCommittedReminderTriggerRecoverySource(
        string databasePath,
        INotificationDeliveryAttemptJournalStore attemptStore,
        IEnumerable<NotificationChannelId>? channelIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.attemptStore = attemptStore ?? throw new ArgumentNullException(nameof(attemptStore));
        this.channelIds = ValidateChannels(channelIds);
    }

    public async ValueTask<IReadOnlyList<NotificationTriggerFact>> ListCommittedTriggerFactsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        await using var context = ReminNoteDatabase.CreateContext(connection);
        var instances = await context.ReminderInstances
            .AsNoTracking()
            .OrderBy(instance => instance.TriggeredAtUtc)
            .ThenBy(instance => instance.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var triggers = new List<NotificationTriggerFact>(instances.Count);
        foreach (var instanceEntity in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await HasUndeliveredChannelAsync(instanceEntity, cancellationToken)
                    .ConfigureAwait(false))
            {
                continue;
            }

            // Rehydrate through the P3-03 domain mapping before projecting the
            // immutable notification fact. Corrupt or non-P3 rows therefore
            // fail the recovery gate instead of being dispatched ambiguously.
            triggers.Add(ReminderScheduler.ToNotificationTriggerFact(
                instanceEntity.ToDomain()));
        }

        return triggers;
    }

    private async ValueTask<bool> HasUndeliveredChannelAsync(
        ReminderInstanceEntity instance,
        CancellationToken cancellationToken)
    {
        foreach (var channelId in channelIds)
        {
            var current = await attemptStore.FindCurrentAsync(
                    instance.Id,
                    instance.LogicalReminderId,
                    channelId,
                    cancellationToken)
                .ConfigureAwait(false);

            // A pending or retryable current event still belongs to recovery;
            // NotificationDeliveryDispatcher performs the final atomic
            // replay/backoff decision against the same durable projection.
            if (current is null || current.IsPending || current.Retryable)
            {
                return true;
            }
        }

        return false;
    }

    private static NotificationChannelId[] ValidateChannels(
        IEnumerable<NotificationChannelId>? configuredChannels)
    {
        var channels = (configuredChannels ?? NotificationChannels.P3).ToArray();
        if (channels.Length == 0)
        {
            throw new ArgumentException(
                "At least one notification channel is required for recovery.",
                nameof(configuredChannels));
        }

        foreach (var channel in channels)
        {
            NotificationChannels.RequireKnown(channel);
        }

        if (channels.Distinct().Count() != channels.Length)
        {
            throw new ArgumentException(
                "Recovery channels cannot contain duplicates.",
                nameof(configuredChannels));
        }

        return channels;
    }
}
