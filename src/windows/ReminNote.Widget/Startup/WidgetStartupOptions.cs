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

        string? repositoryRoot = null;
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

        if (repositoryRoot is null)
        {
            throw new ArgumentException("Widget 正式启动必须提供 --repo-root 路径。", nameof(args));
        }

        return ReminNoteDatabase.ValidateRepositoryRoot(repositoryRoot);
    }
}
