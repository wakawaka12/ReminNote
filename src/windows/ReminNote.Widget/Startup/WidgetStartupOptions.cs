using System.IO;
using ReminNote.Agent.Runtime;

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

        return AgentStartupPaths.ValidateRepositoryRoot(repositoryRoot);
    }

    public static string? ResolveProfileName(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? profile = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--profile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException("--profile 必须带 profile key。", nameof(args));
            }

            profile = args[index];
        }

        return profile;
    }

    public static string ResolveWidgetInstanceId(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var instanceId = "default";
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--widget-instance", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException("--widget-instance 必须带实例标识。", nameof(args));
            }

            instanceId = args[index];
        }

        if (instanceId.Length > 128 || instanceId.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Widget instance 标识长度或字符非法。", nameof(args));
        }

        return instanceId;
    }
}
