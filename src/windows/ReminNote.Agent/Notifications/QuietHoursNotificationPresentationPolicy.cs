using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Reminders.Policy;

namespace ReminNote.Agent.Notifications;

/// <summary>
/// Persisted presentation settings. They live beside the profile database,
/// not inside the reminder tables, so changing notification policy can never
/// rewrite Task, Rule, Schedule or Instance facts.
/// </summary>
public sealed record NotificationQuietHoursWindowSetting(
    string Start,
    string End);

public sealed record NotificationQuietHoursOverrideSetting(
    string StartsAtUtc,
    string EndsAtUtc,
    string Mode);

public sealed record NotificationPresentationPolicySettings(
    string? TimeZoneId = null,
    IReadOnlyList<NotificationQuietHoursWindowSetting>? QuietHours = null,
    IReadOnlyList<NotificationQuietHoursOverrideSetting>? Overrides = null,
    bool SummaryAllowed = true,
    bool AllowHighPriorityDuringQuietHours = true,
    bool AllowPinnedHighPriorityDuringQuietHours = true)
{
    public static NotificationPresentationPolicySettings Default { get; } = new();
}

/// <summary>
/// Small, atomic JSON store for the user's Quiet Hours policy. The Agent
/// reads this once at startup; a future settings command can replace the file
/// atomically and restart/reload the Agent without touching Active SQLite.
/// </summary>
public static class NotificationPresentationPolicyStore
{
    public const string FileName = "notification-policy.json";
    private const int MaxWindows = 32;
    private const int MaxOverrides = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static NotificationPresentationPolicySettings Load(string dataRoot)
    {
        var path = GetPath(dataRoot);
        if (!File.Exists(path))
        {
            return NotificationPresentationPolicySettings.Default;
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<NotificationPresentationPolicySettings>(json, JsonOptions)
                ?? NotificationPresentationPolicySettings.Default;
            Validate(settings);
            return settings;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            FormatException or
            ArgumentException or
            DomainValidationException)
        {
            // A malformed preference must not prevent the Agent from starting
            // or turn a core reminder into a lost fact. Empty policy is the
            // fail-open presentation default; the host can log the path.
            return NotificationPresentationPolicySettings.Default;
        }
    }

    public static void Save(
        string dataRoot,
        NotificationPresentationPolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        var path = GetPath(dataRoot);
        Directory.CreateDirectory(dataRoot);
        var temporary = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(temporary, json, new System.Text.UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
                // The replacement already succeeded; cleanup is best effort.
            }
        }
    }

    public static string GetPath(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException("Notification policy data root must be absolute.", nameof(dataRoot));
        }

        var root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, FileName));
        if (!path.StartsWith(string.Concat(root, Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Notification policy path escaped its data root.", nameof(dataRoot));
        }

        return path;
    }

    public static QuietHoursPolicy ToDomainPolicy(
        NotificationPresentationPolicySettings settings,
        out DateTimeZone timeZone)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        var windows = (settings.QuietHours ?? Array.Empty<NotificationQuietHoursWindowSetting>())
            .Select(window => new QuietHoursWindow(
                ParseLocalTime(window.Start, nameof(window.Start)),
                ParseLocalTime(window.End, nameof(window.End))))
            .ToArray();
        var overrides = (settings.Overrides ?? Array.Empty<NotificationQuietHoursOverrideSetting>())
            .Select(item => new QuietHoursOverride(
                ParseInstant(item.StartsAtUtc, nameof(item.StartsAtUtc)),
                ParseInstant(item.EndsAtUtc, nameof(item.EndsAtUtc)),
                ParseOverrideMode(item.Mode)))
            .ToArray();

        timeZone = ResolveTimeZone(settings.TimeZoneId);
        return new QuietHoursPolicy(windows, overrides);
    }

    private static void Validate(NotificationPresentationPolicySettings settings)
    {
        if (settings.QuietHours is { Count: > MaxWindows } ||
            settings.Overrides is { Count: > MaxOverrides })
        {
            throw new ArgumentException("Quiet Hours settings exceed the bounded policy limits.", nameof(settings));
        }

        foreach (var window in settings.QuietHours ?? Array.Empty<NotificationQuietHoursWindowSetting>())
        {
            ArgumentNullException.ThrowIfNull(window);
            _ = ParseLocalTime(window.Start, nameof(window.Start));
            _ = ParseLocalTime(window.End, nameof(window.End));
            _ = new QuietHoursWindow(
                ParseLocalTime(window.Start, nameof(window.Start)),
                ParseLocalTime(window.End, nameof(window.End)));
        }

        foreach (var item in settings.Overrides ?? Array.Empty<NotificationQuietHoursOverrideSetting>())
        {
            ArgumentNullException.ThrowIfNull(item);
            _ = new QuietHoursOverride(
                ParseInstant(item.StartsAtUtc, nameof(item.StartsAtUtc)),
                ParseInstant(item.EndsAtUtc, nameof(item.EndsAtUtc)),
                ParseOverrideMode(item.Mode));
        }

        if (settings.TimeZoneId is { Length: > 128 })
        {
            throw new ArgumentException("Quiet Hours timezone ID is too long.", nameof(settings));
        }

        _ = ResolveTimeZone(settings.TimeZoneId);
    }

    private static LocalTime ParseLocalTime(string value, string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        foreach (var pattern in new[] { "HH:mm", "HH:mm:ss" })
        {
            var parse = LocalTimePattern.CreateWithInvariantCulture(pattern).Parse(value);
            if (parse.Success)
            {
                return parse.Value;
            }
        }

        throw new FormatException($"{fieldName} must be HH:mm or HH:mm:ss.");
    }

    private static Instant ParseInstant(string value, string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var full = InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss.FFFFFFFFF'Z'");
        var whole = InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss'Z'");
        var parse = full.Parse(value);
        if (parse.Success && string.Equals(full.Format(parse.Value), value, StringComparison.Ordinal))
        {
            return parse.Value;
        }

        parse = whole.Parse(value);
        if (parse.Success && string.Equals(whole.Format(parse.Value), value, StringComparison.Ordinal))
        {
            return parse.Value;
        }

        throw new FormatException($"{fieldName} must be a canonical UTC instant.");
    }

    private static QuietHoursOverrideMode ParseOverrideMode(string value) =>
        Enum.TryParse<QuietHoursOverrideMode>(value, false, out var mode) && Enum.IsDefined(mode)
            ? mode
            : throw new FormatException("Quiet Hours override mode is invalid.");

    private static DateTimeZone ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return DateTimeZoneProviders.Tzdb.GetSystemDefault();
        }

        return DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId)
            ?? throw new FormatException("Quiet Hours timezone ID is not a TZDB zone.");
    }
}

/// <summary>
/// Bridges the domain Quiet Hours policy to the notification dispatcher. It
/// uses the presentation evaluation instant (not the historical scheduled
/// instant), retains the core-trigger fact, and records suppression as a
/// durable channel attempt with a retry hint at the active window's end.
/// </summary>
public sealed class QuietHoursNotificationPresentationPolicy : INotificationPresentationPolicy
{
    private readonly QuietHoursPolicy quietHours;
    private readonly DateTimeZone timeZone;
    private readonly bool summaryAllowed;
    private readonly bool allowHighPriorityDuringQuietHours;
    private readonly bool allowPinnedHighPriorityDuringQuietHours;

    public QuietHoursNotificationPresentationPolicy()
        : this(new QuietHoursPolicy(), DateTimeZoneProviders.Tzdb.GetSystemDefault())
    {
    }

    public QuietHoursNotificationPresentationPolicy(
        QuietHoursPolicy quietHours,
        DateTimeZone timeZone,
        bool summaryAllowed = true,
        bool allowHighPriorityDuringQuietHours = true,
        bool allowPinnedHighPriorityDuringQuietHours = true)
    {
        this.quietHours = quietHours ?? throw new ArgumentNullException(nameof(quietHours));
        this.timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        this.summaryAllowed = summaryAllowed;
        this.allowHighPriorityDuringQuietHours = allowHighPriorityDuringQuietHours;
        this.allowPinnedHighPriorityDuringQuietHours = allowPinnedHighPriorityDuringQuietHours;
    }

    public static QuietHoursNotificationPresentationPolicy FromSettings(
        NotificationPresentationPolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var policy = NotificationPresentationPolicyStore.ToDomainPolicy(settings, out var timeZone);
        return new QuietHoursNotificationPresentationPolicy(
            policy,
            timeZone,
            settings.SummaryAllowed,
            settings.AllowHighPriorityDuringQuietHours,
            settings.AllowPinnedHighPriorityDuringQuietHours);
    }

    public NotificationPresentationDecision Evaluate(NotificationPresentationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var availability = context.ChannelStatus.Health switch
        {
            NotificationChannelHealth.BLOCKED => ReminderChannelAvailability.BLOCKED,
            NotificationChannelHealth.UNAVAILABLE or NotificationChannelHealth.UNKNOWN => ReminderChannelAvailability.UNAVAILABLE,
            _ => ReminderChannelAvailability.AVAILABLE,
        };
        var trigger = context.CoreTrigger;
        var priority = trigger.PrioritySnapshot switch
        {
            NotificationPriority.LOW => ReminderPriority.LOW,
            NotificationPriority.HIGH => ReminderPriority.HIGH,
            _ => ReminderPriority.NORMAL,
        };
        var decision = quietHours.Evaluate(new ReminderPresentationInput(
            context.EvaluatedAtUtc,
            timeZone,
            priority,
            trigger.PinnedSnapshot,
            coreTriggered: true,
            availability,
            summaryAllowed,
            allowHighPriorityDuringQuietHours,
            allowPinnedHighPriorityDuringQuietHours));

        return decision.Disposition switch
        {
            ReminderPresentationDisposition.DELIVER => NotificationPresentationDecision.Present,
            ReminderPresentationDisposition.SUMMARY or ReminderPresentationDisposition.SUPPRESSED_QUIET_HOURS =>
                NotificationPresentationDecision.SuppressedQuietHours(
                    decision.ReasonCode == ReminderPolicyCodes.QuietHoursSummary
                        ? NotificationErrorCodes.PolicySuppressedQuietHours
                        : decision.ReasonCode,
                    quietHours.GetActiveQuietHoursEnd(context.EvaluatedAtUtc, timeZone)),
            ReminderPresentationDisposition.BLOCKED => NotificationPresentationDecision.Present,
            ReminderPresentationDisposition.UNAVAILABLE => NotificationPresentationDecision.Present,
            _ => NotificationPresentationDecision.Present,
        };
    }
}
