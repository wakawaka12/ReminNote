using System.Text;
using NodaTime;
using ReminNote.Core.Reminders.Notifications;

#pragma warning disable CA1707 // Slice directory names mirror the frozen plan.
namespace ReminNote.Tests.P3_04;

public sealed class NotificationContractTests
{
    private static readonly Instant Now = Instant.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void StatusAndAttemptRoundTripUseBoundedStableJson()
    {
        var status = new NotificationChannelStatus(
            NotificationChannelId.Toast,
            [
                NotificationCapability.READ_ON_CLOSE,
                NotificationCapability.PRESENT,
                NotificationCapability.REPLACE_LOGICAL_REMINDER,
            ],
            NotificationChannelHealth.DEGRADED,
            Now,
            "toast.permission_pending");
        var statusJson = NotificationContractJson.SerializeChannelStatus(status);
        var statusRoundTrip = NotificationContractJson.DeserializeChannelStatus(statusJson);

        Assert.Equal(status.ChannelId, statusRoundTrip.ChannelId);
        Assert.Equal(status.Capabilities, statusRoundTrip.Capabilities);
        Assert.Equal(status.Health, statusRoundTrip.Health);
        Assert.Equal(status.ObservedAtUtc, statusRoundTrip.ObservedAtUtc);
        Assert.Equal(status.HealthCode, statusRoundTrip.HealthCode);

        var attempt = new NotificationDeliveryAttempt(
            Id("0191f6a4-3b25-7c12-8d34-56789abcde11"),
            Id("0191f6a4-3b25-7c12-8d34-56789abcde01"),
            Id("0191f6a4-3b25-7c12-8d34-56789abcde05"),
            NotificationChannelId.Toast,
            Id("0191f6a4-3b25-7c12-8d34-56789abcde12"),
            Id("0191f6a4-3b25-7c12-8d34-56789abcde13"),
            NotificationPurposeSnapshot.TaskStart,
            NotificationPriority.HIGH,
            true,
            Now.Plus(Duration.FromSeconds(1)),
            NotificationDeliveryOutcome.FAILED,
            "adapter.timeout");
        var attemptJson = NotificationContractJson.SerializeDeliveryAttempt(attempt);
        var attemptRoundTrip = NotificationContractJson.DeserializeDeliveryAttempt(attemptJson);

        Assert.Equal(attempt, attemptRoundTrip);
        var text = Encoding.UTF8.GetString(attemptJson);
        Assert.DoesNotContain("message", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(text, NotificationContractJson.SerializeDeliveryAttemptText(attempt));
    }

    [Fact]
    public void SerializationRejectsUnknownDuplicateBomAndOversizedInput()
    {
        var statusJson = NotificationContractJson.SerializeChannelStatus(
            new NotificationChannelStatus(
                NotificationChannelId.Toast,
                [NotificationCapability.PRESENT],
                NotificationChannelHealth.HEALTHY,
                Now));

        var unknown = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(statusJson).Replace(
                "}", ",\"future\":true}",
                StringComparison.Ordinal));
        Assert.Equal(
            NotificationErrorCodes.SerializationUnknownField,
            Assert.Throws<NotificationContractException>(() =>
                NotificationContractJson.DeserializeChannelStatus(unknown)).Code);

        var duplicate = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(statusJson).Replace(
                "\"health\":\"HEALTHY\"",
                "\"health\":\"HEALTHY\",\"health\":\"HEALTHY\"",
                StringComparison.Ordinal));
        Assert.Equal(
            NotificationErrorCodes.SerializationInvalid,
            Assert.Throws<NotificationContractException>(() =>
                NotificationContractJson.DeserializeChannelStatus(duplicate)).Code);

        var bom = new byte[statusJson.Length + 3];
        bom[0] = 0xEF;
        bom[1] = 0xBB;
        bom[2] = 0xBF;
        statusJson.CopyTo(bom, 3);
        Assert.Equal(
            NotificationErrorCodes.SerializationInvalid,
            Assert.Throws<NotificationContractException>(() =>
                NotificationContractJson.DeserializeChannelStatus(bom)).Code);

        var oversized = Encoding.UTF8.GetBytes("{" + new string('x', 4_100) + "}");
        Assert.Equal(
            NotificationErrorCodes.SerializationTooLarge,
            Assert.Throws<NotificationContractException>(() =>
                NotificationContractJson.DeserializeChannelStatus(oversized)).Code);
    }

    [Fact]
    public void IllegalStatesAndLifecycleBoundariesAreRejectedOrIdempotent()
    {
        Assert.Equal(
            NotificationErrorCodes.UnknownChannel,
            Assert.Throws<NotificationContractException>(() =>
                new NotificationChannelStatus(
                    NotificationChannelId.Parse("FUTURE"),
                    [NotificationCapability.PRESENT],
                    NotificationChannelHealth.HEALTHY,
                    Now)).Code);

        Assert.Equal(
            NotificationErrorCodes.DeliveryOutcomeInvalid,
            Assert.Throws<NotificationContractException>(() =>
                new NotificationChannelDeliveryResponse(
                    NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS)).Code);

        var unread = NotificationLifecycleState.Unread;
        var read = unread.ApplyToastClose(Now);
        Assert.Equal(NotificationReminderLifecycle.READ, read.Lifecycle);
        Assert.Equal(Now, read.ReadAtUtc);
        Assert.Equal(read, read.ApplyToastClose(Now.Plus(Duration.FromMinutes(1))));
        Assert.Equal(NotificationReminderLifecycle.READ, read.ApplyToastClose(Now).Lifecycle);

        var resolved = unread.Resolve(NotificationResolutionAction.DONE, Now);
        Assert.Equal(NotificationReminderLifecycle.RESOLVED, resolved.Lifecycle);
        Assert.Equal(Now, resolved.ReadAtUtc);
        Assert.Equal(resolved, resolved.Resolve(NotificationResolutionAction.DONE, Now.Plus(Duration.FromHours(1))));
        Assert.Equal(
            NotificationErrorCodes.ResolutionConflict,
            Assert.Throws<NotificationContractException>(() =>
                resolved.Resolve(NotificationResolutionAction.IGNORE, Now)).Code);

        Assert.Equal(
            NotificationErrorCodes.InvalidLifecycle,
            Assert.Throws<NotificationContractException>(() =>
                new NotificationLifecycleState(
                    NotificationReminderLifecycle.UNREAD,
                    readAtUtc: Now)).Code);
        Assert.Equal(
            NotificationErrorCodes.InvalidTimestamp,
            Assert.Throws<NotificationContractException>(() =>
                new NotificationLifecycleState(
                    NotificationReminderLifecycle.RESOLVED,
                    readAtUtc: Now,
                    resolvedAtUtc: Now.Minus(Duration.FromSeconds(1)),
                    resolutionAction: NotificationResolutionAction.DONE)).Code);
    }

    [Fact]
    public void LifecycleSerializationPreservesReadAndResolvedFacts()
    {
        var state = NotificationLifecycleState.Unread
            .MarkRead(Now)
            .Resolve(NotificationResolutionAction.SNOOZE, Now.Plus(Duration.FromMinutes(2)));
        var json = NotificationContractJson.SerializeLifecycleState(state);
        var roundTrip = NotificationContractJson.DeserializeLifecycleState(json);

        Assert.Equal(state, roundTrip);
        Assert.Equal(
            NotificationErrorCodes.InvalidLifecycle,
            Assert.Throws<NotificationContractException>(() =>
                NotificationContractJson.DeserializeLifecycleState(
                    "{\"lifecycle\":\"READ\",\"readAtUtc\":null}")).Code);
    }

    private static Guid Id(string text) => Guid.Parse(text);
}

#pragma warning restore CA1707
