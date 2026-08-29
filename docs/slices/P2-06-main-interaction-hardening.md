# P2-06 Main 交互加固 Slice

- 阶段：P2 Real TODAY / Main interaction hardening
- 日期：2026-08-29
- 基线：`6f8e127` 的 Main 交互二审后工作树（当前工作树含二审报告提交）
- 状态：代码与自动化验证已完成；真实桌面 GUI 验收待用户执行

## 目标

处理 P2 Main/Today 交互二审中仍阻塞交互闭环的 R1-R4：

1. 已有结果的 Task 不再暴露或接受拖动排序入口；
2. 拖动只能从真正的拖动手柄开始，保留任务选择、完成和置顶等普通点击；
3. Main 在启动、激活和短周期轮询时收敛 Today Read Model，刷新失败保留当前列表，退出时释放轮询资源；
4. 选中的 Task 在刷新后消失时清除选中态并关闭详情，给出明确失效提示，不回退到另一个 Task。

## 范围

允许修改：

- `src/windows/ReminNote.Windows/Features/Today/**`；
- 必要的 `src/windows/ReminNote.Windows/MainWindow.xaml.cs`、`App.xaml.cs` 或 `Startup/**`；
- `tests/ReminNote.Tests/TodayPageViewModelTests.cs`；
- `tests/ReminNote.Tests/StartupAndMainUiInteractionTests.cs`；
- 本文件。

不实现：

- Widget、Reminder、Agent 业务服务、IPC、Single Writer、WAL、Change Journal；
- 排序 application service 的日期/分组/版本上下文提升或多行批量原子写入；
- 任何结果字段、计划字段或历史语义变更；
- `reviews/`、`second-review/` 和既有报告文件修改。

## 实现要点

- `TodayTaskViewModel.CanReorder` 以及 Main Today 的拖动/相邻移动入口共同拒绝已有结果的 Task。
- COMPLETED 分组关闭拖放行为，完成 Task 的手柄折叠；可排序分组使用绑定的 `IsReorderEnabled`。
- 拖放行为通过 `IsDragHandle` 附加属性识别专用 Button；非手柄的卡片、完成按钮、选择区域和置顶按钮不会建立拖动状态。
- MainWindow 复用 WPF Dispatcher 增加 2 秒 Today 刷新 timer；Loaded、Activated 和二次实例激活请求都会请求刷新，刷新请求串行化并使用窗口生命周期 CancellationToken。
- 查询成功后才应用 Read Model；查询失败只更新失败反馈，不清空已有列表。
- Today Read Model 应用时按 TaskId 恢复选择；若原 TaskId 不存在，清除选择、发出失效事件并关闭 TodayDetailsWindow，保留明确失效提示。

## 自动化与静态回归

- R1：验证完成 Task 的 `CanReorder`、COMPLETED 分组拖放状态以及拖动/相邻移动均不进入 application write mock。
- R2：静态检查专用手柄、命中标记、手柄可见性绑定和行为的手柄过滤；真实鼠标命中仍需人工验收。
- R3：验证 Today 查询失败时现有 VM 列表与选择不变；静态检查 Main 的 Loaded/Activated/timer/关闭清理以及二次实例激活转交。
- R4：验证刷新移除已选 Task 时 `SelectedTask` 清空、TaskId 失效事件发出且不回退到 PrimaryTask；静态检查 Main 订阅事件并关闭详情窗口。

本 Slice 实际验证：

- `./scripts/build.ps1 -Configuration Release`：通过；8 个项目构建，0 warning、0 error。
- `./scripts/test.ps1 -Configuration Release`：通过；`ReminNote.Tests` 180/180，Errors 0、Failed 0、Skipped 0、Not Run 0；脚本内 P0-07 通过。
- `./scripts/verify-p0-07.ps1`：通过；213 resource keys、7 locked projects、1 test project，并完成 Shell/TODAY/ANIME/Widget 标记检查。
- `git diff --check`：通过；无 whitespace errors。
- 未启动或控制桌面 GUI，未修改 `D:\Anime\\.devdata`、`reviews/`、`second-review/` 或既有报告。

## 人工验收步骤

以下步骤由用户在 Windows 桌面执行。本窗口不启动或控制桌面 GUI。

环境：仓库根目录为当前工作树，使用 Release 输出和该工作树的 `.devdata/reminnote.sqlite`；执行前关闭已有 Main/Widget 实例。

### Test 1 — COMPLETED 不可排序（R1）

1. 构建并启动 Main，进入 TODAY。
2. 创建两个同一日期、同一分组的开放 Task，再完成其中一个；展开 `COMPLETED`。
3. 观察开放 Task 和完成 Task 的行，分别点击选择、完成和置顶控件。
4. 尝试从完成 Task 的行或其原手柄位置开始拖动，再刷新 TODAY。

预期：完成 Task 没有可发现/可命中的拖动手柄，不能改变任何 Task 的顺序；选择、完成和置顶的普通点击仍按原语义工作。失败：完成 Task 可启动拖动、排序写入成功、计划/结果字段被改写，或普通点击被拖动逻辑吞掉。

### Test 2 — 只有手柄可拖动（R2）

1. 在同一日期、同一 Today 分组准备两个开放 Task。
2. 从任务标题、时间、空白卡面、完成按钮和置顶按钮分别按住并移动鼠标。
3. 从左侧 `⋮⋮` 手柄按住并拖到另一个任务的上半部或下半部。
4. 再将一个任务拖向其他分组或不同日期任务，并点击普通任务区域。

预期：只有手柄可以启动排序；卡片其他区域只执行其普通点击行为；手柄有约 32 DIP 的合理命中区域、鼠标光标提示和可读的 `拖动手柄` AutomationProperties。有效手柄拖动只改变同日期同分组顺序，跨组/跨日期不改计划。失败：点击卡面后移动即可排序、完成/置顶按钮触发拖动、手柄难以命中/无辅助功能名称，或排序改写计划日期/时间。

### Test 3 — Main 启动、激活和短周期刷新（R3）

1. 启动 Main 并停留在 TODAY；确认初始列表可读。
2. 用同一工作树启动 Widget，在 Widget 完成、Quick Add 或记录结果，再回到 Main；也可等待超过 2 秒轮询周期。
3. 启动同一个 Main 可执行文件的第二个实例，观察已有 Main 是否被激活而不是打开第二个窗口。
4. 在测试期间制造一次查询失败（例如临时让查询依赖返回失败），观察原列表；随后关闭 Main。

预期：启动后有 Today 列表，激活/二次实例请求和短周期轮询会重新读取同一份本地 Task，通常在一个轮询周期内收敛；刷新失败保留刷新前的列表和选择并给出失败提示；关闭无 timer/事件异常。失败：Main 长期显示 Widget 已写入前的旧状态、刷新失败清空列表、二次启动打开第二个 Main，或退出时出现未处理异常。

### Test 4 — 选中 Task 消失后的详情安全（R4）

1. 在 Main TODAY 选中一个会在刷新后从活动列表移除的 Task（例如较早日期的未完成 Task），打开 `TODAY 任务详情`。
2. 用 Widget 或另一个已运行的宿主完成该 Task，触发 Main 的刷新；也可以在 Main 详情打开期间等待短周期刷新。
3. 观察详情窗口、TODAY 选中态和交互提示；随后重新选择另一个 Task，再执行其详情操作。

预期：原 TaskId 在新 Read Model 中不存在时，详情窗口关闭或明确显示失效状态，TODAY 选择被清除，并出现“已选 Task 已不在 TODAY 列表中”一类明确提示；不会自动把详情绑定到 PrimaryTask，也不能把后续操作落到另一个 Task。刷新失败时则保留原选择和详情，不得把失败误判为 Task 消失。失败：详情静默显示另一个 Task、原详情按钮继续操作另一个 Task、选中态无提示，或查询失败导致详情被关闭。

## 已知剩余风险

- WPF 的真实鼠标命中、拖动手柄 UI Automation 行为、窗口激活和详情关闭需要用户桌面验收；静态检查与 ViewModel 测试不能替代该验收。
- P2 当前一次拖动仍可能由多个独立 application service 写入组成；排序服务级上下文校验与复合操作原子性属于二审报告标记的 P2.5 后置项，本 Slice 不扩大处理。
- Main/Widget 双宿主的真实重启、跨午夜、已有 P1 数据迁移和 `.devdata` 数据安全仍遵循既有 P2 验收稿，不能由本 Slice 的自动化测试单独宣称通过。
