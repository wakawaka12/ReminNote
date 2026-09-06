using ReminNote.Core.Protocol;

#pragma warning disable CA1707 // P3 slice names mirror the frozen plan.
namespace ReminNote.Tests.P3_06;

public sealed class ReminderNotificationActivationTests
{
    [Fact]
    public void SummaryUriRoundTripsBoundedLogicalIdsInCanonicalOrder()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var uri = ReminderNotificationActivation.CreateSummaryUri([second, first]);

        Assert.StartsWith("reminnote://reminders?ids=", uri, StringComparison.Ordinal);
        Assert.True(ReminderNotificationActivation.TryParse(uri, out var activation));
        Assert.NotNull(activation);
        Assert.True(activation!.IsSummary);
        Assert.Equal(
            new[] { first, second }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal),
            activation.LogicalReminderIds);
    }

    [Fact]
    public void SingleUriRoundTripsOneLogicalId()
    {
        var id = Guid.CreateVersion7();
        var uri = ReminderNotificationActivation.CreateSingleUri(id);

        Assert.True(ReminderNotificationActivation.TryParse(uri, out var activation));
        Assert.NotNull(activation);
        Assert.False(activation!.IsSummary);
        Assert.Equal([id], activation.LogicalReminderIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.test/reminders?ids=0191f6a4-3b25-7c12-8d34-56789abcde01")]
    [InlineData("reminnote://reminder/0191f6a4-3b25-6c12-8d34-56789abcde01")]
    [InlineData("reminnote://reminders?ids=0191f6a4-3b25-7c12-8d34-56789abcde01&x=y")]
    [InlineData("reminnote://reminders?ids=0191f6a4-3b25-7c12-8d34-56789abcde01,0191f6a4-3b25-7c12-8d34-56789abcde01")]
    public void InvalidActivationUriIsRejectedWithoutThrowing(string? uri)
    {
        Assert.False(ReminderNotificationActivation.TryParse(uri, out var activation));
        Assert.Null(activation);
    }

    [Fact]
    public void SummaryUriRejectsMoreThanTheToastMemberLimit()
    {
        var ids = Enumerable
            .Range(0, 65)
            .Select(_ => Guid.CreateVersion7())
            .ToArray();

        Assert.Throws<ArgumentException>(
            () => ReminderNotificationActivation.CreateSummaryUri(ids));
    }
}

#pragma warning restore CA1707
