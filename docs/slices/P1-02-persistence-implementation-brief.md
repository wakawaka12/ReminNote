# P1-02 SQLite Persistence 实施说明

- 阶段：P1 Real Task domain + SQLite persistence
- 窗口：P1-02 Persistence
- 性质：已完成实现的持久化边界与验收记录
- 依据：主计划 Migration policy、Task persistence model、P1 阶段预报告和已冻结的 P1-00 决策

## 目标

在 `ReminNote.Infrastructure` 中提供真实 SQLite/EF Core 持久化，使 P1 的 Task 可以通过 migration 建库并经基本 repository/query/application service 完成 CRUD。数据库模型应从第一天按长期 Task 模型设计，不制作一次性平表原型，也不提前创建 Reminder、Anime 或 Sync 表。

## 数据库位置与所有权

P1 开发运行的数据库路径已冻结为：

```text
<仓库根目录>/.devdata/reminnote.sqlite
```

实现应由启动/应用组合层传入或解析该开发路径；Core 不知道文件系统路径，也不得把生产路径作为开发默认值。SQLite 可能伴随生成 `-wal`、`-shm` 或 journal 文件；它们和主库一样属于开发数据，必须留在 `.devdata/`，不得提交。

只有显式执行 `./scripts/clean.ps1 -PurgeDevData` 才允许清理开发数据。普通 build/test/run、默认 `./scripts/clean.ps1` 和 migration 升级不得删除或重建该数据库。`-PurgeDevData` 只能用于本仓库开发数据，不能成为生产/真实用户数据恢复方案。

## 范围

必须覆盖：

- SQLite provider、EF Core DbContext、Task 实体映射和 migration；
- 一个长期可扩展的关系型 `Task` 表，而不是按 `ANYTIME`/`TIME`/`RANGE` 拆表；
- 与 `TaskTimeType`、`LocalDate`、`TimePoint`、`RangeStart`、`RangeEnd` 对应的持久化形状；
- 数据库约束与应用层验证双重阻止非法时间组合；
- 基本 create/read/update/delete 和查询路径；
- 空库建库、重复应用 migration、已有数据升级和失败保护测试；
- 对查询适用位置使用只读/no-tracking 语义，具体方法由 P1-04 与实现确认。

不属于本窗口：

- Reminder/Anime/Sync 表、网络缓存或 Token；
- Agent IPC、Named Pipe、P2.5 Single Writer 或 WAL 迁移策略；
- 生产目录、用户可选 DataRoot、加密和完整恢复模式；
- 用“删库重跑”替代 schema migration。

## Schema 约束原则

- 时间字段的合法组合必须可以在领域层和数据库层被验证。
- 跨午夜由 `RangeEnd < RangeStart` 推导；不得新增重复的布尔列。
- 计划日期/时间保存为本地民用值；需要绝对时间的记录才使用 UTC/Unix-compatible instant。
- 计划历史不能在更新时被静默改写为“实际完成区间”。
- migration 只包含当前 P1 Task 所需对象；P1-06 必须核对表清单，确认没有 Reminder、Anime、Sync 空表。

## Migration 验收

P1-06 需要使用真实 migration 完成下列顺序：

1. 对一个确认是开发库的空路径应用全部 migration，建出目标 schema；
2. 再次执行 migration，结果幂等且不丢失已有 Task；
3. 在库中写入合法 Task，关闭并重启应用，再读回相同 ID、标题和时间形状；
4. 对非法组合验证应用层拒绝，并确认数据库约束不会接受绕过应用层的非法写入；
5. 对后续 schema 变化使用新的 forward migration；不得把删除 `.devdata/reminnote.sqlite` 作为日常升级步骤。

P1 的开发数据在兼容边界前可以按明确记录重置，但这只用于一次性开发库，不代表产品升级策略。升级、备份、失败处理和恢复草案见 [P1 数据安全与 migration 草案](P1-data-safety-and-migration-draft.md)。

## 交付证据

应交付 migration 文件清单、DbContext/映射位置、数据库约束说明、空库到当前库的命令输出、CRUD 与重启保留测试结果、失败场景结果，以及确认没有越界表/文件/路径的检查结果。没有 P1-00 冻结后的实际路径证据时，不得把其他目录下的临时 SQLite 文件当作 P1 验收库。

## 当前实现与验证证据

实现位于 `src/windows/ReminNote.Infrastructure/Persistence/`，包括 `ReminNoteDbContext`、设计时 factory、`ReminNoteDatabase`、`TaskEntity`/配置和 `TaskRepository`。真实 migration 为 `20260828025922_InitialTaskSchema`，文件为 `Persistence/Migrations/20260828025922_InitialTaskSchema.cs`、对应 Designer 和 model snapshot；`Up` 只创建 `tasks`，`Down` 只删除 `tasks`。

`tasks` 保存 UUID v7、标题、TaskTimeType、本地日期、时间点/范围、结果、记录时间、记录备注、CreatedAt 和 UpdatedAt。LocalDate 使用 `uuuu-MM-dd`，LocalTime 使用纳秒日值，Instant 使用固定 9 位 UTC 文本；没有重复的跨午夜布尔列。数据库约束覆盖 UUID v7、标题非空、时间形状和值域、结果元数据、结果时间顺序、PARTIAL 只能用于 RANGE 及 aggregate 时间顺序。

`tests/ReminNote.Tests/TaskPersistenceTests.cs` 和 `SqliteTestDatabase.cs` 使用每测试独立且保持连接的 `:memory:` SQLite，验证 migration 幂等、schema 白名单、跨午夜/结果 round-trip、跨 DbContext CRUD，以及 8 类绕过应用层的非法 raw INSERT。当前不把 `.devdata` 作为自动化测试数据库。

真实开发库的写入入口为显式 `ReminNoteDatabase.CreateDevelopmentContext(repositoryRoot)`；P1 不自动 purge，也不把删除数据库当作正常升级。`Down` 删除 `tasks` 是破坏性操作，真实开发数据必须先备份并停止写入。
