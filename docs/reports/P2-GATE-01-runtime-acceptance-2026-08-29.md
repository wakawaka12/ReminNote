# P2-GATE-01 实机运行验收记录（2026-08-29）

## 结论先行

本记录**不解除 P2-GATE-01 最终门禁**。本窗口在隔离临时 clone 上完成了当前 Release 的构建、真实 Widget 启动、UI Automation 交互、RANGE/DONE/History 和 SQLite 完整性 smoke；但没有形成 Main/Widget 双宿主同库双向 Quick Add、真实 23:00–01:00 跨午夜时序、已有 P1 数据 forward migration/幂等，或用户正常桌面人工签字的完整证据。因此最终状态为 **Pending（待用户人工完成）**，不能写成 P2 已最终通过。

所有 UI Automation 和命令均由 Codex 执行，只能作为“Codex 实机 smoke”证据，不能冒充用户最终签字。暂停测试后没有再启动、操作或关闭 Main/Widget，也没有继续争抢桌面 UI。

## 验收元数据与边界

| 项目 | 记录 |
|---|---|
| 验收窗口 | 2026-08-29 13:06–13:20（Asia/Shanghai；暂停前） |
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
| 当前版本 migration 目标 clone | `C:\Users\EMT\.codex\temp\ReminNote-P2-GATE-01-2026-08-29-migration-current`；数据库为其 `.devdata\reminnote.sqlite` | `d3754879...`；已复制 P1 旧库，暂停前尚未应用当前版本 migration |

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
| 已有 P1 Task 的 forward migration、连续两次启动、幂等 | **Pending** | 已由 `7b1e67c` 生成 P1 旧库并复制到当前版本 clone；暂停前尚未应用当前 migration，未取得升级后 schema/行数/history/integrity 证据。 |
| 无效 `--repo-root` 两宿主非零退出且不建未知库 | **Pending** | 本窗口尚未执行；现有 Main 单实例会使第二个 Main 进程走激活路径，不能把该结果当作参数校验。 |
| 用户正常桌面人工验收和最终签字 | **Pending** | UIA/Codex 操作不是用户签字；当前用户已接手手工测试，最终结论必须由用户在正常桌面完成。 |

## 桌面并发、UI Automation 和进程限制

- 初始只读观察到一个来自 `D:\Anime` 的 Main 进程（PID `6708`，标题 `ReminNote`），它占用与产品相同的 Main 单实例互斥；没有关闭或操作它。
- isolated Widget 的 UIA smoke 期间，桌面上出现了本窗口未发起的额外 Task `P2 Widget 同库任务`，以及一次输入前缀 `y` 的键盘干扰；之后还观察到并发 PowerShell/UIA 操作。为防止把并发动作写成产品行为，isolated 库没有再清理，结果按标题和 ID 分开记录。
- 在暂停前的后续阶段，UIA 树读取开始阻塞；这也是没有继续争抢 UI、没有声称重启后 UIA 或 Main UI 通过的原因。
- 本窗口没有捕获可作为用户签字的截图/录屏；已有证据是进程路径、命令行、窗口标题、UIA 控件/文本、harness 输出和只读 SQLite 查询。
- 暂停时不再主动收尾进程。收尾阶段最后一次只读观察到的桌面进程属于用户在 `D:\Anime` 手工测试的 Main/Widget（路径分别为 `D:\Anime\src\windows\ReminNote.Windows\bin\Release\net10.0-windows\ReminNote.Windows.exe` 和 `D:\Anime\src\windows\ReminNote.Widget\bin\Release\net10.0-windows\ReminNote.Widget.exe`）；没有触碰这些进程。

## P1 migration 生成材料（仅准备，未宣称通过）

在 `7b1e67cb3d8adc7131064a6d2a939c7cdb6819dc` 的 P1 seed clone 中，harness 只读 schema 为 `__EFMigrationsHistory`、`__EFMigrationsLock`、`tasks`，随后创建：

```text
ID：01a04bf5-0a65-7cb4-b1a8-6d9a1a3a01f4
标题：P2GATE01-P1-legacy-20260829
时间：RANGE 2026-08-28 23:00-01:00（跨午夜）
结果：未记录
```

该 SQLite 主文件已复制到 `...-migration-current\.devdata\reminnote.sqlite`，原 P1 seed 文件仍保留。当前版本的 `schema`/`Migrate`、第二次幂等启动、行数/History/integrity 核对均因用户暂停而未执行，所以该部分必须由用户后续完成。

## 解除门禁所需的用户后续动作

1. 在不与其他 UIA/自动化竞争的正常桌面会话中，以同一个明确的临时根目录同时启动 Main 和 Widget，记录两个真实进程路径、窗口标题和 SQLite 绝对路径。
2. 通过 Main Quick Add 创建后在 Widget 刷新确认，再通过 Widget Quick Add 创建后在 Main 刷新确认；分别验证非法粘连不增加 `tasks` 行、合法 `明天 18:00` 的日期/时间/标题。
3. 对 RANGE 验证明确的 `NEEDS REVIEW`、`COMPLETED`、`PARTIAL`、`MISSED`、History 和重启保留；在真实时钟 23:00、次日 00:30、01:00 观察跨午夜状态，不改系统时钟。
4. 使用已保留的 P1 seed 副本执行当前版本 forward migration，连续启动两次，核对旧 Task、schema、migration history、`integrity_check` 和可读写性；原始 seed 与原始开发库必须保留。
5. 使用不存在或不含 Git/`ReminNote.sln` 的路径测试 Main/Widget `--repo-root`，保存两宿主非零退出码和“没有创建未知数据库”的证据。
6. 用户本人在正常桌面完成上述 P2-GATE-01 项目后再签字；在此之前，P2 只能标记为有条件实现/待人工验收。

## 本次报告提交边界

本次只新增本文件 `docs/reports/P2-GATE-01-runtime-acceptance-2026-08-29.md`。临时 clone、数据库、编译产物和 sidecar 均保留在报告列出的路径，未删除；不要将临时数据库或并发 smoke 数据复制回 `D:\Anime`。
