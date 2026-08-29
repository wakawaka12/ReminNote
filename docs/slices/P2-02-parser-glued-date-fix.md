# P2-02 Parser 中文日期/时间粘连修复

- 阶段：P2 Real TODAY / Widget task loop
- 日期：2026-08-29
- 基线：`96befb7`
- 状态：代码与自动化验证完成，Main/Widget 真实桌面验收待用户执行

## 问题

TaskParser 原先只按空白切分 token。`今天18:00`、`明天18:00` 和
`后天08:05` 不会进入日期或时间消费分支，整段输入会被当成标题，并静默回退为
逻辑今天的 `ANYTIME`。这违反 P2-00 对未识别日期/时间形状“稳定报错且不写库”的契约。

## 本次改动

只修改以下归属文件：

- `src/windows/ReminNote.Core/Tasks/Parsing/TaskParser.cs`
- `tests/ReminNote.Tests/TaskParserTests.cs`
- 本 Slice 记录

Parser 在日期消费、时间消费和标题回退前检查 `今天`、`明天`、`后天` 后紧跟
ASCII 数字、冒号、连字符或 en dash 的 token：

- 数字/冒号开头的粘连时间返回 `task.parser.time.invalid`；
- 含连字符或 en dash 的粘连范围返回 `task.parser.range.invalid`；
- 失败结果的 `Value` 为空，不进入 `CreateAsync`。

合法的空格分隔日期/时间、显式 ISO 日期、TIME、RANGE 和 ANYTIME 语法保持不变。
未修改 UI、Infrastructure、Application service、排序、Reminder/Agent/IPC/Sync，
也未新增依赖；未修改 `reviews/` 或 `second-review/` 中任何既有文件。

## 自动化证据

以下命令均在本 Slice worktree 执行：

- `scripts/test.ps1 -Configuration Release`：通过；`ReminNote.Tests` **170/170**，
  Errors 0、Failed 0、Skipped 0、Not Run 0；脚本内 P0-07 通过。
- `tests/ReminNote.Tests/bin/Release/net10.0/ReminNote.Tests.exe -noLogo -noColor -class ReminNote.Tests.TaskParserTests`：
  通过；Parser 类 **40/40**。
- `scripts/build.ps1 -Configuration Release`：通过；8 个项目构建，0 警告、0 错误。
- `git diff --check`：通过；无 whitespace errors。

新增回归覆盖三种中文相对日期的无标题/带标题粘连时间，以及 ASCII/en dash 粘连范围，
并断言稳定错误码和失败结果不含解析值。Main 与 Widget 的既有 Quick Add 路径在调用
`CreateAsync` 前检查 `parsed.IsSuccess`，因此 Parser 失败时不会发起应用写入；真实
桌面和 SQLite 行数核对仍按下方步骤由用户执行。

## 人工验收

### 环境

1. 关闭已有 Main/Widget 实例。
2. 使用本 worktree 作为仓库根目录启动两个宿主，并传入同一个
   `--repo-root C:\Users\EMT\.codex\worktrees\9949\Anime`。
3. 确认只使用该根目录下的 `.devdata/reminnote.sqlite`；验收前用只读 SQLite 工具记录
   `SELECT COUNT(*) FROM tasks;` 的初始行数。

### 验收 1：粘连日期/时间必须拒绝

分别在 Main Quick Add 和 Widget Quick Add 中输入并提交：

- `今天18:00 粘连今天`
- `明天18:00 粘连明天`
- `后天08:05 粘连后天`
- `明天18:00-19:00 粘连 ASCII 范围`
- `后天08:05–09:00 粘连 en dash 范围和标题`

预期：前三项显示稳定的 `task.parser.time.invalid`，后两项显示
`task.parser.range.invalid`；不显示创建成功提示，标题不出现在 Today，SQLite
`tasks` 行数与初始值相同。

失败判定：任一输入被创建为逻辑今天的 ANYTIME、错误标题或其他时间形状；界面显示
成功；提交后行数增加；或任一宿主写入了不同数据库路径。

### 验收 2：合法空格语法仍可创建

在任一宿主输入 `明天 18:00 合法空格任务` 并提交，再刷新另一宿主。

预期：创建成功，计划日期为逻辑今天的次日、时间为 `18:00`、标题为
`合法空格任务`；另一宿主能在轮询/刷新后看到同一 Task。

失败判定：合法输入被拒绝、日期/时间被丢失、标题被改写，或两宿主看到不同数据。

## 剩余风险

本窗口不能替用户完成正常桌面 GUI 点击、重启保留和真实 SQLite 行数签字；自动化测试
已覆盖 Parser 成功/失败及现有 Quick Add 失败早退路径，但上述两项真实宿主验收仍是
P2-GATE-01 的人工证据。未在本 Slice 扩大到 P2.5 单写者、Agent/IPC、Reminder 或 Sync。
