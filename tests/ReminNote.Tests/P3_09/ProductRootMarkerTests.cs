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
