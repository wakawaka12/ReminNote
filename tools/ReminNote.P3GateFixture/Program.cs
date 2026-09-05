using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.P3GateFixture;

internal static class Program
{
    private const string P25Migration = "20260831090000_P25StorageConsistency";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var invocation = Parse(args);
            Directory.CreateDirectory(invocation.DataRoot);
            var paths = new P275ProfilePaths(invocation.DataRoot, invocation.ProfileName);
            paths.EnsureRuntimeDirectories();

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = paths.ActiveDatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true
            }.ToString();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var context = new ReminNoteDbContext(
                ReminNoteDbContext.CreateOptions(connection));
            await context.Database.MigrateAsync(P25Migration).ConfigureAwait(false);
            Console.WriteLine($"fixtureDatabase={paths.ActiveDatabasePath}");
            Console.WriteLine($"fixtureSchema={P25Migration}");
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            IOException or
            SqliteException)
        {
            Console.Error.WriteLine($"fixtureFailure={exception.GetType().Name}");
            return 1;
        }
    }

    private static Invocation Parse(string[] args)
    {
        string? dataRoot = null;
        var profileName = "p3-09-gate";
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--data-root":
                    dataRoot = ReadValue(args, ref index, "--data-root");
                    break;
                case "--profile":
                    profileName = ReadValue(args, ref index, "--profile");
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[index]}");
            }
        }

        if (string.IsNullOrWhiteSpace(dataRoot) ||
            !Path.IsPathFullyQualified(dataRoot) ||
            dataRoot.StartsWith("\\\\", StringComparison.Ordinal) ||
            dataRoot.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("--data-root 必须是绝对本地目录。");
        }

        return new(Path.GetFullPath(dataRoot), profileName);
    }

    private static string ReadValue(
        string[] args,
        ref int index,
        string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} 必须带值。");
        }

        return args[index];
    }

    private sealed record Invocation(string DataRoot, string ProfileName);
}
