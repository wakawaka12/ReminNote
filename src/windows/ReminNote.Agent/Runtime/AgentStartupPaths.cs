namespace ReminNote.Agent.Runtime;

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
        if (!Directory.Exists(root) ||
            !hasGitMetadata ||
            !File.Exists(Path.Combine(root, "ReminNote.sln")))
        {
            throw new ArgumentException(
                $"Repository root must contain .git and ReminNote.sln: {root}",
                nameof(repositoryRoot));
        }

        return root;
    }
}
