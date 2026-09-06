using Microsoft.Data.Sqlite;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Application;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Persistence.P25;

namespace ReminNote.Infrastructure.Application;

/// <summary>
/// Query-only Reminder projection for one P2.5 profile. The snapshot reader
/// owns the read transaction and connection guards; this adapter only maps
/// rows into the Core read model. It never creates a schema or writes a row.
/// </summary>
public sealed class ReadOnlyReminderQueryService : IReminderQueryService
{
    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'");

    private readonly string databasePath;
    private readonly string profileScope;

    public ReadOnlyReminderQueryService(string databasePath, string profileScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ProtocolProfileScope.Validate(profileScope);
        this.databasePath = Path.GetFullPath(databasePath);
        this.profileScope = profileScope;
    }

    public async ValueTask<ReminderReadSnapshot> GetAsync(
        ReminderQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        try
        {
            await using var reader = await P25StorageReader
                .OpenReadOnlyAsync(databasePath, profileScope, cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await reader.ReadSnapshotAsync(
                    (context, token) => ReadItemsAsync(context, query, token),
                    cancellationToken)
                .ConfigureAwait(false);
            return ReminderReadSnapshot.Fresh(snapshot.SnapshotRevision, snapshot.Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return ReminderReadSnapshot.Unavailable(GetUnavailableCode(exception));
        }
    }

    private static async ValueTask<IReadOnlyList<ReminderReadModel>> ReadItemsAsync(
        P25SnapshotContext context,
        ReminderQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = context.CreateCommand(
            """
            SELECT
                ri.id,
                ri.schedule_id,
                ri.rule_id,
                ri.occurrence_id,
                ri.logical_reminder_id,
                ri.purpose_snapshot,
                ri.priority_snapshot,
                ri.pinned_snapshot,
                ri.triggered_at_utc,
                ri.lifecycle,
                ri.resolution_action,
                t.id,
                t.title
            FROM reminder_instances AS ri
            INNER JOIN tasks AS t ON t.id = ri.occurrence_id
            WHERE ($includeResolved = 1 OR ri.lifecycle <> 'RESOLVED')
            ORDER BY
                CASE WHEN ri.pinned_snapshot = 1 THEN 0 ELSE 1 END,
                CASE ri.priority_snapshot
                    WHEN 'HIGH' THEN 0
                    WHEN 'NORMAL' THEN 1
                    ELSE 2
                END,
                ri.triggered_at_utc DESC,
                ri.id ASC
            LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$includeResolved", query.IncludeResolved ? 1 : 0);
        command.Parameters.AddWithValue("$limit", query.MaxItems);

        var items = new List<ReminderReadModel>(query.MaxItems);
        await using var rows = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new ReminderReadModel(
                ReminderInstanceId.Parse(rows.GetString(0)),
                ReminderScheduleId.Parse(rows.GetString(1)),
                ReminderRuleId.Parse(rows.GetString(2)),
                OccurrenceId.Parse(rows.GetString(3)),
                TaskId.Parse(rows.GetString(11)),
                rows.GetString(12),
                ParseEnum<ReminderPurpose>(rows.GetString(5), "purpose_snapshot"),
                ParseEnum<ReminderPriority>(rows.GetString(6), "priority_snapshot"),
                ParseBoolean(rows, 7, "pinned_snapshot"),
                ParseInstant(rows.GetString(8), "triggered_at_utc"),
                ParseEnum<ReminderLifecycle>(rows.GetString(9), "lifecycle"),
                rows.IsDBNull(10)
                    ? null
                    : ParseEnum<ResolutionAction>(rows.GetString(10), "resolution_action"),
                logicalReminderId: LogicalReminderId.Parse(rows.GetString(4)).Value));
        }

        return items;
    }

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.read_model.enum.invalid",
                $"The read model field {fieldName} is not a supported enum token.",
                fieldName));
        }

        return parsed;
    }

    private static bool ParseBoolean(
        Microsoft.Data.Sqlite.SqliteDataReader rows,
        int ordinal,
        string fieldName)
    {
        var value = rows.GetValue(ordinal);
        var number = value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            _ => -1
        };
        if (number is 0 or 1)
        {
            return number == 1;
        }

        throw new DomainValidationException(new DomainValidationError(
            "reminder.read_model.boolean.invalid",
            $"The read model field {fieldName} must contain 0 or 1.",
            fieldName));
    }

    private static Instant ParseInstant(string value, string fieldName)
    {
        var parsed = InstantPattern.Parse(value);
        if (!parsed.Success)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.read_model.instant.invalid",
                $"The read model field {fieldName} is not a canonical UTC instant.",
                fieldName));
        }

        return parsed.Value;
    }

    private static string GetUnavailableCode(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException =>
            "storage.not_ready",
        InvalidOperationException => "storage.not_ready",
        SqliteException => "storage.not_ready",
        DomainValidationException or FormatException or ArgumentException or
        InvalidCastException or OverflowException or IndexOutOfRangeException => "storage.integrity_failed",
        _ => "storage.not_ready"
    };

    private static bool IsFatal(Exception exception) => exception switch
    {
        OutOfMemoryException or StackOverflowException or AccessViolationException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsFatal),
        _ => exception.InnerException is not null && IsFatal(exception.InnerException)
    };
}
