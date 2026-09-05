using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReminNote.Infrastructure.Persistence;

namespace ReminNote.Tests;

/// <summary>
/// Owns one private in-memory SQLite connection per test. Keeping the
/// connection open lets multiple DbContext instances observe the same schema
/// without creating a file in the repository or in the development database.
/// </summary>
internal sealed class SqliteTestDatabase : IDisposable
{
    private readonly DbContextOptions<ReminNoteDbContext> options;
    private readonly string? temporaryRoot;

    public SqliteTestDatabase(bool fileBacked = false)
    {
        if (fileBacked)
        {
            temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                $"reminnote-sqlite-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryRoot);
            DatabasePath = Path.Combine(temporaryRoot, "reminnote.sqlite");
            Connection = new SqliteConnection(
                $"Data Source={DatabasePath};Foreign Keys=True");
        }
        else
        {
            Connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        }

        Connection.Open();

        if (fileBacked)
        {
            // Production-shaped contexts opt into the formal P3 model and
            // migration assembly; the legacy in-memory fixture remains P2-only.
            options = ReminNoteDbContext.CreateOptions(Connection);
        }
        else
        {
            var builder = new DbContextOptionsBuilder<ReminNoteDbContext>();
            builder.UseSqlite(
                Connection,
                sqlite => sqlite.MigrationsAssembly(typeof(ReminNoteDbContext).Assembly.GetName().Name));
            options = builder.Options;
        }
    }

    public SqliteConnection Connection { get; }

    public string? DatabasePath { get; }

    public ReminNoteDbContext CreateContext() => new(options);

    public void Migrate()
    {
        using var context = CreateContext();
        context.Database.Migrate();
    }

    public void MigrateToInitial()
    {
        using var context = CreateContext();
        context.Database.Migrate("20260828025922_InitialTaskSchema");
    }

    public void Dispose()
    {
        Connection.Dispose();
        if (temporaryRoot is not null)
        {
            try
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for Windows SQLite sidecar handles.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup; the test data remains outside the repository.
            }
        }
    }
}
