namespace ReminNote.Core;

/// <summary>
/// Stable, non-localized information about a domain validation failure.
/// User-facing wording belongs to the application/UI localization boundary.
/// </summary>
public readonly record struct DomainValidationError(
    string Code,
    string Message,
    string? FieldName = null);

/// <summary>
/// Raised when a value or aggregate would violate a domain invariant.
/// </summary>
public sealed class DomainValidationException : ArgumentException
{
    public DomainValidationException(DomainValidationError error)
        : this([error])
    {
    }

    public DomainValidationException(IReadOnlyList<DomainValidationError> errors)
        : base(CreateMessage(errors))
    {
        if (errors is null || errors.Count == 0)
        {
            throw new ArgumentException("At least one validation error is required.", nameof(errors));
        }

        Errors = errors.ToArray();
    }

    public IReadOnlyList<DomainValidationError> Errors { get; }

    private static string CreateMessage(IReadOnlyList<DomainValidationError> errors)
    {
        if (errors is null || errors.Count == 0)
        {
            return "The domain value is invalid.";
        }

        return string.Join("; ", errors.Select(error => $"{error.Code}: {error.Message}"));
    }
}
