using System.IO;
using NodaTime;
using ReminNote.Core;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Reminders.Policy;

#pragma warning disable CA1707 // Slice directory and test names mirror the frozen plan.
namespace ReminNote.Tests.P3_07;

public sealed class ReminderPolicyTests
{
    private static readonly Instant QuietHoursSample = Instant.FromUtc(2026, 8, 28, 14, 30);
    private static readonly Instant RecoveryTrigger = Instant.FromUtc(2026, 8, 28, 9, 0);
    private static readonly Instant RecoveryNow = Instant.FromUtc(2026, 8, 28, 10, 0);
    private static readonly DateTimeZone Shanghai = DateTimeZoneProviders.Tzdb["Asia/Shanghai"];

    [Fact]
    public void QuietHoursSupportsCrossMidnightHalfOpenWindowsAndOverrides()
    {
        var policy = new QuietHoursPolicy(
            [new QuietHoursWindow(new LocalTime(22, 0), new LocalTime(7, 0))],
            [
                new QuietHoursOverride(
                    QuietHoursSample.Minus(Duration.FromMinutes(15)),
                    QuietHoursSample.Plus(Duration.FromMinutes(15)),
                    QuietHoursOverrideMode.BYPASS)
            ]);

        Assert.True(policy.IsQuiet(Instant.FromUtc(2026, 8, 28, 14, 0), Shanghai));
        Assert.False(policy.IsQuiet(Instant.FromUtc(2026, 8, 28, 23, 0), Shanghai));
        Assert.False(policy.IsQuiet(QuietHoursSample, Shanghai));
        Assert.True(policy.IsQuiet(QuietHoursSample.Plus(Duration.FromMinutes(31)), Shanghai));

        var forced = new QuietHoursPolicy(
            [],
            [
                new QuietHoursOverride(
                    QuietHoursSample.Minus(Duration.FromMinutes(1)),
                    QuietHoursSample.Plus(Duration.FromMinutes(1)),
                    QuietHoursOverrideMode.FORCE_QUIET)
            ]);
        Assert.True(forced.IsQuiet(QuietHoursSample, DateTimeZone.Utc));
    }

    [Fact]
    public void QuietHoursReportsTheActiveWindowEndForPresentationRetry()
    {
        var policy = new QuietHoursPolicy(
            [new QuietHoursWindow(new LocalTime(22, 0), new LocalTime(7, 0))]);
        var now = Instant.FromUtc(2026, 8, 28, 15, 30); // 23:30 in Shanghai

        Assert.Equal(
            Instant.FromUtc(2026, 8, 28, 23, 0),
            policy.GetActiveQuietHoursEnd(now, Shanghai));
        Assert.Null(policy.GetActiveQuietHoursEnd(
            Instant.FromUtc(2026, 8, 28, 8, 0),
            Shanghai));

        var bypass = new QuietHoursPolicy(
            [new QuietHoursWindow(new LocalTime(22, 0), new LocalTime(7, 0))],
            [new QuietHoursOverride(
                now.Minus(Duration.FromMinutes(1)),
                now.Plus(Duration.FromMinutes(1)),
                QuietHoursOverrideMode.BYPASS)]);
        Assert.Null(bypass.GetActiveQuietHoursEnd(now, Shanghai));
    }

    [Fact]
    public void PresentationSeparatesCoreTriggerChannelStateAndQuietHours()
    {
        var policy = new QuietHoursPolicy([new QuietHoursWindow(new LocalTime(22, 0), new LocalTime(7, 0))]);

        var notTriggered = policy.Evaluate(Input(
            coreTriggered: false,
            channelAvailability: ReminderChannelAvailability.AVAILABLE));
        Assert.Equal(ReminderPresentationDisposition.NOT_TRIGGERED, notTriggered.Disposition);
        Assert.Equal(ReminderPolicyCodes.CoreNotTriggered, notTriggered.ReasonCode);

        var blocked = policy.Evaluate(Input(channelAvailability: ReminderChannelAvailability.BLOCKED));
        Assert.Equal(ReminderPresentationDisposition.BLOCKED, blocked.Disposition);
        Assert.Equal(ReminderPolicyCodes.ChannelBlocked, blocked.ReasonCode);

        var unavailable = policy.Evaluate(Input(channelAvailability: ReminderChannelAvailability.UNAVAILABLE));
        Assert.Equal(ReminderPresentationDisposition.UNAVAILABLE, unavailable.Disposition);
        Assert.Equal(ReminderPolicyCodes.ChannelUnavailable, unavailable.ReasonCode);

        var summary = policy.Evaluate(Input(summaryAllowed: true));
        Assert.Equal(ReminderPresentationDisposition.SUMMARY, summary.Disposition);
        Assert.True(summary.IsQuietHours);
        Assert.True(summary.SummaryEligible);

        var suppressed = policy.Evaluate(Input(summaryAllowed: false));
        Assert.Equal(ReminderPresentationDisposition.SUPPRESSED_QUIET_HOURS, suppressed.Disposition);

        var high = policy.Evaluate(Input(priority: ReminderPriority.HIGH));
        Assert.Equal(ReminderPresentationDisposition.DELIVER, high.Disposition);
        Assert.True(high.IsEscalated);

        var pinnedHigh = policy.Evaluate(Input(
            priority: ReminderPriority.HIGH,
            pinned: true,
            allowHighPriorityDuringQuietHours: false,
            allowPinnedHighPriorityDuringQuietHours: true));
        Assert.Equal(ReminderPresentationDisposition.DELIVER, pinnedHigh.Disposition);
        Assert.Equal(ReminderPolicyCodes.QuietHoursEscalated, pinnedHigh.ReasonCode);
    }

    [Fact]
    public void RepeatEvaluatorBoundsChainAndIsDeterministic()
    {
        var limits = new ReminderRepeatSafetyLimits(maxCount: 4, maxIntervalSeconds: 3_600);
        var policy = new RepeatPolicy(enabled: true, intervalSeconds: 900, maxCount: 4);

        var allowed = ReminderRepeatPolicyEvaluator.Evaluate(policy, nextAttemptOrdinal: 2, limits);
        Assert.True(allowed.Allowed);
        Assert.Equal(4, allowed.EffectiveMaxCount);
        Assert.Equal(ReminderPolicyCodes.RepeatAllowed, allowed.ReasonCode);
        Assert.Equal(allowed, ReminderRepeatPolicyEvaluator.Evaluate(policy, 2, limits));

        var reached = ReminderRepeatPolicyEvaluator.Evaluate(policy, 5, limits);
        Assert.False(reached.Allowed);
        Assert.Equal(ReminderPolicyCodes.RepeatLimitReached, reached.ReasonCode);

        var disabled = ReminderRepeatPolicyEvaluator.Evaluate(RepeatPolicy.Disabled, 2, limits);
        Assert.False(disabled.Allowed);
        Assert.Equal(ReminderPolicyCodes.RepeatDisabled, disabled.ReasonCode);

        var unbounded = ReminderRepeatPolicyEvaluator.Evaluate(
            new RepeatPolicy(enabled: true, intervalSeconds: 900, maxCount: null),
            nextAttemptOrdinal: 2,
            limits);
        Assert.True(unbounded.Allowed);
        Assert.Equal(limits.MaxCount, unbounded.EffectiveMaxCount);

        var intervalOutOfRange = ReminderRepeatPolicyEvaluator.Evaluate(
            new RepeatPolicy(enabled: true, intervalSeconds: 3_601, maxCount: 4),
            nextAttemptOrdinal: 2,
            limits);
        Assert.False(intervalOutOfRange.Allowed);
        Assert.Equal(ReminderPolicyCodes.RepeatIntervalOutOfRange, intervalOutOfRange.ReasonCode);

        var countOutOfRange = ReminderRepeatPolicyEvaluator.Evaluate(
            new RepeatPolicy(enabled: true, intervalSeconds: 900, maxCount: 5),
            nextAttemptOrdinal: 2,
            limits);
        Assert.False(countOutOfRange.Allowed);
        Assert.Equal(ReminderPolicyCodes.RepeatMaxCountOutOfRange, countOutOfRange.ReasonCode);
    }

    [Fact]
    public void WakePolicySeparatesProfileSafeModeAndOsCapability()
    {
        var request = ReminderWakePolicy.Resolve(
            WakePolicy.DEFAULT,
            new WakeProfile(DefaultAllowsWake: true, SafeMode: false),
            new WakeCapability(OsSupportsWake: true, Healthy: true));
        Assert.Equal(WakeCapabilityOutcome.REQUEST, request.Outcome);
        Assert.True(request.MayRequestWake);
        Assert.True(request.ReminderTruthUnchanged);

        var no = ReminderWakePolicy.Resolve(
            WakePolicy.NO,
            new WakeProfile(DefaultAllowsWake: true, SafeMode: false),
            new WakeCapability(OsSupportsWake: true, Healthy: true));
        Assert.Equal(WakeCapabilityOutcome.NOT_REQUESTED, no.Outcome);

        var denied = ReminderWakePolicy.Resolve(
            WakePolicy.DEFAULT,
            new WakeProfile(DefaultAllowsWake: false, SafeMode: false),
            new WakeCapability(OsSupportsWake: true, Healthy: true));
        Assert.Equal(WakeCapabilityOutcome.DENIED, denied.Outcome);
        Assert.Equal(ReminderPolicyCodes.WakeDeniedByProfile, denied.ReasonCode);

        var safeMode = ReminderWakePolicy.Resolve(
            WakePolicy.YES,
            new WakeProfile(DefaultAllowsWake: true, SafeMode: true),
            new WakeCapability(OsSupportsWake: true, Healthy: true));
        Assert.Equal(ReminderPolicyCodes.WakeDeniedSafeMode, safeMode.ReasonCode);

        var unavailable = ReminderWakePolicy.Resolve(
            WakePolicy.YES,
            new WakeProfile(DefaultAllowsWake: false, SafeMode: false),
            new WakeCapability(OsSupportsWake: false, Healthy: false));
        Assert.Equal(WakeCapabilityOutcome.UNAVAILABLE, unavailable.Outcome);
        Assert.Equal(ReminderPolicyCodes.WakeUnavailable, unavailable.ReasonCode);
    }

    [Fact]
    public void NearestWakeupUsesOnlyRequestedCandidatesAndWakesImmediatelyForDue()
    {
        var now = Instant.FromUtc(2026, 8, 28, 12, 0);
        var nearest = now.Plus(Duration.FromMinutes(30));
        var result = ReminderWakePolicy.FindNearestWakeup(
            now,
            [
                new PendingWakeCandidate(Guid.NewGuid(), now.Plus(Duration.FromHours(2)), WakeCapabilityOutcome.DENIED),
                new PendingWakeCandidate(Guid.NewGuid(), nearest, WakeCapabilityOutcome.REQUEST),
                new PendingWakeCandidate(Guid.NewGuid(), now.Minus(Duration.FromMinutes(1)), WakeCapabilityOutcome.REQUEST)
            ]);

        Assert.Equal(now, result);
        Assert.Null(ReminderWakePolicy.FindNearestWakeup(
            now,
            [new PendingWakeCandidate(Guid.NewGuid(), nearest, WakeCapabilityOutcome.UNAVAILABLE)]));
    }

    [Fact]
    public void RecoveryExpiresObsoletePreStartWithoutInventingCoreFact()
    {
        var decision = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(
            ReminderPurpose.TASK_PRE_START,
            taskStartAtUtc: RecoveryNow.Minus(Duration.FromMinutes(1)),
            recoveredAtUtc: RecoveryNow));

        Assert.Equal(ReminderRecoveryAction.EXPIRE, decision.Action);
        Assert.Equal(ReminderPolicyCodes.RecoveryPreStartObsolete, decision.ReasonCode);
        Assert.Equal(ScheduleState.EXPIRED, decision.SuggestedScheduleState);
        Assert.False(decision.RecordCoreInstance);
        Assert.Null(decision.CoreFactAtUtc);
    }

    [Fact]
    public void RecoverySummarizesNormalStartButTriggersHighAndPinnedReminders()
    {
        var normal = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(ReminderPurpose.TASK_START));
        Assert.Equal(ReminderRecoveryAction.SUMMARY, normal.Action);
        Assert.Equal(ReminderPolicyCodes.RecoveryStartSummary, normal.ReasonCode);
        Assert.True(normal.RecordCoreInstance);
        Assert.Equal(RecoveryNow, normal.CoreFactAtUtc);

        var high = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(
            ReminderPurpose.TASK_START,
            priority: ReminderPriority.HIGH));
        Assert.Equal(ReminderRecoveryAction.TRIGGER_ONCE, high.Action);
        Assert.Equal(ReminderPolicyCodes.RecoveryStartTriggered, high.ReasonCode);

        var pinned = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(
            ReminderPurpose.TASK_START,
            pinned: true));
        Assert.Equal(ReminderRecoveryAction.TRIGGER_ONCE, pinned.Action);
        Assert.False(pinned.UsesOriginalTriggerAtUtc);
    }

    [Fact]
    public void RecoveryClassifiesCustomRangeDuplicateCancellationAndAnime()
    {
        var custom = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(
            ReminderPurpose.TASK_CUSTOM,
            recoveredAtUtc: RecoveryTrigger.Plus(Duration.FromHours(24))));
        Assert.Equal(ReminderRecoveryAction.TRIGGER_ONCE, custom.Action);
        Assert.Equal(RecoveryNow.Plus(Duration.FromHours(23)), custom.CoreFactAtUtc);

        var expired = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(
            ReminderPurpose.TASK_CUSTOM,
            recoveredAtUtc: RecoveryTrigger.Plus(Duration.FromHours(25))));
        Assert.Equal(ReminderRecoveryAction.EXPIRE, expired.Action);
        Assert.Equal(ReminderPolicyCodes.RecoveryCustomExpired, expired.ReasonCode);

        var rangeEnd = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(ReminderPurpose.TASK_RANGE_END));
        Assert.Equal(ReminderRecoveryAction.TRIGGER_ONCE, rangeEnd.Action);
        Assert.Equal(ReminderPolicyCodes.RecoveryRangeEndTriggered, rangeEnd.ReasonCode);

        var duplicate = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(
            ReminderPurpose.TASK_START,
            coreInstanceExists: true));
        Assert.Equal(ReminderRecoveryAction.SKIP, duplicate.Action);
        Assert.Equal(ScheduleState.CONSUMED, duplicate.SuggestedScheduleState);

        var cancelled = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(
            ReminderPurpose.TASK_START,
            occurrenceCancelled: true));
        Assert.Equal(ReminderRecoveryAction.SKIP, cancelled.Action);
        Assert.Equal(ScheduleState.CANCELLED, cancelled.SuggestedScheduleState);

        var anime = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(ReminderPurpose.ANIME_CUSTOM));
        Assert.Equal(ReminderRecoveryAction.REJECT, anime.Action);
        Assert.Equal(ReminderPolicyCodes.RecoveryAnimeNotActivated, anime.ReasonCode);
    }

    [Fact]
    public void RecoveryDefersFutureAndRejectsMissingPreStartContext()
    {
        var future = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(
            ReminderPurpose.TASK_START,
            recoveredAtUtc: RecoveryTrigger.Minus(Duration.FromMinutes(1))));
        Assert.Equal(ReminderRecoveryAction.DEFER, future.Action);
        Assert.Equal(ScheduleState.PENDING, future.SuggestedScheduleState);
        Assert.False(future.RecordCoreInstance);

        var missingTaskStart = ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput(
            ReminderPurpose.TASK_PRE_START,
            taskStartAtUtc: null));
        Assert.Equal(ReminderRecoveryAction.REJECT, missingTaskStart.Action);
        Assert.Equal(ReminderPolicyCodes.RecoveryPreStartRequiresTaskStart, missingTaskStart.ReasonCode);
    }

    [Fact]
    public void InvalidPolicyInputsExposeStableValidationCodes()
    {
        AssertCode(
            ConstructEmptyQuietHoursWindow,
            "reminder.quiet_hours.window.empty");
        AssertCode(
            ConstructEmptyQuietHoursOverride,
            "reminder.quiet_hours.override.range_invalid");
        AssertCode(
            () => ReminderWakePolicy.Resolve(
                (WakePolicy)999,
                new WakeProfile(true, false),
                new WakeCapability(true, true)),
            "reminder.wake.policy.invalid");
        AssertCode(
            () => ReminderRecoveryPolicy.Default.Evaluate(RecoveryInput((ReminderPurpose)999)),
            "reminder.purpose.invalid");
    }

    [Fact]
    public void VerificationUsesAnIsolatedTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ReminNote-P3-07-{Guid.NewGuid():N}");
        var protectedDatabase = Path.GetFullPath(@"D:\Anime\.devdata\reminnote.sqlite");
        var normalizedRoot = Path.GetFullPath(root);

        Assert.NotEqual(protectedDatabase, normalizedRoot, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(root);
        try
        {
            var marker = Path.Combine(root, "policy-test.marker");
            File.WriteAllText(marker, "P3-07 isolated policy verification");
            Assert.Equal("P3-07 isolated policy verification", File.ReadAllText(marker));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        Assert.False(Directory.Exists(root));
    }

    private static ReminderPresentationInput Input(
        bool coreTriggered = true,
        ReminderChannelAvailability channelAvailability = ReminderChannelAvailability.AVAILABLE,
        bool summaryAllowed = true,
        ReminderPriority priority = ReminderPriority.NORMAL,
        bool pinned = false,
        bool allowHighPriorityDuringQuietHours = true,
        bool allowPinnedHighPriorityDuringQuietHours = true) =>
        new(
            QuietHoursSample,
            Shanghai,
            priority,
            pinned,
            coreTriggered,
            channelAvailability,
            summaryAllowed,
            allowHighPriorityDuringQuietHours,
            allowPinnedHighPriorityDuringQuietHours);

    private static ReminderRecoveryInput RecoveryInput(
        ReminderPurpose purpose,
        Instant? triggerAtUtc = null,
        Instant? recoveredAtUtc = null,
        Instant? taskStartAtUtc = null,
        bool occurrenceCancelled = false,
        bool taskResultRecorded = false,
        bool coreInstanceExists = false,
        bool customReminderMeaningful = true,
        ReminderPriority priority = ReminderPriority.NORMAL,
        bool pinned = false) =>
        new(
            purpose,
            triggerAtUtc ?? RecoveryTrigger,
            recoveredAtUtc ?? RecoveryNow,
            taskStartAtUtc,
            occurrenceCancelled,
            taskResultRecorded,
            coreInstanceExists,
            customReminderMeaningful,
            priority,
            pinned);

    private static void AssertCode(Action action, string expectedCode)
    {
        var exception = Assert.Throws<DomainValidationException>(action);
        Assert.Contains(exception.Errors, error => error.Code == expectedCode);
    }

    private static void ConstructEmptyQuietHoursWindow()
    {
        _ = new QuietHoursWindow(new LocalTime(9, 0), new LocalTime(9, 0));
    }

    private static void ConstructEmptyQuietHoursOverride()
    {
        _ = new QuietHoursOverride(
            QuietHoursSample,
            QuietHoursSample,
            QuietHoursOverrideMode.BYPASS);
    }
}

#pragma warning restore CA1707
