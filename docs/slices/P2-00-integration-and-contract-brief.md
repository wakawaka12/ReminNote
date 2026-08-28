# P2-00 集成与契约冻结简报

- 阶段：P2 Real TODAY / Widget Task loop
- 日期：2026-08-28
- 状态：契约已由用户确认；模块提交已合并；总成风险项 2/3/4 已在接管期间关闭（Main live 组合、无日期真实查询、改期/排序入口），剩余复核项为双宿主真实进程人工验收与真实库迁移幂等验证
- 负责窗口：主线总成窗口
- 前置基线：`codex/p0-integration@1737305`，包含 P1 合并提交 `7b1e67c`

## 当前合并检查点

当前分支已保留以下本地提交历史，文档中的“已合并”以这些提交为准：

- `89b2799`：P2 Task 持久化、历史、migration、工作区和写入边界；
- `98b3a76`：P2-01 Today 查询与分组；
- `effddf3`：P2-02 确定性 TaskParser；
- `42f6609`：P2-03 Main Today ViewModel 的 live Task 闭环；
- `6c51799`：P2-04 Widget live Task loop；
- `e4b626e`：Widget 项目 `packages.lock.json`。

这些提交证明模块代码和各自的独立测试检查点已经进入当前分支，不等同于最终 Main/Widget 双宿主验收已经通过。

## 目标

在派发 P2-01 至 P2-05 时冻结 Today 查询、时区/工作日、Parser、结果/历史、改期/排序/继续关系、Main/Widget 写入边界和 migration 范围。P2 仍是本地 Task loop，不提前进入 P2.5 Agent/IPC/Single Writer/WAL/Change Journal，也不实现 Reminder、Anime 或 Sync。

## 已确认的产品契约

### 时间、时区和工作日

- Task 仍只有 `ANYTIME`、`TIME`、`RANGE` 三种时间形状。
- 计划日期是本地日历日期；跨午夜 RANGE 由 `RangeEnd < RangeStart` 推导，并始终归属开始日期。结束日期只用于计算区间结束时刻，不改写任务的计划日期。
- 当前时刻由 Noda Time `IClock` 提供，经 `IUserTimeZoneProvider` 转换为用户本地时间。
- P2 默认使用系统时区，不提供自定义时区设置；测试必须可注入固定时区和固定时钟。
- 工作日边界默认 `00:00`，以分钟为粒度，允许 `00:00` 至 `23:59`；本地时间早于边界时，逻辑工作日为前一自然日。
- 工作日边界持久化在同一开发 SQLite 的单行 `app_settings` 中；它改变 TODAY/复盘/ANYTIME 归属，不改变日历日期或绝对时间。
- “今天/明天/后天”按调用方传入的逻辑工作日解释；显式 `yyyy-MM-dd` 仍按日历日期解释。

### TODAY Read Model

- 查询必须使用调用方传入的 `Instant Now`；不能在查询逻辑中散落 `DateTime.Now` 或 `DateTime.Today`。
- 当前实现的 `TodayQueryRequest` 使用 `Now` 和可选的已解析 `Workday`；P2 只实现默认分组，不提前实现计划中其他分组模式。
- 默认展示逻辑今天的计划和未完成的历史计划；未来日期不进入 TODAY。当前逻辑工作日已完成的计划仍显示在 `COMPLETED`，已完成历史计划不进入活动 Today 列表。
- 默认分组为 `OVERDUE`、`MORNING`、`AFTERNOON`、`EVENING`、`ANYTIME`、`COMPLETED`；建议时段边界为 `MORNING < 12:00`、`AFTERNOON 12:00–18:00`、`EVENING >= 18:00`。
- 未完成的过去 `TIME`、已结束的 RANGE 和更早日期的 `ANYTIME` 进入 `OVERDUE`，但不自动写入 `MISSED`。`TIME` 在恰好计划时刻仍未逾期，超过计划时刻才进入 `OVERDUE`。
- RANGE 在结束后无结果显示 `AWAITING RESULT`，并计入顶部 `NEEDS REVIEW`；它仍保留在原计划时段/位置。
- RANGE 在计划区间内显示为进行中计划，但不暗示应用知道用户实际工作状态。跨午夜 RANGE 在换日后仍必须可见并保留开始日期/开始时段，直到次日结束时刻才进入等待结果。
- 已有结果的 Task 进入 `COMPLETED` 分组，具体结果仍显示 `COMPLETED`、`PARTIAL` 或 `MISSED`。
- 列表顺序按分组、计划日期、持久化 `sort_order`、计划时间和 UUID 字符串稳定排序；`NEEDS REVIEW` 是独立摘要，不把任务从原计划分组挪到列表顶部。

### 结果、历史、改期和继续

- `DONE` 等价于记录 `COMPLETED`；`RecordedAt` 只代表用户记录动作的时间，不代表实际完成时间。
- 当前 `ResultRecord` 继续采用覆盖式当前状态；每次结果记录都追加不可变 `task_history` 事件，因此重新记录不会抹掉旧结果事实。
- 历史事件类型为 `ResultRecorded`、`PlanChanged`、`Continued`、`SortOrderChanged` 和迁移用的 `ImportedResult`。`PlanChanged` 保存旧计划快照；`ResultRecorded` 保存写入后的结果快照；排序/继续事件保存对应边界的快照。
- 未来或尚未开始的 Task 可以直接改日期/时间，不生成失败结果。已经过时但没有结果的 Task 也只能由用户明确执行改期；改期保存旧计划历史，不自动变成 `MISSED`。
- 已有结果的 Task 仍禁止修改计划时间，沿用稳定错误 `task.time_spec.changed_after_result`；改名仍允许。P2 继续使用 P1 的硬删除语义，不实现 Trash，也不承诺删除后的历史审计保留。
- `PARTIAL` 只允许用于 RANGE。继续工作时创建新的 UUID v7 Task，并通过 `continued_from_task_id` 指向原 Task；原 Task、原计划和原结果保持不变。
- “计划改期”和“列表排序”是两种不同操作。列表排序只作用于同一计划日期和同一 Today 分组，使用持久化 `sort_order`；跨组移动必须改日期/时间，不能用排序值伪造改期。

### 确定性 TaskParser

Parser 的纯函数输入为文本和调用方传入的逻辑今天，不读取系统时钟或数据库。基础语法为：

```text
[日期] [时间或范围] 标题
```

支持：

```text
今天 / 明天 / 后天 / yyyy-MM-dd
HH:mm
HH:mm-HH:mm
HH:mm–HH:mm
```

规则：

- 没有日期和时间：逻辑今天的 `ANYTIME`；
- 只有日期：指定日期的 `ANYTIME`；
- 只有时间：逻辑今天的 `TIME`；
- 范围结束等于开始时拒绝；跨午夜由领域模型推导；范围两端不能用空格拆开；
- 不识别的日期/时间形状、缺少标题或非法日期/时间必须返回稳定错误并且不写库；
- P2 不实现 `#标签`、`!优先级` 字段；遇到以这些符号开头的保留语法直接拒绝，不能静默丢弃；
- 标题上限冻结为 500 个字符，超长拒绝，不截断。P1 已存在的超长标题由重 hydration 保留，不因 P2 migration 截断。

当前 Parser 使用的稳定错误码包括 `task.parser.empty`、`task.parser.reserved_syntax`、`task.parser.date.invalid`、`task.parser.range.invalid`、`task.parser.time.invalid`、`task.parser.title.required` 和 `task.title.too_long`；领域拒绝仍由 Core 的稳定错误码表达。

## 数据库、迁移和写入边界

P2 使用从 `InitialTaskSchema` 到 `P2TaskLoop` 的 forward migration，不以删除数据库代替升级。SQLite 表约束要求 UUID v7、时间形状、结果元数据、时间顺序、非负排序和合法 continuation 关系；已有结果 Task 在升级时各建立一条 `ImportedResult` 历史快照，保留现有 Task ID、标题、计划和结果。默认写入 `app_settings` 单例行。`Down` 可恢复 P1 的 `tasks` 表形状，但 P2 的历史和设置会随回退移除，因此回退不是历史保留方案。

允许新增的 Task 相关对象为：

- `task_history`：结果、旧计划、排序、继续关系和 P1 导入快照事件；
- `app_settings`：单行工作日边界；
- `tasks.sort_order`；
- `tasks.continued_from_task_id`。

不得创建 Reminder、Anime 或 Sync 表。

P2 的两个宿主都必须：

- 通过同一 `ITaskApplicationService` / `ITaskQueryService` / `ITodayQueryService` 访问业务；
- 不在 ViewModel 中直接访问 `DbContext`、SQL 或 EF entity；
- 只使用通过 `.git` 与 `ReminNote.sln` 校验的仓库根目录下的 `.devdata/reminnote.sqlite`；初始化未成功时拒绝正常读写；
- 在本地直接调用 application service，不引入 Agent、Named Pipe 或 IPC；
- 对完整读-改-写操作使用跨进程写门，防止 P1 已知 RMW 覆盖；锁超时必须失败并保持原数据，不静默覆盖；
- 在启动、激活、写入后和短周期轮询时刷新 Today Read Model；P2 不引入 Change Journal 或推送同步。

当前实现的写门是命名跨进程 `Semaphore(1, 1)`，名称为 `Local\\ReminNote.P2.TaskWrite`，默认等待 5 秒，超时抛出 `task.write_gate.busy`。契约要求的是跨进程完整写保护，选择 Semaphore 而非线程归属型 Mutex 是因为应用租约跨越 `await`，可能由后续 continuation 线程释放；这不改变失败不覆盖的产品语义。P2.5 仍必须设计命令串行化、版本/冲突检测和 Change Journal，并移除 Main 的直接业务写入路径。

## 独立窗口所有权

| 窗口 | 所有权 | 明确禁止 |
|---|---|---|
| P2-00 | ADR、根级活文档、Solution/包锁、migration、DI/启动组合、最终集成与验收 | 修改 `reviews/`、`second-review/` |
| P2-01 | `Core/Today/**`、Today query service、Today 领域/查询测试 | 修改 migration、WPF 页面、根级配置 |
| P2-02 | `Core/Tasks/Parsing/**`、Parser 测试 | 修改 UI、数据库和根级配置 |
| P2-03 | `Windows/Features/Today/**`、Main Today ViewModel 测试 | 修改 `App.xaml.cs`、`MainWindow.*`、Widget |
| P2-04 | Widget Task loop 源码和 Widget 测试 | 修改根级包版本、Solution、Main TODAY |
| P2-05 | 新增测试矩阵、人工验收稿、P2 Slice/阶段报告 | 修改产品语义、migration、既有审查材料 |

公共 `TaskApplicationContracts.cs`、项目引用、中央包版本、lock 文件、migration 和 DI 组合由 P2-00 串行处理。独立窗口只能在自己的 worktree 中本地提交，不得 push、merge、rebase 或 release。

## 集成顺序

```text
P2-00 契约冻结
        ↓
P2-01 Today + P2-02 Parser（可并行）
        ↓
P2-00 串行整合公共契约、持久化和 forward migration
        ↓
P2-03 Main TODAY + P2-04 Widget（可并行）
        ↓
P2-05 测试、手工验收和报告
        ↓
P2-00 最终集成与用户验收
```

## 当前实现核对与总成风险

本轮只恢复和修订公开文档，不恢复总成 stash 中的源码或项目变更。对当前合并树的只读核对结果如下：

1. P2 持久化、Today 分类器、Parser、Main Today live ViewModel、Widget live ViewModel/启动入口和 Widget lock 文件均已进入当前分支；Widget App 已创建并初始化 `TaskWorkspace`，使用两秒 DispatcherTimer 轮询，并在写入后刷新。
2. 【已关闭】Main 的 live `TodayPageViewModel` 原本仍挂在 P0 Mock 服务上；总成接管期间 `App.xaml.cs` 已注册真实 `TaskWorkspace`（校验 `--repo-root`）、`ITaskApplicationService`/`ITaskQueryService`/`ITodayQueryService`/`IClock`，并在显示窗口前执行 `Initialize()`，`TodayFeatureRegistration` 的 DI 构造改用真实 query/application/clock（提交 `da2fc4d`）。
3. 【已关闭】Main Today UI 原本没有计划编辑/改期或持久化排序的用户命令；接管期间已在选中任务面板补齐"改期/上移/下移"（改期复用冻结 `TaskParser` 与 `UpdateAsync` 生成 `PlanChanged` 历史；排序仅同日期同分组相邻交换 `sort_order`，跨日期/越界拒绝写库），并有 4 项 VM 测试（见 `docs/reports/P2-总成接管记录-2026-08-28.md` 4.1）。
4. 【已关闭】Today SQLite 端到端无日期查询：`TaskQueryService` 原把 `TaskQuery` 的空 `PlanDate` 翻译成 `local_date IS NULL`，真实 SQLite 上 Today 全量读取恒为空；已改为仅显式日期才过滤，并新增真实 SQLite 回归测试（提交 `3b33875`，整仓 145/145 通过）。
5. 【待执行】`TaskWorkspace` 初始化边界、migration 保留和 named Semaphore 写门的隔离测试已存在，但尚未以 Main/Widget 两个真实进程完成人工验收（含重启保留、RANGE NEEDS REVIEW、跨午夜、非法 Parser、数据路径核对）。

以上第 2-4 项由总成接管窗口（ZCode）在 Codex 额度中断期间关闭，证据与取舍记录于 `docs/reports/P2-总成接管记录-2026-08-28.md`；第 5 项仍未执行，在该项关闭前，P2 状态报告为"模块已合并、Main live 组合与真实查询已接通、双宿主人工验收待执行"。

## P2 验收门槛

- locked restore、Release build、P0-07 门禁成功，且没有新增未解释警告；
- 真实 `.devdata/reminnote.sqlite` forward migration 保留 P1 数据，重复执行幂等；
- Main TODAY 在正式 live 组合接通后能创建、查询、完成、记录 RANGE 结果、改期、排序、继续和重启读取；
- Widget 能读取同一 Task 数据，执行允许的 DONE/结果/Quick Add 操作，并在规定刷新窗口内看到 Main 的变化；Snooze/Reschedule 不得擅自改期；
- 自动化覆盖工作日边界、时区、跨午夜、Today 分组、Parser 成功/失败、结果历史、改期保护、继续关系、排序、无日期真实查询和写门超时；
- 手工验收覆盖 Main/Widget 双进程、SQLite 重启保留、RANGE NEEDS REVIEW、跨午夜、非法 Parser、改期/继续、数据路径和审查文件不变；
- 失败包括：P0 Mock 仍被当作真实入口、结果自动变 MISSED、跨午夜换日丢失、Parser 静默丢字段、UI 直写数据库、无日期 SQLite 查询为空、migration 删除原库、未知路径写入、写门超时静默覆盖或审查材料被修改。

## 大模块提交检查点

本阶段采用本地、可审查的检查点提交：

1. P2-00 契约、公共契约、migration/DI 基础设施完成后提交；
2. Today/Parser/持久化 Task loop 完成并有自动化证据后提交；
3. Main TODAY 真实入口完成并通过构建/手测后提交；
4. Widget 真实入口完成并通过双宿主手测后提交；
5. P2 最终自动化、人工验收和阶段报告完成后提交。

每次提交说明必须对应一个已完成的大模块，包含实际命令与结果、人工测试步骤、失败判定和剩余风险。提交只更新本地 Git 历史；没有用户另行授权时，不 push 到 GitHub、不 tag、不 release，也不修改 `reviews/` 或 `second-review/`。
