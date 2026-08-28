using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ReminNote.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by the EF migration tooling. It uses an in-memory
/// SQLite connection only to construct the model and never opens the
/// development database implicitly.
/// </summary>
public sealed class ReminNoteDbContextFactory : IDesignTimeDbContextFactory<ReminNoteDbContext>
{
    public ReminNoteDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ReminNoteDbContext>();
        builder.UseSqlite(
            "Data Source=:memory:",
            sqlite => sqlite.MigrationsAssembly(typeof(ReminNoteDbContext).Assembly.GetName().Name));
        return new ReminNoteDbContext(builder.Options);
    }
}
