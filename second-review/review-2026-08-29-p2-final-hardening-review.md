# ReminNote P2 修复结果独立终审（二审整改后）

复核日期：2026-08-29

目标总成基线：

- Widget 失败反馈修复：4406e7d4faf0d2e3d7d342ba5eadecf18e176f83
- Main TODAY 交互与刷新收敛：1dc095f8c1a015d723e2fffebd7465a3958d388f

本窗口当前 worktree 保持在旧的报告提交 0c552079d2d11a6c8b3c81a7f3de43cb7519577c，没有 checkout 或切换到目标总成；目标提交通过 git show/git diff 直接读取。

## 最终结论

R1-R5 的二审整改均有对应的代码变更；其中 R1、R2、R4、R5 可判定为当前 P2 代码入口已解除，R3 的主要缺口已解除但仍有一个刷新并发边界，不能把“所有刷新请求全局串行化”表述为已经完全成立。

本次没有发现 BLOCKER 或 MAJOR。自动化证据显示目标 Slice 记录的 Release 构建、180/180 测试和 P0-07 均通过；本窗口没有把旧 worktree 的测试结果冒充为目标提交的重新执行结果。用户本次委托明确报告修复结果已完成，但真实 23:00 → 次日 00:30 → 01:00 跨午夜时序仍缺人工门禁；该门禁继续阻塞 P2 最终完成声明。

### R1-R5 逐项裁决

| 项目 | 裁决 | 代码证据 | 剩余边界 |
|---|---|---|---|
| R1：已有结果 Task 的拖动/排序保护 | 已解除（P2 UI/VM 入口） | TodayTaskViewModel.CanReorder 同时要求 live、未完成和有效 TaskId；COMPLETED 分组关闭拖放，拖动与相邻移动入口都拒绝不可排序 Task，完成 Task 的手柄折叠。新增测试验证不进入 application write mock。 | ReorderTaskCommand 仍没有结果/分组/版本上下文；过期 Main read model 与另一宿主竞态时，服务层仍可接受 sort_order。这是 Slice 明确留给 P2.5 的防御性边界，不应称为 P2 service 不变量。 |
| R2：只有专用手柄可以拖动 | 已解除（代码） | 拖动手柄改为 Button，设置 IsDragHandle、32 DIP 命中区、Cursor 和 AutomationProperties；行为从手柄向上查找，普通卡面、完成、选择、置顶区域不会建立 DragState。 | OnDragOver 只检查 source/target 的 CanReorder，跨分组/跨日期到达目标时可能先显示 Move，最终 Drop 仍由 ViewModel 拒绝。真实鼠标命中、按钮点击不被吞和 UIA 操作仍需人工确认。 |
| R3：Main 刷新、激活和退出收敛 | 主要缺口已解除；严格判定为有条件 | MainWindow 已加入 Loaded、Activated、二次实例激活请求和 2 秒 DispatcherTimer；刷新失败由 ViewModel 保留旧列表并提示，关闭时停止 timer、解绑 Tick、取消生命周期 token。 | MainWindow 的 _todayRefreshInProgress 只串行化窗口自身发起的刷新；Add/Reschedule/Result/Reorder 等 ViewModel 写入完成后的 RefreshAsync 使用默认 token，TodayPageViewModel 没有统一 refresh gate。定时查询和写入后查询仍可能重叠并以较旧 read model 后应用，通常下一周期才收敛。 |
| R4：选中 Task 消失后的详情安全 | 已解除（VM 与窗口关闭链路） | ApplyReadModel 按 TaskId 恢复选择；TaskId 不存在时清除 SelectedTask、发出 SelectedTaskInvalidated、设置明确失效提示，不回退到 PrimaryTask；MainWindow 订阅事件并关闭 TodayDetailsWindow。新增测试覆盖不回退和刷新失败保留旧选择。 | 现有测试对窗口行为主要是静态检查和 ViewModel 测试，真实 WPF 详情窗口关闭、焦点恢复仍不是本窗口实测证据。 |
| R5：Widget 写入/刷新失败反馈 | 已解除（代码与新增测试） | Quick Add、DONE/PARTIAL/MISSED 结果和直接刷新分别区分写入失败、写入成功但刷新失败、取消和非致命通用异常；刷新先准备下一份 read model，失败不清空旧队列；Quick Add 输入在写入失败时保留；反馈区域增加 Polite LiveSetting。 | 真实数据库故障注入和用户桌面可见性未在本窗口执行；P2-07 将它们列为人工验收范围。 |

## 1. Main / Widget Task 身份与结果路径

目标 1dc095f 的 Main 代码仍保留具体 TodayTaskViewModel 的回调绑定。任务行的 DONE、PARTIAL、MISSED、继续执行和详情请求都由具体行对象发起；结果写入使用该对象的 DomainTaskId。刷新时不再按首项恢复选中状态。

Widget 4406e7d 保留 _liveSelectedTask、_selectedTaskItem 和 _currentTaskId 的同一身份链。队列刷新按 TaskId 保留选择；选中项消失时不会继续使用旧队列对象。此前新增的非首项 PARTIAL/DONE 测试仍在目标测试树中，证明正常 live 选择路径不会把结果写到首项。

Main 顶部 Primary Focus 的 DONE 仍明确绑定 PrimaryTask，而不是 SelectedTask。这是产品文案所表示的主焦点动作，不应被当作“顶部动作跟随任意选中项”的证据；普通任务行和详情动作才是选中 Task 语义。

## 2. R1 / R2：拖动入口、边界与跨进程说明

### 已解除的当前 P2 保护

1dc095f 在 TodayTaskViewModel 增加 CanReorder，完成状态变化会通知该属性；COMPLETED 分组的 IsReorderEnabled 为 false；TodayResources 将拖放行为绑定到该分组属性，将手柄可见性绑定到 CanReorder。TodayTaskDragDropBehavior 只有从 IsDragHandle 命中后才建立状态，且在拖动和 Drop 阶段均检查 CanReorder。

ReorderTaskByDropAsync 仍检查 source/target 的 TaskId、PlanDate、CurrentGroup 和同日期同分组位置；跨日期、跨 Today 分组的最终应用写入不会发生。已有结果的 Task 在正常、最新的 Main read model 中既没有手柄，也不能进入 ViewModel reorder write path。新增 CompletedTasksCannotEnterTheReorderWritePath 测试覆盖了拖动、相邻移动和 Completed 分组开关。

### 不应被误读成已解决的 service 边界

P2-06 明确不实现排序 application service 的日期/分组/版本上下文提升或多行批量原子写入。目标提交也没有修改 ReorderTaskCommand 或 TaskApplicationService：服务仍主要按 TaskId 和 SortOrder 写入，跨进程 Semaphore 仍然只保护每次单独调用。

因此，若 Main 在 2 秒刷新前持有一个开放 Task 的旧 read model，而 Widget 已经为该 Task 记录结果，Main 仍有一个短暂窗口可以提交排序命令。它不会直接改写计划日期或结果字段，但不能称为跨进程的“已有结果排序绝对拒绝”。该残留与 IMP-P2-2 的 service 不变量和 P2-EXTRA-01 的复合排序原子性一致，应归入 P2.5，而不是把当前 UI 修复描述成强一致写入。

同样，OnDragOver 仍会在 Drop 前对同组/同日期不做预检查，跨边界时的视觉 Move 效果可能短暂误导；实际 Drop 经过 ViewModel 守卫后不会写入。这个问题是交互提示精度，不构成跨边界数据写入。

## 3. R3：刷新生命周期与剩余并发边界

目标 MainWindow 的代码证据完整覆盖以下生命周期：

- Loaded 后启动 2 秒 DispatcherTimer 并请求一次刷新；
- Activated 后请求刷新；
- 二次实例通过 ActivateFromExternalRequest 激活已有窗口并请求刷新；
- timer、事件订阅和生命周期 CancellationToken 在窗口关闭时清理；
- RefreshAsync 查询成功后才 ApplyReadModel，查询失败保持当前列表和选中项。

这足以解除此前“Main 没有激活/短周期刷新”的主要 R3 缺口，也解释了用户从另一宿主写入后 Main 最终能够收敛的代码路径。

但刷新串行化并不覆盖所有调用方。MainWindow 的布尔锁只包住 MainWindow.RefreshTodayAsync；TodayPageViewModel 内部写入方法在 application write 完成后自行调用无参 RefreshAsync，而该 VM 没有类似 Widget 的 SemaphoreSlim refresh gate。定时查询、激活查询和写入后查询可在 await 期间交错。一个先启动的旧查询可能在新写入后的查询之后 ApplyReadModel，造成短暂旧状态、选中态或分组回退，直到下一个 2 秒周期再次纠正。

因此 R3 判定为“主要修复已解除、严格全局串行化仍有 MINOR 风险”。这不是结果/计划数据被覆盖的证据，但如果 P2-00 的“刷新请求串行化”按字面验收，应在 P2 完成声明前处理或明确接受最终一致窗口。

## 4. R4：选中项失效与详情窗口

ApplyReadModel 先记录旧 SelectedTask 的 DomainTaskId，再构造新任务列表。若该 ID 不存在，则：

- SelectedTask 被清除；
- _selectionInvalidated 被置为 true，后续刷新不会偷偷回退到 PrimaryTask；
- InteractionMessage 提示“已选 Task 已不在 TODAY 列表中 · 详情已关闭，请重新选择”；
- SelectedTaskInvalidated 事件通知 MainWindow；
- 若改期面板正指向该 Task，也会一并取消。

MainWindow 收到事件后关闭现存 TodayDetailsWindow，并保留正常关闭后的激活/焦点恢复路径。刷新异常发生在查询阶段时不会进入 ApplyReadModel，因此原选择和详情不会因失败而被误判为消失。

这已经解决此前“overdue Task 完成后详情静默切到 PrimaryTask”的风险。目标 Slice 的自动化覆盖是 ViewModel 行为和 MainWindow 静态订阅检查；没有把真实桌面窗口操作写成已验证事实。

## 5. R5：Widget 失败反馈

4406e7d 将 Widget 刷新拆成准备下一份队列、成功后提交当前状态的路径。直接刷新遇到非致命异常时设置“当前列表保持不变”的反馈并继续向宿主抛出；宿主记录异常。写入后刷新失败由调用方分别设置“已写入/已记录，但 TODAY 刷新失败”，不伪造“已刷新”。

写入异常和刷新异常分开捕获，OperationCanceledException 继续传播而不变成普通写入失败；IsFatalException 会阻止 OOM、StackOverflow、AccessViolation、SEH 及其包装异常被通用反馈吞掉。Quick Add 在写入失败时保留输入，结果失败时保留队列和结果状态。XAML 的 TaskFeedback、QuickAddFeedback 都有 Polite LiveSetting。

目标测试树新增了 Quick Add 通用写入失败、结果通用写入失败、两类写入成功后刷新失败、直接刷新失败保持旧队列和取消传播测试。R5 的代码与单元覆盖相互吻合；剩下的是实际数据库/桌面故障展示，不在本窗口执行。

## 6. Parser 粘连修复回归

目标 1dc095f 的 TaskParser.cs 仍包含 TryGetGluedRelativeDateError，并在日期解析前和时间解析前各检查一次。它会把今天18:00、明天18:00、后天08:05 及其带标题形式拒绝为稳定的 task.parser.time.invalid；粘连 RANGE 形式拒绝为 task.parser.range.invalid。

目标测试树的 TaskParserTests 仍包含：

- RejectsGluedRelativeDateTimeBeforeTreatingInputAsTitle；
- 今天/明天/后天与点时间的粘连用例；
- 明天/后天与 ASCII hyphen 或 en dash RANGE 的粘连用例；
- 明天 23:00-01:00 跨午夜 RANGE 的开始日期归属用例。

4406e7d 和 1dc095f 的 hardening diff 没有修改 TaskParser.cs 或 TaskParserTests.cs，因此该修复在两个目标提交中保持有效。这里的结论来自提交树代码和测试内容；本窗口没有切换 worktree 重新执行目标提交测试。

## 7. 代码/自动化证据与人工证据分账

### 本窗口可直接确认的代码和静态证据

- 目标两个提交的父子关系、文件清单和差异已通过 git show/git diff 读取；
- 目标 hardening diff 的 git diff --check 通过；
- R1-R5 的对应实现、资源绑定、事件订阅和新增测试已逐项静态核对；
- 当前 worktree 未切换，未启动或控制 Main/Widget，未访问 D:\Anime\\.devdata\\reminnote.sqlite。

### 目标 Slice 中记录的自动化证据

P2-06 和 P2-07 Slice 均记录了 scripts/build.ps1 -Configuration Release：8 个项目、0 warning、0 error；scripts/test.ps1 -Configuration Release：ReminNote.Tests 180/180，0 error、0 failed、0 skipped、0 not run；并记录 P0-07 通过。P2-06 还记录了 git diff --check 通过。

这些是目标提交内的 Slice/交付记录。本窗口因为 worktree 停留在旧 HEAD 且明确不切换总成工作树，没有把旧 HEAD 的本地执行结果冒充为 1dc095f 的重新构建/测试结果。

### 用户人工证据边界

本次委托明确告知：用户已报告当前 P2 修复结果完成，但真实跨午夜 23:00 → 次日 00:30 → 01:00 的人工门禁仍缺失。本报告只记录这条用户报告，不据此推断用户执行过其他未报告的测试。

真实跨午夜门禁仍需在允许的人工环境中验证：23:00 开始的 RANGE 是否在次日 00:30 仍按开始日期归属、到 01:00 结束后才进入结果/NEEDS REVIEW 语义、Main/Widget 刷新和历史记录是否一致。代码中的固定 Instant、数据库结构或已有 23:00–01:00 静态/自动化用例都不能替代真实时钟时序。

因此，本报告不声称用户已执行或签字确认双宿主同库、重启、无效 repo-root、UIA 命中、拖动边界或其他未在本次委托明确报告的桌面测试。仓库中既有 P2-GATE-01 记录也明确把 Codex smoke 与用户签字分开。

## 8. P2 与 P2.5 判定

P2 代码整改：R1、R2、R4、R5 已解除；R3 的启动/激活/轮询/失败保留主路径已解除，但全局刷新并发串行化仍是 MINOR 风险。没有新的 BLOCKER/MAJOR。

P2 完成声明：仍不通过。原因是本次明确保留的真实跨午夜人工门禁尚未闭环；另外，若严格按 P2-00 要求全局串行化刷新，则 R3 的小并发边界也需要接受或修正。

P2.5 后置项，不在本次 P2 代码阻塞中重复升级：

- IMP-P2-2：排序的日期、分组、结果和版本上下文提升到 application/service 不变量；
- P2-EXTRA-01：拖动多行 sort_order 更新的批量原子性、失败回滚和一致性；
- Agent/IPC、Single Writer、WAL、Change Journal、revision 与跨进程变更通知。

## 最终裁决

目标 1dc095f 相对于此前 6f8e127 的 P2 交互整改是实质有效的，R1-R5 的主要代码缺口已经得到处理；R3 保留一个明确、可复现于代码结构的刷新竞态，属于 MINOR 而非 BLOCKER/MAJOR。Parser 粘连修复保持有效。自动化门禁按目标 Slice 记录为通过，但真实跨午夜人工门禁仍为 Pending，因此当前只能判定为“代码整改基本通过、P2 最终验收待人工门禁”，不能写成 P2 已完成。
