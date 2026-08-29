# ReminNote P2 Main / Widget 任务选择与拖动交互独立复核（二审）

复核日期：2026-08-29
复核基线：ReminNote 主总成 HEAD 6f8e127ada7dcb843018a5d5ef7f874e0b0f4ee4
复核范围：P2-00、P2-03、P2-04、P2-05 Slice，P2 阶段报告与既有终审报告，以及当前 Main / Widget 实现、测试和门禁脚本。

## 结论摘要

6f8e127 的 Release 构建、自动化测试和 P0-07 主总成门禁均可复现通过；正常的 live Task 选择路径也已经从“默认首项”改为按真实 Task 身份传递。Widget 选择非首项后记录 PARTIAL 或 DONE 的两项新测试证明，结果不会直接落到首项。

但是，本次交互复核不建议将该 HEAD 无条件写成 P2 交互闭环完成。当前没有发现 BLOCKER 或 MAJOR 级别的数据破坏问题，但发现数项处于 P2 范围内的 MINOR 风险，其中结果 Task 仍可被拖动、拖动从整张任务卡而非手柄启动、Main 缺少激活/短周期刷新，以及选中 Task 消失后详情窗口可能静默切换到另一个 Task，均会影响本次 P2 交互验收。按主计划的严重度规则，这些应在 P2 交互完成声明前处理，或由产品明确记录范围例外。

独立复核结论：自动化门禁通过；交互验收有条件通过；P2 总体完成声明仍阻塞。P2.5 的服务级排序上下文和复合排序原子性继续作为后置项，不把它们误报成当前 P2 的 BLOCKER。

## 风险清单：事实、等级与 P2 判定

| 编号 | 事实证据 | 风险等级 | P2 是否阻塞 | 判定与归属 |
|---|---|---|---|---|
| R1 | TodayResources.xaml:569 将拖放行为附加到所有任务分组；COMPLETED 分组可以展开。TodayPageViewModel.ReorderTaskByDropAsync 和 MoveTaskWithinGroupAsync 没有 IsCompleted 守卫；TaskApplicationService.ReorderAsync 只按 TaskId 和非负 SortOrder 写入，Task.SetSortOrder 也不检查已有结果。 | MINOR | 是 | 已有结果的 Task 仍有可达的排序写入口，违反结果保护的交互意图。应在 UI 和当前 P2 的应用入口共同拒绝，或明确改变契约。 |
| R2 | TodayTaskDragDropBehavior.OnPreviewMouseLeftButtonDown 从任意 OriginalSource 找到任务并保存拖动状态；移动超过阈值即 DoDragDrop。XAML 中的“拖动手柄”只是带 Name/HelpText 的 TextBlock，没有独立输入命中或 UIA 拖动动作。 | MINOR | 是 | 点击完成、选择、置顶或卡片其他区域后移动鼠标可能意外排序；UIA 也不能操作这个静态手柄。 |
| R3 | Main App 的单实例激活回调只调用 ActivateMainWindow；未见激活后 RefreshAsync 或 Main 短周期轮询。Widget App 有 2 秒轮询。P2-00 明确要求启动、激活、写入后和短周期轮询刷新 Today Read Model。 | MINOR | 是（严格按 P2-00） | Main 可能长期显示 Widget 已完成/改期后的旧状态，形成跨进程 stale UI；若产品只接受手动刷新，必须在验收范围中明确例外。 |
| R4 | TodayPageViewModel.ApplyReadModel 按 ID 保留选中项，找不到时回退到 PrimaryTask。完成非当前工作日的 overdue Task 后，查询会把该 Task 排除；TodayDetailsWindow 仍保持打开并绑定 page.SelectedTask，因此详情内容可能静默变成另一个 Task，或在没有候选时变空。 | MINOR | 是 | 选中项消失的刷新语义不安全地“继续操作”，用户可能把后续 DONE/结果理解为仍作用于原 Task。应关闭/提示详情窗口或提供明确的选中失效状态。 |
| R5 | Widget 的 QuickAdd 和 live 结果写入只过滤 DomainValidationException、TaskWriteGateBusyException；通用写入异常或“写入成功但 RefreshAsync 失败”会从 ViewModel 冒出，没有统一失败反馈。Main 对相同路径有通用异常提示。 | MINOR | 是（失败路径验收） | Widget 的旧列表大体会保留，但用户不能可靠知道写入或刷新是否成功；尤其不能把异常路径当作已回滚。 |
| R6 | Main TodayResources 中未见针对 PrimaryTask 为空的明确 empty-state 模板；Widget 有暂无任务文字，但 Complete/Snooze/Reschedule 按钮仍可见，空选择时依赖命令内部 no-op/提示。Main/Widget 选中态主要是视觉 DataTrigger，未暴露 SelectionItem 语义；Widget TaskFeedback 未设置 LiveSetting。 | MINOR / STYLE-NIT | 否（不构成数据安全阻塞） | 空列表和辅助功能语义不完整。静态 AutomationProperties.Name/HelpText 大体存在，但不能据此宣称 UIA 已验证。 |
| R7 | ReorderTaskByDropAsync 对同组同日期的每个 Task 分别调用 ReorderAsync；每次调用各自有事务和跨进程 Semaphore，失败后没有批量回滚，也没有 expected group/date/version 上下文。 | STYLE-NIT（既有 P2-EXTRA-01） | 否（按当前 Slice 约定） | 仍是 P2.5 的复合排序原子性/回滚后置项；当前 P2 只应保留并标记，不应冒充已解决。 |

## 1. Main / Widget 任务身份与结果写入

### 已确认通过的部分

Main live 的任务行、选择按钮和详情动作均捕获具体的 TodayTaskViewModel；结果路径继续使用该对象的 DomainTaskId。刷新时 TodayPageViewModel 以 SelectedTask 的 DomainTaskId 查找新 read model，而不是按集合首项恢复。普通任务行的 DONE、PARTIAL、MISSED 和继续执行路径因此具备真实 Task 身份。

Widget live 在 RefreshAsync 中按 TaskId 恢复队列选择；SelectTask 将 _liveSelectedTask、_selectedTaskItem 和 _currentTaskId 同步到同一条队列项；RecordLiveResultAsync 以 _currentTaskId 和 _liveSelectedTask.Task.Title 写入。当前新增测试 LiveWidgetCanSelectANonPrimaryTaskBeforeRecordingItsResult 和 LiveWidgetCompleteActsOnTheSelectedNonPrimaryTask 已覆盖非首项 PARTIAL/DONE，证明这两条路径不会直接写入首项。

P0 Mock 无参构造路径仍使用 Mock 数据和本地状态；无参 RefreshAsync 不访问 live application/query service。正式 Widget 入口要求显式 --repo-root 并构造 live workspace；正式 Main 入口构造 live workspace、application service 和 query service。未发现 Mock 无参兼容模式与 live 正式入口混用，也未发现 P0 Mock 结果写入 SQLite 的路径。

### 需要保留的边界说明

Main 顶部的 Primary Focus DONE 绑定的是 PrimaryTask，不是 SelectedTask。这是明确的“主焦点”语义，不能拿它作为“任意选中项操作”的证明；如果产品要求顶部 DONE 也始终操作当前选中项，则它仍是契约不一致。

Main 详情窗口的问题不在普通行回调的 TaskId 错绑，而在选中 Task 被查询结果移除后的回退策略：页面把 SelectedTask 回退到 PrimaryTask，窗口没有关闭或提示。这个状态转换使用户在原任务完成后继续操作时，视觉上下文可能已经变成另一个 Task，应作为本次交互闭环问题处理。

## 2. 拖动排序、边界与跨进程风险

### 已确认通过的边界

ReorderTaskByDropAsync 对 source/target 做非空、TaskId、PlanDate 检查，并要求 CurrentGroup 相同且 PlanDate 相同；同一分组日期的目标位置计算不会主动跨到其他日期或 Today 分组。既有的 MoveTaskWithinGroupAsync 也有相邻项、日期和分组边界检查。跨日期、跨分组的最终应用写入在正常 ViewModel 调用中会被拒绝。

TaskApplicationService、TaskWorkspace 和两端的 live 构造路径都遵守当前 P2 的本地 application/query service 边界，没有把 ViewModel 直接连到 DbContext、SQL 或 Agent/IPC。命名为 Local\\ReminNote.P2.TaskWrite 的 Semaphore 能串行化每一次应用写入，单次 repository 更新也有事务。

### 当前失败点

上述边界是 ViewModel/UI 层约束，不是应用服务的完整不变量。ReorderTaskCommand 只有 TaskId 和 SortOrder；应用服务不重新检查 Task 的计划日期、Today 分组、已有结果、邻接关系或版本。Main 和 Widget 通过同一个本地数据库工作时，若 Main read model 已过期、Widget 先完成 Task，Main 仍可把该 Task 的排序命令提交给服务。Semaphore 能避免同时写入，但不能把过期 UI 上下文变成有效性验证。

此外，COMPLETED 分组仍安装了拖放行为，结果 Task 没有被隐藏排序入口保护。即使其 sort_order 改动不影响计划时间和结果本身，也已经绕过“已有结果 Task 不提供排序操作”的现有验收意图。

一次拖动会对多个 Task 发起独立写入。若中途返回 null、闸门超时或出现其他异常，前面的 sort_order 可能已经落库，当前 UI 却仍保留旧顺序，因为失败分支没有 RefreshAsync 或回滚。该点与既有 P2-EXTRA-01 一致，当前不提升为 P2 BLOCKER，但不能称为复合操作具备完整失败回滚。

## 3. 刷新、空列表、选中项消失、失败反馈和 UI Automation

### 刷新与选择

Widget 的刷新顺序是先查询、成功后再更新队列；它按旧 TaskId 恢复选中项，选中项消失时回退到新的 primary，队列为空时显示暂无 TODAY Task 文案，不会用旧队列对象继续写结果。这个部分是当前实现中较完整的路径。

Main 也在查询成功后再 ApplyReadModel，查询失败时原列表通常保留；写入后自己的路径会刷新。缺口是没有与 Widget 对称的激活刷新和短周期轮询，且详情窗口没有处理 SelectedTask 从存在到消失的生命周期。

### 空列表

Widget 的标题、元数据和时间有 live 空值文案；但操作按钮没有统一的“无当前 Task”可执行性/可见性状态。Main PrimaryTask 为空时，主卡片主要依赖空绑定值和 null command，缺少清晰的空状态说明与动作收敛。该问题不会把结果写到首项，但会降低空列表验收的可解释性。

### 失败反馈

单次 Task 写入通过 application service 和 repository 事务保护，Main 的写入/刷新失败路径有保留旧 read model 和提示。排序复合写入没有事务级回滚。Widget 的通用异常过滤不完整，不能把其当前行为描述为“失败后完整回滚”；至少应区分“写入失败”和“写入成功但刷新失败”。

### UI Automation

Main 的完成、选择、置顶、分组展开和详情关闭控件有 Name/HelpText，Main 交互消息区域设置了 Polite LiveSetting；Widget 的队列选择按钮和操作按钮有 AutomationProperties.Name，选择动作也能进入实际 ViewModel。

不过，任务选中态只有视觉样式，没有可供 UIA 查询的 SelectionItem/IsSelected 语义；Main 的拖动手柄是静态 TextBlock，不能被 UIA 作为拖动控件操作；Widget 的 TaskFeedback 没有 LiveSetting。P0-07 的资源键/静态标记检查不能替代真实 UIA 行为验证，本次也未启动或控制桌面窗口。

## 4. 测试覆盖与门禁复现

当前新增测试覆盖了：

- Main ViewModel 直接调用拖动排序后，同一组内的目标顺序和 sort_order；
- Main 无参 Mock 任务选择触发详情请求；
- Widget live 选择非首项后记录 PARTIAL；
- Widget live 选择非首项后完成；
- 既有的同日期/同分组上下移动边界、结果记录、改期、继续执行、刷新和重启路径。

仍未覆盖：

- WPF TodayTaskDragDropBehavior 的实际鼠标命中、拖动启动区域、Drop 目标和 Completed 分组；
- Main 拖动跨日期/跨分组拒绝；
- 结果 Task 拖动必须拒绝；
- Main/Widget 选中项在刷新中消失、空列表、详情窗口关闭或提示；
- Widget 通用写入异常、刷新异常和“多次排序写入中途失败”；
- UIA 的选中态、LiveSetting 和拖动手柄行为。

测试项目没有链接 WPF 的 TodayTaskDragDropBehavior 或 XAML，因此新增的 Main 拖动测试是 ViewModel 单元覆盖，不是 UI 交互覆盖。

本轮实际复现结果：

| 验证项 | 结果 |
|---|---|
| scripts/test.ps1 -Configuration Release | 通过；锁定还原、测试项目构建通过；ReminNote.Tests 174/174，Errors 0、Failed 0、Skipped 0、Not Run 0 |
| scripts/build.ps1 -Configuration Release | 通过；8 个项目构建，0 warnings、0 errors |
| P0-07 主总成门禁 | 已在该 HEAD 的完整 scripts/test.ps1 结果中通过；记录为 213 resource keys、7 locked projects、1 test project，并完成 Shell/TODAY/ANIME/Widget 标记检查 |
| git diff --check（报告新增前） | 通过 |
| 用户桌面 Main/Widget | 本轮未启动、未控制 |
| D:\\Anime\\.devdata | 本轮未以其为目标，未写入 |

## 5. P2 阻塞项与 P2.5 后置项的区分

### 本次应视为 P2 交互阻塞的项

1. R1：结果/已完成 Task 仍能从可展开的 COMPLETED 分组进入拖动排序。
2. R2：拖动从整个任务卡启动，静态“拖动手柄”既不能约束鼠标，也不能满足 UIA 操作。
3. R3：按 P2-00 冻结契约，Main 缺少激活和短周期刷新；如果产品决定只接受手动刷新，需在验收记录中明确范围例外。
4. R4：选中 Task 被刷新移除后，详情窗口可能静默操作回退 Task。
5. R5：Widget 通用写入/刷新异常没有稳定的失败反馈。
6. 既有 P2-GATE-01：真实 Main/Widget 的用户桌面验收和 SQLite/重启/跨日等手工证据仍未由本轮提供；本轮遵守“不启动/控制用户桌面”的限制，不能把自动化通过当成用户 sign-off。

这些项目目前都没有证据显示会直接破坏结果数据或计划时间，因此等级没有上升到 BLOCKER/MAJOR；但它们属于 P2 交互语义和失败闭环，不宜在未处理或未明确豁免前宣布 P2 完成。

### 明确保留为 P2.5 后置项的项

- IMP-P2-2：把排序的日期/分组/版本等有效性上下文提升到 service/application 不变量，而不是只依赖 UI；
- P2-EXTRA-01：将一次拖动的多条 sort_order 更新变为批量原子操作，提供失败回滚或等效一致性保证；
- Agent/IPC/Single Writer/WAL/change journal/revision 等跨进程正式架构，不属于 P2-00/P2-04 的当前本地 Task loop 范围。

这些后置项不应被拿来否定已通过的 P2 本地单次写入边界，但当前代码也不应声称已经提供复合排序的跨进程强一致和完整回滚。

## 6. 最终判定

事实层面：6f8e127 的自动化门禁可重复通过；Main/Widget live 的普通 Task 身份传递已明显改善；P0 Mock 与 live 正式入口没有混用。

风险层面：结果 Task 拖动、整卡拖动、Main 刷新收敛、选中项消失后的详情生命周期和 Widget 通用失败反馈仍有明确实现缺口；测试没有覆盖这些关键边界。

最终判定：P2 Main/Widget 交互二审为“有条件通过自动化、阻塞 P2 完成声明”。无 BLOCKER/MAJOR；R1-R5 和既有 P2-GATE-01 在当前证据下仍阻塞交互闭环确认。P2.5 的排序 service 不变量与批量原子性按后置项处理。

本报告为本次唯一新增文件；复核过程中未修改产品源码、测试源码、项目配置或 second-review 下既有文件。
