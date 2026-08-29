# ReminNote P2 Parser 粘连日期/时间修复二次复核

- 复核日期：2026-08-29
- 复核工作树：`C:\Users\EMT\.codex\worktrees\0e50\Anime`
- 复核提交：`9a7d293e6db52d617087d888168217165cc367af`
- 对照基线：`96befb7dcb8a49a8bcc59a73ad4d82ba96ebe8ff`
- 原始修复提交：`3ad77a7fcdd7517ad7f48ff6fd486c24d472c4a8`
- 复核范围：仅 Parser 粘连日期/时间修复、既有 Main/Widget Quick Add 失败早退、TaskParser 测试、P2-02 Slice、指定自动化门禁；未修改产品代码、测试代码或既有审查文件。

## 复核结论

**部分通过。** Parser 修复本身通过复核，可以解除 `IMP-P2-1`；Main/Widget 的失败早退和 Release 自动化门禁均通过。整体未达到 P2 完成声明条件，原因是：

1. `P2-GATE-01` 的双宿主真实桌面、SQLite 行数/路径、重启和迁移等人工验收仍未执行；
2. P2-02 Slice 的人工验收路径存在文档不一致（见 `P2-SR-01`），执行前必须改用与总成提交一致的仓库根目录并澄清记录。

## 发现与裁决

### P2-SR-01：P2-02 Slice 的人工验收根目录与当前总成提交不一致

- 严重度：Minor（文档/验收证据风险，不是产品代码缺陷）
- 证据：`docs/slices/P2-02-parser-glued-date-fix.md:53-56` 同时写明“使用本 worktree”，又把 `--repo-root` 写成 `C:\Users\EMT\.codex\worktrees\9949\Anime`。
- 事实核对：当前复核 worktree 的 `HEAD` 是 `9a7d293e6db52d617087d888168217165cc367af`；`C:\Users\EMT\.codex\worktrees\9949\Anime` 的 `HEAD` 是原始提交 `3ad77a7fcdd7517ad7f48ff6fd486c24d472c4a8`。两者共同父提交均为 `96befb7dcb8a49a8bcc59a73ad4d82ba96ebe8ff`，`git diff --stat 3ad77a7..9a7d293` 为空，但 9949 仍不是当前总成 checkout。
- 裁决：Slice 没有虚构 GUI 结果，且自动化证据与本次复跑一致；但人工执行说明不准确，可能导致按错误 worktree 验收。该项在人工验收前应澄清为当前总成 worktree（或明确的 `D:\Anime` 总成根目录）。本窗口按要求不修改 Slice。

### P2-GATE-01：已知人工门禁仍开放

- `D:\Anime\reviews\review-2026-08-28-p2-final.md:6-14` 明确要求同时完成 `IMP-P2-1` 与 P2-GATE-01，后者包括双宿主同库、重启保留、跨午夜实际时序、P1 数据 forward migration、非法根目录非零等。
- 本窗口未进行 GUI 点击、双进程签字或真实库行数核对；Slice 也在 `docs/slices/P2-02-parser-glued-date-fix.md:85-89` 如实记录这些人工证据仍待用户执行。
- 裁决：不是本次 Parser 代码修复的阻断项，但阻断 P2 完成声明。

## Parser 契约与实现核对

终审要求见 `D:\Anime\reviews\review-2026-08-28-p2-final.md:34-42`；P2-00 的 Parser 契约见 `docs/slices/P2-00-integration-and-contract-brief.md:60-87`。

- `src/windows/ReminNote.Core/Tasks/Parsing/TaskParser.cs:49-56` 在日期消费前调用粘连检查，命中即返回失败；`TaskParser.cs:74-85` 在时间消费前再次检查，覆盖日期已被合法消费后出现的粘连 token。
- `TaskParser.cs:255-279` 只针对 `今天`、`明天`、`后天` 后紧跟 ASCII 数字、冒号、连字符或 en dash 的 token 返回稳定错误；数字/冒号形状为 `task.parser.time.invalid`，含 ASCII 连字符或 en dash 的范围为 `task.parser.range.invalid`。
- `TaskParser.cs:355-369` 定义 `TaskParserResult`；失败结果 `Value` 为 `null`，`IsSuccess` 为假，且错误列表保留稳定错误码。
- 合法空格语法未被破坏：`tests/ReminNote.Tests/TaskParserTests.cs:62-72` 覆盖“明天 23:59”日期加时间，`TaskParserTests.cs:84-98` 覆盖 ASCII/en dash 合法范围；`TaskParserTests.cs:164-173` 覆盖 `00:00` 与 `23:59` 时钟边界。

### 目标输入覆盖

`tests/ReminNote.Tests/TaskParserTests.cs:115-131` 新增 8 个回归样例：

- `TaskParserTests.cs:116-118`：`今天18:00`、`明天18:00`、`后天08:05`，均要求 `task.parser.time.invalid`；
- `TaskParserTests.cs:119-121`：上述三种粘连时间带标题，仍要求 `task.parser.time.invalid`，不会静默成为逻辑今天 `ANYTIME` 标题；
- `TaskParserTests.cs:122-123`：ASCII 连字符和 en dash 粘连范围并带标题，均要求 `task.parser.range.invalid`。

`TaskParserTests.cs:220-226` 的 `AssertFailure` 同时断言失败、`Value == null`、恰好一个错误及精确错误码。因此本次终审列出的日期、时间、粘连范围、粘连加标题、错误码和失败值边界均有自动化覆盖；既有空输入、非法日期/时间、零时长范围、保留语法、标题长度和确定性回归仍在同一测试类中保留（`TaskParserTests.cs:133-209`）。

## Main/Widget 写入边界

- Main：`src/windows/ReminNote.Windows/Features/Today/TodayPageViewModel.cs:377-383` 解析 Quick Add 后，失败立即设置错误码并 `return`；`TodayPageViewModel.cs:385-390` 的 `CreateAsync` 只在该早退之后执行。因此 Parser 失败不会从此路径进入应用写入，也不会由 ViewModel 直接写 SQLite。既有失败不写入测试见 `tests/ReminNote.Tests/TodayPageViewModelTests.cs:69-75`，其中第二次保留语法失败后 `CreatedCommands` 仍只有一次。
- Widget：`src/windows/ReminNote.Widget/ViewModels/WidgetViewModel.cs:603-613` 同样在 `CreateAsync` 前检查 `parsed.IsSuccess` 并返回；调用位于 `WidgetViewModel.cs:615-619`。既有失败不写入测试见 `tests/ReminNote.Tests/WidgetInteractionTests.cs:120-136`，失败后 `CreatedTasks` 为空。
- 这些 Main/Widget 测试使用保留语法作为既有 Parser 失败样例，不是新增粘连字符串的 UI 集成测试；新增粘连字符串的精确语义由 Parser 回归测试覆盖，早退代码对具体错误形状不分支。真实宿主输入粘连样例及 SQLite 行数不变仍属于下方人工验收。

## 变更范围与审查材料保护

`git diff --name-status 96befb7..9a7d293` 的真实结果仅为：

```text
A  docs/slices/P2-02-parser-glued-date-fix.md
M  src/windows/ReminNote.Core/Tasks/Parsing/TaskParser.cs
M  tests/ReminNote.Tests/TaskParserTests.cs
```

没有 `reviews/` 或既有 `second-review/` 路径变化；没有修改 UI、Infrastructure、Application service、数据库、项目配置或测试以外的产品文件。`git diff --stat 3ad77a7..9a7d293` 为空，说明总成 cherry-pick 未改变原 Parser/测试修复内容，仅补入该 Slice。未执行 push、merge、rebase、force-push、purge 或 `scripts/clean.ps1`；未创建或修改 `.devdata/reminnote.sqlite`。

## 实际执行命令与结果

以下命令均在 `C:\Users\EMT\.codex\worktrees\0e50\Anime` 执行：

| 命令 | 真实结果 |
|---|---|
| `scripts/test.ps1 -Configuration Release` | 退出码 0；测试项目构建 0 警告、0 错误；`ReminNote.Tests` 总计 170，Errors 0、Failed 0、Skipped 0、Not Run 0；脚本内 P0-07 通过。 |
| `tests/ReminNote.Tests/bin/Release/net10.0/ReminNote.Tests.exe -noLogo -noColor -class ReminNote.Tests.TaskParserTests` | 退出码 0；Parser 类 40，Errors 0、Failed 0、Skipped 0、Not Run 0。 |
| `scripts/build.ps1 -Configuration Release` | 退出码 0；8 个项目成功生成，0 警告、0 错误。 |
| `scripts/verify-p0-07.ps1` | 退出码 0；`213` 个资源键、`7` 个锁定项目、`1` 个测试项目及 Shell/TODAY/ANIME/Widget 标记检查通过。 |
| `git diff --check` | 退出码 0；无输出、无 whitespace errors。 |
| `git diff --check 96befb7..9a7d293` | 退出码 0。 |

验证前后均核对：`HEAD=9a7d293e6db52d617087d888168217165cc367af`，当前 worktree 无未提交改动，`.devdata/reminnote.sqlite` 不存在；因此本次验证未修改开发/生产数据。

## 人工验收剩余项

在修正/澄清 `P2-SR-01` 的仓库根目录后，仍需由用户执行：

1. Main 与 Widget 双宿主均提交 `今天18:00`、`明天18:00`、`后天08:05` 及粘连范围输入（含标题），确认分别显示稳定错误、无成功提示、Today 不出现错误标题，SQLite `tasks` 行数不增加且两宿主使用同一数据库路径；
2. 提交 `明天 18:00 合法空格任务`，确认日期为逻辑今天次日、时间为 `18:00`、标题不变，并由另一宿主刷新看到同一 Task；
3. 完成终审 P2-GATE-01 的双宿主同库、重启保留、跨午夜 `00:30/01:00` 实际时序、已有 P1 数据 forward migration/幂等、非法根目录非零及路径核对；同时保留审查材料不变证据。

## 最终门禁判断

- `IMP-P2-1`：**解除**。实现已在日期/时间消费及标题回退前拒绝目标粘连形状；8 个精确回归样例和 Parser 类 40/40 通过，错误码与失败值边界满足终审要求。
- P2 完成声明：**当前不允许**。P2-GATE-01 人工证据尚未闭环，且 P2-02 Slice 的人工验收路径需先澄清；P2.5 项按终审结论仍是随完成声明记录的后置门槛，不是本次 Parser 复核的新增阻断项。
