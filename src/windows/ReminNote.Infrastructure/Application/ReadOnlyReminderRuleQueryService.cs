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
/// Read-only projection of reminder rules. Keeping this separate from the
/// triggered-instance query lets Main/Widget edit future rules that have not
/// produced an Instance yet. The connection and transaction are still owned
/// by the P2.5 snapshot reader; this type never writes SQLite.
/// </summary>
public sealed class ReadOnlyReminderRuleQueryService : IReminderRuleQueryService
{
    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'");

    private readonly string databasePath;
    private readonly string profileScope;

    public ReadOnlyReminderRuleQueryService(string databasePath, string profileScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ProtocolProfileScope.Validate(profileScope);
        this.databasePath = Path.GetFullPath(databasePath);
        this.profileScope = profileScope;
    }

    public async ValueTask<ReminderRuleReadSnapshot> GetAsync(
        ReminderRuleQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        try
        {
            await using var reader = await P25StorageReader
                .OpenReadOnlyAsync(databasePath, profileScope, cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await reader.ReadSnapshotAsync(
                    (context, token) => ReadRulesAsync(context, query, token),
                    cancellationToken)
                .ConfigureAwait(false);
            return ReminderRuleReadSnapshot.Fresh(snapshot.SnapshotRevision, snapshot.Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return ReminderRuleReadSnapshot.Unavailable(GetUnavailableCode(exception));
        }
    }

    private static async ValueTask<IReadOnlyList<ReminderRuleReadModel>> ReadRulesAsync(
        P25SnapshotContext context,
        ReminderRuleQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = context.CreateCommand(
            """
            SELECT
                id,
                target_kind,
                target_id,
                occurrence_id,
                purpose,
                timing_kind,
                timing_anchor,
                offset_seconds,
                absolute_at_utc,
                priority,
                pinned,
                repeat_enabled,
                repeat_interval_seconds,
                repeat_max_count,
                wake_policy,
                enabled,
                rule_revision,
                created_at_utc,
                updated_at_utc
            FROM reminder_rules
            WHERE ($taskId IS NULL OR target_id = $taskId)
            ORDER BY target_id, created_at_utc, id
            LIMIT $limit;
            """);
        command.Parameters.AddWithValue(
            "$taskId",
            query.TaskId is { } taskId ? taskId.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$limit", query.MaxItems);

        var rules = new List<ReminderRuleReadModel>(query.MaxItems);
        await using var rows = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var targetKind = ParseEnum<ReminderTargetKind>(rows.GetString(1), "target_kind");
            var timingKind = ParseEnum<ReminderTimingKind>(rows.GetString(5), "timing_kind");
            var anchor = rows.IsDBNull(6)
                ? (ReminderAnchor?)null
                : ParseEnum<ReminderAnchor>(rows.GetString(6), "timing_anchor");
            var offsetSeconds = rows.IsDBNull(7) ? (long?)null : rows.GetInt64(7);
            var absoluteAtUtc = rows.IsDBNull(8)
                ? (Instant?)null
                : ParseInstant(rows.GetString(8), "absolute_at_utc");
            var timing = ReminderTiming.FromPersistence(
                timingKind,
                anchor,
                offsetSeconds,
                absoluteAtUtc);

            rules.Add(new ReminderRuleReadModel(
                ReminderRuleId.Parse(rows.GetString(0)),
                targetKind,
                TaskId.Parse(rows.GetString(2)),
                OccurrenceId.Parse(rows.GetString(3)),
                ParseEnum<ReminderPurpose>(rows.GetString(4), "purpose"),
                timing,
                ParseEnum<ReminderPriority>(rows.GetString(9), "priority"),
                ParseBoolean(rows, 10, "pinned"),
                new RepeatPolicy(
                    ParseBoolean(rows, 11, "repeat_enabled"),
                    rows.IsDBNull(12) ? null : rows.GetInt64(12),
                    rows.IsDBNull(13) ? null : rows.GetInt32(13)),
                ParseEnum<WakePolicy>(rows.GetString(14), "wake_policy"),
                ParseBoolean(rows, 15, "enabled"),
                rows.GetInt64(16),
                ParseInstant(rows.GetString(17), "created_at_utc"),
                ParseInstant(rows.GetString(18), "updated_at_utc")));
        }

        return rules;
    }

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.rule.read_model.enum.invalid",
                $"The read model field {fieldName} is not a supported enum token.",
                fieldName));
        }

        return parsed;
    }

    private static bool ParseBoolean(
        SqliteDataReader rows,
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
            "reminder.rule.read_model.boolean.invalid",
            $"The read model field {fieldName} must contain 0 or 1.",
            fieldName));
    }

    private static Instant ParseInstant(string value, string fieldName)
    {
        var parsed = InstantPattern.Parse(value);
        if (!parsed.Success)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.rule.read_model.instant.invalid",
                $"The read model field {fieldName} is not a canonical UTC instant.",
                fieldName));
        }

        return parsed.Value;
    }

    private static string GetUnavailableCode(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException =>
            "storage.not_ready",
        InvalidOperationException or SqliteException => "storage.not_ready",
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
