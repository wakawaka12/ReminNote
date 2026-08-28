using System.IO;
using ReminNote.Infrastructure.Persistence;

namespace ReminNote.Widget.Startup;

/// <summary>
/// Parses the small set of arguments owned by the Widget process. The explicit
/// repository root keeps the Widget on the development SQLite path selected by
/// the caller and prevents an arbitrary current directory from becoming a
/// silent data location.
/// </summary>
public static class WidgetStartupOptions
{
    public static string ResolveRepositoryRoot(
        IReadOnlyList<string> args,
        string? currentDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var repositoryRoot = currentDirectory ?? Directory.GetCurrentDirectory();
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--repo-root", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException("--repo-root 必须带路径。", nameof(args));
            }

            repositoryRoot = args[index];
        }

        return ReminNoteDatabase.ValidateRepositoryRoot(repositoryRoot);
    }
}
