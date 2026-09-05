using ReminNote.Core.Runtime;

namespace ReminNote.Agent.Runtime;

/// <summary>
/// Resolved startup root selection. Development checkouts use the explicit
/// repository <c>.devdata</c> directory; packaged/portable launches use the
/// stable package-adjacent <c>UserData</c> directory or an explicitly selected
/// data root. The selection itself is path-only and never opens a database.
/// </summary>
public sealed record AgentStartupPathSelection(
    string DataRoot,
    string? RepositoryRoot,
    bool IsPortable);

/// <summary>
/// Validates the explicit repository root accepted by the post-cutover hosts.
/// This is path policy only; it never opens the business database.
/// </summary>
public static class AgentStartupPaths
{
    public static string ValidateRepositoryRoot(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var root = Path.GetFullPath(repositoryRoot);
        var hasGitMetadata = Directory.Exists(Path.Combine(root, ".git")) ||
            File.Exists(Path.Combine(root, ".git"));
        var hasPackageMarker = ProductRootMarker.IsValid(root);
        if (!Directory.Exists(root) ||
            (!hasPackageMarker &&
             (!hasGitMetadata ||
              !File.Exists(Path.Combine(root, "ReminNote.sln")))))
        {
            throw new ArgumentException(
                $"Root must contain .git/ReminNote.sln or a valid {ProductRootMarker.FileName}: {root}",
                nameof(repositoryRoot));
        }

        return root;
    }

    /// <summary>
    /// Resolves the mutually exclusive <c>--data-root</c>/<c>--repo-root</c>
    /// startup arguments. With no explicit argument, a valid package marker
    /// selects <c>UserData</c>; a development checkout selects <c>.devdata</c>.
    /// The returned data directory may not exist yet; the caller owns its
    /// bounded creation before constructing the transport profile.
    /// </summary>
    public static AgentStartupPathSelection ResolveDataRoot(
        IReadOnlyList<string> args,
        string? currentDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? dataRoot = null;
        string? repositoryRoot = null;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--data-root":
                    if (dataRoot is not null || ++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
                    {
                        throw new ArgumentException("--data-root 必须且只能带一个路径。", nameof(args));
                    }

                    dataRoot = ValidateLocalPath(args[index], "--data-root");
                    break;
                case "--repo-root":
                    if (repositoryRoot is not null || ++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
                    {
                        throw new ArgumentException("--repo-root 必须且只能带一个路径。", nameof(args));
                    }

                    repositoryRoot = ValidateRepositoryRoot(args[index]);
                    break;
            }
        }

        if (dataRoot is not null && repositoryRoot is not null)
        {
            throw new ArgumentException("--data-root 与 --repo-root 不能同时使用。", nameof(args));
        }

        if (dataRoot is not null)
        {
            return new AgentStartupPathSelection(dataRoot, null, IsPortable: true);
        }

        if (repositoryRoot is not null)
        {
            return new AgentStartupPathSelection(
                Path.GetFullPath(Path.Combine(repositoryRoot, ".devdata")),
                repositoryRoot,
                IsPortable: false);
        }

        var launchRoot = Path.GetFullPath(currentDirectory ?? Directory.GetCurrentDirectory());
        if (ProductRootMarker.IsValid(launchRoot))
        {
            return new AgentStartupPathSelection(
                Path.GetFullPath(Path.Combine(launchRoot, "UserData")),
                launchRoot,
                IsPortable: true);
        }

        var developmentRoot = ValidateRepositoryRoot(launchRoot);
        return new AgentStartupPathSelection(
            Path.GetFullPath(Path.Combine(developmentRoot, ".devdata")),
            developmentRoot,
            IsPortable: false);
    }

    private static string ValidateLocalPath(string path, string option)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} 必须是绝对本地路径。", option);
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (root is not null && fullPath.Length > root.Length)
            {
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            throw new ArgumentException($"{option} 路径无法规范化。", option, exception);
        }
    }
}
