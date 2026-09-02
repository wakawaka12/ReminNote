# P3-02 Reminder 时间计算与 Schedule 派生

状态：本窗口完成纯计算实现；不声明 P3 Alpha、不可运行的生产 scheduler，也不替代 P3-00 接受记录。

基线：`a871e06cfdeb6c9d6121aacfbf341a9a217713e3`（P2.75）。P3-00 原文仅从总成工作树
`C:\Users\EMT\.codex\worktrees\c716\Anime\docs\slices\P3-00-contract-freeze.md`
只读取得；本窗口没有复制、改写或提交该文件。

## 1. 所有权与范围

实现和测试只位于：

- `src/windows/ReminNote.Core/Reminders/Calculation/`
- `tests/ReminNote.Tests/P3_02/`

本阶段没有修改 Task.TimeSpec、P3-01 Domain/Infrastructure、DbContext/Migrations、Agent、UI、协议入口、项目文件或锁文件，也没有读写
`D:\Anime\.devdata\reminnote.sqlite`、`reviews/` 或 `second-review/`。

计算器不持有 `IClock`，不调用系统当前时间，不做 IO，不生成 ID，不写 SQLite；相同输入始终得到相同值。

## 2. 最小输入与输出

`ReminderRuleCalculationInput` 是 P3-01 `ReminderRule` 的窄投影：

| 计算输入 | P3-01 映射 |
| --- | --- |
| `RuleId` | `ReminderRule.ReminderRuleId` |
| `TargetKind` / `TargetId` | `TASK_INSTANCE` / 当前 `Task.Id` |
| `OccurrenceId` | 当前一次 Task occurrence 的稳定身份；P3 一次性映射可等于 `Task.Id` |
| `Purpose` / `Timing` / `Priority` / `Pinned` / `Enabled` | 同名 Rule 快照 |
| `RuleRevision` | Rule 创建从 1 开始的 revision |
| `LogicalReminderId` | 由 Agent/Domain 适配器为第一条 schedule 链提供；规则语义变化时提供新的值 |
| `TaskTimeSpec` | 现有 `ReminNote.Core.Tasks.TimeSpec`，不复制 Task 类型 |

`ReminderSchedulePlan` 是新 PENDING schedule 的纯计划，包含 rule/target/occurrence/logical 快照、purpose/priority/pin、cause、origin、两个 revision、`TriggerAtUtc`、relative 计算用的 `TimeZoneId` 和 PENDING 状态。适配器再补 P3-01 持久字段（创建时间、全局 revision、terminal 时间/原因等）。

`PendingScheduleSnapshot` 只表示待执行旧项，`ReminderScheduleOrigin` 只表示已 CONSUMED 的来源 schedule。两者都不接受 `ReminderInstance`，因此历史触发事实永远不会被重算或改写。

## 3. 时间语义

`ReminderTiming.Relative` 只接受 `TASK_TIME`、`RANGE_START`、`RANGE_END` 和有符号 `int64` 秒偏移；`AbsoluteUtc` 直接使用 `AtUtc`。

计算公式为：

```text
anchorLocalDateTime
  -- DateTimeZone.Id + deterministic Noda Time mapping --> anchorInstant
  + Duration.FromSeconds(offsetSeconds)
  --> TriggerAtUtc
```

- `ANYTIME` 的 relative 形状稳定拒绝；只有 `TASK_CUSTOM + AbsoluteUtc` 产生提醒。
- `TIME` 只使用 `TimePointSpec.LocalDateTime` 与 `TASK_TIME`；全局 lead 必须已经物化到 Rule offset。
- `RANGE` 使用现有 `StartLocalDateTime` / `EndLocalDateTime`；跨午夜的结束日期由 `TimeRangeSpec.EndLocalDate`（start date + 1）提供。
- skipped local time 使用 `Resolvers.ReturnForwardShifted` 向前移到有效本地时间；ambiguous local time 使用 `Resolvers.ReturnEarlier` 选择较早 offset。输出同时保留原始 anchor、本地解析后的值、offset、resolution、zone id 和 UTC instant。
- relative offset 默认限制为 ±`315,360,000` 秒（10 × 365 天）；Repeat 默认最大间隔相同。超界或 Noda Time Instant 溢出稳定返回 `DomainValidationException`，而不是让整数/SQLite 溢出决定结果。该边界通过 `ReminderCalculationLimits` 可注入更严格 profile。

## 4. 派生、重建与终止

`ReminderSchedulePlanner` 提供以下纯操作：

1. `DeriveFirstSchedule`：校验 UUID v7、target/purpose/timing/Task shape 和首条 `scheduleRevision=1`；disabled Rule 返回 `reminder.rule.disabled`，否则生成 `cause=RULE`、PENDING 的计划。
2. `CalculateForOccurrence`：由未来 occurrence 适配器传入稳定 `OccurrenceId + TimeSpec`，先检查 occurrence 属于该 Rule，再复用同一计算器；本阶段不发明递归任务模型。
3. `Rebuild`：只接收 pending snapshot。语义变化要求 `RuleRevision + 1`、新的 `LogicalReminderId`、不复用旧 schedule id；旧项按 `(ScheduleRevision, ScheduleId)` 确定性排序并产生 `SUPERSEDED + replacementScheduleId`。Task 时间变化标记 `TASK_TIME_CHANGED`；pending 中的 zone provenance 与当前 `DateTimeZone.Id` 不同标记 `TIME_ZONE_CHANGED`。disabled 新 Rule 没有 replacement，旧项改为 `CANCELLED/RULE_DISABLED`。完全相同的 Rule（且无 zone change）是 no-op，不提升 revision。
4. `CancelPending`：仅为指定 occurrence 的 pending 项产生 `CANCELLED` 转换，允许 `RULE_DISABLED`、`TASK_RESULT_RECORDED`、`TASK_DELETED`、`RECOVERY_OBSOLETE`；不会触碰其他 occurrence。
5. `DeriveSnooze`：来源必须为 CONSUMED；新 UUID/revision、trigger 必须晚于来源，生成新 `cause=SNOOZE`、origin 指向上一条、沿用同一 logical/purpose/priority/pin，来源 trigger 不变。
6. `DeriveRepeat`：来源必须为 CONSUMED；新 `cause=REPEAT`、origin 指向上一条，正间隔加到来源 trigger。`maxCount` 包含首次触发；null 使用默认最多 64 次，超限返回 `reminder.repeat.limit_reached`，disabled 返回 `reminder.repeat.disabled`，不创建无限链。

所有 terminal 转换都只是计划结果；Agent 仍须在自己的业务事务中完成唯一写入、幂等、global revision、历史 Instance 保留以及 due/recovery/purpose 策略。

## 5. 总成接入适配点

- P3-01 将自己的 `ReminderRule` 映射为 `ReminderRuleCalculationInput`，将 P3-02 的 `ReminderSchedulePlan` 映射回 `ReminderSchedule`。P3-01 负责 UUID v7 分配、created/terminal 时间戳及持久化约束；本窗口只校验 UUID v7，不生成它们。
- Agent 在 Rule/Task/time-zone 语义变化事务中读取 pending schedule 投影，调用 `Rebuild`，先落 terminal transition，再落 replacement；不得把已存在的 `ReminderInstance` 传入重建。
- profile 时区变化必须提供旧 pending 的 `TimeZoneId` provenance 和新的 `DateTimeZone`，由 Agent 明确发起 revisioned rebuild，禁止 read-time 静默漂移。
- Snooze/repeat 的起点由 Agent 从已消费 schedule 组装 `ReminderScheduleOrigin`；本阶段不处理 Toast/channel attempt、Quiet Hours、wake 能力、recovery 窗口或 Task result command。
- 未来 recurring occurrence 由适配器实现 `IReminderOccurrenceInputResolver` 或在边界先解析为 `ReminderOccurrenceInput`；OccurrenceId 必须独立稳定，不能用序号或当前时间临时替代。

## 6. 隔离验证记录

验证只使用当前 worktree 的构建输出和 xUnit 纯内存数据，没有建立或访问产品 SQLite：

```text
DOTNET_ROLL_FORWARD=LatestMajor dotnet restore tests/ReminNote.Tests/ReminNote.Tests.csproj --locked-mode
DOTNET_ROLL_FORWARD=LatestMajor dotnet build src/windows/ReminNote.Core/ReminNote.Core.csproj --no-restore
DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/ReminNote.Tests/ReminNote.Tests.csproj --no-restore --filter FullyQualifiedName~ReminNote.Tests.P302
```

结果：Core 构建 0 警告/0 错误；P3-02 过滤测试 23/23 通过。运行环境的 `global.json` 指向未安装的 10.0.100，因此实际使用已安装的 .NET 10.0.400 并通过 `DOTNET_ROLL_FORWARD=LatestMajor` 选择兼容 SDK；没有修改 `global.json`。

测试覆盖表驱动 TIME/RANGE/ANYTIME、Absolute UTC、跨午夜、DST gap/overlap、offset 边界、非法 shape/zone/identity、首派生、禁用、时间/时区 revision、no-op、supersede/cancel、Snooze、Repeat 上限、occurrence scope 和重复调用一致性。

尚需总成确认的集成点：P3-01 领域类型的最终字段/构造器映射、持久化 scheduleRevision 的全历史水位（本 seam 仅能校验 pending 水位）、Agent 事务/幂等接线，以及 P3-07 对迟到 recovery、wake/Quiet Hours 和 channel 行为的策略实现。
