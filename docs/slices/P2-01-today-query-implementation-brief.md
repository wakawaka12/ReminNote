# P2-01 Today 查询与 Read Model 实施说明

- 阶段：P2 Real TODAY / Widget Task loop
- 窗口：P2-01 Today Read Model
- 状态：已完成实现与独立自动化验证；等待总成窗口修复其他并行模块后复跑全量门禁
- 前置契约：`docs/slices/P2-00-integration-and-contract-brief.md`

## 目标

让 Today 查询在不读取系统当前时间的前提下，根据调用方提供的 `Instant`、用户时区和工作日边界，生成可供 Main/Widget 共用的 Today Read Model。查询只返回当前逻辑工作日的计划和未完成历史计划，不改变 Task 或数据库状态。

## 范围

- `ReminNote.Core.Today` 的 Today 分组、状态、Read Model 和纯分类器；
- 使用既有 `IUserTimeZoneProvider`、`IWorkdaySettingsStore` 与 Noda Time 的 Today 查询适配器；
- 默认 `OVERDUE`、`MORNING`、`AFTERNOON`、`EVENING`、`ANYTIME`、`COMPLETED` 分组；
- `TIME`、同日 `RANGE`、跨午夜 `RANGE`、结果和稳定排序的查询测试。

## 不在范围内

- Parser、Quick Add、Reminder、Agent、IPC、WAL、Change Journal；
- migration、数据库实体/表结构、Main/Widget 页面和 ViewModel；
- 将无结果的 RANGE 自动写成 `MISSED`；
- 将 `RecordedAt` 解释成实际完成时间。

## 实现规则

1. 工作日由用户时区中的本地 civil 时间计算：早于边界使用前一自然日，恰好到达边界使用当前自然日。
2. 未来计划不进入 Today；未完成的历史 `ANYTIME`/`TIME` 为 `OVERDUE`。
3. `TIME` 在计划时间之前（含恰好时刻）保留其 MORNING/AFTERNOON/EVENING 位置，超过计划时刻才为 `OVERDUE`。
4. `RANGE` 按包含开始、不包含结束的本地区间判断：尚未开始为 `UPCOMING`，区间内为 `PLANNED`，结束时刻及之后为 `AWAITING RESULT` 并设置 `IsNeedsReview`。
5. 结束的 RANGE 和跨午夜 RANGE 始终保留开始时间对应的分组及计划日期；等待结果通过状态、`IsNeedsReview` 和 `NeedsReviewCount` 表达，不自动改变 Task 结果。
6. 结果存在的 Task 进入 `COMPLETED`；已完成的历史计划不进入 Today，当前逻辑工作日的已完成计划仍显示。
7. 列表顺序按分组、计划日期、持久化 `SortOrder`、计划时间和 UUID 字符串序稳定排序；`NEEDS REVIEW` 是独立摘要，不把任务从原计划分组挪到列表顶部。

## 复用与依赖检查

- 复用 P1 已冻结的 `TimeSpec`、`TaskSnapshot`、`IClock`/Noda Time 和查询边界；不新增 NuGet 依赖。
- 时区转换使用既有 `IUserTimeZoneProvider`，工作日边界使用既有 `WorkdayService` 与 `IWorkdaySettingsStore`。
- Core 继续不引用 WPF、EF Core、SQLite、Serilog 或 Windows API。

## 自动化验收

`TodayQueryTests` 应覆盖：

- 工作日边界前、边界时刻和边界后的日期；
- 固定用户时区下由 `Instant` 转换出的本地日期/时间；
- 过去、当前、未来计划和未来日期过滤；
- TIME、同日 RANGE、跨午夜 RANGE 在午夜前后及结束时刻的状态；
- COMPLETED 分组、历史已完成过滤和 NEEDS REVIEW 计数；
- 分组边界与 `SortOrder`/时间/ID 的确定性排序。

通过判定：Release 构建成功，实际测试可执行文件退出码为 0，Today 查询测试全部通过且无新增警告。

失败判定：未来任务进入 Today、历史已完成任务误显示、跨午夜任务在换日后消失或提前进入复盘、RANGE 被自动记为 `MISSED`、NEEDS REVIEW 计数错误、分组/排序随输入顺序变化，或构建/测试非零退出。

## 本次验证证据

- `C:\Program Files\dotnet\dotnet.exe build src/windows/ReminNote.Core/ReminNote.Core.csproj --configuration Release --no-restore`：0 警告、0 错误；
- `C:\Program Files\dotnet\dotnet.exe build src/windows/ReminNote.Infrastructure/ReminNote.Infrastructure.csproj --configuration Release --no-restore`：0 警告、0 错误；
- 临时隔离测试项目引用当前 Core/Infrastructure 和既有业务测试，Release 编译成功；直接运行 xUnit v3 executable：75/75 通过、0 失败、0 跳过；
- `scripts/verify-p0-07.ps1`：通过，213 个资源键、7 个锁定项目和 UI 标记检查完成；
- 完整 `scripts/build.ps1 -Configuration Release` / `scripts/test.ps1 -Configuration Release` 的锁定还原被总成工作树已有的 Widget lock 文件缺少 Core/Infrastructure 项目引用阻断（`NU1004`）；绕过还原执行完整 Release build 时，剩余失败均来自已有 `src/windows/ReminNote.Widget/ViewModels/WidgetViewModel.cs` 未完成改动。P2-01 未修改这些文件，需总成窗口完成公共锁文件与 Widget 后复跑。

## 人工验收步骤

环境：Windows 10 22H2 或 Windows 11，Release 配置，使用仓库根目录的 `.devdata/reminnote.sqlite`；人工验收前先备份开发库，不使用生产数据。

1. 启动 Release Main，创建一个当天的 ANYTIME、一个当前时间之后的 TIME、一个覆盖当前时刻的 RANGE，以及一个 `23:00–01:00` 跨午夜 RANGE；确认它们分别出现在 ANYTIME、对应时间组和晚间原计划组。
2. 将系统时间（或通过测试夹具）推进到 TIME 之后、同日 RANGE 结束之后；重新激活/刷新 Today。预期 TIME 显示 `OVERDUE`，RANGE 显示 `AWAITING RESULT`、保留原时间组并计入 `NEEDS REVIEW`，数据库中的结果仍为空。
3. 在跨午夜 RANGE 的次日 `00:30` 刷新 Today。预期任务仍可见、仍属于原计划日期/晚间位置，且在 `01:00` 前不计入 NEEDS REVIEW；`01:00` 及之后才等待结果。
4. 为一个当天计划记录 `COMPLETED` 或 RANGE 的 `PARTIAL` 结果并刷新。预期任务进入 `COMPLETED`，不再计入 NEEDS REVIEW；重启后读取结果一致。
5. 准备多个相同分组任务，重复刷新并改变数据库返回顺序（不改变 `SortOrder`）。预期显示顺序仍按分组、日期、`SortOrder`、时间和 ID 一致。

人工失败包括：出现未来日期、已完成历史任务、错误时区日期、跨午夜丢失/换组、RANGE 自动 MISSED、结果时间被当作实际完成时间、NEEDS REVIEW 数量不一致、刷新顺序不稳定或应用崩溃。

## 风险与限制

- 本 Slice 只提供查询分类，不改变 Task 结果；Main/Widget 的写入和刷新接线由其他窗口负责。
- `RANGE` 的“逾期”由 `AWAITING RESULT` 与 `IsOverdue` 表达，同时保留原计划组，以满足 NEEDS REVIEW 与原计划位置两个契约。
- 真实开发库 migration、双宿主手测和最终阶段报告由 P2-00/P2-05 负责；本 Slice 的自动化夹具不得写入 `.devdata`。
