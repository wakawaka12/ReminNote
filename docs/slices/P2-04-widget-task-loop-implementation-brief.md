# P2-04 Widget 真实 Task loop

## 范围

本 Slice 只接入 `ReminNote.Widget` 的本地 Task loop，不实现 Reminder、Anime、Agent 或 IPC。Widget 正式入口解析 `--repo-root <path>`，通过同一个 `TaskWorkspace` 同时提供 `ITodayQueryService` 与 `ITaskApplicationService`，数据库固定使用 `<repo-root>/.devdata/reminnote.sqlite`。无参 `WidgetViewModel` 继续保留 P0 Mock 构造路径，既有 Mock 交互测试不改语义。

## 已接入行为

- 启动时校验仓库根目录，完成同一 SQLite 工作区的迁移；激活已有实例时刷新并激活窗口，正常运行期间短轮询 Today。
- Today 页面绑定真实主任务、开放队列、时间/分组/状态、OPEN/COMPLETED/NEEDS REVIEW 汇总；刷新使用注入时钟，通过 `ITodayQueryService` 获取读模型，不在 ViewModel 直接访问 DbContext、实体或 SQL。
- Quick Add 先经过 `TaskParser`，成功后通过 `ITaskApplicationService.CreateAsync` 写入；解析失败不写库并显示错误码，写入后立即刷新。
- DONE、RANGE 的 PARTIAL/MISSED 通过 `RecordResultAsync` 写入，结果写入后立即刷新主任务、队列和汇总；RANGE 结束无结果保持 NEEDS REVIEW，不自动伪造 MISSED。
- live Widget 的 Snooze/Reschedule 不改变计划时间，显示“请在 Main TODAY 编辑计划”的明确提示；无参构造仍保留 P0 Mock 的原行为。

## 独立验证

`tests/ReminNote.Tests/WidgetInteractionTests.cs` 覆盖：

- TaskParser 驱动的 live Quick Add、非法保留语法不写入；
- DONE 后队列和汇总刷新；
- RANGE 的 PARTIAL 结果和 NEEDS REVIEW 清除；
- live Snooze/Reschedule 不伪造改期；
- `--repo-root` 的显式路径校验；
- 使用真实 `TaskWorkspace`/SQLite 写入、DONE、释放并重启工作区后仍保留结果；
- 原有 P0 Mock 交互兼容。

## 人工验收步骤与失败判定

1. 准备包含 `.git` 和 `ReminNote.sln` 的仓库根目录，分别用 `--repo-root "<repo-root>"` 启动 Main 与 Widget；检查两者未创建其他数据位置，并确认 `<repo-root>/.devdata/reminnote.sqlite` 出现。
2. 在 Main TODAY 创建一个明确时间的 Task，在 Widget 等待一次刷新（不超过短轮询周期）；主任务/队列应出现相同标题、日期、时间和状态。用 Widget Quick Add 添加 `明天 18:00 Widget 验收任务`，回到 Main 刷新后应读到同一条记录。
3. 在 Widget 点击 DONE；主任务应写入 COMPLETED，Widget 汇总和队列应立即刷新到下一条开放任务。关闭并重新启动 Widget（仍传同一 `--repo-root`），已写入 Task 与结果必须保留。
4. 准备一个已结束但没有结果的 RANGE（例如 `23:00-01:00`），刷新后应显示 `NEEDS REVIEW`/等待结果；只有点击明确的 PARTIAL 或 MISSED 后才写入结果并从待处理计数中清除。
5. 对 live Task 点击 Snooze、Reschedule；Widget 必须明确提示由 Main TODAY 编辑，且计划时间、结果和队列顺序不得被伪造改变。无 `--repo-root` 或传入不含 `.git`/`ReminNote.sln` 的路径时，启动必须失败并返回非零退出状态。

以下任一情况即验收失败：Widget 读到的不是 Main 使用的 SQLite、Quick Add 绕过 Parser 或失败仍写库、DONE/结果后不刷新、重启丢失数据、RANGE 自动变为 MISSED、Snooze/Reschedule 擅自改期、无效根目录静默落盘，或出现 Reminder/Agent/IPC 的实现。

## Release 验证记录

本机 `global.json` 要求的 SDK `10.0.100` 未安装，使用已安装的 `10.0.400` MSBuild 直接执行等价 Release 流程：

- `src/windows/ReminNote.Widget/ReminNote.Widget.csproj` Release 构建：通过。
- `tests/ReminNote.Tests/ReminNote.Tests.csproj` Release 构建：通过。
- `ReminNote.sln` Release 构建（本地非锁定还原生成资产后、`/p:Restore=false`）：通过。
- `tests/ReminNote.Tests/bin/Release/net10.0/ReminNote.Tests.exe -noLogo -noColor`：82/82 通过，0 错误、0 失败、0 跳过。

仓库标准 `scripts/test.ps1 -Configuration Release` 的 locked restore 明确因 Widget 新增项目引用报 `NU1004`；Widget 项目引用 Core/Infrastructure 后，公共 `src/windows/ReminNote.Widget/packages.lock.json` 仍由 P2-00 负责更新。本 Slice 未提交该锁文件；整仓 locked restore/build 需在总成窗口完成锁文件整合后再执行。
