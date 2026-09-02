using System.Text;
using ReminNote.Core.Reminders.Export;

namespace ReminNote.Tests.P308;

public sealed class ReminderExportContractTests
{
    [Fact]
    public void RoundTripUsesIsolatedArtifactAndPreservesChecksum()
    {
        using var root = new IsolatedTempRoot();
        var original = CreateDocument();
        var bytes = ReminderExportJson.Serialize(original);
        var artifactPath = Path.Combine(root.Path, "reminders.rnexport.json");
        File.WriteAllBytes(artifactPath, bytes);

        var parsed = ReminderExportJson.Parse(File.ReadAllBytes(artifactPath));

        Assert.Equal(ReminderExportContract.Schema, parsed.Schema);
        Assert.Equal(ReminderExportContract.SchemaVersion, parsed.SchemaVersion);
        Assert.NotNull(parsed.Checksum);
        Assert.Equal(parsed.Checksum, ReminderExportJson.ComputeChecksum(parsed));
        Assert.Equal(bytes, ReminderExportJson.Serialize(parsed));
        Assert.Single(parsed.Rules);
        Assert.Single(parsed.Schedules);
        Assert.Single(parsed.Instances);
    }

    [Fact]
    public void CanonicalOutputIsIndependentOfInputArrayOrder()
    {
        var document = CreateDocument();
        var reordered = document with
        {
            Rules = document.Rules.Reverse().ToArray(),
            Schedules = document.Schedules.Reverse().ToArray(),
            Instances = document.Instances.Reverse().ToArray(),
        };

        Assert.Equal(ReminderExportJson.Serialize(document), ReminderExportJson.Serialize(reordered));
    }

    [Fact]
    public void SensitiveFieldsAreRejectedAndNeverEmittedByDefault()
    {
        var json = Encoding.UTF8.GetString(ReminderExportJson.Serialize(CreateDocument()));
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("machineId", json, StringComparison.OrdinalIgnoreCase);

        var withToken = json.Replace(
            "\"checksum\":",
            "\"token\":\"do-not-export\",\"checksum\":",
            StringComparison.Ordinal);
        var exception = Assert.Throws<ReminderExportContractException>(
            () => ReminderExportJson.Parse(Encoding.UTF8.GetBytes(withToken)));

        Assert.Equal(ReminderExportErrorCodes.SensitiveField, exception.Code);
    }

    [Fact]
    public void UnknownFieldsAndSchemaVersionsFailDeterministically()
    {
        var json = Encoding.UTF8.GetString(ReminderExportJson.Serialize(CreateDocument()));
        var withUnknown = json.Replace(
            "\"checksum\":",
            "\"futureField\":true,\"checksum\":",
            StringComparison.Ordinal);
        var unknown = Assert.Throws<ReminderExportContractException>(
            () => ReminderExportJson.Parse(Encoding.UTF8.GetBytes(withUnknown)));
        Assert.Equal(ReminderExportErrorCodes.UnknownField, unknown.Code);

        var withFutureVersion = json.Replace(
            "\"schemaVersion\":1",
            "\"schemaVersion\":99",
            StringComparison.Ordinal);
        var version = Assert.Throws<ReminderExportContractException>(
            () => ReminderExportJson.Parse(Encoding.UTF8.GetBytes(withFutureVersion)));
        Assert.Equal(ReminderExportErrorCodes.UnsupportedSchema, version.Code);
    }

    [Fact]
    public void DuplicateIdsAndInvalidTimesAreReportedByPureValidation()
    {
        var document = CreateDocument();
        var duplicate = document with { Rules = new[] { document.Rules[0], document.Rules[0] } };
        var duplicateResult = ReminderExportValidator.Validate(duplicate);
        Assert.Contains(duplicateResult.Issues, issue => issue.Code == ReminderExportErrorCodes.DuplicateId);

        var invalidTime = document with
        {
            Schedules = new[] { document.Schedules[0] with { TriggerAtUtc = "2026-08-30T08:00:00+08:00" } },
        };
        var invalidTimeResult = ReminderExportValidator.Validate(invalidTime);
        Assert.Contains(invalidTimeResult.Issues, issue => issue.Code == ReminderExportErrorCodes.InvalidTimestamp);
    }

    [Fact]
    public void TerminalScheduleShapeAndInstanceRelationAreEnforced()
    {
        var document = CreateDocument();
        var malformedTerminal = document with
        {
            Schedules = new[]
            {
                document.Schedules[0] with
                {
                    State = ReminderExportValues.ScheduleCancelled,
                    TerminalReason = null,
                    TerminalAtUtc = null,
                },
            },
            Instances = Array.Empty<ReminderInstanceExport>(),
        };

        var terminalResult = ReminderExportValidator.Validate(malformedTerminal);
        Assert.Contains(terminalResult.Issues, issue => issue.Code == ReminderExportErrorCodes.InvalidState);

        var pendingWithInstance = document with
        {
            Schedules = new[]
            {
                document.Schedules[0] with
                {
                    State = ReminderExportValues.SchedulePending,
                    TerminalReason = null,
                    TerminalAtUtc = null,
                },
            },
        };

        var relationResult = ReminderExportValidator.Validate(pendingWithInstance);
        Assert.Contains(relationResult.Issues, issue =>
            issue.Code == ReminderExportErrorCodes.InvalidRelation &&
            issue.Field == "scheduleId");
    }

    [Fact]
    public void DryRunPlanIsCandidateBoundAndDoesNotActivatePendingSchedule()
    {
        var plan = ReminderRestorePlanner.CreatePlan(new ReminderRestorePlanRequest(
            ReminderExportJson.Parse(ReminderExportJson.Serialize(CreatePendingDocument())),
            DryRun: true,
            CandidatePipelineReady: false,
            Confirmed: false));

        Assert.Equal(ReminderRestorePlanStatus.Ready, plan.Status);
        Assert.True(plan.IsDryRun);
        Assert.False(plan.CanApply);
        Assert.True(plan.CandidateOnly);
        Assert.False(plan.WritesActiveDatabase);
        Assert.False(plan.ActivatesPendingSchedules);
        Assert.Equal(ReminderRestoreScheduleStrategy.RebuildFromRules, plan.ScheduleStrategy);
        Assert.Equal(1, plan.PendingSchedulesDeferred);
        Assert.Contains(plan.Actions, action => action.Kind == ReminderRestoreActionKinds.DeferPendingSchedules);
    }

    [Fact]
    public void ControlledRestoreRequiresCandidateAndRejectsActiveDestination()
    {
        var parsed = ReminderExportJson.Parse(ReminderExportJson.Serialize(CreateDocument()));
        var plan = ReminderRestorePlanner.CreatePlan(new ReminderRestorePlanRequest(
            parsed,
            Destination: ReminderRestoreDestination.Active,
            DryRun: false,
            Confirmed: true,
            CandidatePipelineReady: true));

        Assert.Equal(ReminderRestorePlanStatus.Blocked, plan.Status);
        Assert.False(plan.CanApply);
        Assert.False(plan.WritesActiveDatabase);
        Assert.Contains(plan.Issues, issue => issue.Code == ReminderExportErrorCodes.RestoreActiveWriteForbidden);
    }

    [Fact]
    public void RuleRevisionAndInstanceHistoryConflictsNeverOverwrite()
    {
        var parsed = ReminderExportJson.Parse(ReminderExportJson.Serialize(CreateDocument()));
        var rule = parsed.Rules[0];
        var instance = parsed.Instances[0];
        var existingRules = new Dictionary<string, ReminderRuleExport>(StringComparer.Ordinal)
        {
            [rule.Id] = rule with { RuleRevision = rule.RuleRevision + 1 },
        };
        var existingInstances = new Dictionary<string, ReminderInstanceExport>(StringComparer.Ordinal)
        {
            [instance.Id] = instance with { ResolutionAction = ReminderExportValues.ResolutionIgnore },
        };

        var plan = ReminderRestorePlanner.CreatePlan(new ReminderRestorePlanRequest(
            parsed,
            DryRun: false,
            Confirmed: true,
            CandidatePipelineReady: true,
            ExistingRules: existingRules,
            ExistingInstances: existingInstances));

        Assert.Equal(ReminderRestorePlanStatus.Conflict, plan.Status);
        Assert.False(plan.CanApply);
        Assert.Contains(plan.Issues, issue => issue.Code == ReminderExportErrorCodes.RestoreRuleConflict);
        Assert.Contains(plan.Issues, issue => issue.Code == ReminderExportErrorCodes.RestoreInstanceConflict);
        Assert.Contains(plan.Actions, action => action.Kind == ReminderRestoreActionKinds.Conflict);
    }

    private static ReminderExportDocument CreateDocument()
    {
        const string created = "2026-08-30T00:00:00.000000000Z";
        const string read = "2026-08-30T00:01:00.000000000Z";
        const string resolved = "2026-08-30T00:02:00.000000000Z";
        const string ruleId = "019b2b36-4444-7abc-8def-0123456789ab";
        const string targetId = "019b2b36-4444-7abc-8def-0123456789ac";
        const string occurrenceId = "019b2b36-4444-7abc-8def-0123456789ad";
        const string scheduleId = "019b2b36-4444-7abc-8def-0123456789ae";
        const string logicalId = "019b2b36-4444-7abc-8def-0123456789af";
        const string instanceId = "019b2b36-4444-7abc-8def-0123456789b0";

        var rule = new ReminderRuleExport(
            ruleId,
            ReminderExportValues.TargetKindTaskInstance,
            targetId,
            occurrenceId,
            ReminderExportValues.PurposeTaskStart,
            new ReminderTimingExport(ReminderExportValues.TimingAbsoluteUtc, null, null, created),
            ReminderExportValues.PriorityNormal,
            false,
            new ReminderRepeatPolicyExport(false, null, null),
            ReminderExportValues.WakePolicyDefault,
            true,
            1,
            created,
            created);
        var schedule = new ReminderScheduleExport(
            scheduleId,
            ruleId,
            occurrenceId,
            logicalId,
            null,
            ReminderExportValues.ScheduleCauseRule,
            1,
            1,
            created,
            null,
            ReminderExportValues.ScheduleConsumed,
            ReminderExportValues.ScheduleReasonDueConsumed,
            null,
            created,
            read);
        var instance = new ReminderInstanceExport(
            instanceId,
            scheduleId,
            ruleId,
            occurrenceId,
            logicalId,
            1,
            ReminderExportValues.PurposeTaskStart,
            ReminderExportValues.PriorityNormal,
            false,
            read,
            ReminderExportValues.InstanceResolved,
            read,
            resolved,
            ReminderExportValues.ResolutionDone);
        return ReminderExportDocument.Create(new[] { rule }, new[] { schedule }, new[] { instance });
    }

    private static ReminderExportDocument CreatePendingDocument()
    {
        var document = CreateDocument();
        return document with
        {
            Schedules = new[]
            {
                document.Schedules[0] with
                {
                    State = ReminderExportValues.SchedulePending,
                    TerminalReason = null,
                    ReplacementScheduleId = null,
                    TerminalAtUtc = null,
                },
            },
            Instances = Array.Empty<ReminderInstanceExport>(),
        };
    }

    private sealed class IsolatedTempRoot : IDisposable
    {
        public IsolatedTempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "reminnote-p308-" + Guid.CreateVersion7().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
