using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Application;
using ReminNote.Infrastructure.Persistence;


namespace ReminNote.P1ManualHarness;

internal static class Program
{
    private static readonly LocalDatePattern DatePattern =
        LocalDatePattern.CreateWithInvariantCulture("uuuu-MM-dd");

    private static readonly LocalTimePattern TimePattern =
        LocalTimePattern.CreateWithInvariantCulture("HH:mm");

    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture(
            "uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'");

    public static async System.Threading.Tasks.Task<int> Main(string[] args)
    {
        try
        {
            var invocation = ParseInvocation(args);
            if (invocation.Command is null)
            {
                PrintUsage();
                return 0;
            }

            return invocation.Command.ToLowerInvariant() switch
            {
                "schema" => await ShowSchemaAsync(invocation.RepositoryRoot).ConfigureAwait(false),
                "create" => await CreateAsync(invocation).ConfigureAwait(false),
                "list" => await ListAsync(invocation).ConfigureAwait(false),
                "update" => await UpdateAsync(invocation).ConfigureAwait(false),
                "result" => await RecordResultAsync(invocation).ConfigureAwait(false),
                "delete" => await DeleteAsync(invocation).ConfigureAwait(false),
                _ => throw new ArgumentException($"未知命令：{invocation.Command}", nameof(args))
            };
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"输入错误：{exception.Message}");
            PrintUsage();
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P1 harness 执行失败：{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async System.Threading.Tasks.Task<int> ShowSchemaAsync(string repositoryRoot)
    {
        await using var context = CreateMigratedContext(repositoryRoot);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' " +
                "AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        Console.WriteLine($"数据库：{ReminNoteDatabase.GetDevelopmentDatabasePath(repositoryRoot)}");
        Console.WriteLine($"表：{string.Join(", ", tables)}");
        return 0;
    }

    private static async System.Threading.Tasks.Task<int> CreateAsync(Invocation invocation)
    {
        RequireArgumentCount(invocation, minimum: 3, maximum: 6);
        var title = invocation.Arguments[0];
        var planDate = ParseDate(invocation.Arguments[1]);
        var timeSpec = ParseTimeSpec(invocation.Arguments, 2, planDate);

        await using var context = CreateMigratedContext(invocation.RepositoryRoot);
        var application = new TaskApplicationService(
            new TaskRepository(context),
            SystemClock.Instance);
        var snapshot = await application.CreateAsync(
                new CreateTaskCommand(title, timeSpec))
            .ConfigureAwait(false);

        PrintSnapshot(snapshot);
        return 0;
    }

    private static async System.Threading.Tasks.Task<int> ListAsync(Invocation invocation)
    {
        RequireArgumentCount(invocation, minimum: 1, maximum: 1);
        var planDate = ParseDate(invocation.Arguments[0]);

        await using var context = CreateMigratedContext(invocation.RepositoryRoot);
        var query = new TaskQueryService(context);
        var count = 0;
        await foreach (var snapshot in query.ListAsync(new TaskQuery(planDate)))
        {
            PrintSnapshot(snapshot);
            count++;
        }

        Console.WriteLine($"共 {count} 条");
        return 0;
    }

    private static async System.Threading.Tasks.Task<int> UpdateAsync(Invocation invocation)
    {
        RequireArgumentCount(invocation, minimum: 4, maximum: 7);
        var id = TaskId.Parse(invocation.Arguments[0]);
        var title = invocation.Arguments[1];
        var planDate = ParseDate(invocation.Arguments[2]);
        var timeSpec = ParseTimeSpec(invocation.Arguments, 3, planDate);

        await using var context = CreateMigratedContext(invocation.RepositoryRoot);
        var application = new TaskApplicationService(
            new TaskRepository(context),
            SystemClock.Instance);
        var snapshot = await application.UpdateAsync(
                new UpdateTaskCommand(id, title, timeSpec))
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            Console.Error.WriteLine($"未找到 Task：{id}");
            return 4;
        }

        PrintSnapshot(snapshot);
        return 0;
    }

    private static async System.Threading.Tasks.Task<int> RecordResultAsync(Invocation invocation)
    {
        RequireArgumentCount(invocation, minimum: 2, maximum: int.MaxValue);
        var id = TaskId.Parse(invocation.Arguments[0]);
        var result = ParseResult(invocation.Arguments[1]);
        var note = invocation.Arguments.Count > 2
            ? string.Join(" ", invocation.Arguments.Skip(2))
            : null;

        await using var context = CreateMigratedContext(invocation.RepositoryRoot);
        var application = new TaskApplicationService(
            new TaskRepository(context),
            SystemClock.Instance);
        var snapshot = await application.RecordResultAsync(
                new RecordTaskResultCommand(id, result, note))
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            Console.Error.WriteLine($"未找到 Task：{id}");
            return 4;
        }

        PrintSnapshot(snapshot);
        return 0;
    }

    private static async System.Threading.Tasks.Task<int> DeleteAsync(Invocation invocation)
    {
        RequireArgumentCount(invocation, minimum: 1, maximum: 1);
        var id = TaskId.Parse(invocation.Arguments[0]);

        await using var context = CreateMigratedContext(invocation.RepositoryRoot);
        var application = new TaskApplicationService(
            new TaskRepository(context),
            SystemClock.Instance);
        var deleted = await application.DeleteAsync(id).ConfigureAwait(false);

        Console.WriteLine(deleted ? $"已删除：{id}" : $"未找到 Task：{id}");
        return deleted ? 0 : 4;
    }

    private static ReminNoteDbContext CreateMigratedContext(string repositoryRoot)
    {
        var context = ReminNoteDatabase.CreateDevelopmentContext(repositoryRoot);
        context.Database.Migrate();
        return context;
    }

    private static TimeSpec ParseTimeSpec(
        IReadOnlyList<string> arguments,
        int typeIndex,
        LocalDate planDate)
    {
        var type = arguments[typeIndex].ToUpperInvariant();
        var values = arguments.Skip(typeIndex + 1).ToArray();

        return type switch
        {
            "ANYTIME" when values.Length == 0 => TimeSpec.Anytime(planDate),
            "TIME" when values.Length == 1 => TimeSpec.At(planDate, ParseTime(values[0])),
            "RANGE" when values.Length == 2 =>
                TimeSpec.Range(planDate, ParseTime(values[0]), ParseTime(values[1])),
            "ANYTIME" or "TIME" or "RANGE" =>
                throw new FormatException($"{type} 的时间参数数量不正确。"),
            _ => throw new FormatException($"不支持的时间形状：{type}")
        };
    }

    private static TaskResult ParseResult(string value)
    {
        if (Enum.TryParse<TaskResult>(value, ignoreCase: true, out var result) &&
            result is TaskResult.COMPLETED or TaskResult.MISSED or TaskResult.PARTIAL)
        {
            return result;
        }

        throw new FormatException($"不支持的结果：{value}");
    }

    private static LocalDate ParseDate(string value)
    {
        try
        {
            return DatePattern.Parse(value).GetValueOrThrow();
        }
        catch (Exception exception)
        {
            throw new FormatException($"日期格式必须为 yyyy-MM-dd：{value}", exception);
        }
    }

    private static LocalTime ParseTime(string value)
    {
        try
        {
            return TimePattern.Parse(value).GetValueOrThrow();
        }
        catch (Exception exception)
        {
            throw new FormatException($"时间格式必须为 HH:mm：{value}", exception);
        }
    }

    private static Invocation ParseInvocation(string[] arguments)
    {
        var repositoryRoot = Directory.GetCurrentDirectory();
        var remaining = new List<string>();

        for (var index = 0; index < arguments.Length; index++)
        {
            if (string.Equals(arguments[index], "--help", StringComparison.OrdinalIgnoreCase))
            {
                return new Invocation(repositoryRoot, null, []);
            }

            if (string.Equals(arguments[index], "--repo-root", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= arguments.Length)
                {
                    throw new ArgumentException("--repo-root 必须带路径。", nameof(arguments));
                }

                repositoryRoot = arguments[index];
                continue;
            }

            remaining.Add(arguments[index]);
        }

        repositoryRoot = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(repositoryRoot) ||
            (!File.Exists(Path.Combine(repositoryRoot, ".git")) &&
             !Directory.Exists(Path.Combine(repositoryRoot, ".git"))) ||
            !File.Exists(Path.Combine(repositoryRoot, "ReminNote.sln")))
        {
            throw new ArgumentException(
                $"--repo-root 必须指向包含 ReminNote.sln 的 Git 仓库根目录：{repositoryRoot}",
                nameof(arguments));
        }

        return remaining.Count == 0
            ? new Invocation(repositoryRoot, null, [])
            : new Invocation(repositoryRoot, remaining[0], remaining.Skip(1).ToArray());
    }

    private static void RequireArgumentCount(
        Invocation invocation,
        int minimum,
        int maximum)
    {
        if (invocation.Arguments.Count < minimum || invocation.Arguments.Count > maximum)
        {
            throw new ArgumentException(
                $"命令 {invocation.Command} 参数数量不正确。",
                nameof(invocation));
        }
    }

    private static void PrintSnapshot(TaskSnapshot snapshot)
    {
        Console.WriteLine($"ID：{snapshot.Id}");
        Console.WriteLine($"标题：{snapshot.Title}");
        Console.WriteLine($"时间：{FormatTimeSpec(snapshot.TimeSpec)}");
        Console.WriteLine($"结果：{snapshot.Result?.ToString() ?? "未记录"}");
        if (snapshot.RecordedAt is { } recordedAt)
        {
            Console.WriteLine($"记录时间：{InstantPattern.Format(recordedAt)}");
        }

        Console.WriteLine($"更新时间：{InstantPattern.Format(snapshot.UpdatedAt)}");
        Console.WriteLine();
    }

    private static string FormatTimeSpec(TimeSpec timeSpec) =>
        timeSpec switch
        {
            AnytimeSpec anytime =>
                $"ANYTIME {DatePattern.Format(anytime.LocalDate)}",
            TimePointSpec point =>
                $"TIME {DatePattern.Format(point.LocalDate)} {TimePattern.Format(point.TimePoint)}",
            TimeRangeSpec range =>
                $"RANGE {DatePattern.Format(range.LocalDate)} " +
                $"{TimePattern.Format(range.RangeStart)}-{TimePattern.Format(range.RangeEnd)}" +
                (range.IsCrossMidnight ? "（跨午夜）" : string.Empty),
            _ => throw new InvalidOperationException(
                $"未知的 TimeSpec：{timeSpec.GetType().Name}")
        };

    private static void PrintUsage()
    {
        Console.WriteLine("ReminNote P1 手动验收 harness（仅开发库）");
        Console.WriteLine("用法：所有命令都应在仓库根目录执行，可用 --repo-root <path> 显式指定仓库。");
        Console.WriteLine("  schema");
        Console.WriteLine("  create <标题> <yyyy-MM-dd> ANYTIME");
        Console.WriteLine("  create <标题> <yyyy-MM-dd> TIME <HH:mm>");
        Console.WriteLine("  create <标题> <yyyy-MM-dd> RANGE <HH:mm> <HH:mm>");
        Console.WriteLine("  list <yyyy-MM-dd>");
        Console.WriteLine("  update <ID> <标题> <yyyy-MM-dd> <时间形状及参数>");
        Console.WriteLine("  result <ID> <COMPLETED|MISSED|PARTIAL> [备注]");
        Console.WriteLine("  delete <ID>");
        Console.WriteLine("注意：harness 不提供 purge；只有 scripts/clean.ps1 -PurgeDevData 可以显式重置开发数据。");
    }

    private sealed record Invocation(
        string RepositoryRoot,
        string? Command,
        IReadOnlyList<string> Arguments);
}
