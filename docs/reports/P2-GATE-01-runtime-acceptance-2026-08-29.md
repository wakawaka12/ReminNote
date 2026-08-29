# P2-GATE-01 实机运行验收记录（2026-08-29）

## 结论先行

本记录**不解除 P2-GATE-01 最终门禁**。本窗口在隔离临时 clone 上完成了当前 Release 的构建、真实 Widget 启动、UI Automation 交互、RANGE/DONE/History 和 SQLite 完整性 smoke；恢复收尾阶段又在隔离临时副本中完成了已有 P1 数据的 forward migration、连续第二次正常 schema/迁移路径和幂等核对。仍没有形成 Main/Widget 双宿主同库双向 Quick Add、真实 23:00–01:00 跨午夜时序、无效 `--repo-root` 两宿主可执行文件非零退出，或用户正常桌面人工签字的完整证据。因此最终状态仍为 **Pending（待用户人工完成）**，不能写成 P2 已最终通过。

所有 UI Automation 和命令均由 Codex 执行，只能作为“Codex 实机 smoke”证据，不能冒充用户最终签字。暂停测试后没有再启动、操作或关闭 Main/Widget，也没有继续争抢桌面 UI。

## 验收元数据与边界

| 项目 | 记录 |
|---|---|
| 验收窗口 | 2026-08-29 13:06–13:20（Asia/Shanghai；暂停前） |
| 恢复后非 GUI 补证 | 2026-08-29 13:31:43 +08:00 为收尾记录时刻；仅检查临时数据库和构建 DLL，未启动、操作、激活或关闭任何 Main/Widget |
| 产品基线 | `d3754879e53038148e18a13c1acfe2be88f0d374`（`d375487`） |
| 基线分支 | `codex/p0-integration`；总成根目录为 `D:\Anime` |
| 当前收尾工作树 | `C:\Users\EMT\.codex\worktrees\e8a2\Anime`，只新增本报告 |
| 构建工具 | `C:\Program Files\dotnet\dotnet.exe`，x64 SDK 10.0.400 |
| 桌面环境 | Windows 11 家庭版，Build 26200，Session 1 |
| 时钟处理 | 未修改系统时钟；没有用固定 `Instant` 冒充真实跨午夜时序 |
| 原始库保护 | 未以 `D:\Anime\.devdata\reminnote.sqlite` 作为写入目标；未执行 purge/clean、删除、覆盖或恢复原库 |
| 网络与提交边界 | 未 push、merge、rebase；未修改产品源码、测试源码、项目配置、既有文档、`reviews/` 或 `second-review/` |

## 临时根目录、数据库和资源保留

以下目录均为本机临时产物，**本窗口没有删除**，以便复核和人工清理。清理前必须再次确认其中没有进程或文件句柄；不得将其当作生产数据。

| 用途 | 临时根目录 / 数据库 | 基线与状态 |
|---|---|---|
| 首个 Widget smoke clone | `C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29`；数据库为其 `.devdata\reminnote.sqlite` | `d3754879...`；Release 构建和一次 UIA Quick Add 成功，但随后出现本窗口未发起的并发数据变更，未作为最终隔离证据使用 |
| 正式 isolated Widget smoke clone | `C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-isolated`；数据库为其 `.devdata\reminnote.sqlite` | `d3754879...`；有真实 Widget UIA、RANGE/DONE/History 和 SQLite 证据；最终数据库文件约 65536 bytes |
| P1 旧库生成 clone | `C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-p1-seed`；数据库为其 `.devdata\reminnote.sqlite` | `7b1e67cb3d8adc7131064a6d2a939c7cdb6819dc`；只有 P1 schema，生成一条旧 Task，数据库文件约 24576 bytes |
| 当前版本 migration 目标 clone | `C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-migration-current`；数据库为其 `.devdata\reminnote.sqlite` | `d3754879...`；已复制 P1 旧库，恢复后已执行当前版本 forward migration 和第二次幂等 schema/迁移路径 |
| Widget 无效参数验证目标 | `C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-invalid-widget-root` | 目录在验证后仍不存在；只做 Widget DLL 入口方法级检查，未启动 Widget EXE |

临时 clone 均有真实 `.git` 目录并包含 `ReminNote.sln`。本轮确实写入的数据库都在上述临时根目录的 `.devdata` 下；没有把临时库复制回 `D:\Anime`。

## 已取得的 Codex 实机 smoke 证据

### Release 构建和空库 schema

- 在 isolated clone 执行 locked restore 和 Release solution build，结果均为退出码 0，8 个项目构建成功，0 warning、0 error。
- P1 seed clone 也完成了 Release restore/build，结果为退出码 0，0 warning、0 error。
- isolated clone 首次 `schema` 输出为：`__EFMigrationsHistory`、`__EFMigrationsLock`、`app_settings`、`task_history`、`tasks`。没有出现 Reminder、Anime 或 Sync 表。
- isolated clone 的 migration history 为：
  - `20260828025922_InitialTaskSchema`
  - `20260828120000_P2TaskLoop`
  - `20260828130000_P2ContinuationDeleteBoundary`

### 真实 Widget 启动与 UI Automation

isolated Widget 以如下实际二进制启动：

```text
C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-isolated\src\windows\ReminNote.Widget\bin\Release\net10.0-windows\ReminNote.Widget.exe --repo-root C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-isolated
```

首次 isolated Widget 的 PID 为 `34396`，窗口标题为 `ReminNote Widget`，进程路径和命令行均来自 isolated clone，`Responding=True`。UI Automation 真实读取到窗口和控件名称，包括 `TODAY`、`ANIME`、`完成`、`延后`、`改期`、`记录 PARTIAL 结果`、`记录 MISSED 结果`、`QUICK ADD`、`LOCAL SQLITE · LIVE TASKS`。

初始 UIA 文本真实显示：

- PRIMARY：`P2GATE01-Iso-Range-Missed-20260829`；状态为 `AWAITING RESULT · NEEDS REVIEW`，时间为 `22:00–22:30`；
- 队列中有跨午夜 `P2GATE01-Iso-Range-Partial-20260829`，时间为 `23:00–01:00`，也显示 `NEEDS REVIEW`；
- summary 为 `3 OPEN · 0 DONE · NEEDS REVIEW · 2`；
- RANGE 结果操作提示为“请记录 COMPLETED、PARTIAL 或 MISSED 结果”。

这证明了当前二进制能在真实 Windows 桌面创建窗口并暴露 UIA 控件，但仍然只是自动化 smoke，不是人工验收签字。

### Widget RANGE、DONE、History 和重启保留

通过 UI Automation 实际点击 isolated Widget 控件，取得以下反馈：

| 操作 | UIA 反馈 / summary |
|---|---|
| 对 `P2GATE01-Iso-Range-Missed-20260829` 点击 `记录 MISSED 结果` | `已记录「P2GATE01-Iso-Range-Missed-20260829」的 MISSED 结果。`；随后 summary 为 `2 OPEN · 0 DONE · NEEDS REVIEW · 1` |
| 对 `P2GATE01-Iso-Range-Partial-20260829` 点击 `记录 PARTIAL 结果` | `已记录「P2GATE01-Iso-Range-Partial-20260829」的 PARTIAL 结果。`；随后 primary 切换到普通 Task，summary 为 `1 OPEN · 0 DONE` |
| 对 `P2GATE01-Iso-Done-20260829` 点击 `完成` | `已记录「P2GATE01-Iso-Done-20260829」的 COMPLETED 结果。`；随后 summary 为 `0 OPEN · 1 DONE` |

随后用 harness 和只读 SQLite 查询复核：

- `P2GATE01-Iso-Range-Missed-20260829`：计划日 `2026-08-28`，RANGE `22:00–22:30`，结果 `MISSED`；
- `P2GATE01-Iso-Range-Partial-20260829`：计划日 `2026-08-28`，RANGE `23:00–01:00`（跨午夜），结果 `PARTIAL`，备注为 Widget 写入的 PARTIAL 备注；
- `P2GATE01-Iso-Done-20260829`：计划日 `2026-08-29`，TIME `18:00`，结果 `COMPLETED`；
- 三条 Task 的 ID、结果和计划字段均能在 harness 读取，`task_history` 为 3 行；
- `SELECT COUNT(*) FROM tasks` 为 3，`SELECT COUNT(*) FROM task_history` 为 3；
- `PRAGMA integrity_check` 返回 `ok`；`PRAGMA foreign_key_check` 无结果行；
- 结果记录使用实际操作时间写入 History，未将结果时间伪装成计划时间。

之后正常关闭首次 isolated Widget 并重新启动同一 isolated 二进制，重启 PID 为 `30992`，窗口标题仍为 `ReminNote Widget`，进程正常响应。重启后的 harness/只读 SQLite 读取仍保留上述三条 Task、三条 History 和结果字段。重启后的再次 UIA 树读取因桌面上并发 UIA 操作而阻塞，没有把它写成新的 UIA 通过证据；因此这里的重启结论是**数据库/harness 保留通过，重启后 UIA 复核受环境限制**。

### Widget Quick Add Parser 和同库写入 smoke

在 isolated Widget 中，UI Automation 输入非法粘连文本：

```text
明天18:00 P2GATE01-Iso-Invalid-20260829
```

实际反馈为 `无法创建 Task：task.parser.time.invalid`。该次操作前后 `tasks` 行数均为 4，且只读列表中没有该非法标题，证明本次 Widget Parser 失败路径没有新增非法行。行数为 4 是因为并发活动已在此前额外写入一条非本轮命令的 Task；判断依据还包括非法标题不存在，而不是只依赖总行数。

随后有两次合法输入记录：

1. 第一次尝试合法 `明天 18:00 P2GATE01-Iso-QuickAdd-20260829` 时，桌面发生并发键盘/UIA 干扰；点击前没有读取输入框的最终值，实际写入了 `y明天 18:00 P2GATE01-Iso-QuickAdd-20260829`，被 Parser 当作 ANYTIME 标题。该次是本窗口的**环境干扰失败**，不能归因于产品，也不能作为合法语法通过证据。
2. 第二次输入 `明天 18:00 P2GATE01-Iso-QuickAdd2-20260829` 时，先通过 UIA `ValuePattern` 读取并确认输入值与期望字符串完全一致，再点击 `添加`。实际反馈为 `已写入本地 Task：「P2GATE01-Iso-QuickAdd2-20260829」`；数据库行数从 5 增加到 6；harness 读取到计划日 `2026-08-30`、TIME `18:00`，标题和时间均正确。

因此“Widget 侧非法不写库”和“Widget 侧合法明天 18:00 写入同一临时 SQLite”可列为 **Pass（Codex UIA smoke，带并发环境限定）**。isolated 最终库中还保留了并发活动产生的 `P2 Widget 同库任务` 和上面第一次被干扰的 `y明天…` 行；本窗口没有删除或覆盖它们。

## Pass / Fail / Pending 清单

| 验收项 | 状态 | 证据与边界 |
|---|---|---|
| 当前提交 Release locked restore/build | **Pass** | x64 SDK 10.0.400；isolated 和 P1 seed 均 0 warning/0 error。属于构建 smoke。 |
| 临时库路径和 schema 安全边界 | **Pass** | 写入只在临时 clone `.devdata`；schema/migration history 已读取；无 Reminder/Anime/Sync 表。 |
| Widget 实际启动、窗口标题和 UIA 控件 | **Pass** | isolated 二进制真实创建 `ReminNote Widget`，UIA 读取到实际控件和 live SQLite 文案。 |
| Widget RANGE 的 NEEDS REVIEW、MISSED、PARTIAL | **Pass** | UIA 反馈、summary、harness、Task 行和 History 均相互复核；包括 `23:00–01:00` 结构。 |
| Widget DONE / COMPLETED | **Pass** | UIA `完成` 反馈、summary `0 OPEN · 1 DONE`，SQLite 与 History 可读。 |
| Widget 重启后结果保留 | **Pass（带限定）** | 重启后二进制正常启动，harness/SQLite 保留；重启后 UIA 树因并发操作阻塞，未冒充 UIA 通过。 |
| Widget 非法粘连 Parser 不写库 | **Pass（带限定）** | 实际 UIA 错误码 `task.parser.time.invalid`；非法标题不存在，行数未因该输入增加。 |
| Widget 合法 `明天 18:00` Quick Add | **Pass（第二次、带限定）** | 点击前核对 UIA ValuePattern，反馈和数据库均为正确标题、`2026-08-30 18:00`。 |
| 一次被并发桌面干扰的合法输入 | **Fail（环境事件）** | 实际写入 `y明天…` ANYTIME；因并发键入且未在点击前核对值，不能归因产品，也不计为合法用例通过。 |
| Main 临时 clone 真实启动 | **Pending** | 未启动。暂停前已有 `D:\Anime` Main 占用 `Local\ReminNote.Windows.Main` 单实例互斥；启动 isolated Main 不能保证成为本库主实例。 |
| Main ↔ Widget 双宿主同库双向 Quick Add | **Pending** | 本窗口只取得 Widget 侧 UIA 写入和数据库读取；没有同一临时根的 Main→Widget、Widget→Main 双向人工/无竞争证据。 |
| DONE/RANGE 跨 Main 与 Widget 的刷新链路 | **Pending** | Widget 侧完成；Main 侧未在同一隔离运行窗口取得证据。 |
| 真实 23:00 → 次日 00:30 → 01:00 跨午夜时序 | **Pending** | 只验证了 `23:00–01:00` 的领域/数据库结构；未等待真实时钟节点，未修改系统时钟，不能替代时序验收。 |
| 已有 P1 Task 的 forward migration、连续两次启动、幂等 | **Pass（非 GUI 补证；用户最终签字仍 Pending）** | `migration-current` 上当前版本正常 schema/迁移路径连续执行两次均退出码 0；旧 Task 行数为 1 且内容保持；升级后表、三条 migration history、`integrity_check=ok` 和无外键违规均已核对；`p1-seed` 未改写。 |
| 无效 `--repo-root` 两宿主非零退出且不建未知库 | **Pending** | 本轮未启动任何 Main/Widget EXE；仅反射调用 Widget 入口方法，确认不存在路径抛 `System.ArgumentException` 且目录未创建。这是方法级 Pass，不是两宿主可执行文件非零退出证据；Main 和 Widget EXE 仍待用户在无竞争桌面复核。 |
| 用户正常桌面人工验收和最终签字 | **Pending** | UIA/Codex 操作不是用户签字；当前用户已接手手工测试，最终结论必须由用户在正常桌面完成。 |

## 桌面并发、UI Automation 和进程限制

- 初始只读观察到一个来自 `D:\Anime` 的 Main 进程（PID `6708`，标题 `ReminNote`），它占用与产品相同的 Main 单实例互斥；没有关闭或操作它。
- isolated Widget 的 UIA smoke 期间，桌面上出现了本窗口未发起的额外 Task `P2 Widget 同库任务`，以及一次输入前缀 `y` 的键盘干扰；之后还观察到并发 PowerShell/UIA 操作。为防止把并发动作写成产品行为，isolated 库没有再清理，结果按标题和 ID 分开记录。
- 在暂停前的后续阶段，UIA 树读取开始阻塞；这也是没有继续争抢 UI、没有声称重启后 UIA 或 Main UI 通过的原因。
- 本窗口没有捕获可作为用户签字的截图/录屏；已有证据是进程路径、命令行、窗口标题、UIA 控件/文本、harness 输出和只读 SQLite 查询。
- 暂停时不再主动收尾进程。收尾阶段最后一次只读观察到的桌面进程属于用户在 `D:\Anime` 手工测试的 Main/Widget（路径分别为 `D:\Anime\src\windows\ReminNote.Windows\bin\Release\net10.0-windows\ReminNote.Windows.exe` 和 `D:\Anime\src\windows\ReminNote.Widget\bin\Release\net10.0-windows\ReminNote.Widget.exe`）；没有触碰这些进程。
- 恢复后严格没有启动、操作、激活或关闭任何 Main/Widget；只加载 `migration-current` 的 Widget 构建 DLL 做参数解析入口检查，因此没有再次争抢桌面 UI，也没有触碰用户在 `D:\Anime` 或已存在 isolated clone 的进程。

## P1 migration 生成材料与恢复后非 GUI 补证

在 `7b1e67cb3d8adc7131064a6d2a939c7cdb6819dc` 的 P1 seed clone 中，harness 只读 schema 为 `__EFMigrationsHistory`、`__EFMigrationsLock`、`tasks`，随后创建：

```text
ID：01a04bf5-0a65-7cb4-b1a8-6d9a1a3a01f4
标题：P2GATE01-P1-legacy-20260829
时间：RANGE 2026-08-28 23:00-01:00（跨午夜）
结果：未记录
```

截至暂停时，该 SQLite 主文件已复制到 `...-migration-current\.devdata\reminnote.sqlite`，原 P1 seed 文件仍保留；当时尚未执行当前版本迁移。恢复后补证仅在该临时副本执行，未触碰原始开发库。

### 恢复后 P1 forward migration、第二次幂等和完整性

- 在 `...-migration-current` 使用当前基线的 `dotnet restore ReminNote.sln --locked-mode` 和 Release build，均退出码 0；构建结果为 0 warning、0 error。随后只运行 `ReminNote.P1ManualHarness.exe schema` 的正常初始化/schema 路径两次，第一次和第二次均退出码 0。
- 第一次完成后 schema 为 `__EFMigrationsHistory`、`__EFMigrationsLock`、`app_settings`、`task_history`、`tasks`；第二次输出完全相同。两次 `list 2026-08-28` 均只有同一条旧 Task：ID `01a04bf5-0a65-7cb4-b1a8-6d9a1a3a01f4`、标题 `P2GATE01-P1-legacy-20260829`、`RANGE 2026-08-28 23:00-01:00（跨午夜）`、结果未记录。
- 两个 SQLite 文件均使用只读连接核对，结果如下：

| 副本 | 文件证据 | 表/行数 | migration history | 完整性 |
|---|---|---|---|---|
| P1 seed（未改写） | 24576 bytes；SHA-256 `C750758076FED7A7C08284894372698771AEDE9342F9BEF329D23AF88970E802` | `tasks=1`；旧 schema 无 `task_history`；旧 Task 与上文相同 | `20260828025922_InitialTaskSchema` | `integrity_check=ok`；`foreign_key_check` 无行 |
| 当前版本升级副本 | 65536 bytes；SHA-256 `A31626018346F3DC064F1911261692ED5FE0063C817C3CAB619AE08F3310E021` | `tasks=1`；`task_history=0`；旧 Task ID/标题/时间保持不变 | `20260828025922_InitialTaskSchema`、`20260828120000_P2TaskLoop`、`20260828130000_P2ContinuationDeleteBoundary` | `integrity_check=ok`；`foreign_key_check` 无行 |

结论：已有 P1 Task 的 forward migration、旧行保留、连续第二次迁移无新增/破坏性变化、migration history 和 SQLite 完整性在隔离副本中均为 **Pass（Codex 非 GUI 补证）**。这不是用户正常桌面人工签字。

### Widget 无效 `--repo-root` 入口补证（非 GUI）

- 仅从 `...-migration-current\src\windows\ReminNote.Widget\bin\Release\net10.0-windows\ReminNote.Widget.dll` 反射调用 `WidgetStartupOptions.ResolveRepositoryRoot(IReadOnlyList<string>, string?)`，参数为 `--repo-root C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-invalid-widget-root`。
- 方法拒绝结果为 `System.ArgumentException`：`Repository root must contain .git and ReminNote.sln: C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-invalid-widget-root`；验证后该目录仍不存在（`INVALID_ROOT_EXISTS_AFTER=False`）。
- 这是入口方法级参数校验的 **Pass**，不是 Widget 可执行文件退出码证据。由于当前用户桌面存在单实例并发且本轮严禁启动/激活 GUI，Widget EXE 非零退出和 Main EXE 非零退出仍为 **Pending**。

## 解除门禁所需的用户后续动作

1. 在不与其他 UIA/自动化竞争的正常桌面会话中，以同一个明确的临时根目录同时启动 Main 和 Widget，记录两个真实进程路径、窗口标题和 SQLite 绝对路径。
2. 通过 Main Quick Add 创建后在 Widget 刷新确认，再通过 Widget Quick Add 创建后在 Main 刷新确认；分别验证非法粘连不增加 `tasks` 行、合法 `明天 18:00` 的日期/时间/标题。
3. 对 RANGE 验证明确的 `NEEDS REVIEW`、`COMPLETED`、`PARTIAL`、`MISSED`、History 和重启保留；在真实时钟 23:00、次日 00:30、01:00 观察跨午夜状态，不改系统时钟。
4. 本窗口已在保留的 P1 seed 临时副本完成当前版本 forward migration、连续第二次正常迁移路径、旧 Task、schema、migration history、`integrity_check` 和外键核对；如流程要求用户亲自复核该项，只能继续使用可恢复副本，不能把本报告的 Codex 补证写成用户签字。
5. 使用不存在或不含 Git/`ReminNote.sln` 的路径测试 Main/Widget `--repo-root`，保存两宿主非零退出码和“没有创建未知数据库”的证据。
6. 用户本人在正常桌面完成上述 P2-GATE-01 项目后再签字；在此之前，P2 只能标记为有条件实现/待人工验收。

## 本次报告提交边界

本次只新增本文件 `docs/reports/P2-GATE-01-runtime-acceptance-2026-08-29.md`。临时 clone、数据库、编译产物和 sidecar 均保留在报告列出的路径，未删除；不要将临时数据库或并发 smoke 数据复制回 `D:\Anime`。

## 本轮 rerun 补充证据（仅追加）

本节是 2026-08-29 恢复后的独立一次性临时 clone 补测记录；不改写前文暂停阶段的结论。补测收尾记录时间为 `2026-08-29 14:00:07 +08:00`。本轮开始前只读进程检查结果为 `NO_REMINNOTE_GUI_PROCESSES`，因此本轮只处理随后由本轮创建的进程；最终 `REMAINING_REMINNOTE_GUI_PROCESSES=0`。

### 隔离 clone、构建和启动环境

- 一次性临时 clone：`C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-rerun`；真实 `.git` 和 `ReminNote.sln` 均存在，HEAD 为 `d3754879e53038148e18a13c1acfe2be88f0d374`。
- 只在该 clone 执行 `dotnet restore ReminNote.sln --locked-mode` 和 `dotnet build ReminNote.sln --configuration Release --no-restore --nologo`，均退出码 0，0 warning、0 error。所有有效启动均显式使用同一个 `--repo-root`，数据库为 `C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-rerun\.devdata\reminnote.sqlite`。
- 首次启动尝试（Main PID `31984`、Widget PID `32208`）约 3 秒内退出，未创建临时数据库。Windows Application/.NET Runtime 事件记录为 WPF `MS.Internal.FontCache.Util` 的 `UriFormatException`，只读环境检查确认该启动命令未继承 `WINDIR`；这是**环境事件 Fail**，不能归因产品，也没有继续用它冒充启动通过。
- 在启动子进程明确设置 `WINDIR=C:\WINDOWS`、`SystemRoot=C:\WINDOWS` 后重试成功：Main PID `15108`、Widget PID `34236`，实际窗口标题分别为 `ReminNote`、`ReminNote Widget`；进程路径和命令行均指向上述 rerun clone。

### Main ↔ Widget 双向 Quick Add 与刷新

- Main UIA `QUICK ADD` 的 `ValuePattern` 先确认输入为 `今天 18:20 P2GATE01-Rerun-MainToWidget-20260829`；Main 反馈为 `已添加「P2GATE01-Rerun-MainToWidget-20260829」到本地 Task · TODAY 已刷新`。约 2 秒后，Widget UIA 自动刷新读到同名 Task，窗口标题仍为 `ReminNote Widget`，summary 为 `1 OPEN · 0 DONE`。结论：**Pass（Codex UIA smoke）**。
- Widget UIA `QUICK ADD` 的 `ValuePattern` 先确认输入为 `今天 18:40 P2GATE01-Rerun-WidgetToMain-20260829`，`添加` 控件变为 enabled，反馈为 `已写入本地 Task：「P2GATE01-Rerun-WidgetToMain-20260829」`。随后 Main UIA 点击真实 `刷新 TODAY`，反馈为 `TODAY 已刷新 · 已重新读取本地 Task`，并读到两条唯一标题。结论：**Pass（Codex UIA smoke）**。
- 双向写入均落在同一个上述 SQLite 路径；没有使用 `D:\Anime\.devdata\reminnote.sqlite`。

### RANGE、DONE、NEEDS REVIEW 和 History

- Widget Quick Add 实际创建 `今天 09:00-10:00 P2GATE01-Rerun-RangeMissed-20260829` 与 `今天 10:30-11:30 P2GATE01-Rerun-RangePartial-20260829`。创建后 UIA 显示 `4 OPEN · 0 DONE · NEEDS REVIEW · 2` 及 `RANGE 计划已结束 · 请记录 COMPLETED、PARTIAL 或 MISSED 结果`，证明本轮实时状态为 `NEEDS REVIEW`。
- 点击 `记录 MISSED 结果` 后，UIA 反馈为 `已记录「P2GATE01-Rerun-RangeMissed-20260829」的 MISSED 结果。`，summary 为 `3 OPEN · 1 DONE · NEEDS REVIEW · 1`。
- 点击 `记录 PARTIAL 结果` 后，UIA 反馈为 `已记录「P2GATE01-Rerun-RangePartial-20260829」的 PARTIAL 结果。`，summary 为 `2 OPEN · 2 DONE`；SQLite 中保留 Widget 写入的结果备注。
- 随后对 Main→Widget Task 点击 Widget `完成`，UIA 反馈为 `已记录「P2GATE01-Rerun-MainToWidget-20260829」的 COMPLETED 结果。`，summary 为 `1 OPEN · 3 DONE`；Main 点击刷新后也显示 `1 OPEN · 3 DONE` 和 `COMPLETED` 文案。
- harness `list 2026-08-29` 退出码为 0，共 4 条：Main→Widget `COMPLETED`，Widget→Main 保持未记录的 1 条 OPEN，两个 RANGE 分别为 `MISSED`、`PARTIAL`。只读 SQLite 查询同时确认 `tasks=4`、`task_history=3`、`PRAGMA integrity_check=ok`、`PRAGMA foreign_key_check` 无行。记录的三条 History 对应 `MISSED`、`PARTIAL`、`COMPLETED`，未把计划时间冒充结果记录时间。

最终数据库文件证据（所有 GUI 已退出后读取）：文件长度 `65536` bytes，SHA-256 `39A783CC94473B341ECBB9DC7EBA186643261B17344E125EA4EC96714932A541`；表为 `__EFMigrationsHistory`、`__EFMigrationsLock`、`app_settings`、`task_history`、`tasks`；migration history 为 `20260828025922_InitialTaskSchema`、`20260828120000_P2TaskLoop`、`20260828130000_P2ContinuationDeleteBoundary`。

### 顺序重启和资源释放

- 仅关闭本轮 Widget PID `34236`，`CloseMainWindow=True`、5 秒内退出；在 Main PID `15108` 仍运行时以同一 `--repo-root` 启动 Widget PID `30652`。重启后的 Widget 标题为 `ReminNote Widget`，summary 保持 `1 OPEN · 3 DONE`，开放 Task 保留。
- 仅关闭本轮 Main PID `15108`，`CloseMainWindow=True`、5 秒内退出；在 Widget PID `30652` 仍运行时以同一 `--repo-root` 启动 Main PID `2208`。重启后的 Main 标题为 `ReminNote`，summary 保持 `1 OPEN · 3 DONE`，开放 Task 保留；SQLite/History 中的已完成结果仍保留。
- 最终仅关闭本轮 PID `30652`、`2208`，二者均优雅退出；未使用强制终止，剩余同名进程为 0。结论：**Pass（Codex 顺序重启/数据库保留 smoke）**，仍不是用户人工签字。

### 两宿主无效 `--repo-root` 可执行文件

使用不存在且未预先创建的独立路径 `C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-invalid-exe-root`，在无有效 Main/Widget 进程时按 Main 后 Widget 顺序实际运行 Release EXE，工作目录仍为有效 rerun clone：

| 宿主 | 退出码 | 非零 | 无效根目录创建 | 未知数据库创建 |
|---|---:|---|---|---|
| Main | `-532462766` | 是 | 否 | 否 |
| Widget | `1` | 是 | 否 | 否 |

每次退出后同名 GUI 进程均为 0。结论：**Pass（两宿主 EXE 参数失败 smoke）**；该结果只说明无效根目录不会静默创建未知库，不解除用户人工门禁。

### 本轮最终状态分类

| 类别 | 本轮结论 |
|---|---|
| Codex smoke Pass | 同一临时库 Main→Widget、Widget→Main Quick Add/刷新；`NEEDS REVIEW`、`MISSED`、`PARTIAL`、`COMPLETED`、History；顺序重启保留；两宿主无效 `--repo-root` 非零且不建未知库；SQLite integrity/FK。 |
| 环境事件 Fail | 首次未继承 `WINDIR` 的 Main/Widget 启动尝试触发 WPF font-cache `UriFormatException`；明确设置 Windows 根目录变量后有效启动成功。该 Fail 不归因产品。此前报告记录的并发键盘干扰 Fail 仍保留。 |
| Pending | 真实时钟 `23:00 → 次日 00:30 → 01:00` 未等待、未修改系统时钟；用户正常桌面人工验收与最终签字仍 Pending。Codex UIA/命令/SQLite 证据不能代替用户签字。 |

本轮未删除任何临时目录或数据库；rerun clone、数据库、编译产物和无效路径验证记录均保留，清理前仍需确认没有文件句柄。未修改产品源码、测试、项目配置、既有文档、`reviews/`、`second-review/`，未 push、merge 或 rebase。
