using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;
using NodaTime.Text;

namespace ReminNote.Infrastructure.Persistence.Reminders;

/// <summary>
/// P3 Reminder 表共用的 SQLite 表示。此文件只提供配置构件，不会自行触发
/// EF model discovery；调用方必须显式调用 ApplyReminderConfigurations。
/// </summary>
public static class ReminderPersistenceConventions
{
    public const int MaxTimeZoneIdBytes = 128;

    public const int MaxErrorCodeBytes = 160;

    public const int InstantTextLength = 30;

    public static readonly ValueConverter<Guid, string> GuidConverter = new(
        value => value.ToString("D"),
        value => Guid.Parse(value));

    public static readonly ValueConverter<Guid?, string?> NullableGuidConverter = new(
        value => value.HasValue ? value.Value.ToString("D") : null,
        value => string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value));

    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'");

    public static readonly ValueConverter<Instant, string> InstantConverter = new(
        value => InstantPattern.Format(value),
        value => InstantPattern.Parse(value).GetValueOrThrow());

    public static readonly ValueConverter<Instant?, string?> NullableInstantConverter = new(
        value => value.HasValue ? InstantPattern.Format(value.Value) : null,
        value => string.IsNullOrWhiteSpace(value) ? null : InstantPattern.Parse(value).GetValueOrThrow());

    public static string UuidV7Check(string columnName) =>
        $"{columnName} IS NOT NULL AND length({columnName}) = 36 " +
        $"AND {columnName} = lower({columnName}) " +
        $"AND {columnName} NOT GLOB '*[^0-9a-f-]*' " +
        $"AND substr({columnName}, 9, 1) = '-' " +
        $"AND substr({columnName}, 14, 1) = '-' " +
        $"AND substr({columnName}, 19, 1) = '-' " +
        $"AND substr({columnName}, 24, 1) = '-' " +
        $"AND lower(substr({columnName}, 15, 1)) = '7' " +
        $"AND lower(substr({columnName}, 20, 1)) IN ('8', '9', 'a', 'b')";

    public static string NullableUuidV7Check(string columnName) =>
        $"{columnName} IS NULL OR ({UuidV7Check(columnName)})";

    public static string InstantCheck(string columnName, bool nullable = false) =>
        nullable
            ? $"{columnName} IS NULL OR length({columnName}) = {InstantTextLength}"
            : $"length({columnName}) = {InstantTextLength}";
}
