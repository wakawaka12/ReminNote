# P3-01 Reminder 领域模型与持久化映射基础

- 契约输入：`RN-P3-00-CF-1.0.0`（只读，来自总成 worktree）
- 基线：`a871e06cfdeb6c9d6121aacfbf341a9a217713e3`
- 状态：本窗口实现中；不代表 P3-00/P2.75 已由总成接受

## Goal

在 Core 建立 `ReminderRule → ReminderSchedule → ReminderInstance` 的最小可验证
领域模型，并在 Infrastructure 提供后续 migration 可接入的实体、列、索引、唯一性和
状态约束基础。Rule 是业务真相，Schedule 是可重建执行状态，Instance 是 append-only
核心触发事实；Snooze/repeat 只产生派生 Schedule。

## In Scope

- UUID v7 的 Rule/Schedule/Instance/LogicalReminder/Occurrence identity；
- `ReminderTiming`（Relative/AbsoluteUtc）、purpose、priority、pin、repeat、wake、enabled；
- Rule revision no-op/递增更新；Schedule cause/revision、PENDING 终态转换；
- Instance `UNREAD → READ → RESOLVED` 与幂等/冲突动作；
- 作为通知事实子记录的 append-only `ReminderDeliveryAttempt` 值对象/实体；
- `reminder_rules`、`reminder_schedules`、`reminder_instances`、
  `reminder_delivery_attempts` 的实体和显式 opt-in EF 配置；
- 模型级隔离 SQLite 测试和纯领域测试。

## Out of Scope

- 不修改 `ReminNoteDbContext.cs`、`Persistence/Migrations/**`、model snapshot、Solution、
  csproj、包锁、Agent/Main/Widget、IPC 或真实数据库；
- 不创建 recurring-series/TaskInstance schema，不实现 calculator、scheduler、Toast、
  Task result 联动、Recovery、export 或 production DI；
- 不读取、写入、复制或锁定 `D:\Anime\.devdata\reminnote.sqlite`；
- 不修改既有 `reviews/`、`second-review/` 材料。

## Product/contract rules applied

- P3 活跃 Target 只有 `TASK_INSTANCE`；当前一次性 Task 的 target/occurrence adapter 由
  `CreateForTask` 明确映射为同一个 UUID v7；不提前引入 recurrence 表；
- `TASK_PRE_START`/`TASK_START` 使用任务时间或范围开始相对 anchor，`TASK_RANGE_END`
  使用范围结束 anchor，`TASK_CUSTOM` 只接受 UTC absolute；ANYTIME 不凭空产生 clock rule；
- Schedule 的 `triggerAtUtc`、cause、origin、revision 和 Instance 的事实 snapshot 不可由
  规则更新、改期、Snooze 或 channel 结果覆盖；旧 pending 只能进入终态并保留；
- `SNOOZE`/`REPEAT` 必须带 origin；`SNOOZE` 不改变 Rule/原 Schedule；
- 领域状态与实体检查使用稳定 uppercase TEXT enum、UTC 9 位 fractional Instant、UUID v7
  D-format、互斥 timing 列和 bounded zone/error code。

## Mapping seam

当前 DbContext 会扫描 Infrastructure assembly 中的 `IEntityTypeConfiguration<T>`。为遵守
“本窗口不接入 DbContext”，本 Slice 的配置类不实现该接口；总成后续在 approved
migration/model snapshot 中显式调用 `ApplyReminderConfigurations(ModelBuilder)`。这样当前
P2 schema 和测试不会提前出现 Reminder 表，同时配置仍可在隔离 model 中完整验证。

## Acceptance and tests

- 合法/非法 identity、timing、purpose、repeat、timestamp 和 target 构造有稳定拒绝；
- Rule 更新区分 no-op 与 revision+1；Schedule consume/supersede/cancel/expire 单向且保留
  原字段；Instance lifecycle 单向、read timestamp 保留、冲突动作拒绝；Snooze derived 行
  使用同一 logical key 且不改原行；
- 隔离临时 root 下的 SQLite model 验证列、约束、due/target/logical 索引、unique key、
  FK restrict、timing 互斥和 lifecycle timestamp 约束；不使用仓库 `.devdata`。

## Integration requests / risks

- P3-03/P3-06 接入时需在 Agent single-writer transaction 中调用 domain transitions，不能
  让 Scheduler 自行创建 DbContext 或让 UI 成为 writer；
- 总成必须串行加入 Reminder `DbSet`/配置、forward migration、model snapshot、P2.75
  target/verify allow-list，并在 Candidate 上验证；
- `RuleRevision`、`ScheduleRevision`、P2.5 global revision 是三套不同语义；本 Slice 不
  伪造 global journal/revision；
- delivery attempt 只是 Instance 的 append-only channel fact，不得覆盖 Rule/Instance truth。
