# P2-Persistence 持久化与写入边界 Slice

- 阶段：P2 Real TODAY / Widget Task loop
- 日期：2026-08-28
- 状态：本窗口实现完成；等待总成窗口修复既有 Widget 编译阻塞
- 责任范围：Core Task 持久化字段与应用契约、Task 历史、工作日设置、Task 应用服务、SQLite migration、TaskWorkspace、本地跨进程 Task 写门及独立测试
- 前置契约：`docs/slices/P2-00-integration-and-contract-brief.md`

## 本 Slice 完成范围

- `Task` 增加非负 `SortOrder` 和可空 `ContinuedFromTaskId`，新建/改名继续执行 500 字符上限；P1 重 hydration 保留历史超长标题，避免升级截断旧数据。
- 继续关系收紧为 `Task.CreateContinuation`：只有当前结果为 `PARTIAL` 且时间形状为 `RANGE` 的源任务可以产生新的 UUID v7 Task；应用服务同时写入 `Continued` 历史快照。
- `TaskHistoryRecord` 改为带校验的应用边界对象，限制 Task ID、快照 ID、事件类型、时间顺序和 continuation 元数据；`TaskHistoryEntity` 在读写边界重新通过 Core 校验。
- `TaskRepository` 的新增、更新、删除均拥有显式事务；新增/更新可把 Task 与历史快照作为一个 unit of work 写入，失败时回滚并清空 ChangeTracker，避免半写和失败实体残留。
- `task_history` 保存结果、旧计划、排序、继续关系和 P1 导入快照；外键、形状、结果、时间、排序和 continuation 检查约束与索引均落到 SQLite。硬删除 continuation 源时，SQLite trigger 先移除会因 `related_task_id` 非空而失效的 `Continued` 事件，再由可空外键清理子任务快照关系并保留子任务后续历史。
- P1 `InitialTaskSchema` 到 `P2TaskLoop` 使用 forward migration：重建 `tasks` 以加入字段/约束，保留 ID、标题、计划和结果；已有结果各回填一个 `ImportedResult`；默认写入单例 `app_settings`；migration `Down` 可恢复 P1 表形状。
- `AppSettingsRepository` 持久化 00:00–23:59、整分钟的工作日边界；`TaskWorkspace` 只接受含 `.git` 与 `ReminNote.sln` 的仓库根目录，固定使用 `.devdata/reminnote.sqlite`，初始化成功前拒绝正常读写。
- `CrossProcessTaskWriteGate` 使用同名本地内核 Semaphore 覆盖完整应用读-改-写；由于 application lease 跨越 `await`，Semaphore 可由后续 continuation 安全释放，避免 Windows Mutex 的线程归属问题；超时抛出 busy，取消令牌可中断等待。没有加入 Agent、Named Pipe、IPC、WAL、Change Journal 或 Reminder/Anime/Sync。

## 不在本 Slice 的范围

本 Slice 未修改 Today 查询、Parser、Main UI 或 Widget UI。Widget 当前工作树中的既有 `WidgetViewModel.cs` 改动仍有编译错误，由总成窗口处理；本 Slice 不绕过该边界修复 UI。

## 自动化证据

以下命令均在 Windows、.NET 10.0.100 SDK、Release 配置下执行：

1. Core 与 Infrastructure Release 构建：通过，0 warning / 0 error。
2. 临时隔离 xUnit v3 executable（仅加载 Core、Infrastructure、P2 边界测试以及不依赖 UI 的既有 Core/SQLite/application 测试）：82/82 通过（含本次新增删除边界、历史快照完整性、脏历史读边界和修复 migration 幂等/回滚测试）。
3. P2 边界测试包含：P1 长标题与 PARTIAL RANGE 升级、`ImportedResult` 回填、升级失败回滚、降级恢复 P1 表形状、Task+history 原子回滚、历史事件顺序、继续关系、负排序拒绝与稳定排序、工作日设置、显式开发数据库路径、TaskWorkspace 初始化、跨进程写门、continuation 源删除后的历史保留和 P2 修复 migration 回滚。
4. EF 运行时模型与 `ReminNoteDbContextModelSnapshot` 差异检查：`differences=0`。
5. 临时 SQLite 文件实测：升级后的表为 `__EFMigrationsHistory`、`__EFMigrationsLock`、`app_settings`、`task_history`、`tasks`；P1 Task ID/标题/结果保留，历史为 `ImportedResult`；降级后 `tasks` 恢复为 P1 列集合。
6. `./scripts/build.ps1 -Configuration Release`：locked restore 成功；Core、Infrastructure、Bootstrap、Agent、P1 harness 已构建，随后在既有 `src/windows/ReminNote.Widget/ViewModels/WidgetViewModel.cs` 处失败。
7. `./scripts/test.ps1 -Configuration Release`：locked restore 成功，随后在同一既有 Widget 文件的测试项目编译阶段失败，未能进入仓库测试 executable；因此以第 2 项隔离 executable 作为本 Slice 的可运行测试证据。

## 人工验收步骤

1. 在仓库根目录执行 `./scripts/build.ps1 -Configuration Release` 和 `./scripts/test.ps1 -Configuration Release`；总成窗口应先修复 Widget 编译错误，预期两条命令均成功且无 warning。
2. 在一个 P1 数据库副本上启动应用，确认 migration 不删除原 Task；重启后检查原 ID、标题、计划、结果仍在，并且已有结果可在历史中看到一条导入记录。
3. 在同一仓库根目录分别启动 Main 与 Widget，先由一端创建 Task，再由另一端刷新/重启读取；确认双方使用同一个 `.devdata/reminnote.sqlite`，不出现生产路径或 Token 文件。
4. 创建跨午夜 RANGE，记录 `PARTIAL`，执行“继续”；确认原 Task 不变，新 Task 为新 UUID 且 `continued_from_task_id` 指向原 Task。对非 PARTIAL 或非 RANGE 源执行“继续”应显示失败且数据库 Task 数量不变。
5. 创建同一日期、同一 Today 分组的两个 Task，执行排序和重启读取；确认排序值非负、顺序稳定，排序不会改计划时间或日期。输入负排序值应失败并保留原顺序。
6. 将工作日边界设为 `01:00`，检查边界前后的逻辑工作日；确认只影响逻辑 Today，不改 Task 的日历日期或 UTC 时间。
7. 在一个写操作持有本地写门时，从另一个进程发起写操作；超时应明确失败，释放写门后重试应成功，失败前后 Task 和 history 均不产生半写。

## 失败判定

- P1 数据库升级删除原 Task、截断旧标题、丢失结果或没有导入历史。
- Task 更新成功但对应历史缺失，或异常后只留下 Task/历史的一半。
- 非 `PARTIAL RANGE` 产生 continuation、continuation 自指、负排序落库，或相同排序没有稳定 tie-breaker。
- Workspace 未确认仓库根目录就写库，使用 `.devdata` 以外路径，或初始化失败后仍允许普通读写。
- 写门超时静默覆盖另一进程的读-改-写，取消等待不返回，或引入 Agent/IPC/Reminder/Anime/Sync。
- 修改 Today 查询、Parser、Main UI、Widget UI，或触碰 `reviews/`、`second-review/`。

## 已知限制与后续边界

- 本 Slice 的数据库写入保护是同机本地 Semaphore；它不是 P2.5 Agent single writer，也不提供跨机器同步、版本冲突检测或 Change Journal。
- P2 仍使用硬删除；删除 Task 会按外键清理其自身历史；若删除 continuation 源，相关 `Continued` 事件会被 trigger 删除以满足历史表约束，子任务及其后续历史保留且可空源外键置空，不提供 Trash 或完整审计保留策略。
- P1 超长标题为兼容性保留例外；新建和改名仍由 Core 拒绝超过 500 字符。SQLite 约束负责非空，长度规则由当前写入应用边界负责。
- Main/Widget 的真实双宿主接线、刷新和 UI 手工验收依赖总成窗口完成既有 UI 改动；本 Slice 仅提供可复用的应用/query/workspace 边界。
