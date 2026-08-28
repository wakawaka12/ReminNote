# P2-05 最终验收与文档简报

- 阶段：P2 Real TODAY / Widget task loop
- 日期：2026-08-28
- 验收代码基线：`d6edc0e`（验收开始时本地 `codex/p0-integration` 指向同一提交；本窗口 worktree 为 detached HEAD）
- 状态：首轮 Release 构建、160/160 自动化测试、P0-07 门禁和真实空 `.devdata` schema 双次启动已通过；排序修复后总成复跑为 162/162，且已完成条件性排序 UIA；正常桌面 GUI、真实已有数据升级和跨午夜时序仍待用户执行
- 写入边界：本 Slice 只新增本文件、阶段报告，并追加 `docs/reports/P2-总成接管记录-2026-08-28.md`；不修改源码、测试、根级活文档或 `reviews/`、`second-review/`

## 目标

以当前合并工作树的实际代码、提交和命令输出完成 P2 最终验收材料，给出可复核的自动化证据、真实开发库 schema 证据、人工验收步骤和明确的 P2/P2.5/P3 边界。

## 范围

- 汇总 P2-00 契约冻结、P2-01 Today 查询、P2-02 TaskParser、P2 持久化/历史/迁移/写入边界、P2-03 Main 和 P2-04 Widget 的实际提交与范围；
- 记录 Release build/test、P0-07、`git diff --check` 的当前输出；
- 对仓库根目录 `.devdata/reminnote.sqlite` 做不破坏数据的两次 schema/migration 启动检查和只读完整性检查；
- 给出 Main/Widget 双宿主、Quick Add、DONE/结果、RANGE/NEEDS REVIEW、跨午夜、改期/排序/继续、重启和错误路径的人工验收稿。

## 不在范围内

- 不实现或修改 Reminder、ReminderRule/Schedule/Instance、Scheduler、Toast、Tray 通知或 Notification Health；
- 不实现 Agent 业务服务、P2.5 命令 IPC、WAL、Change Journal、版本冲突检测或 Single Writer 迁移；
- 不实现 Anime 网络/Bangumi、Sync、下载、播放或新的产品语义；
- 不把 P0 的 Anime/Widget reminder 文案和 Mock 交互当作真实 Reminder/Anime 证据；
- 不在本窗口点击 WPF GUI，也不声称 GUI、重启或真实已有 P1 数据迁移已通过。

## 验收规则

1. Task 仍只有 `ANYTIME`、`TIME`、`RANGE`；跨午夜 RANGE 由 `RangeEnd < RangeStart` 推导并归属开始日期。
2. Today 使用调用方的 Noda Time `Instant`、系统时区和持久化工作日边界；未完成的 RANGE 结束后为 `AWAITING RESULT`/`NEEDS REVIEW`，不自动写 `MISSED`。
3. Quick Add 只接受确定性日期/时间语法；P2 不实现 `#标签`、`!优先级`，保留语法必须报错且不得写库。
4. 结果记录的 `RecordedAt` 只表示记录动作时间；结果、旧计划、排序和继续关系写入 `task_history`，已有结果的 Task 不得改期。
5. Main 与 Widget 在本地直接复用同一 `TaskWorkspace`、application/query contracts 和开发数据库，并使用本地跨进程写门；这不是 P2.5 Agent 单写者。
6. 所有结论以当前 worktree 实际输出为准；文档中的既有数字不能覆盖本轮命令结果。

## 复用与依赖检查

- 不新增依赖。复用 P1 已有的 EF Core/SQLite、Noda Time、Core snapshot/application contracts、`TaskWorkspace`、`P1ManualHarness` 和 xUnit v3 executable 测试入口。
- schema 检查使用已构建的 `P1ManualHarness schema`；只读核对使用 `Microsoft.Data.Sqlite` 的 `Mode=ReadOnly` 连接。
- 当前 `.devdata` 在检查前没有数据库文件，因此第一次 schema 只建立空开发库；全程没有 purge、删除、覆盖或业务 create/update/delete。

## 自动化与真实库证据

- `scripts/build.ps1 -Configuration Release`：退出码 0；还原和 8 个项目构建成功，输出无 warning/error。
- `scripts/test.ps1 -Configuration Release`：首轮 xUnit v3 executable 实际 **160/160**；排序修复后总成重新执行为 **162/162**，Errors 0、Failed 0、Skipped 0、Not Run 0。
- `scripts/verify-p0-07.ps1`：退出码 0；`213` 个资源键、`7` 个锁定项目、`1` 个测试项目及 Shell/TODAY/ANIME/Widget 标记通过。
- `git diff --check`：退出码 0；无 whitespace errors。
- 两次 `P1ManualHarness schema --repo-root <当前仓库根目录>` 均退出码 0，表集合均为 `__EFMigrationsHistory`、`__EFMigrationsLock`、`app_settings`、`task_history`、`tasks`。
- 第二次后只读核对：`integrity_check=ok`、`foreign_key_check` 行数为 0；migration history 为 `20260828025922_InitialTaskSchema`、`20260828120000_P2TaskLoop`、`20260828130000_P2ContinuationDeleteBoundary`；业务行数为 `app_settings=1`、`task_history=0`、`tasks=0`。
- 两次 schema 的 SQLite 主文件 SHA256 不同，因此本 Slice 不把“字节哈希相同”当作幂等条件；以两次退出码、同一 migration history、同一表集合和只读完整性检查作为证据。当前库为空，不能由此宣称已有 P1 数据的真实升级保留已通过。
- 排序修复提交为 `8a86a002e3148ff58570aeba4971fc3f462ea283`：修复同日期同分组默认 `sort_order=0` 的 `0↔0` no-op，并新增回归测试；重复排序值先按当前可见顺序归一化后交换，同时保留跨日期、跨分组及已有结果保护。总成修复后 Release build 为 8 个项目、0 warning/0 error，P0-07 为 `213 resource keys/7 locked projects/1 test project`，`git diff --check` 通过。

## 人工验收（待用户执行）

当前窗口没有可真实点击 WPF Main/Widget 的 GUI 操作能力，以下项目全部标为“待用户执行”。

### 环境

```powershell
$repo = 'C:\Users\EMT\.codex\worktrees\98b7\Anime'
$main = Join-Path $repo 'src\windows\ReminNote.Windows\bin\Release\net10.0-windows\ReminNote.Windows.exe'
$widget = Join-Path $repo 'src\windows\ReminNote.Widget\bin\Release\net10.0-windows\ReminNote.Widget.exe'

Start-Process -FilePath $main -WorkingDirectory $repo -ArgumentList @('--repo-root', $repo)
Start-Process -FilePath $widget -WorkingDirectory $repo -ArgumentList @('--repo-root', $repo)
```

执行前关闭同一用户下已经运行的 Main/Widget，确认测试使用 `.devdata/reminnote.sqlite`，不要指向生产数据。

### Test 1 — 双宿主同库与 Quick Add

1. 按上面的命令启动 Main 与 Widget。
2. Main 进入 TODAY，点击 `Quick Add`，输入 `今天 18:00 P2-05 Main 同库任务`，点击 `添加到 TODAY`。
3. Widget 保持 TODAY 页面，等待不超过 2 秒的轮询周期；再用 Widget Quick Add 输入 `明天 18:00 P2-05 Widget 同库任务` 并点击 `添加`。
4. 回到 Main 点击 `刷新 TODAY`。

预期：两端都能看到对方写入的 Task，日期/时间/标题一致；成功提示表明走真实本地 Task application/query 边界。失败：一端看不到另一端的 Task、出现第二个数据库位置、刷新超过一个轮询周期仍不一致，或 Quick Add 失败后仍新增行。

### Test 2 — DONE、RANGE 结果与重启

1. 在 Widget TODAY 选中开放 Task，点击 `完成`；在 Main 点击 `刷新 TODAY`。
2. 用 Main Quick Add 创建 `今天 23:00-01:00 P2-05 RANGE 验收`，或用一个已结束的测试 RANGE。
3. 在 Main 选中该 RANGE，确认状态为 `AWAITING RESULT`，点击 `记录 PARTIAL` 或 `记录 MISSED`。
4. 关闭并重新启动 Main 和 Widget，仍传入同一个 `--repo-root $repo`。

预期：DONE/结果写入后两端刷新，Task 进入完成/结果状态；未记录的结束 RANGE 计入 `NEEDS REVIEW` 且不自动变 `MISSED`；明确记录后复盘计数清除；重启后结果仍在。失败：结果丢失、自动写 MISSED、历史计划改变、重启后读不到同一 Task 或刷新不收敛。

### Test 3 — 跨午夜归属

1. 在一个实际日期的 `23:00` 前，用 Main Quick Add 创建当天的 `23:00-01:00 P2-05 跨午夜` RANGE。
2. 次日 `00:30` 点击 `刷新 TODAY`，检查任务的开始日期和晚间分组。
3. 次日 `01:00` 或之后再次刷新。

预期：`00:30` 任务仍可见、仍归属原开始日期/晚间位置且不计入 NEEDS REVIEW；`01:00` 后才显示 `AWAITING RESULT` 并计入 NEEDS REVIEW。失败：换日后消失、归到次日、提前复盘或自动 MISSED。若不能等待真实时序，自动化 `TodayQueryTests` 已覆盖固定 Instant，但不能替代本项 GUI 人工验收。

### Test 4 — Main 改期、排序与继续

1. Main 选中一个尚未记录结果的 live Task，点击 `改期`；在日期框输入 `明天`，时间框输入 `20:00`，点击 `确认改期`。
2. 在同一日期、同一 Today 分组创建两个 Task，分别选中并点击 `上移`/`下移`；刷新并重启后再次观察顺序。
3. 对已记录 `PARTIAL` 的 RANGE 点击 `继续 Task`。

预期：改期沿用 Parser 语法，成功后原计划留在历史；排序只交换同日期同分组的 `sort_order`，不改变日期/时间；继续创建新 UUID 的 `标题（继续）` Task，并建立 `continued_from_task_id`，原 Task/结果/计划不变。失败：已有结果仍可改期、跨日期/跨组排序、排序伪造改期、继续非 `PARTIAL RANGE`、或原历史被覆盖。

### Test 5 — 失败路径与路径边界

1. Main 或 Widget Quick Add 输入 `#标签 不应静默丢弃`，确认界面报告 `task.parser.reserved_syntax`，再用只读 `P1ManualHarness list` 或 UI 列表核对没有新增 Task。
2. 关闭所有 Widget 实例，用不存在或不含 `.git`/`ReminNote.sln` 的目录作为 `--repo-root` 启动 Widget；用同样方式启动 Main。

预期：非法 Parser 输入不写库；无效根目录启动失败并返回非零退出码，不在未知目录创建数据库。失败：静默创建、写入其他路径、进程返回 0 或已有数据被覆盖。

## 风险与后续边界

- 本窗口初始未能人工点击 Main/Widget；后续独立窗口已完成条件性 UI smoke 和排序修复后二次 UIA，但正常桌面 GUI 签字、重启/完整点击、RANGE/跨午夜和真实已有 P1 数据升级仍待用户执行。
- P2 的 `CrossProcessTaskWriteGate` 是本机命名 Semaphore，Widget 的 live Snooze/Reschedule 只显示“请在 Main TODAY 编辑计划”，不伪造改期；P2.5 仍需 Agent 命令串行化、冲突检测和 Change Journal。
- 现有 Main/Widget 单实例激活管道只负责宿主激活，不是 P2.5 业务 IPC；当前没有 Agent Task 命令通道。
- P2 继续使用硬删除；没有 Trash、完整删除审计、Reminder、Sync 或 Anime 网络能力。

## P2-05 最终补充：排序修复后二次条件性验收（2026-08-28）

独立窗口基于当前 Release Main，显式使用 `--repo-root D:\Anime`，并在启动子进程时补齐 `WINDIR=C:\WINDOWS`。Main TODAY 同日期同分组默认 `sort_order=0` 无法交换的产品缺陷已由 `8a86a002e3148ff58570aeba4971fc3f462ea283` 修复并配套回归测试：根因是旧实现把 `0↔0` 写回后由应用层判定为 no-op；现在重复排序值先按当前同日期同分组可见顺序归一化，再执行交换，并保留跨日期、跨分组和已有结果保护。

本次排序修复验证仅追加两条带明显前缀的 Task：`P2_FINAL_SMOKE SORT_FIX2_C`、`P2_FINAL_SMOKE SORT_FIX2_D`。选中 C 点击“下移”后，UI 顺序为 D→C；只读 SQLite 确认 C/D `sort_order=4/3`，并存在 `task_history.kind=3` 的 `SortOrderChanged` 历史。重新选中 C 点击“上移”后，UI 顺序为 C→D；最终 C/D `sort_order=3/4`，排序历史仍存在。已有结果的 Task 无改期/排序按钮，跨日期/跨分组保护通过；Main 正常关闭，工作树及 `reviews/`、`second-review/` 无变化。

此前一次反向操作未重新选中 C，导致 UI Automation 实际选中 D；这是测试选中状态问题，不是产品失败。以上属于依赖子进程环境修正的条件性 UI smoke，不是用户正常桌面签字。默认 Codex exec 环境的 `WINDIR` 限制仍需区分；跨午夜 `00:30`、用户正常桌面完整点击/重启和真实已有 P1 数据 forward migration 仍待用户执行。

本次仅追加上述两条 `P2_FINAL_SMOKE` Task，未清空、删除或覆盖 `D:\Anime\.devdata\reminnote.sqlite`；当前真实库只读检查为 `integrity_check=ok`、`foreign_key_check` 行数 0、3 条 migration。P2 实现、自动化门禁和条件性 UI smoke 已完成，但不扩张 P2.5 边界：Reminder、Agent 业务 IPC、Sync 等仍未实现。
