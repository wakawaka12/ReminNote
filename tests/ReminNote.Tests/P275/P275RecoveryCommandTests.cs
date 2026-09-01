using System.Runtime.Versioning;
using ReminNote.Agent.Runtime;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Tests.P275;

[SupportedOSPlatform("windows")]
public sealed class P275RecoveryCommandTests
{
    [Fact]
    public void StatusCommandAcceptsOneExplicitRootAndRejectsRestoreFlags()
    {
        var command = AgentRecoveryCommandLine.Parse(
        [
            "--data-root",
            "C:\\isolated-data",
            "--profile",
            "default",
            "--recovery-status"
        ]);

        Assert.Equal(AgentRecoveryCommandKind.Status, command.Kind);
        Assert.Equal("C:\\isolated-data", command.DataRoot);
        Assert.Equal("default", command.ProfileName);
        Assert.Null(command.BackupArtifactId);
        Assert.False(command.Confirm);

        var exception = Assert.Throws<AgentRecoveryCommandException>(() =>
            AgentRecoveryCommandLine.Parse(
            [
                "--data-root",
                "C:\\isolated-data",
                "--recovery-status",
                "--confirm"
            ]));
        Assert.Equal(P275MigrationFailureCodes.ArgumentsInvalid, exception.FailureCode);
    }

    [Fact]
    public void RestoreCommandCarriesOnlyValidatedArtifactIdAndExplicitConfirmation()
    {
        var command = AgentRecoveryCommandLine.Parse(
        [
            "--repo-root",
            "C:\\isolated-clone",
            "--profile",
            "profile-a",
            "--recovery-restore",
            "migration-empty-target-20260831T000000000Z-00000000-0000-0000-0000-000000000000.sqlite",
            "--confirm"
        ]);

        Assert.Equal(AgentRecoveryCommandKind.Restore, command.Kind);
        Assert.Equal("C:\\isolated-clone", command.RepoRoot);
        Assert.Equal("profile-a", command.ProfileName);
        Assert.Equal(
            "migration-empty-target-20260831T000000000Z-00000000-0000-0000-0000-000000000000.sqlite",
            command.BackupArtifactId);
        Assert.True(command.Confirm);

        foreach (var invalid in new[]
                 {
                     new[] { "--data-root", "C:\\isolated-data", "--recovery-restore", "..\\active.sqlite", "--confirm" },
                     new[] { "--data-root", "C:\\isolated-data", "--recovery-restore", "C:\\outside.sqlite", "--confirm" },
                     new[] { "--data-root", "C:\\isolated-data", "--recovery-restore", "backup.sqlite" },
                     new[] { "--data-root", "C:\\isolated-data", "--recovery-status", "--repo-root", "C:\\other" },
                     new[] { "--data-root", "--recovery-status" }
                 })
        {
            var exception = Assert.Throws<AgentRecoveryCommandException>(() =>
                AgentRecoveryCommandLine.Parse(invalid));
            Assert.Equal(P275MigrationFailureCodes.ArgumentsInvalid, exception.FailureCode);
        }
    }

    [Fact]
    public void RetryCommandRequiresAnExplicitRecoveryRetryFlag()
    {
        var command = AgentRecoveryCommandLine.Parse(
        [
            "--data-root",
            "C:\\isolated-data",
            "--profile",
            "default",
            "--recovery-retry"
        ]);

        Assert.Equal(AgentRecoveryCommandKind.Retry, command.Kind);
        Assert.Null(command.BackupArtifactId);
        Assert.False(command.Confirm);

        foreach (var invalid in new[]
                 {
                     new[] { "--data-root", "C:\\isolated-data", "--recovery-retry", "--confirm" },
                     new[] { "--data-root", "C:\\isolated-data", "--recovery-retry", "--recovery-status" },
                     new[] { "--data-root", "C:\\isolated-data", "--recovery-retry", "--recovery-restore", "backup.sqlite", "--confirm" }
                 })
        {
            var exception = Assert.Throws<AgentRecoveryCommandException>(() =>
                AgentRecoveryCommandLine.Parse(invalid));
            Assert.Equal(P275MigrationFailureCodes.ArgumentsInvalid, exception.FailureCode);
        }
    }

    [Fact]
    public void RestoreRequestUsesProfileLocalBackupPathWithoutOpeningBusinessDatabase()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "reminnote-p275-command-" + Guid.CreateVersion7().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var profile = new AgentRecoveryProfile(
                root,
                "default",
                root,
                Path.Combine(root, "reminnote.sqlite"),
                "profile-scope-placeholder");
            var command = AgentRecoveryCommandLine.Parse(
            [
                "--data-root",
                root,
                "--recovery-restore",
                "known-backup.sqlite",
                "--confirm"
            ]);

            var request = AgentRecoveryCommandAdapter.CreateRestoreRequest(profile, command);

            Assert.Same(profile, request.Profile);
            Assert.Equal("known-backup.sqlite", request.BackupArtifactId);
            Assert.False(File.Exists(profile.DatabasePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
