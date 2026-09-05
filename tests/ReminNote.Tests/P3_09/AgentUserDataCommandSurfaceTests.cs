using ReminNote.Agent.Runtime;

namespace ReminNote.Tests.P309;

public sealed class AgentUserDataCommandSurfaceTests
{
    [Fact]
    public void ExportCommandRequiresAnExplicitLocalArtifactAndKeepsRootsExclusive()
    {
        var root = Directory.CreateTempSubdirectory("reminnote-export-command-");
        var artifact = System.IO.Path.Combine(root.FullName, "user-data.json");
        var command = AgentUserDataCommandLine.Parse(
            ["--user-data-export", artifact, "--data-root", root.FullName]);

        Assert.Equal(AgentUserDataCommandKind.Export, command.Kind);
        Assert.Equal(System.IO.Path.GetFullPath(artifact), command.ArtifactPath);
        Assert.False(command.Confirm);
        Assert.False(command.ImportCandidate);
        Assert.Throws<ArgumentException>(() => AgentUserDataCommandLine.Parse(
            ["--user-data-export", artifact, "--data-root", root.FullName, "--repo-root", root.FullName]));
        root.Delete(recursive: true);
    }

    [Fact]
    public void RestoreImportRequiresConfirmationAndCandidateRoot()
    {
        var root = Directory.CreateTempSubdirectory("reminnote-restore-command-");
        var artifact = System.IO.Path.Combine(root.FullName, "user-data.json");
        var candidate = System.IO.Path.Combine(root.FullName, "candidate");

        var command = AgentUserDataCommandLine.Parse(
            [
                "--user-data-restore", artifact,
                "--candidate-root", candidate,
                "--confirm", "--import-candidate"
            ]);
        Assert.Equal(AgentUserDataCommandKind.Restore, command.Kind);
        Assert.True(command.Confirm);
        Assert.True(command.ImportCandidate);

        Assert.Throws<ArgumentException>(() => AgentUserDataCommandLine.Parse(
            ["--user-data-restore", artifact, "--import-candidate", "--candidate-root", candidate]));
        Assert.Throws<ArgumentException>(() => AgentUserDataCommandLine.Parse(
            ["--user-data-restore", artifact]));
        root.Delete(recursive: true);
    }

    [Fact]
    public void CandidateVerifyIsReadOnlyAndPromotionRequiresExplicitConfirmation()
    {
        var root = Directory.CreateTempSubdirectory("reminnote-candidate-command-");
        var candidate = System.IO.Path.Combine(root.FullName, "candidate");
        var dataRoot = System.IO.Path.Combine(root.FullName, "userdata");

        var verify = AgentUserDataCommandLine.Parse(
            ["--user-data-verify", candidate, "--data-root", dataRoot]);
        Assert.Equal(AgentUserDataCommandKind.VerifyCandidate, verify.Kind);
        Assert.False(verify.Confirm);

        Assert.Throws<ArgumentException>(() => AgentUserDataCommandLine.Parse(
            ["--user-data-verify", candidate, "--data-root", dataRoot, "--confirm"]));

        var promote = AgentUserDataCommandLine.Parse(
            ["--user-data-promote", candidate, "--data-root", dataRoot, "--confirm"]);
        Assert.Equal(AgentUserDataCommandKind.PromoteCandidate, promote.Kind);
        Assert.True(promote.Confirm);
        root.Delete(recursive: true);
    }
}
