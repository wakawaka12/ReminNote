# P3-00 ReminderRule / ReminderSchedule / ReminderInstance 契约冻结

| 项目 | 内容 |
|---|---|
| Slice | `P3-00` |
| 契约版本 | `RN-P3-00-CF-1.0.0` |
| 当前状态 | 冻结候选；待总成窗口接受后，才可作为后续 P3 生产实现输入 |
| P2.75 基线 | `a871e06cfdeb6c9d6121aacfbf341a9a217713e3` |
| 基线核对 | 当前 detached `HEAD`、`main`、`origin/main` 均指向上述提交；工作树进入本 Slice 前干净 |
| 本 Slice 性质 | 只冻结契约、依赖、风险、测试矩阵和后续 DAG；不实现生产业务代码 |
| 数据安全范围 | 不读取、不写入、不复制 `D:\Anime\.devdata\reminnote.sqlite` |

## 0. 结论先行

本文件冻结 P3 的最小语义边界：

```text
ReminderRule       = 业务真相：为什么、对谁、以什么规则提醒
ReminderSchedule   = 可重建的派生执行项：何时执行、哪个版本、是否仍可执行
ReminderInstance   = 已发生的核心触发事实及其生命周期
```

三者不是同一张表的不同状态，也不能由 Toast、Tray 或 Widget 中的临时状态替代。
`Rule` 是唯一业务意图来源；`Schedule` 可以从 `Rule + Task occurrence + 时区规则`
重建；`Instance` 保存已经发生的触发事实和用户处理结果。历史触发事实不可被新的
规则、改期、Snooze 或通知通道结果改写。

P3-00 不创建 Reminder 表、不更新 EF migration/model snapshot、不注册新的 IPC
operation、不添加 NuGet 包、不改变 Agent/Main/Widget 的生产接线。所有后续生产修改
必须在本文件被总成接受后，按第 12 节 DAG 派发。

## 1. 审查输入与基线说明

### 1.1 已阅读的权威输入

本 Slice 的冻结结论来自以下材料，原材料保持原样：

- `ReminNote_MASTER_DEVELOPMENT_PLAN.md`：提醒三层模型、时间语义、Snooze、生命周期、优先级、Quiet Hours、恢复、通知通道和 P3 稳定门禁。
- `ARCHITECTURE.md`：Core / Infrastructure / Agent / Windows / Widget / Bootstrap 边界，Agent 唯一业务 writer，P2.75 Candidate 流程。
- `PRODUCT_RULES.md`：Task 的 `ANYTIME` / `TIME` / `RANGE` 形状，计划时间与提醒时间分离，P2 不提前实现 Reminder。
- `DECISIONS.md`：Noda Time、EF/SQLite、UUID v7、P2.5 Agent/IPC 边界和 ADR-0018 P2.75 安全候选。
- `SECURITY.md`：真实数据库、迁移、IPC、加密和生产配置的安全约束。
- `TESTING.md`：自动化/隔离临时 root/真实进程/桌面人工验收的证据分层。
- `docs/PARALLEL_DEVELOPMENT.md`：worktree、文件 owner、总成串行区域和禁止抢改范围。
- `docs/slices/P2.5-00-contract-freeze.md`：唯一 writer、RN-CJ-1、RequestId/idempotency、global revision、read-only channel、DAG 和 T01-T26。
- `docs/slices/P2.5-00-integration-review.md`：P2.5 条件通过结论、未决风险和总成接线要求。
- `docs/slices/P2.75-00-contract-freeze.md`：`Resolve → Quiesce/Lock → Backup → Stage → Forward Migration → Verify → Atomic Promote → Startup`、fail-closed、恢复和 V01-V24。
- `docs/reports/P2收尾与P2.5-P2.75-P3开发计划-2026-08-29.md`：P3-00 至 P3-09 的建议拆分和先后关系。
- `docs/reports/P2-阶段集成报告-2026-08-28.md`、`docs/reports/P2-阶段总结与下一阶段开发约束-2026-08-29.md`、`docs/reports/P2-总成接管记录-2026-08-28.md`、`docs/reports/P2-GATE-01-runtime-acceptance-2026-08-29.md`：P2 当前状态、P2.5/P2.75 前置和证据限制。

### 1.2 基线偏差记录

`docs/slices/P2.75-00-contract-freeze.md` 的输入提案记录了较早的 source commit
`3ab342b...`，但本窗口已核对当前 P2.75 基线为用户指定的
`a871e06cfdeb6c9d6121aacfbf341a9a217713e3`。本文件只记录这个基线修正，不改写
既有 P2.75 契约或报告。

P2.75 契约本身仍写明“冻结候选，待总成审查并记录接受后生效”。因此，本文件可以
先冻结 P3 的语义和依赖审查，但不能把当前 commit、隔离测试或本文称为 P2.75
生产通过、P3 可用或用户数据已迁移。

历史 P2 阶段报告还保留过一套不同的 P2.5 `01/02/03/04` 拆分；本文件不沿用它。
后续所有 P3 依赖均以权威 P2.5 契约的 canonical map 为准：`01=Core contract`、
`02=Storage consistency`、`03=Agent writer`、`04=Transport`。这只是依赖引用
修正，不改写历史报告。

### 1.3 当前源码事实

基线源码审查得到以下事实：

| 事实 | 当前证据 | 对 P3 的约束 |
|---|---|---|
| Core 已使用 Noda Time | `src/windows/ReminNote.Core/ReminNote.Core.csproj` 只引用 `NodaTime`；`TimeSpec` 保持 local-civil 语义 | Reminder Core 继续使用 `Instant` / `LocalDateTime` / `Duration`，不把 EF/SQLite/WPF 带入 Core |
| Task 只有三种计划形状 | `src/windows/ReminNote.Core/Tasks/TimeSpec.cs`：`ANYTIME`、`TIME`、`RANGE`；跨午夜由 `EndLocalDate` 派生 | Reminder calculator 必须复用现有形状，不在 Task 上增加 reminder 字段或改变计划日期归属 |
| 当前数据库没有 Reminder 表 | `ReminNoteDbContext` 只有 Task、TaskHistory、AppSettings、P2.5 revision/journal/receipt；现有测试还断言没有 `reminders` 表 | P3-00 不预建表；P3-01 才能写新的 forward migration |
| 当前 Agent 有明确启动门 | `AgentRuntime` 先执行 P2.75 gate，再 `OpenReadyAsync`；非 `READY + WRITABLE` 不开业务 pipe | Reminder scheduler 不能绕过 gate；迁移失败时不得触发、写入或修复 Reminder |
| 当前生产命令路径已面向 Agent | Main/Widget 使用 `AgentTaskClient`；读使用 `ReadOnlyTaskServices`；P2 direct `TaskWorkspace` 仍只可作为旧测试/隔离边界 | P3 UI 继续通过 Agent command + read-only query；需在后续 cutover 证明没有第二个生产 writer |
| 当前实际 mutation adapter 在 Agent | `AgentProtocolDispatcher → P25StorageStore.Writer.ExecuteMutationAsync → AgentDomainMutationAdapter → TransactionBoundTaskRepository` | P3 scheduler 和提醒动作必须复用同一 Agent writer transaction，不接到独立 DbContext 或 UI service |
| `SingleProfileWriterActor` 是准备 seam | 其注释明确标记为 P2.5-03 non-runnable preparation seam | P3 不把该 seam 自行宣布为生产 writer；由总成确认唯一实际 writer 实现 |
| P2.75 默认 target 仍是 P2.5 migration | `AgentMigrationStartupComposition.DefaultPlan` 当前 target 只到 `20260831090000_P25StorageConsistency` | P3-01 必须串行更新 approved target、migration、snapshot、verify 和 startup plan |
| UI 中已有 Mock reminder 文案 | Widget/Anime 文案明确写着 Mock、`ReminderInstance 未持久化` | 不能把 Mock 行为当作 P3 业务实现或验收证据 |

## 2. 范围、非范围与规范词

### 2.1 本文件冻结的范围

- 三层对象的职责、字段语义、身份和版本关系。
- Task 时间到 Reminder 时间的最小映射规则。
- purpose、Snooze、repeat、优先级、PIN、wake policy 和 Quiet Hours 的语义。
- Agent writer、global revision、idempotency、read-only、migration gate 的依赖关系。
- 触发、重启/睡眠恢复、Task result 联动和通知通道健康的最小可观察结果。
- P3-01 至 P3-09 的 owner、前置、并行和串行集成点。

### 2.2 明确不在 P3-00 的范围

- Reminder 生产类、EF entity、DbContext `DbSet`、migration、model snapshot 或 SQL DDL。
- Named Pipe 的新 wire payload、协议版本升级、operation allow-list 或 Windows ACL 实现。
- Toast/Tray/Widget 的 WPF 代码、系统通知 API、声音或 wake OS API。
- Anime 网络、Bangumi、Sync、自动更新、插件、复杂 merge 或加密系统。
- 对既有 `reviews/`、`second-review/` 材料的修改、覆盖或重排。
- 对 `D:\Anime\.devdata\reminnote.sqlite` 的任何读取、写入、复制、锁定或 sidecar 操作。

### 2.3 规范词

- **必须**：后续实现和验收不得违反。
- **不得**：明确禁止的行为。
- **可**：不改变语义即可采用的实现选择。
- **保留**：枚举值或接口概念可以先存在，但 P3 不得激活其业务路径。

## 3. 共同值对象与身份

### 3.1 时间与序列化

- Core 使用 Noda Time 类型：业务运算使用 `Instant`、`LocalDate`、`LocalTime`、`LocalDateTime`、`Duration`。
- 所有持久化和 export 的绝对时间使用 UTC；采用当前 Task/P2 设置沿用的 invariant pattern：
  `uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'`。
- 相对偏移在 domain/wire/export 中使用有符号整数秒 `int64`，不得使用浮点数、依赖本地文化的小数或 Windows `DateTime` 运算。
- `TriggerAtUtc` 和 `TriggeredAtUtc` 必须区分：前者是计划执行时间，后者是 Agent 实际记录核心触发的时间。
- `LocalDate` 是 Task 计划归属日期；跨午夜 RANGE 的结束日期使用现有 `TimeRangeSpec.EndLocalDate` 派生，不重写 Task 的 `LocalDate`。

### 3.2 UUID 与 occurrence

以下持久身份均使用 UUID v7，并以小写 `D` 格式传输或导出：

- `ReminderRuleId`
- `ReminderScheduleId`
- `ReminderInstanceId`
- `LogicalReminderId`
- `OccurrenceId`

`LogicalReminderId` 不是第四个业务实体，而是同一条提醒逻辑链（原始计划、repeat、
Snooze）的稳定替换 key。新的 Rule revision 会开始新的 logical chain；同一 chain
内的重复 Toast 应使用该值作为 replace/update key，而不是用窗口句柄或进程 PID。

当前代码还没有独立的 `TaskInstance` / recurring-series aggregate。P3 的最小 adapter
约定如下：

```text
当前一次性 Task：
    TargetKind  = TASK_INSTANCE
    TargetId    = Task.Id
    OccurrenceId = Task.Id

未来真正的 recurring Task：
    每个 TaskInstance 使用独立稳定的 OccurrenceId；
    一个 occurrence 的结果不能取消另一个 occurrence 的提醒。
```

这个映射允许 P3 复用当前 Task，而不把“未来 recurrence”偷偷加入 P2 表。P3-01
不得借 Reminder migration 创建未冻结的 recurring-series schema。

## 4. ReminderRule 最小契约

### 4.1 责任

`ReminderRule` 表示当前有效的业务意图，回答“对哪个 occurrence、因为哪种目的、
相对哪个时间点、以什么优先级提醒”。它是可变的当前真相，但每次语义变化都必须
提高 `RuleRevision`；已发生的 `ReminderInstance` 不随之改变。

### 4.2 逻辑形状

```text
ReminderRule {
    ReminderRuleId       id                  // UUID v7
    TargetKind            targetKind          // P3: TASK_INSTANCE
    Guid                  targetId            // 当前 P3 映射到 Task.Id
    OccurrenceId          occurrenceId        // 稳定 occurrence identity
    ReminderPurpose       purpose
    ReminderTiming        timing
    ReminderPriority      priority            // LOW | NORMAL | HIGH
    bool                  pinned              // 与 priority 独立
    RepeatPolicy          repeatPolicy
    WakePolicy            wakePolicy           // DEFAULT | YES | NO
    bool                  enabled
    long                  ruleRevision         // 从 1 开始、单调递增
    Instant               createdAtUtc
    Instant               updatedAtUtc
}
```

P3-00 不冻结任意自由文本 message 字段。通知显示标题和目标上下文由 read model
查询 Target；未来若需要自定义文案，必须另行冻结长度、转义、hash 和 export 语义，
不能把用户文本随意塞入 scheduler 或 Toast。

### 4.3 Target 与 purpose

P3 可执行的 Target 只有 `TASK_INSTANCE`。以下 Anime 值可以在共享契约中保留，但
P3 不接受其创建、调度或恢复命令：

```text
TASK_PRE_START
TASK_START
TASK_RANGE_END
TASK_CUSTOM

ANIME_PRE_AIRING   // reserved
ANIME_AIRING       // reserved
ANIME_CUSTOM       // reserved
```

purpose 是恢复和过期策略的输入，不是纯展示标签。任何 schedule、instance、export
和 channel projection 都必须保留 purpose snapshot；Snooze/repeat 不得创造新的业务
purpose。

### 4.4 Timing

`ReminderTiming` 是受限的 discriminated shape：

```text
Relative {
    Anchor = TASK_TIME | RANGE_START | RANGE_END
    OffsetSeconds = signed int64
}

AbsoluteUtc {
    AtUtc = Instant
}
```

约束：

- `Relative` 必须有且只有一个 anchor 和 offset；`AbsoluteUtc` 必须有且只有一个 UTC instant。
- `TASK_TIME` 只适用于 `TIME` Task；`RANGE_START` / `RANGE_END` 只适用于 `RANGE` Task。
- `ANYTIME` 不生成默认时钟提醒；只有显式 `TASK_CUSTOM + AbsoluteUtc` 才能产生提醒。
- `TASK_PRE_START` 的默认形状是 `TASK_TIME` 或 `RANGE_START` 加负 offset；`TASK_START` 可表示计划点本身或明确的 start anchor；`TASK_RANGE_END` 使用 `RANGE_END` anchor；`TASK_CUSTOM` 使用绝对 UTC。
- `TIME` 的全局 lead 必须在创建/更新 Rule 时物化为 Rule timing；scheduler 不得隐式读取一个未进入 Rule revision 的 magic default。
- 一个 Task 可以有多个 Rule；多个 Rule 之间不能因为 purpose 相同就合并或覆盖。
- offset 必须在 Noda Time `Instant` 可表示范围内；P3-02 必须给出产品配置的最大/最小边界，并对溢出做稳定拒绝，不能让 SQLite 整数溢出成为调度结果。

### 4.5 Repeat、wake、enabled 与 revision

```text
RepeatPolicy {
    bool enabled
    long? intervalSeconds       // enabled 时必须为正数
    int? maxCount               // 代表一个 logical chain 的总触发次数；null 由产品策略限制
}
```

- `maxCount` 包含第一次核心触发；`enabled=false` 时一次核心触发后不自动 repeat。
- `maxCount=null` 不表示无限制地创建 SQLite 行；P3-07 必须设置并测试一个 profile 安全上限，具体产品默认值在实现窗口记录。
- `WakePolicy` 只表达是否允许该 reminder 请求 wake；它不保证 OS 能力，也不能覆盖 P2.75 fail-closed。
- `enabled=false` 是业务禁用，必须使未来 pending schedule 不可执行；历史 instance 保留。
- 逻辑删除如果被支持，必须是可导出的 tombstone/disabled 语义，不得物理删除导致历史丢失。
- 创建 Rule 的 `ruleRevision=1`；任何 timing、purpose、target、priority、pin、repeat、wake、enabled 的语义变化都 `+1`。完全相同的更新是 no-op，不产生新的 global revision。
- Rule 自身 `createdAtUtc` 不变，`updatedAtUtc >= createdAtUtc`；更新 timestamp 由 Agent 注入的 clock 产生。

## 5. ReminderSchedule 最小契约

### 5.1 责任与逻辑形状

`ReminderSchedule` 是可重建的执行计划，不是事实历史。其最小逻辑形状为：

```text
ReminderSchedule {
    ReminderScheduleId  id                  // UUID v7
    ReminderRuleId       ruleId
    OccurrenceId         occurrenceId
    LogicalReminderId    logicalReminderId
    ReminderScheduleId?  originScheduleId   // SNOOZE/REPEAT 必须有
    ScheduleCause        cause               // RULE | SNOOZE | REPEAT
    long                 ruleRevision       // 创建时的 Rule snapshot
    long                 scheduleRevision   // (rule, occurrence) 内单调递增
    Instant              triggerAtUtc
    string?              timeZoneId          // relative schedule 的计算 provenance
    ScheduleState        state
    ScheduleStateReason? terminalReason
    ReminderScheduleId?  replacementScheduleId
    Instant               createdAtUtc
    Instant?              terminalAtUtc
}
```

### 5.2 ScheduleState

P3 最小状态集：

```text
PENDING       // 唯一可被 due worker 选中的状态
CONSUMED      // due 事务已消费；核心 Instance 已写入
SUPERSEDED    // 被新的 Rule/Task/time-zone revision 替代
CANCELLED     // Target result、禁用或删除使其不再执行
EXPIRED       // 已到期但依据 purpose/recovery 判定为不再有意义
```

状态规则：

- 只有 `PENDING` 可进入 due transaction；任何 terminal state 都不得再次触发。
- `CONSUMED` 表示调度执行资格已被消费，不承载“Toast 是否显示”“用户是否已读”或“通道是否可用”等历史事实。
- `SUPERSEDED` 必须保留旧行；如果确实有 replacement，记录 `replacementScheduleId`；如果没有 replacement，应使用 `CANCELLED` 而不是伪造 supersede。
- `CANCELLED` 和 `EXPIRED` 不删除行，且必须带稳定原因，例如 `TASK_RESULT_RECORDED`、`RULE_DISABLED`、`RECOVERY_OBSOLETE`。
- `triggerAtUtc`、`ruleRevision`、`scheduleRevision`、`cause`、`originScheduleId` 创建后不可改写。
- `(ruleId, occurrenceId, scheduleRevision)` 必须唯一；`scheduleRevision` 从 1 开始，不因删除或失败重用。
- scheduler 不得只以 timestamp 判断“该不该提醒”；必须读取 purpose、target 当前结果、priority/pin 和恢复上下文。

### 5.3 Schedule revision 与 logical chain

- Rule 创建一个 occurrence 的第一条执行计划时生成新的 `LogicalReminderId`。
- Rule 或 Task plan 改变时，旧 pending schedule 进入 `SUPERSEDED`，新 schedule 使用新的 `ruleRevision` 和新的 `LogicalReminderId`；旧 Instance 永不移动到新 chain。
- Snooze 和 repeat 都生成新 `ReminderScheduleId`，不改写原 schedule 的 `triggerAtUtc`，继承原 `purpose`、`priority`、`pinned` snapshot 语义，并沿用同一 `LogicalReminderId`。
- Snooze/repeat 的新 schedule `cause` 分别为 `SNOOZE` / `REPEAT`，`originScheduleId` 指向链中的前一条 schedule。
- scheduler 崩溃、重启或重复扫描同一 due 项，不得产生第二条等价 pending schedule 或第二个核心 Instance。

## 6. ReminderInstance 最小契约

### 6.1 责任与逻辑形状

`ReminderInstance` 是核心 reminder 已经发生的一次触发尝试以及该尝试的用户处理
生命周期。为同时满足“事实不可重写”和“重复 Toast 仍有同一逻辑 key”，P3 冻结为：

- 每一次核心触发尝试一行 Instance；
- 同一 `LogicalReminderId` 下的 repeat 是多行 Instance，使用递增 `attemptOrdinal`；
- channel retry 不新建核心 Instance，而是在该 Instance 的 channel-attempt 子记录中追加事实；
- `LogicalReminderId` 不是可见 UI 的独立事实实体，而是归组/replace key。

```text
ReminderInstance {
    ReminderInstanceId  id                  // UUID v7
    ReminderScheduleId  scheduleId
    ReminderRuleId      ruleId
    OccurrenceId        occurrenceId
    LogicalReminderId   logicalReminderId
    int                 attemptOrdinal
    ReminderPurpose     purposeSnapshot
    ReminderPriority    prioritySnapshot
    bool                pinnedSnapshot
    Instant             triggeredAtUtc       // Agent 记录核心触发事实的时间
    ReminderLifecycle   lifecycle            // UNREAD | READ | RESOLVED
    Instant?            readAtUtc
    Instant?            resolvedAtUtc
    ResolutionAction?   resolutionAction
}
```

### 6.2 不可变事实与生命周期

- `scheduleId`、`ruleId`、`occurrenceId`、`logicalReminderId`、`attemptOrdinal`、purpose/priority/pin snapshot 和 `triggeredAtUtc` 是不可变事实。
- `lifecycle` 只允许 `UNREAD → READ → RESOLVED`，不得反向或跨越回退。
- Toast close 只执行 `UNREAD → READ`，不等于完成 underlying Task，也不等于 `RESOLVED`。
- 解析动作如果直接由 UNREAD 用户操作触发，内部可在一个 Agent transaction 中记录 read fact 后立即 resolved；对外仍满足单向生命周期，且不丢失 `readAtUtc`。
- `resolutionAction` 第一次成功写入后不可换成另一个动作；完全相同的重试必须幂等，冲突动作必须稳定拒绝。
- 既有 Instance 不得因为 Rule 更新、Task 改期、时区变化、Snooze 或 channel 失败而改写触发时间和 purpose。
- 不触发的 schedule 不创建 Instance：例如恢复时已过时的 pre-start schedule 进入 `EXPIRED`，而不是伪造一个“已显示”的 Instance。

### 6.3 ResolutionAction

```text
DONE
SNOOZE
WATCHED       // P3 保留；Anime 业务未启用
WATCH_LATER   // P3 保留；Anime 业务未启用
SKIP
IGNORE
```

P3 Task 目标实际启用 `DONE`、`SNOOZE`、`SKIP`、`IGNORE`；Anime 相关动作只保留
枚举和拒绝码，不得在 P3 生产路径被激活。

- `IGNORE` 只解决当前 Instance，不禁用 Rule、不取消未来其他 occurrence。
- `SNOOZE` 解决当前 Instance 的本次呈现，并在同一 Agent 业务事务中创建 derived `SNOOZE` schedule；不改变 Rule、不重写原 schedule。
- `DONE` 对 Task occurrence 记录 `COMPLETED` 结果，并在同一业务事务中取消该 occurrence 的所有未来 pending schedule（包括 Snooze/repeat）。
- `SKIP` 只解决当前提醒；是否影响 Task result 由后续 Task command 明确，不得隐式删除 Rule。
- `WATCHED` / `WATCH_LATER` 目前不得对 `TASK_INSTANCE` 产生成功结果。

## 7. 时间计算、恢复与策略契约

### 7.1 TaskTime → ReminderTime

| Task time | 允许的最小 Reminder 语义 | 约束 |
|---|---|---|
| `ANYTIME` | 显式 `TASK_CUSTOM + AbsoluteUtc` | 不凭空生成时钟提醒 |
| `TIME` | `TASK_PRE_START`、`TASK_START`、或显式 custom | Task local date/time 不改写；global lead 物化进 Rule |
| `RANGE` | `RANGE_START` 前/开始、`RANGE_END` 前/结束 | 使用现有 `StartLocalDateTime`/`EndLocalDateTime`，跨午夜结束日为 start date + 1 |

计算公式：

```text
relative:
    anchorLocalDateTime
        --(profile user time zone + deterministic local mapping)-->
    anchorInstant
        + Duration.FromSeconds(offsetSeconds)
        --> TriggerAtUtc

absolute:
    TriggerAtUtc = AtUtc
```

规则：

- 依赖注入 `IClock` 和 `IUserTimeZoneProvider`；scheduler/calculator 不调用隐藏的 `DateTime.Now`。
- 当前 `IUserTimeZoneProvider` 公开 `DateTimeZone`，但尚未公开持久化的 stable zone id；P3-02 必须在 calculator boundary 解决 `DateTimeZone.Id` provenance。
- skipped local time 使用向前移动到有效时间的 deterministic policy；ambiguous local time 选择较早 offset。P3-02 必须用 Noda Time 等价 resolver 并加入 DST golden tests，不得交给 OS 默认行为。
- relative schedule 保存计算时使用的 `timeZoneId` 和 `TriggerAtUtc`。profile 时区改变必须由 Agent 产生一个明确的 rebuild/supersede 事务；不能在读取时静默漂移。
- 自定义本地时间必须在 command boundary 解析成 UTC 后再形成 `AbsoluteUtc` Rule；SQLite 中不保存含糊的 local `DateTime` 作为可执行 trigger。

### 7.2 恢复、睡眠和离线

Agent 重启、机器从睡眠唤醒或长时间离线后，必须从 SQLite 的 `PENDING` schedule
重新计算最近 wakeup，并一次性处理所有 due 项；不得依赖进程内 timer 尚存。

恢复分类最小规则：

| purpose | 已过 `TriggerAtUtc` 的恢复处理 |
|---|---|
| `TASK_PRE_START` | 若当前已过 Task start，通常标记 `EXPIRED/RECOVERY_OBSOLETE`，不弹过期的 pre-start Toast |
| `TASK_START` | 若 occurrence 尚未有结果，可按 priority/pin 触发一次或进入 summary；不得重复触发同一 schedule |
| `TASK_RANGE_END` | 若 occurrence 尚未有结果，通常仍保留一次 end/result prompt；必须使用 purpose 而非只比较 timestamp |
| `TASK_CUSTOM` | 只要 occurrence 未被取消且 custom reminder 尚有业务意义，按配置触发一次；具体迟到窗口由 P3-07 固化并测试 |
| `ANIME_*` | P3 不执行；必须稳定拒绝或保持未激活状态 |

任何恢复决定都必须留下可观察的 schedule terminal reason 或 Instance/channel fact；
不能把“错过了”和“通道被阻塞”合并成一个成功显示。

### 7.3 Quiet Hours

Quiet Hours 是 notification presentation policy，不是 schedule truth：

- scheduler 继续计算、消费和记录核心触发；不因 Quiet Hours 修改 Rule 或把 schedule 时间推后。
- Quiet Hours 可有多个区间、临时覆盖和跨午夜区间；以 profile user timezone 解释，P3-07 负责边界/DST 测试。
- 普通提醒可以在 Quiet Hours 聚合为 summary；`HIGH` 和 `PIN + HIGH` 可按策略单独提升。
- channel result 必须区分 `SUPPRESSED_QUIET_HOURS`、`BLOCKED`、`UNAVAILABLE` 和 `FAILED`。
- Quiet Hours 结束后的 summary 使用原 `LogicalReminderId` / Instance facts，不创建伪造的旧时间触发记录。

### 7.4 Priority、PIN、repeat 与 wake

- priority 仅允许 `LOW`、`NORMAL`、`HIGH`；PIN 是独立 bool，`PIN + HIGH` 合法。
- Rule 的 priority/pin 在 Instance 中 snapshot，避免后续 Rule 修改重写历史呈现事实。
- repeat 是 derived schedule 行；重复 Windows notification 应用 `LogicalReminderId` 更新同一逻辑 Toast，而不是无界地创建独立视觉 toast。
- `DEFAULT` wake 由产品/设备策略决定；`YES` / `NO` 是 per-rule override。OS 不支持时，记录 channel health，不修改 reminder truth。

## 8. 操作、事务与 Agent 边界

### 8.1 全局写入规则

所有 Reminder 业务事实，包括 Rule、Schedule、Instance、resolution、Task result 联动、
delivery attempt 和 recovery decision，都必须由 Agent 串行写入：

```text
Main / Widget / future channel callback
        -> Agent business command pipe
        -> existing P2.5 validation + idempotency + expectedRevision
        -> Agent single writer transaction
        -> Task/Reminder rows + journal + receipt + global revision
        -> commit 后 event / read refresh
```

scheduler 是 Agent 内部 producer，不是 SQLite 第二 writer。它必须进入同一 writer
serialization 和 transaction adapter；不能自己打开 `ReminNoteDbContext`、调用
`Database.Migrate()`、使用 `TaskWorkspace` 或以 timer callback 直接 `SaveChanges`。

### 8.2 外部 command 与内部 scheduler

外部 Reminder command 在后续 P3 wire Slice 中遵循 P2.5：

- 使用既有 Request/Response envelope、profile scope、ACL、`RequestId`、`ExpectedRevision`、RN-CJ-1 canonical hash 和 bounded payload。
- 每次 wire attempt 使用新的 `RequestId`；重试使用相同 logical idempotency key 和 payload hash。
- 新 command 不得复用 control/activation pipe；control pipe 只做生命周期/health。
- status、timeout、disconnect、Agent restart 以 receipt/reconcile 为准，不以客户端是否收到 response 判断是否写入。

Agent 内部 due/retry 也必须有稳定 idempotency identity。最低要求是由
`ReminderScheduleId`（以及必要时的 delivery channel key）构成，重复扫描不得重复
消费；具体 operation name 和 receipt 是否对用户可见由 P3-03 记录，但不能绕过
P2.5 global revision/journal。

### 8.3 关键事务

| 操作 | 同一事务必须完成的事实 |
|---|---|
| 创建/更新 Rule | Rule revision + 旧 pending supersede + 新 Schedule + journal/receipt/global revision |
| Task 改期 | Task plan + 受影响 occurrence 的旧 schedule supersede + 新 schedule；历史 Instance 不变 |
| due 处理 | PENDING → CONSUMED + 一个核心 Instance；重复执行必须幂等 |
| SNOOZE | 当前 Instance lifecycle/action + derived SNOOZE schedule；原 Rule/original schedule 不变 |
| DONE / Task result | Task result + 当前 Instance resolve + 同 occurrence 所有未来 PENDING schedule cancel；其他 occurrence 不变 |
| Rule disable/delete | Rule disabled/tombstone + 未来 PENDING schedule cancel；历史 Instance 保留 |
| channel delivery | 不改变 Rule truth；每次 channel attempt 追加事实，使用 Instance/logical key 去重 |

Task 的 `COMPLETED`、`MISSED`、`PARTIAL` 都是当前 P2 `ResultRecord` 已存在的终止
事实；当前一次性 Task 的未来提醒只能属于该 Task occurrence。若未来产品把某个
result 改成“非终止”，必须先更新本契约和测试，不得由 Reminder scheduler 猜测。

### 8.4 Channel delivery 子记录

P3-00 不把 delivery attempt 定义成第四个业务根，但冻结其语义最小形状，以避免
Instance 被覆盖写坏：

```text
ReminderDeliveryAttempt {
    attemptId       UUID v7
    instanceId      ReminderInstanceId
    channel         TOAST | TRAY | WIDGET | SOUND | WAKE_TIMER
    attemptedAtUtc  Instant
    outcome         DELIVERED | BLOCKED | UNAVAILABLE | FAILED |
                    SUPPRESSED_QUIET_HOURS | NOT_ATTEMPTED
    errorCode       bounded stable code, nullable
}
```

P3-04/P3-01 可将它实现为 append-only child table 或等价的有界不可变投影，但不得
把上一次 `BLOCKED` 覆盖成 `DELIVERED` 而丢失事实。Core trigger 已发生、但 Toast
不可用时仍必须保留 Instance；“没有触发”只能由没有 Instance 或明确的 schedule
`EXPIRED` 事实表达。

## 9. 最小持久化约束（只冻结语义，不落地 DDL）

P3-01 负责将以下约束转换为 EF entity/configuration/migration。P3-00 不修改当前
`ReminNoteDbContext`、migration 或 model snapshot。

### 9.1 表与关系建议

建议的持久化集合为：

- `reminder_rules`
- `reminder_schedules`
- `reminder_instances`
- `reminder_delivery_attempts`（若采用子表；不是独立业务 root）

最低关系：

- 一个 Rule 对多个 occurrence/schedule；一个 occurrence 可有多个 Rule。
- 一个 Schedule 最多产生一个核心 Instance；repeat 使用新 Schedule。
- 一个 Instance 可有多个 channel attempts。
- Task target 到 Reminder history 不得使用会级联删除 Instance 的 FK。删除/禁用 Task 时，未来 schedule 取消，历史 Instance 和必要 target snapshot 仍可导出。

### 9.2 SQLite/EF 约束

- 新 enum 建议以稳定 uppercase TEXT 存储并加 allow-list check；不得依赖 enum 数值顺序作为公开契约。
- UUID 使用现有小写 `D` 字符串形式和 UUID v7 check；不能把 PID、窗口句柄或用户文本当 ID。
- Instant 使用现有 9 位 fractional UTC pattern；所有 timestamp order check 必须可验证。
- `offset_seconds` 为 INTEGER；relative/absolute 的 nullable columns 必须有互斥 check，拒绝 mixed state。
- schedule 状态、cause、terminal reason、resolution action、channel outcome 均须有稳定 allow-list。
- due query 至少需要 `(state, trigger_at_utc)` 索引；target/occurrence、logical chain、rule revision 需要可审计的索引。
- `scheduleRevision`、`attemptOrdinal` 从 1 开始并拒绝负数/重复；所有 terminal transitions 必须有时间和原因。
- delivery attempts 不得用无界用户文本；error code、channel、purpose 等均需 bounded。
- migration 必须是 P2.75 Candidate 上的 forward migration；Active DB 在 backup、apply、verify 成功前字节和 sidecar 代次不变。

### 9.3 revision、journal、receipt

Reminder 的每个实际业务变更继续使用 P2.5 的一个 global revision：

- Rule update、schedule supersede/rebuild、due consume、Instance lifecycle、Task result 联动和 restore import 都产生完整 journal batch（若有实际变化）。
- no-op、rejected、stale、未进入 commit 的 timeout 不伪造 global revision。
- journal entity type/kind 必须 bounded、稳定且足以让 read model 判断 Reminder change；不能用 event 代替 durable truth。
- P3 新 command 的 canonical payload 也必须遵守 RN-CJ-1，不允许把 `DateTime`、浮点 offset 或未知字段混入 hash。

## 10. 依赖审查结果与后续 owner

| 依赖 | 已确认的可复用部分 | 缺口/风险 | 解决窗口 |
|---|---|---|---|
| Core time | `TimeSpec`、`TimeRangeSpec.EndLocalDate`、`IClock`、`IUserTimeZoneProvider`、NodaTime 3.3.3 | timezone id、DST resolver、offset bound 未落实现 | P3-02 / P3-07 |
| Core domain boundary | 当前 Core 不引用 EF/SQLite/WPF；Task application contracts 已使用显式 service | 需要新增 Reminder value objects/service port，但不能先改生产代码 | P3-01 / P3-02 / P3-04 |
| Infrastructure persistence | EF Core 10.0.11、SQLite、现有 Instant/Guid converter、transaction-bound repository | DbContext 当前没有 Reminder；delivery attempt 物理形状需决定 | P3-01，总成串行 migration |
| Agent writer | `P25StorageStore.Writer`、global revision、journal、receipt、transaction-bound Task repo | 当前 `SingleProfileWriterActor` 仍是 non-runnable seam；需确认唯一 production writer | P2.5 acceptance / P3-03 |
| Agent lifecycle | P2.75 gate、`OpenReadyAsync`、profile scope、WAL/FK/readiness | Default migration target 仍只到 P2.5；P3 target 尚未加入 | P3-01 总成集成 |
| Main/Widget read/write | `AgentTaskClient` command、`ReadOnlyTaskServices` query-only read；两个宿主都已走 Agent client | Reminder query/client/refresh 尚不存在；旧 `TaskWorkspace` 仍在仓库 | P3-06 / P3-08 / P2.5 static proof |
| Windows channels | WPF host、Widget、CommunityToolkit.Mvvm 已有；现有 reminder 文案是 Mock | Toast/Tray/Widget capability、health、idempotent replace 尚未实现 | P3-04 / P3-05 |
| Package/dependency | 中央版本已有 NodaTime、EF/SQLite、Hosting、Mvvm、xUnit | P3-00 不引入新包；Toast OS API 的选型需单独 license/security/maintenance 审查 | P3-04 / P3-05 |
| Test isolation | P2/P2.75 已有 temporary SQLite/profile/root 和证据分层 | 任何新 Reminder 测试必须显式 data-root，不能默认 repo `.devdata` | 全部 P3 测试窗口 |

## 11. 风险登记

严重度约定：`B0` 代表不能进入 P3 runnable；`MAJOR` 代表必须在相关窗口关闭；
`MEDIUM` 代表可在不改变契约的前提下补齐。

| ID | 严重度 | 风险 | 当前判断 | 退出条件 |
|---|---|---|---|---|
| R-P300-01 | B0 | P2.75 契约仍是待接受候选；不能把 P3 migration 当成可直接上线 | 未关闭 | 总成记录 P2.75 acceptance，隔离 V01-V24 适用项和人工证据分开归档 |
| R-P300-02 | B0 | P2.75 target plan 当前只到 P2.5，若 P3-01 漏改会导致 Agent 以旧 schema 启动 | 已识别 | P3 migration ID、approved target、snapshot、verify allow-list、startup plan 一次串行更新 |
| R-P300-03 | B0 | P2 direct `TaskWorkspace` 仍存在，或未来 Reminder 接到第二 DbContext，形成双 writer | 已识别 | static scan + real-process handle proof：只有 Agent 有 writable handle；Main/Widget/legacy service 不可用于 production |
| R-P300-04 | B0 | due transaction 与 channel side effect 非原子，崩溃可能重复 Toast 或丢 delivery fact | 未关闭 | append-only delivery attempt/outbox 语义、logical replace key、crash/retry 测试通过 |
| R-P300-05 | MAJOR | 当前没有独立 TaskInstance/recurrence 模型，target/occurrence 若含糊会误取消未来 occurrence | 已给出 P3 一次性映射 | P3-01 adapter 明确 current Task.Id 映射；跨 occurrence cancellation regression 通过；不得提前造 recurrence schema |
| R-P300-06 | MAJOR | DST gap/fold、时区改变和跨午夜 RANGE 可能造成错误 TriggerAtUtc | 未关闭 | Noda deterministic resolver、zone provenance、P2 cross-midnight + DST matrix 通过 |
| R-P300-07 | MAJOR | Rule revision、schedule revision 与 P2 global revision 混淆，可能覆盖历史或产生假 revision | 已定义三者分工 | contract/unit + transaction tests 验证 no-op、stale、retry、supersede、journal batch |
| R-P300-08 | MAJOR | Task 删除级联删除 ReminderInstance，会破坏历史和 export | 已识别 | FK/SQL negative tests；删除只 cancel/disable future schedule，history 可读可导出 |
| R-P300-09 | MAJOR | repeat/Snooze 无上限或 retry 不幂等，可能造成 reminder storm 和无限数据 | 已识别 | bounded interval/count/chain policy，same-key retry 和 high/pin repeat tests |
| R-P300-10 | MAJOR | Quiet Hours、channel blocked/unavailable、core not triggered 三者混成一个状态 | 已定义语义 | channel capability/health matrix 和 recovery history inspection 通过 |
| R-P300-11 | MAJOR | 新 P3 wire command 与 RN-CJ-1/P2.5 1.0 混用，造成 hash/idempotency 不一致 | 未实现 | P3 command schema/golden vectors 只在 P3-04/06 接受后实现；未知字段/重试/receipt tests |
| R-P300-12 | MEDIUM | 历史报告有一套过时的 P2.5 01/02/03/04 编号 | 已记录 | 后续均引用 P2.5 contract 的 canonical map：01 Core、02 Storage、03 Agent、04 Transport |
| R-P300-13 | MEDIUM | P2 cross-midnight 手工证据在历史报告中仍 Pending | 未关闭 | P3-02 不得删除 regression；Alpha gate 前补齐并单独标注 user sign-off |
| R-P300-14 | MEDIUM | `ANIME_*` 枚举或现有 Mock 文案被误接到 P3 真实 scheduler | 已识别 | P3 active allow-list 只含 Task；Anime command/target 明确拒绝；Mock 不计入证据 |
| R-P300-15 | MEDIUM | export/controlled restore 若绕过 Candidate 会写 Active 或泄露 secret | 未关闭 | P3-08 只通过 Agent/P2.75 candidate pipeline；structured version/checksum/secret exclusion tests |

## 12. 测试矩阵与证据边界

本窗口未运行生产 Reminder 测试，因为没有生产 Reminder 代码可运行；以下是后续
窗口必须实现的矩阵。所有涉及数据库的测试都必须使用每次新建的隔离 temporary
root/profile；不得使用仓库 `.devdata`，不得接触 `D:\Anime\.devdata\reminnote.sqlite`。

| ID | 证据级别 | 最小用例 | 通过条件 | 失败信号 |
|---|---|---|---|---|
| T-P300-01 | contract/unit | Rule/Schedule/Instance 合法/非法 shape、UUID、enum、timestamp order、relative/absolute mixed columns | 非法值稳定拒绝；合法值 round-trip 不改语义 | 空 ID、未知 enum、负 count、混合 timing 被接受 |
| T-P300-02 | contract/unit | ANYTIME/TIME/RANGE；TIME lead；RANGE start/end；跨午夜 | TriggerAtUtc 与 Task shape 一致；ANYTIME 不生成默认 clock reminder | 改写 Task LocalDate、跨午夜落回同日或凭空提醒 |
| T-P300-03 | contract/unit | DST gap/fold、多个 timezone、timezone change rebuild | gap/fold 命中固定 policy；相对 schedule 保存 zone provenance；zone change supersede/rebuild | 依赖 OS 默认、同一 local time 随读取时漂移 |
| T-P300-04 | contract/unit | 同一 Rule 多 reminder、不同 Rule 同 purpose、Rule update | 每条 logical reminder 独立；旧 pending 保留为 SUPERSEDED；新 revision 可查询 | 旧 schedule 消失、history 被改写、同 purpose 被错误合并 |
| T-P300-05 | contract/unit + temp SQLite | due、重复扫描、Agent restart、PENDING consume | 一个 schedule 至多一个核心 Instance；重启从 durable PENDING 继续；无 duplicate logical attempt | timer 内存丢失、重复 Instance、已消费 schedule 再触发 |
| T-P300-06 | contract/unit + temp SQLite | 一次/多次 Snooze、Snooze 后 Rule update | 原 Rule/original schedule 不变；derived schedule cause=SNOOZE；链可恢复 | Snooze 修改 Rule、覆盖原 trigger、重启丢 snooze |
| T-P300-07 | contract/unit | UNREAD/READ/RESOLVED、Toast close、重复 action、冲突 action | 单向生命周期；close 只 read；相同 retry no-op；冲突动作拒绝 | READ 回退、close 直接 DONE、history action 被替换 |
| T-P300-08 | temp SQLite + Agent | DONE/Task result、PARTIAL、MISSED、未来 occurrence | 同 occurrence 的 pending+Snooze/repeat 全 cancel；其他 occurrence 不变；Instance 保留 | 只取消原 schedule、误伤 future occurrence、物理删历史 |
| T-P300-09 | contract/unit | LOW/NORMAL/HIGH、PIN 独立、repeat count/interval、wake policy | PIN+HIGH 合法；repeat count 有界且同 logical key；wake 能力失败不改 truth | priority 与 pin 耦合、无限 repeat、wake 失败伪造成功 |
| T-P300-10 | temp SQLite + fake channel | Quiet Hours、summary、HIGH/PIN+HIGH、跨午夜 Quiet Hours | schedule truth 不推迟；channel result 区分 suppressed/blocked/unavailable | Quiet Hours 改 schedule、所有高优先级被静默 |
| T-P300-11 | temp SQLite + fake/real channel | Toast/Tray/Widget/Sound/WakeTimer capability health | core Instance 保留；每次 delivery attempt append-only；同 logical Toast 可 replace | channel unavailable 导致 Rule/Instance 丢失或重复 storm |
| T-P300-12 | Agent real-process isolated | concurrent command、same idempotency retry、new RequestId、stale revision、timeout/reconnect | 单 writer、一个 global revision batch、receipt 可 reconcile、无 half write | direct SQLite write、假成功、revision gap/rollback 错误 |
| T-P300-13 | Candidate temp root | P2.75 source→backup→candidate→P3 forward migration→verify→promote；apply/FK/integrity/history failure | Active/backup/Candidate/marker 保持契约；非 READY 无普通 mutation | 在 Active 上 migrate、失败删库、无验证 promote |
| T-P300-14 | read-only real-process isolated | Main/Widget reminder list、snapshot revision、Agent unavailable、gap/full refresh | query-only read；旧 snapshot 按 stale/unavailable 显示；无 UI write fallback | UI `SaveChanges`、read PRAGMA/schema、Agent 失败切回 P2 writer |
| T-P300-15 | P2 regression | parser、Today、Task result/history、cross-midnight、reschedule、sort、continue | P2 既有语义不变；P3 schema 不把 Task plan 与 Reminder time 混为一谈 | P2 behavior regression、PARTIAL 误联动、Reminder 表提前进入 P2 |
| T-P300-16 | temp root + export tool | structured export、secret exclusion、version/checksum、controlled restore | export 可重放/校验；restore 只进 Candidate 并受 Agent/P2.75 gate | 裸 SQLite path、secret 泄露、直接覆盖 Active |
| T-P300-17 | real process + desktop manual | Agent restart、sleep/wake、Toast unavailable、Widget/Main drawer、真实用户处理 | evidence 分别记录；用户能看见事实/失败状态，不以 CLI 代替桌面 sign-off | 只通过 fake/CLI/UIA 就宣布 Alpha |

证据必须分成五类：

1. `contract/unit/fake`：只证明纯逻辑和接口。
2. `CLI/harness/temporary clone`：证明隔离 fixture/命令，不证明桌面体验。
3. `real-process isolated`：证明真实进程、PID、handle、ACL、DB/hash/state。
4. `normal desktop manual`：证明正常桌面和用户可见行为。
5. `user sign-off`：明确版本、范围、时间和未覆盖项。

## 13. 后续窗口 DAG 与文件 owner

### 13.1 DAG

```text
P2.5 acceptance + P2.75 acceptance / P2.75-04 evidence
                         |
                   P3-00 contract freeze
                         |
       +-----------------+------------------+------------------+
       |                 |                  |                  |
    P3-01             P3-02              P3-04              P3-07
 domain/schema      pure calculator      notification       policy/recovery
 migration          TaskTime mapping     abstraction        rules/tests
       |                 |                  |                  |
       +-----------+-----+------------------+                  |
                   |                                        |
                 P3-03                                      |
            Agent scheduler                                  |
                   |                                        |
                   +-------------------+--------------------+
                                       |
                         +-------------+-------------+
                         |                           |
                       P3-05                       P3-06
                 Toast/Tray/Widget          Drawer/Center/actions
                         |                           |
                         +-------------+-------------+
                                       |
                                      P3-08
                      completion/history/export/restore
                                       |
                                     P3-09
                         stability gate / Alpha report
```

图中 P3-08 位于 P3-05/P3-06 之后表示最终数据入口接线；P3-08 的 export/restore
准备可以与 P3-03、P3-05、P3-06 并行，只有 Candidate restore 和 completion
联动的最终接入必须由总成串行完成。

### 13.2 窗口定义

| 窗口 | owner | 前置 | 可并行内容 | 必须串行/不得越界 |
|---|---|---|---|---|
| P3-00 | 总成契约文档 | P2.75-04 结果与 P2.5/P2.75 接受状态 | 本文件审查、纯文档 | 不写 production code、migration、wire、UI |
| P3-01 | Core domain + Infrastructure persistence | P3-00 | entity/configuration、isolated schema tests 可准备 | EF migration、model snapshot、Solution、packages.lock、P2.75 target 由总成串行集成 |
| P3-02 | Core calculator + Agent application seam | P3-00；语义依赖 P3-01 | 纯计算、golden vectors、rebuild rules | 不直接打开 SQLite；不接 UI timer；不改 Task plan contract |
| P3-03 | Agent scheduler | P3-01 + P3-02；需 P3-04 dispatch seam | nearest wakeup、due transaction、restart/recovery fake | 不生成第二 writer；最终 transaction wiring 串行 |
| P3-04 | Core/Agent notification abstraction | P3-00 | channel capability、health、delivery/outbox contract | 不依赖 WPF/Toast API；不代替 Agent truth |
| P3-05 | Windows/Widget channel adapters | P3-04 + P3-03 trigger seam | Toast、Tray、Widget、taskbar/sound 独立准备 | 不改 Rule/Schedule/Instance 业务真相；真实 channel 集成串行验收 |
| P3-06 | Main/Widget UI | P3-02 + P3-03 + P3-04 | Drawer/Center、lifecycle/action、Snooze/DONE | 只读 query + Agent command；不接 SQLite writer，不改 control pipe |
| P3-07 | Agent/application + tests | P3-02 + P3-03 + P3-04 | Quiet Hours、priority/PIN/repeat、sleep/shutdown policy | recovery integration 和 user-facing semantics 串行确认 |
| P3-08 | Infrastructure/Agent/tools | P3-01 + P3-02 + P2.75 | completion linkage、history projection、export、controlled restore | restore 只能进入 Candidate/P2.75；数据入口串行接线 |
| P3-09 | 总成/测试/报告 | P3-03 + P3-05 + P3-06 + P3-07 + P3-08 | 无 | 稳定门禁、真实进程、桌面人工、用户 sign-off 串行 |

### 13.3 共享文件和安全边界

以下内容属于总成串行区，任何 P3 独立窗口不得抢改：

- `ReminNote.sln`、`Directory.Build.*`、`Directory.Packages.props`、所有 `packages.lock.json`。
- EF migration、`ReminNoteDbContextModelSnapshot`、P2.75 `DefaultPlan`、公开启动/切换接线。
- 公共 `App.xaml.cs`、`MainWindow.*`、跨窗口共享的 Agent/Widget lifecycle wiring。
- `ReminNote_MASTER_DEVELOPMENT_PLAN.md`、`ARCHITECTURE.md`、`PRODUCT_RULES.md`、`DECISIONS.md`、`SECURITY.md`、`TESTING.md` 和既有报告/评审材料。
- `reviews/`、`second-review/` 中的任何文件。

并行窗口只能在自己的 worktree 修改明确 owner 文件；一旦发现需要改动上述文件，
停止本地扩张并提交集成请求，不以“顺手修复”方式越界。

## 14. P3-00 接受门与下一步

### 14.1 本契约可以被接受的条件

总成接受记录至少要确认：

1. 三层对象的职责、字段、identity、revision、terminal state 和历史不可变语义。
2. `ANYTIME` / `TIME` / `RANGE`、跨午夜、DST、timezone change 和 absolute UTC 的时间决定。
3. Snooze、repeat、purpose、lifecycle、DONE/Task result cancellation、Quiet Hours、priority/PIN/wake 的行为。
4. Agent 唯一 writer、P2.5 global revision/idempotency/read-only、P2.75 Candidate/fail-closed 的依赖不被削弱。
5. channel attempt 不覆盖 Instance truth，且通知不可用时的事实分类明确。
6. B0/MAJOR 风险有 owner、退出条件和对应测试；P3-01/02/03/04 的接口不会各自发明第二套语义。
7. DAG、文件 owner、隔离 root 和证据分类被后续窗口引用。

### 14.2 接受后第一批派发建议

- 先派 P3-01、P3-02、P3-04、P3-07 的 non-runnable/纯测试准备；不创建表或启动生产 Reminder。
- 由总成单独安排 P3-01 migration/model snapshot/target plan 的串行集成和 P2.75 Candidate 验证。
- P3-03 只在 P3-01 schema 和 P3-02 calculator 契约可用后接入 Agent writer。
- P3-05/P3-06 的真实桌面通道不得先于 Agent scheduler 的 durable truth 和 read-only/query contract。
- P3-09 之前不得称为 P3 Alpha，不得用 Mock UI、fake、CLI、旧 binary 或删除数据库清除风险。

## 15. 本窗口验证记录

已完成的只读核对：

- `HEAD`、默认分支提示、`main`/`origin/main` 和工作树状态核对；当前 HEAD 与用户指定 P2.75 基线一致。
- 源码、项目引用、中心包版本、现有 Task/P2.5/P2.75 路径和 Reminder 关键字审查。
- 按第 1.1 节完整阅读计划、架构、产品规则、决策、安全、测试、并行开发、P2.5/P2.75 契约和相关报告。
- 只检查受保护数据库路径是否存在；未打开、读取、复制、连接或写入 `D:\Anime\.devdata\reminnote.sqlite`。
- 未修改既有 `reviews/`、`second-review/`、P2/P2.5/P2.75 原始材料；未修改任何 production source、migration、Solution 或包锁文件。

本文件落地后的验证应只使用新建的隔离临时 root 做路径/文档静态检查；本 Slice
没有需要连接 SQLite 的生产测试，因此不以任何真实数据库运行结果作为 P3-00 证据。
