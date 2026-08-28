using NodaTime;
using ReminNote.Core;
using ReminNote.Core.Tasks;

namespace ReminNote.Tests;

internal static class TestValues
{
    public static readonly LocalDate PlanDate = new(2026, 8, 28);

    public static readonly LocalTime Afternoon = new(14, 0);

    public static readonly Instant CreatedAt = Instant.FromUnixTimeSeconds(1_780_000_000);

    public static readonly Instant ChangedAt = CreatedAt.Plus(Duration.FromMinutes(5));

    public static TaskId TaskId(string value = "0191f6a4-3b25-7c12-8d34-56789abcde01") =>
        ReminNote.Core.Tasks.TaskId.Parse(value);

    public static TaskId AnotherTaskId() =>
        ReminNote.Core.Tasks.TaskId.Parse("0191f6a4-3b25-7c12-8d34-56789abcde02");

    public static void AssertValidationCode(Action action, string expectedCode)
    {
        var exception = Xunit.Assert.Throws<DomainValidationException>(action);
        Xunit.Assert.Contains(exception.Errors, error => error.Code == expectedCode);
    }

    public static void AssertUuidV7(Guid value)
    {
        Xunit.Assert.NotEqual(Guid.Empty, value);

        Span<byte> bytes = stackalloc byte[16];
        Xunit.Assert.True(value.TryWriteBytes(bytes));
        Xunit.Assert.Equal(0x70, bytes[7] & 0xF0);
        Xunit.Assert.Equal(0x80, bytes[8] & 0xC0);
    }
}
