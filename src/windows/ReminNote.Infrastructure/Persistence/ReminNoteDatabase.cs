using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ReminNote.Infrastructure.Persistence;

/// <summary>
/// Explicit development-database boundary. This helper never discovers or
/// falls back to a production location; callers must provide the repository
/// root and opt into the development path.
/// </summary>
public static class ReminNoteDatabase
{
    public const string DevelopmentRelativePath = ".devdata/reminnote.sqlite";

    public static string GetDevelopmentDatabasePath(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var root = Path.GetFullPath(repositoryRoot);
        return Path.Combine(root, ".devdata", "reminnote.sqlite");
    }

    public static string CreateDevelopmentConnectionString(string repositoryRoot)
    {
        var path = GetDevelopmentDatabasePath(repositoryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        };

        return connectionString.ToString();
    }

    public static ReminNoteDbContext CreateDevelopmentContext(string repositoryRoot)
    {
        var connectionString = CreateDevelopmentConnectionString(repositoryRoot);
        return new ReminNoteDbContext(connectionString);
    }

    public static DbContextOptions<ReminNoteDbContext> CreateDevelopmentOptions(string repositoryRoot)
    {
        var connectionString = CreateDevelopmentConnectionString(repositoryRoot);
        return ReminNoteDbContext.CreateOptions(connectionString);
    }
}
