namespace ReminNote.Core.Reminders.Export;

public static class ReminderExportErrorCodes
{
    public const string InvalidJson = "export.invalid_json";
    public const string MissingField = "export.field.missing";
    public const string FieldType = "export.field.type";
    public const string UnknownField = "export.field.unknown";
    public const string SensitiveField = "export.field.sensitive";
    public const string UnsupportedSchema = "export.schema.unsupported";
    public const string InvalidId = "export.id.invalid";
    public const string DuplicateId = "export.id.duplicate";
    public const string InvalidEnum = "export.enum.invalid";
    public const string InvalidTimestamp = "export.time.invalid";
    public const string InvalidTiming = "export.timing.invalid";
    public const string InvalidRevision = "export.revision.invalid";
    public const string InvalidRelation = "export.relation.invalid";
    public const string InvalidState = "export.state.invalid";
    public const string InvalidHistory = "export.history.immutable";
    public const string ChecksumMissing = "export.checksum.missing";
    public const string ChecksumMismatch = "export.checksum.mismatch";
    public const string RestoreCandidateRequired = "restore.candidate.required";
    public const string RestoreConfirmationRequired = "restore.confirmation.required";
    public const string RestoreActiveWriteForbidden = "restore.active.write_forbidden";
    public const string RestoreRuleConflict = "restore.conflict.rule_revision";
    public const string RestoreInstanceConflict = "restore.conflict.instance_immutable";
    public const string RestoreGlobalRevisionConflict = "restore.conflict.global_revision";
}

public sealed record ReminderExportIssue(
    string Code,
    string Message,
    string? EntityKind = null,
    string? EntityId = null,
    string? Field = null);

public sealed record ReminderExportValidationResult(IReadOnlyList<ReminderExportIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed class ReminderExportContractException : FormatException
{
    public ReminderExportContractException(
        string code,
        string message,
        string? field = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Field = field;
    }

    public string Code { get; }

    public string? Field { get; }
}
