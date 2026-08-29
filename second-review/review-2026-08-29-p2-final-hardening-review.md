# ReminNote P2 总成独立终审报告：R1-R5 与 P2-GATE-01

复核日期：2026-08-29

## 审查范围与基线

- 仓库：`D:\Anime`
- 分支：`codex/p0-integration`
- 审查前 HEAD：`47b0a6e2782e2d768b52b5b9e51689c0ce16a68d`
- P2 代码基线：`4406e7d4faf0d2e3d7d342ba5eadecf18e176f83`（Widget 失败反馈）与 `1dc095f8c1a015d723e2fffebd7465a3958d388f`（Main TODAY 交互与刷新）
- 本次工作树变更范围：仅本报告；未改源码、既有 `reviews/`、既有 `second-review/` 或 `docs/reports/`。

本次按委托只做静态代码/测试审查和只读验证。不启动或控制 Main/Widget GUI，不执行 `scripts/run.ps1` 或任何宿主 EXE；没有打开、查询或写入 `D:\Anime\.devdata\reminnote.sqlite`，测试也未指向该文件。

## 最终结论

R1-R5 的整改代码确实存在，但不能据此宣称所有边界已经完全闭环。Main 的 R1/R2/R4 主路径、R3 启动与轮询主路径，以及 Widget 的 R5 ViewModel 反馈路径有自动化/代码证据；同时仍保留三类需要明确记录的边界：

1. R1 的保护主要位于 Main UI/ViewModel，`TaskApplicationService.ReorderAsync` 仍可被旧 read model 或另一宿主按 TaskId 直接调用，尚不是服务层结果/日期/版本不变量。
2. R4 的 Main 选择失效链路已闭环，但 Widget 刷新选中项消失时仍有回退到下一主任务的代码，不能判定双宿主语义完全闭环。
3. R3 的窗口级刷新锁不覆盖 Today ViewModel 写入后的无参刷新；R5 的 ViewModel 致命异常过滤也没有被 Widget 宿主层的宽泛 `catch (Exception)` 完整保持。

因此本次裁决为：代码整改基本通过、存在明确的 MINOR/边界风险；P2 最终完成声明仍不通过，原因还包括 `P2-GATE-01` 真实跨午夜人工门禁尚未完成。

## R1-R5 逐项裁决

| 项目 | 当前裁决 | 已确认的代码/自动化证据 | 尚未闭环或证据边界 |
|---|---|---|---|
| R1：COMPLETED/result Task 不可拖动/排序 | Main UI/VM 入口通过；服务不变量未闭环 | `TodayTaskViewModel.CanReorder` 要求 live、未完成且有 TaskId；COMPLETED 分组 `IsReorderEnabled` 为 false；拖放与相邻移动入口均检查该属性；`CompletedTasksCannotEnterTheReorderWritePath` 通过 | `TaskApplicationService.ReorderAsync` 仍按 TaskId 取行并调用 `SetSortOrder`；`Task.SetSortOrder` 只校验非负 sort order。旧 read model/跨宿主直接排序仍可进入服务层，属于已记录的 P2.5 防御性边界 |
| R2：只有专用手柄触发拖动并有 UI Automation 语义 | 代码通过；真实 UIA/鼠标未实测 | XAML 使用独立 Button 拖动手柄，宽 32 DIP，绑定 `CanReorder`，设置 `IsDragHandle=True`、Automation Name/HelpText；行为层只有命中手柄才建立 DragState，普通卡片控件不会进入拖动路径；DragOver/Drop 继续检查 `CanReorder` | 本窗口按要求未启动 WPF；没有真实鼠标命中、普通按钮点击不被吞、UIA 客户端拖动模式的运行证据。跨日期/分组在 Drop 最终被 VM 拒绝，但 DragOver 仍可能短暂显示 Move |
| R3：Main 启动、激活、短周期 Today 刷新、退出清理 | 主路径通过；严格全局串行化为条件通过 | `MainWindow` 在 Loaded 启动 2 秒 DispatcherTimer，Activated 和二次实例激活均请求刷新；关闭时停止 timer、解绑 Tick、取消生命周期 token；`App` 初始化宿主并通过单实例管道激活已有窗口；静态生命周期测试通过 | `_todayRefreshInProgress` 只覆盖 MainWindow 自身的刷新调用；写入完成后的 Today ViewModel 无参 `RefreshAsync` 可与 timer/激活刷新重叠，旧查询可能在新查询后应用，下一周期才纠正。没有真实 Main 窗口启动/激活/退出实测 |
| R4：选中 Task 被刷新移除时清除、关闭、提示 | Main 通过；Widget 未完全通过 | Main `ApplyReadModel` 按 TaskId 恢复选择；找不到原选中项时清除 `SelectedTask`、保持失效标记、设置“已选 Task 已不在 TODAY 列表中 · 详情已关闭，请重新选择”、触发 `SelectedTaskInvalidated`；MainWindow 收到事件关闭详情窗口；`RefreshClearsASelectedTaskThatDisappearedWithoutFallingBackToPrimary` 通过 | Widget `RefreshCoreAsync` 在旧选中项不在新队列时，仍执行 `nextSelectedItem ??= nextItems.FirstOrDefault(...)` 回退到主任务，再传给 `SetSelectedTask`。现有“自身 DONE 后切到下一开放任务”测试只证明该产品路径，不能证明外部刷新移除时不静默换项；缺少清除/提示测试 |
| R5：Widget 写入/刷新失败、旧列表、取消、致命边界 | ViewModel 反馈路径通过；宿主致命边界需修正/确认 | 刷新先构造下一份 read model，成功后才替换队列；非致命刷新失败保留旧队列并反馈；写入失败与写入成功但刷新失败使用不同反馈；Quick Add 写入失败保留输入；取消继续抛出；`IsFatalException` 不把 OOM、StackOverflow、AccessViolation、SEH 及包装异常转成普通反馈；相关 Widget 失败/取消测试在全量套件通过 | `Widget/App.xaml.cs:109-132` 仍以宽泛 `catch (Exception)` 包住 ViewModel 刷新，ViewModel 逃出的致命异常在宿主边界可能只被记录而不继续上抛；没有真实数据库故障注入、桌面反馈可见性或退出中取消竞态的本窗口证据 |

## 1. R1/R2：任务结果与拖动入口

当前 Main 的正常 live read model 中，已有结果的任务没有排序手柄；`CanReorder` 还会被完成状态变化通知。完成分组关闭拖放，`ReorderTaskByDropAsync` 和相邻移动均在进入 application write 前拒绝不可排序任务。对应单元测试还检查了拖放、相邻移动和完成分组开关均不会进入 reorder mock。

这足以证明此前“完成任务仍可从 Main 普通路径拖动/排序”的入口缺口已修复，但不能把 UI 守卫扩大解释为服务层强不变量。当前 application service 只接收 `ReorderTaskCommand(TaskId, SortOrder)`，没有结果、计划日期、分组或版本上下文；这与 P2.5 中的排序防御性后置项一致。

拖动行为的启动条件也已收窄到专用 Button：PreviewMouseLeftButtonDown 先向视觉树上查找 `IsDragHandle`，随后才查找任务并建立 DragState；超过系统拖动阈值后才调用 `DoDragDrop`。普通卡片、完成任务和其他卡片按钮不会建立该状态。XAML 的拖动手柄具有明确的 32 DIP 命中宽度和 Automation Name/HelpText。

但本窗口没有运行 WPF/UI Automation，因此“真实命中手柄”“点击完成/选择/置顶按钮不被拖动行为吞掉”“辅助技术客户端实际看到的拖动语义”均只列为代码/静态证据，不列为人工实测通过。

## 2. R3：Main 生命周期与刷新边界

代码覆盖以下主链路：Loaded 后开启 2 秒轮询并请求初次刷新；Activated 请求刷新；二次实例通过 `ActivateFromExternalRequest` 还原、激活和聚焦已有窗口并请求刷新；关闭时停止 timer、解绑事件、取消生命周期 token；查询失败不调用 `ApplyReadModel`，因此保留当前列表和选择。

现有 `StartupAndMainUiInteractionTests.MainTodayRefreshLifecycleCoversStartupActivationAndShutdown` 检查这些源码挂接点，单实例管道测试也在全量套件内通过。不过这是静态/组件级自动化，不是本窗口实际启动 Main、切换前后台、等待 2 秒刷新或退出后的桌面证据。

还需保留一个并发事实：MainWindow 的 `_todayRefreshInProgress` 只包住窗口 timer/activation 发起的 `RefreshTodayAsync`。Today Page 的 Quick Add、改期、结果和排序等写入路径在写入后直接调用 VM 的无参 `RefreshAsync`，没有共享的 refresh gate。定时查询和写入后查询可能交错，较早的旧 read model 可能短暂覆盖较新的状态，之后由下一个周期纠正。若 P2-00 的“刷新请求串行化”是严格要求，这仍需修正或明确接受。

## 3. R4：选择失效与双宿主差异

Main 的 `ApplyReadModel` 先保存旧选中 TaskId，再构造新列表。旧 ID 不存在时，它清除选择、设置失效标记、取消指向该任务的改期面板、写入明确提示并触发 `SelectedTaskInvalidated`；MainWindow 订阅该事件并关闭 TodayDetailsWindow。查询失败发生在 read model 应用前时，不会把旧选择误判为消失。

Widget 目前的选择算法不同：刷新只把未完成任务放入队列，旧选中项从队列消失后会回退到新队列首项/主任务。该逻辑对“用户刚在 Widget 自身记录 DONE 后自然转到下一开放任务”是有意的，现有自动化覆盖了这个场景；但对另一宿主完成任务、或外部刷新导致当前选择消失的场景，它没有区分原因，可能造成静默切换。若 R4 的验收范围包含 Widget，必须补充清除当前选择、提示失效并避免静默切换的状态机和测试。

## 4. R5：Widget 失败反馈与取消传播

`RefreshCoreAsync` 在查询完成、下一队列构造完成后才提交 `_todayItems`、主任务和选择状态，所以非致命查询失败不会先清空旧队列。Quick Add 和 DONE/PARTIAL/MISSED 结果操作分别处理：

- 写入失败：保留旧列表；Quick Add 输入保留；展示稳定的写入失败反馈。
- 写入成功、刷新失败：保留旧列表，并明确说明“已写入/已记录但 TODAY 刷新失败”；不显示“已刷新”假消息。
- 取消：`OperationCanceledException` 继续传播，不改写成普通失败反馈。
- 致命异常：ViewModel 的 `IsFatalException` 排除 OOM、StackOverflow、AccessViolation、SEH 及 Aggregate/InnerException 包装，不把它们吞成普通反馈。

`WidgetInteractionTests` 已覆盖 Quick Add 通用写入失败、结果通用写入失败、写入成功后刷新失败、直接刷新失败保留旧队列和取消传播；本次全量测试重新通过。需要单独记录的宿主层问题是 `Widget/App.xaml.cs` 的刷新包装器仍无致命异常过滤，因而 ViewModel 的边界尚未在进程宿主层完全保持；本窗口也没有做真实 SQLite 故障注入或退出中取消竞态。

## 5. Parser 中文相对日期粘连

`TaskParser.Parse` 在日期和时间解析前调用 `TryGetGluedRelativeDateError`。因此 `今天18:00`、`明天18:00`、`后天08:05` 及带标题形式会返回稳定的 `task.parser.time.invalid`；粘连的 RANGE 形式返回 `task.parser.range.invalid`，不会把日期时间误当作标题。`TaskParserTests` 同时保留了 `明天 23:00-01:00` 的跨午夜起始日期归属测试。

这部分是代码与自动化证据，不是桌面人工输入证据；本次没有启动 Main/Widget 来验证输入框行为。

## 6. P2-GATE-01 真实跨午夜状态

现有 `docs/reports/P2-GATE-01-runtime-acceptance-2026-08-29.md` 明确把真实 `23:00 → 次日 00:30 → 01:00` 时序列为 **Pending**：没有等待真实时钟节点，也没有修改系统时钟；固定 `Instant`、领域结构测试、`23:00-01:00` 的数据库结构或已有 Codex smoke 都不能替代该人工门禁。

本次委托也明确说明该真实门禁尚未完成。本报告不声称用户做过双宿主同库、UIA、拖动边界、重启、真实时钟跨午夜或其他未提供结果的测试；既有 `P2-GATE-01`、`reviews/` 和其他历史报告均未在本窗口修改。该状态记录是准确的，P2 不能写成最终完成或用户已签字。

## 7. 本窗口实际自动化验证

| 验证 | 结果 |
|---|---|
| `scripts/build.ps1 -Configuration Release` | 通过；8 个项目，0 个警告，0 个错误 |
| `scripts/test.ps1 -Configuration Release` | 通过；`ReminNote.Tests` 共 186 项，Errors 0、Failed 0、Skipped 0、Not Run 0；脚本内 P0-07 也通过 |
| `scripts/verify-p0-07.ps1` | 通过；213 个资源键、7 个锁定项目、1 个测试项目，Shell/TODAY/ANIME/Widget 标记检查通过 |
| `git diff --check` | 通过 |

上述测试运行在当前 `D:\Anime` 工作树的代码上；本窗口没有把旧报告里的 180/180 记录当作本次结果。测试未写入目标 `.devdata` SQLite，也未启动 GUI。

## 8. 收口建议

1. 若 R4 是双宿主要求，先修 Widget 外部刷新移除选中项的“回退到主任务”逻辑，并加入对应的 ViewModel 测试。
2. 若 R1 要求跨宿主强一致，给 reorder command/service 增加结果、计划日期、分组和版本/条件更新上下文；否则应继续把现有 service 边界标为 P2.5。
3. 明确是否接受 R3 的窗口级而非全局 refresh gate；同时决定 Widget 宿主是否需要与 ViewModel 一致地重新抛出/分类致命异常。
4. 由用户在正常桌面完成真实 `23:00 → 次日 00:30 → 01:00` 跨午夜门禁后，再由总成窗口核对证据并更新 P2 完成状态。

**终审裁决：**当前代码整改有实质效果，自动化门禁通过；Main R1/R2/R4 主路径和 Widget R5 ViewModel 主路径可确认，R3 有并发边界，Widget R4 与宿主致命异常边界仍需处理/确认，真实 P2-GATE-01 跨午夜人工验收仍 Pending。因此不通过“P2 已最终完成”的声明。
