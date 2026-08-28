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

    public SqliteTestDatabase()
    {
        Connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        Connection.Open();

        var builder = new DbContextOptionsBuilder<ReminNoteDbContext>();
        builder.UseSqlite(
            Connection,
            sqlite => sqlite.MigrationsAssembly(typeof(ReminNoteDbContext).Assembly.GetName().Name));
        options = builder.Options;
    }

    public SqliteConnection Connection { get; }

    public ReminNoteDbContext CreateContext() => new(options);

    public void Migrate()
    {
        using var context = CreateContext();
        context.Database.Migrate();
    }

    public void Dispose()
    {
        Connection.Dispose();
    }
}
