using System.Collections.ObjectModel;

namespace ReminNote.Core.Protocol;

/// <summary>
/// Validated launch intent emitted by a Reminder Toast.  The URI carries
/// logical reminder identities only; it never carries display text, profile
/// data paths, or a command to execute.
/// </summary>
public sealed record ReminderNotificationActivation
{
    public const string Scheme = "reminnote";
    public const string SingleHost = "reminder";
    public const string SummaryHost = "reminders";
    public const string IdsQueryKey = "ids";
    public const int MaxUriCharacters = 4_096;

    public ReminderNotificationActivation(IEnumerable<Guid> logicalReminderIds)
    {
        ArgumentNullException.ThrowIfNull(logicalReminderIds);
        var values = logicalReminderIds.ToArray();
        if (values.Length == 0 || values.Length > 64)
        {
            throw new ArgumentException(
                "A reminder activation must contain between one and 64 logical IDs.",
                nameof(logicalReminderIds));
        }

        if (values.Distinct().Count() != values.Length)
        {
            throw new ArgumentException(
                "A reminder activation cannot contain duplicate logical IDs.",
                nameof(logicalReminderIds));
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (!IsUuidV7(values[index]))
            {
                throw new ArgumentException(
                    $"logicalReminderIds[{index}] must be a UUID v7.",
                    nameof(logicalReminderIds));
            }
        }

        LogicalReminderIds = new ReadOnlyCollection<Guid>(
            values
                .OrderBy(value => value.ToString("D"), StringComparer.Ordinal)
                .ToArray());
    }

    public IReadOnlyList<Guid> LogicalReminderIds { get; }

    public int Count => LogicalReminderIds.Count;

    public bool IsSummary => Count > 1;

    /// <summary>
    /// Parses only the two URIs emitted by the Toast sink.  Invalid or
    /// attacker-controlled command-line values return false and never escape
    /// a contract exception into WPF startup.
    /// </summary>
    public static bool TryParse(
        string? value,
        out ReminderNotificationActivation? activation)
    {
        activation = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxUriCharacters)
        {
            return false;
        }

        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
                uri.UserInfo.Length != 0 ||
                uri.Port != -1 ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }

            var host = uri.Host;
            if (string.Equals(host, SummaryHost, StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath != "/" && uri.AbsolutePath != string.Empty ||
                    !TryReadQueryValue(uri, IdsQueryKey, out var encodedIds))
                {
                    return false;
                }

                var ids = Uri.UnescapeDataString(encodedIds)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (ids.Length == 0 || ids.Length > 64 ||
                    ids.Any(id => !TryParseUuidV7(id, out _)))
                {
                    return false;
                }

                var parsed = ids.Select(ParseUuidV7).ToArray();
                if (parsed.Distinct().Count() != parsed.Length)
                {
                    return false;
                }

                activation = new ReminderNotificationActivation(parsed);
                return true;
            }

            if (!string.Equals(host, SingleHost, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.Query))
            {
                return false;
            }

            var path = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (!TryParseUuidV7(path, out var logicalReminderId))
            {
                return false;
            }

            activation = new ReminderNotificationActivation([logicalReminderId]);
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string CreateSingleUri(Guid logicalReminderId)
    {
        RequireUuidV7(logicalReminderId);
        return $"{Scheme}://{SingleHost}/{logicalReminderId:D}";
    }

    public static string CreateSummaryUri(IEnumerable<Guid> logicalReminderIds)
    {
        var activation = new ReminderNotificationActivation(logicalReminderIds);
        var uri =
            $"{Scheme}://{SummaryHost}?{IdsQueryKey}=" +
            string.Join(',', activation.LogicalReminderIds.Select(id => id.ToString("D")));
        if (uri.Length > MaxUriCharacters)
        {
            throw new ArgumentException(
                "The reminder activation URI exceeds its bounded length.",
                nameof(logicalReminderIds));
        }

        return uri;
    }

    private static bool TryReadQueryValue(
        Uri uri,
        string expectedKey,
        out string value)
    {
        value = string.Empty;
        var query = uri.Query;
        if (query.StartsWith('?'))
        {
            query = query[1..];
        }

        var parts = query.Split('&', StringSplitOptions.None);
        if (parts.Length != 1)
        {
            return false;
        }

        var separator = parts[0].IndexOf('=');
        if (separator <= 0 || separator == parts[0].Length - 1)
        {
            return false;
        }

        var key = Uri.UnescapeDataString(parts[0][..separator]);
        if (!string.Equals(key, expectedKey, StringComparison.Ordinal))
        {
            return false;
        }

        value = parts[0][(separator + 1)..];
        return value.Length > 0;
    }

    private static bool TryParseUuidV7(string value, out Guid id)
    {
        id = default;
        return Guid.TryParseExact(value, "D", out id) &&
            string.Equals(value, id.ToString("D"), StringComparison.Ordinal) &&
            IsUuidV7(id);
    }

    private static Guid ParseUuidV7(string value) =>
        Guid.ParseExact(value, "D");

    private static void RequireUuidV7(Guid id)
    {
        if (!IsUuidV7(id))
        {
            throw new ArgumentException("The logical reminder ID must be a UUID v7.", nameof(id));
        }
    }

    private static bool IsUuidV7(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        return id != Guid.Empty &&
            id.TryWriteBytes(bytes) &&
            id.ToString("D")[14] == '7' &&
            (bytes[8] & 0xC0) == 0x80;
    }
}
