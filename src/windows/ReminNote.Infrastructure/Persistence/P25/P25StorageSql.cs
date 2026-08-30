using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ReminNote.Infrastructure.Persistence.P25;

internal static class P25StorageSql
{
    public static SqliteCommand CreateCommand(
        SqliteConnection connection,
        string commandText,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        return command;
    }

    public static void AddBlobParameter(
        SqliteCommand command,
        string name,
        byte[] value)
    {
        var parameter = command.Parameters.Add(name, SqliteType.Blob);
        parameter.Value = value;
    }

    public static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static string ToStorageValue(P25ReceiptStatus status) => status switch
    {
        P25ReceiptStatus.Pending => "PENDING",
        P25ReceiptStatus.Committed => "COMMITTED",
        P25ReceiptStatus.RejectedStale => "REJECTED_STALE",
        P25ReceiptStatus.Rejected => "REJECTED",
        P25ReceiptStatus.RolledBack => "ROLLED_BACK",
        P25ReceiptStatus.Cancelled => "CANCELLED",
        P25ReceiptStatus.TimedOut => "TIMED_OUT",
        P25ReceiptStatus.Unknown => "UNKNOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown receipt status.")
    };

    public static P25ReceiptStatus ParseReceiptStatus(string value) => value switch
    {
        "PENDING" => P25ReceiptStatus.Pending,
        "COMMITTED" => P25ReceiptStatus.Committed,
        "REJECTED_STALE" => P25ReceiptStatus.RejectedStale,
        "REJECTED" => P25ReceiptStatus.Rejected,
        "ROLLED_BACK" => P25ReceiptStatus.RolledBack,
        "CANCELLED" => P25ReceiptStatus.Cancelled,
        "TIMED_OUT" => P25ReceiptStatus.TimedOut,
        "UNKNOWN" => P25ReceiptStatus.Unknown,
        _ => throw new InvalidOperationException($"Unknown P2.5 receipt status '{value}'.")
    };

    public static P25MutationOutcome ToOutcome(
        P25ReceiptStatus status,
        bool changed,
        bool replayed) =>
        status == P25ReceiptStatus.Committed && !changed
            ? P25MutationOutcome.NoOp
            : replayed
                ? P25MutationOutcome.Replayed
                : status switch
                {
                    P25ReceiptStatus.Pending => P25MutationOutcome.Pending,
                    P25ReceiptStatus.Committed => P25MutationOutcome.Changed,
                    P25ReceiptStatus.RejectedStale => P25MutationOutcome.Stale,
                    P25ReceiptStatus.Rejected => P25MutationOutcome.Rejected,
                    P25ReceiptStatus.RolledBack => P25MutationOutcome.RolledBack,
                    P25ReceiptStatus.Cancelled => P25MutationOutcome.Cancelled,
                    P25ReceiptStatus.TimedOut => P25MutationOutcome.TimedOut,
                    P25ReceiptStatus.Unknown => P25MutationOutcome.Unknown,
                    _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown receipt status.")
                };

    public static async ValueTask<P25Receipt?> ReadReceiptAsync(
        SqliteConnection connection,
        string actualUserSid,
        string profileScope,
        string idempotencyKey,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            """
            SELECT
                actual_user_sid,
                profile_scope,
                idempotency_key,
                operation,
                hash_version,
                canonical_payload_hash,
                status,
                changed,
                committed_revision,
                error_code,
                first_accepted_at_utc,
                updated_at_utc,
                first_request_id,
                last_request_id,
                attempt_count,
                agent_instance_id
            FROM command_receipt
            WHERE actual_user_sid = $actualUserSid
              AND profile_scope = $profileScope
              AND idempotency_key = $idempotencyKey;
            """,
            transaction);
        command.Parameters.AddWithValue("$actualUserSid", actualUserSid);
        command.Parameters.AddWithValue("$profileScope", profileScope);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var hash = (byte[])reader[5];
        return new P25Receipt(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            hash.ToArray(),
            ParseReceiptStatus(reader.GetString(6)),
            reader.GetInt64(7) != 0,
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            ParseUtc(reader.GetString(10)),
            ParseUtc(reader.GetString(11)),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetInt32(14),
            reader.IsDBNull(15) ? null : reader.GetString(15));
    }

    public static void ValidateTextByteCount(string value, int maxBytes, string parameterName)
    {
        if (Encoding.UTF8.GetByteCount(value) > maxBytes)
        {
            throw new InvalidOperationException(
                $"P2.5 storage metadata '{parameterName}' exceeds its UTF-8 limit.");
        }
    }
}
