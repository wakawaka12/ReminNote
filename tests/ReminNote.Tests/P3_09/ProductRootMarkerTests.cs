using ReminNote.Agent.Runtime;
using ReminNote.Core.Runtime;
using ReminNote.Infrastructure.Persistence;

namespace ReminNote.Tests.P309;

public sealed class ProductRootMarkerTests
{
    [Fact]
    public void ValidPackageMarkerAllowsPackagedRootValidation()
    {
        using var root = TemporaryRoot.Create();
        root.WriteMarker("0.3.0-alpha.1");

        Assert.True(ProductRootMarker.IsValid(root.Path));
        Assert.Equal(root.Path, AgentStartupPaths.ValidateRepositoryRoot(root.Path));
        Assert.Equal(root.Path, ReminNoteDatabase.ValidateRepositoryRoot(root.Path));
    }

    [Fact]
    public void MalformedOrWrongVersionMarkerDoesNotBypassRepositoryBoundary()
    {
        using var root = TemporaryRoot.Create();
        File.WriteAllText(
            Path.Combine(root.Path, ProductRootMarker.FileName),
            "{\"product\":\"OtherApp\",\"formatVersion\":1}");

        Assert.False(ProductRootMarker.IsValid(root.Path));
        Assert.Throws<ArgumentException>(() => AgentStartupPaths.ValidateRepositoryRoot(root.Path));
        Assert.Throws<ArgumentException>(() => ReminNoteDatabase.ValidateRepositoryRoot(root.Path));
    }

    [Fact]
    public void PortableMarkerAndExplicitDataRootUseStableUserDataBoundary()
    {
        using var package = TemporaryRoot.Create();
        package.WriteMarker("0.3.0-alpha.2");

        var implicitSelection = AgentStartupPaths.ResolveDataRoot(
            Array.Empty<string>(),
            package.Path);
        Assert.True(implicitSelection.IsPortable);
        Assert.Equal(
            System.IO.Path.Combine(package.Path, "UserData"),
            implicitSelection.DataRoot);

        using var selected = TemporaryRoot.Create();
        var explicitSelection = AgentStartupPaths.ResolveDataRoot(
            ["--data-root", selected.Path],
            package.Path);
        Assert.True(explicitSelection.IsPortable);
        Assert.Equal(selected.Path, explicitSelection.DataRoot);
        Assert.Null(explicitSelection.RepositoryRoot);
    }

    [Fact]
    public void DevelopmentRepositoryAndPortableArgumentsCannotBeMixed()
    {
        using var repository = TemporaryRoot.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(repository.Path, ".git"));
        File.WriteAllText(System.IO.Path.Combine(repository.Path, "ReminNote.sln"), "placeholder");

        var developmentSelection = AgentStartupPaths.ResolveDataRoot(
            ["--repo-root", repository.Path]);
        Assert.False(developmentSelection.IsPortable);
        Assert.Equal(
            System.IO.Path.Combine(repository.Path, ".devdata"),
            developmentSelection.DataRoot);
        Assert.Equal(repository.Path, developmentSelection.RepositoryRoot);

        Assert.Throws<ArgumentException>(() => AgentStartupPaths.ResolveDataRoot(
            ["--repo-root", repository.Path, "--data-root", System.IO.Path.Combine(repository.Path, "UserData")]));
    }

    private sealed class TemporaryRoot : IDisposable
    {
        private readonly DirectoryInfo directory =
            Directory.CreateTempSubdirectory("reminnote-package-root-");

        public string Path => directory.FullName;

        public static TemporaryRoot Create() => new();

        public void WriteMarker(string appVersion)
        {
            File.WriteAllText(
                System.IO.Path.Combine(Path, ProductRootMarker.FileName),
                $"{{\"product\":\"ReminNote\",\"formatVersion\":{ProductRootMarker.FormatVersion},\"appVersion\":\"{appVersion}\"}}");
        }

        public void Dispose()
        {
            if (directory.Exists)
            {
                directory.Delete(recursive: true);
            }
        }
    }
}
