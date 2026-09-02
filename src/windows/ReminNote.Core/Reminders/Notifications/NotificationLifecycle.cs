using NodaTime;

#pragma warning disable CA1707 // Frozen wire names use uppercase underscore codes.
namespace ReminNote.Core.Reminders.Notifications;

public enum NotificationReminderLifecycle
{
    UNREAD,
    READ,
    RESOLVED,
}

public enum NotificationResolutionAction
{
    DONE,
    SNOOZE,
    WATCHED,
    WATCH_LATER,
    SKIP,
    IGNORE,
}

/// <summary>
/// Immutable lifecycle state for a ReminderInstance projection. The only
/// permitted direction is UNREAD → READ → RESOLVED. Toast close is represented
/// by <see cref="ApplyToastClose"/> and can only mark a fact as read.
/// </summary>
public sealed record NotificationLifecycleState
{
    public NotificationLifecycleState(
        NotificationReminderLifecycle lifecycle,
        Instant? readAtUtc = null,
        Instant? resolvedAtUtc = null,
        NotificationResolutionAction? resolutionAction = null)
    {
        if (!Enum.IsDefined(lifecycle))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidLifecycle,
                "Reminder lifecycle is not supported.",
                nameof(lifecycle));
        }

        if (lifecycle == NotificationReminderLifecycle.UNREAD &&
            (readAtUtc is not null || resolvedAtUtc is not null || resolutionAction is not null))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidLifecycle,
                "UNREAD cannot carry read, resolved, or resolution facts.",
                nameof(lifecycle));
        }

        if (lifecycle == NotificationReminderLifecycle.READ &&
            (readAtUtc is null || resolvedAtUtc is not null || resolutionAction is not null))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidLifecycle,
                "READ requires readAtUtc and cannot carry resolved facts.",
                nameof(lifecycle));
        }

        if (lifecycle == NotificationReminderLifecycle.RESOLVED &&
            (readAtUtc is null || resolvedAtUtc is null || resolutionAction is null))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidLifecycle,
                "RESOLVED requires read, resolved, and action facts.",
                nameof(lifecycle));
        }

        if (readAtUtc is { } read && resolvedAtUtc is { } resolved && resolved < read)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidTimestamp,
                "resolvedAtUtc cannot precede readAtUtc.",
                nameof(resolvedAtUtc));
        }

        if (resolutionAction is { } action && !Enum.IsDefined(action))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "Resolution action is not supported.",
                nameof(resolutionAction));
        }

        Lifecycle = lifecycle;
        ReadAtUtc = readAtUtc;
        ResolvedAtUtc = resolvedAtUtc;
        ResolutionAction = resolutionAction;
    }

    public NotificationReminderLifecycle Lifecycle { get; }

    public Instant? ReadAtUtc { get; }

    public Instant? ResolvedAtUtc { get; }

    public NotificationResolutionAction? ResolutionAction { get; }

    public static NotificationLifecycleState Unread { get; } =
        new(NotificationReminderLifecycle.UNREAD);

    /// <summary>
    /// Applies a Toast close. It is intentionally idempotent for an already
    /// read/resolved instance and never records a resolution action.
    /// </summary>
    public NotificationLifecycleState ApplyToastClose(Instant closedAtUtc) =>
        Lifecycle == NotificationReminderLifecycle.UNREAD
            ? MarkRead(closedAtUtc)
            : this;

    /// <summary>
    /// Marks the instance read. A later call cannot rewrite the original read
    /// timestamp, which keeps the lifecycle fact append-only from the caller's
    /// perspective.
    /// </summary>
    public NotificationLifecycleState MarkRead(Instant readAtUtc)
    {
        return Lifecycle switch
        {
            NotificationReminderLifecycle.UNREAD =>
                new NotificationLifecycleState(NotificationReminderLifecycle.READ, readAtUtc),
            NotificationReminderLifecycle.READ or NotificationReminderLifecycle.RESOLVED => this,
            _ => throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidLifecycle,
                "Reminder lifecycle is not supported.",
                nameof(Lifecycle)),
        };
    }

    /// <summary>
    /// Resolves the current instance. Resolving directly from UNREAD records
    /// the read fact at the same instant, matching the Agent transaction seam.
    /// A repeated identical action is a no-op; a conflicting action is rejected.
    /// </summary>
    public NotificationLifecycleState Resolve(
        NotificationResolutionAction action,
        Instant resolvedAtUtc)
    {
        if (!Enum.IsDefined(action))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "Resolution action is not supported.",
                nameof(action));
        }

        return Lifecycle switch
        {
            NotificationReminderLifecycle.UNREAD => new NotificationLifecycleState(
                NotificationReminderLifecycle.RESOLVED,
                resolvedAtUtc,
                resolvedAtUtc,
                action),
            NotificationReminderLifecycle.READ => ResolveFromRead(action, resolvedAtUtc),
            NotificationReminderLifecycle.RESOLVED when ResolutionAction == action => this,
            NotificationReminderLifecycle.RESOLVED => throw NotificationContractException.Invalid(
                NotificationErrorCodes.ResolutionConflict,
                "A resolved reminder cannot change its resolution action.",
                nameof(action)),
            _ => throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidLifecycle,
                "Reminder lifecycle is not supported.",
                nameof(Lifecycle)),
        };
    }

    private NotificationLifecycleState ResolveFromRead(
        NotificationResolutionAction action,
        Instant resolvedAtUtc)
    {
        if (ReadAtUtc is { } readAtUtc && resolvedAtUtc < readAtUtc)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidTimestamp,
                "resolvedAtUtc cannot precede readAtUtc.",
                nameof(resolvedAtUtc));
        }

        return new NotificationLifecycleState(
            NotificationReminderLifecycle.RESOLVED,
            ReadAtUtc,
            resolvedAtUtc,
            action);
    }
}

#pragma warning restore CA1707
