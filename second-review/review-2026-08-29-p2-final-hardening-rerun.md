# ReminNote P2 最新总成修复独立复核（Rerun）

- 复核日期：2026-08-29
- 目标总成 HEAD：`216f3e90c8f16270bf70e45eb396b1b78edae9bc`
- 本次新增修复：`0a3df93807ec0ee54ba35b7e5ad9f0203c79aec5`、`216f3e90c8f16270bf70e45eb396b1b78edae9bc`
- 复核方式：在独立 worktree 以 `git show`/`git diff` 读取目标提交、Slice、测试和既有报告；未切换总成工作树。

## 结论先行

本次未发现 Critical、BLOCKER 或 MAJOR 级代码问题。`216f3e9` 已把 Widget 外部刷新时的选中失效、结果后转到下一开放 Task、非致命异常与致命异常边界补齐；`0a3df93` 已把 Main Today 的异步刷新读模型提交收敛到同一个串行门，并加入代次丢弃和生命周期释放。

R1、R2、R4、R5 的目标交互主路径可判定为已解除。此前 R3 的核心风险——旧查询结果覆盖更新查询结果——也已解除；但 `RefreshFromCommandAsync` 在 `RefreshAsync` 返回/抛错后仍无条件写入成功或失败提示，未携带代次判断。因此 R3 是“核心已解除、反馈副作用仍有 MINOR 残留”，不能写成所有反馈均已严格代次收敛。

P2 完成声明仍为 **Pending/阻塞**，原因是用户明确报告的唯一未完成人工门禁：真实 `23:00 → 次日 00:30 → 01:00` 跨午夜时序。该人工门禁不能由固定 `Instant`、单元测试、数据库中存在 `23:00–01:00` 结构或 Codex 自动化替代。R3 的反馈残留不造成旧 read model 覆盖，也不是当前数据安全 BLOCKER；`IMP-P2-2` 和 `P2-EXTRA-01` 继续作为 P2.5 后置项。

## 证据边界

本窗口只读检查目标提交，没有 checkout 到总成 HEAD，没有启动或控制 Main/Widget，没有访问 `D:\Anime\.devdata`，没有修改产品源码、测试源码、项目配置或既有报告。以下自动化数字按用户/总成已报告事实记录，不冒充本窗口重新执行：Release 8/8、0 warning/0 error，`ReminNote.Tests` 191/191，P0-07 通过，以及 `git diff --check` 通过。

既有总成记录明确区分了自动化/Codex smoke 与用户人工验收。用户已报告：除上述真实跨午夜时序外，`P2-GATE-01` 其余人工测试已完成；本报告不补写用户未提供的点击顺序、路径、数据库行数、截图或具体宿主结果，也不声称本窗口做过桌面验收。

## R1–R5 与 R3 裁决

| 项目 | 静态代码/测试事实 | 裁决 | P2 影响 |
|---|---|---|---|
| R1：结果 Task 不误进入排序 | `TodayTaskViewModel.CanReorder` 要求 live、未完成且有真实 `DomainTaskId`；完成分组关闭排序；拖放和上下移在进入 application write 前都检查。`TodayPageViewModelTests` 保留完成项与边界拒绝覆盖。`TaskApplicationService.ReorderAsync` 仍只有 TaskId/非负 sort order 的服务契约。 | Main 正常 UI/VM 路径已解除；服务级不变量未补齐。 | 服务边界为 P2.5 `IMP-P2-2`，不阻塞当前 P2 代码闭环。 |
| R2：仅专用手柄拖动 | XAML 使用独立 Button 手柄、`IsDragHandle=True`、`CanReorder` 可见性和 Automation Name/HelpText；`TodayTaskDragDropBehavior` 只有命中手柄才建立拖动状态，DragOver/Drop 仍检查可排序条件。 | 已解除（代码/静态测试证据）。 | 未做真实鼠标/UIA 桌面测试，不能把静态证据写成运行验收。 |
| R3：Main 刷新生命周期与并发收敛 | `TodayPageViewModel.RefreshAsync` 统一经过 `SemaphoreSlim`；请求在等待前递增代次，旧查询在 Apply 前丢弃，旧异常不更新自身刷新错误提示；请求取消与 Today 生命周期 token 链接。`Dispose` 标记拒绝新请求、取消在途请求，并在 active refresh 全部退出后释放 CTS/门。Main Loaded、Activated、二次实例激活和 2 秒 timer 均调用同一 VM 刷新；写入后的 Quick Add、改期、结果、继续和排序路径也均调用该入口。 | 旧读模型覆盖新读模型的核心风险已解除；保留反馈代次残留。 | 残留为 MINOR：`RefreshFromCommandAsync` 的外层 `catch` 无条件写 `LiveRefreshFailedMessage`，成功返回后也可能无条件写 `LiveRefreshedMessage`；它可能让旧命令覆盖较新请求的反馈，但不覆盖已应用的读模型。建议后续让命令入口携带/比较刷新结果代次。 |
| R4：选中 Task 消失不静默换项 | Main 仍按 TaskId 恢复选择，找不到时清空选择、提示并触发 `SelectedTaskInvalidated`，MainWindow 关闭详情窗口。Widget 的 `External` 刷新按选中 TaskId 查找；若选中项完成/移出 Today，则清空选中态、保留主显示但显示“请重新选择”语义，不回退到 primary。`OwnResultRecorded` 是唯一明确允许转到下一开放 Task 的原因。新增 `LiveExternalRefreshClearsMissingSelectionWithoutFallingBackToPrimary` 覆盖该路径。 | 已解除。 | 空队列时 Widget 隐藏结果操作；自身结果后无开放项时反馈“当前没有下一开放 Task”，没有伪造转移。Widget 在无开放项但存在已完成项时 primary read model 仍可显示该完成项，这是既有 completed fallback 展示，不是“已转到下一项”成功提示。 |
| R5：Widget 写入/刷新失败与异常边界 | Widget 写入失败保留旧列表并分别反馈；写入成功但刷新失败明确说明“已写入/已记录、刷新失败”，不显示假成功刷新；取消继续传播。`WidgetViewModel.IsFatalException` 识别 OOM、StackOverflow、AccessViolation、SEH 及包装异常。 | ViewModel 主路径已解除。 | 真实存储故障注入和桌面反馈未在本窗口执行。 |

### R3 代次与生命周期的补充判断

`0a3df93` 的并发测试用阻塞查询证明同一时刻最多一个 query，先完成的旧快照不会落入最终列表；失败后、排队取消后和 Dispose 中途均有回归测试。MainWindow 关闭时停止 timer、解绑 Tick、取消生命周期 token，再调用 `TodayPage.Dispose()`；VM 的 active-refresh 计数避免在在途请求结束前释放门。

这证明“写入后刷新”和“宿主触发刷新”在 VM 层共享同一门。MainWindow 自身的 `_todayRefreshInProgress` 仍只是宿主调用去重，不是唯一一致性来源；这没有问题，因为写入路径不依赖该 bool，而统一进入 VM 门。live 构造时的 `LoadInitialReadModel()` 是窗口创建前的同步初始加载，技术上不经过异步门，但在窗口/命令/timer 可用前执行，不构成并发刷新入口。

残余仅在命令包装器的提示副作用：`RefreshAsync` 本身已经用代次保护 `ApplyReadModel` 和其内部失败提示，但 `RefreshFromCommandAsync` 的外层成功/失败处理没有同样的代次结果。这是当前最具体的 R3 复核保留项。

## Widget 外部刷新、自身结果与空状态

- 普通 `RefreshAsync` 使用 `WidgetRefreshReason.External`，优先按旧选中 TaskId 恢复；旧选中 Task 不在新的开放队列时，`SetSelectedTask(null, ..., true)` 清掉 `_currentTaskId`、结果动作和选中态，并设置明确反馈。
- 自身 DONE/PARTIAL/MISSED 的写入刷新使用 `OwnResultRecorded`，只在该明确原因下选取 `nextItems.FirstOrDefault()`；因此“自身结果后转下一开放 Task”与“另一进程导致当前选中项消失”没有混用。
- 无开放 Task 时队列为空、结果按钮不可见；外部失效态显示“未选择 Task”和不可用元数据；自身结果后无下一项显示“当前没有下一开放 Task”。没有把外部刷新丢失静默包装成新选中项，也没有把刷新失败包装成写入成功。
- `WidgetInteractionTests` 新增了外部选中项消失测试，并强化了自身 DONE 后下一开放项和无下一项反馈断言。没有新增 Widget App 宿主 fatal 过滤的运行测试，该部分以共享过滤函数和静态代码证据为主。

## Widget App 宿主致命异常语义

`App.OnStartup` 和其异步 `RefreshAsync` 均使用 `catch (Exception) when (!WidgetViewModel.IsFatalException(exception))`；生命周期取消单独处理，非生命周期 `OperationCanceledException` 继续抛出。ViewModel 的刷新、写入后刷新也用同一 `IsFatalException` 过滤。因此 OOM、StackOverflow、AccessViolation、SEH 以及 Aggregate/InnerException 中包含的致命异常不会被宿主的普通错误反馈分支吞掉，和 ViewModel 语义一致。

该判断是代码证据，不是本窗口注入致命异常或启动 GUI 的结果。另有一个低概率生命周期注意点：Widget `Dispose()` 会立即 Dispose `_refreshGate`，若关闭正好与在途刷新交错，finally 中的 Release 可能产生非致命 `ObjectDisposedException`；这属于宿主关闭竞态的后续 hardening，不改变正常路径的 fatal 过滤裁决，也没有在本次提交中被测试覆盖。

## Parser 粘连修复复核

目标树仍包含 Parser 修复：`TaskParser` 在相对日期消费前、时间消费前和标题回退边界识别 `今天/明天/后天` 后紧跟数字、冒号、ASCII 连字符或 en dash 的粘连形状，并返回稳定的 `task.parser.time.invalid` 或 `task.parser.range.invalid`；失败值为 null，不进入 Main/Widget 的 `CreateAsync`。`TaskParserTests` 保留 `今天18:00`、`明天18:00`、`后天08:05`、带标题粘连范围，以及合法空格跨午夜 RANGE 的覆盖。

`0a3df93` 与 `216f3e9` 没有改写 Parser 文件，故本次结论是目标树静态继承有效；本窗口没有重新运行 Parser 类测试，也没有在桌面输入粘连语法。用户报告的最终总成测试 191/191 可作为总成自动化事实，但不能拆分成“本窗口重跑的 Parser 结果”。

## P0 Mock 与 live 正式入口

Main/Widget 的无参构造仍是 `_isLive == false` 的 P0 内存 Mock 兼容路径；其 `RefreshAsync` 不访问 live query/application service，Mock 结果/Quick Add 不写 SQLite。正式 Main 由 `--repo-root` 校验后构造 `TaskWorkspace`、live application/query service 和 Main VM；正式 Widget 要求 `--repo-root`，由 `TaskWorkspace` 构造 live Widget VM。未见把无参 Mock 作为正式入口，或把 Widget 的 P0 Anime Mock 误当成 TODAY live 数据。

## 测试覆盖与不足

目标提交新增/强化的关键覆盖包括：

- Main：并发刷新同门串行、最新 read model 胜出、失败后门可复用、排队取消释放、Dispose 取消并拒绝后续刷新，以及生命周期静态挂接点；
- Widget：外部选中 Task 消失后不回退、自身 DONE 后转下一开放 Task、无下一项反馈，以及既有写入失败/刷新失败/取消和旧队列保留；
- Parser：目标树已有粘连日期/时间和范围回归；
- P0：无参 Mock 与 live 正式入口的构造边界仍由既有测试/静态入口约束覆盖。

仍没有自动化覆盖：WPF 实际拖动命中/Drop、真实 UIA SelectionItem/LiveSetting 行为、Widget App 宿主 fatal 过滤的进程级测试、跨进程陈旧 read model 对排序命令的服务级拒绝、一次多项排序写入的事务回滚，以及真实跨午夜时钟节点。

## P2 与 P2.5 最终状态

### P2 代码裁决

- R1：Main UI/VM 入口解除；服务上下文验证仍后置。
- R2：专用手柄及可达性/静态 Automation 约束解除；运行时 UIA 仍未由本窗口证明。
- R3：旧读模型覆盖风险解除；命令反馈代次残留为 MINOR 后续修复项。
- R4：Main 和 Widget 的外部选中失效语义解除；自身结果转移路径有明确原因隔离。
- R5：Widget 写入/刷新失败反馈和宿主 fatal 过滤主路径解除；宿主级行为主要为静态证据。
- 未发现新的 P2 代码阻塞项；但 R3 反馈残留不应被文字夸大为“所有反馈已严格收敛”。

### P2.5 后置项

1. `IMP-P2-2`：把排序的计划日期、Today 分组、邻接项或版本/条件上下文提升到 command/application service 不变量。当前 Main UI 守卫有效，但旧 read model/未来第二个调用者仍可直接提交只有 TaskId+SortOrder 的命令。
2. `P2-EXTRA-01`：把一次拖动/交换的多项排序写入改为服务级批量命令或单事务，并补交错写入、失败回滚测试。
3. Agent、Single Writer、业务 IPC、WAL、Change Journal、全局 revision 等架构迁移属于 P2.5 范围，不应倒灌成当前 P2 已实现能力。

### 人工门禁

用户已报告的非午夜人工验收可以作为“用户已报告”记录，但本窗口不代替核对具体操作证据。真实 `23:00 → 次日 00:30 → 01:00` 仍明确标记 **Pending**；因此 P2-GATE-01 未闭环，P2 完成声明仍阻塞。后续只能由用户完成该真实时序，再由总成窗口核对路径、时间、Task 状态/历史和数据库证据。

## 最终裁决

`216f3e9` 相对此前 `1dc095f` 的 Widget 选中失效、自身结果转移和致命异常宿主边界修复有效；`0a3df93` 相对此前刷新竞态实现了 VM 层全入口串行化、代次丢弃和可延迟释放。R1/R2/R4/R5 已解除，R3 核心已解除但保留命令反馈 MINOR。自动化门禁按用户报告为 Release 8/8、0 warning/error、测试 191/191、P0-07 和 diff check 通过；本窗口不把它们冒充为本次重跑。

P2 仍不能宣告最终完成：真实跨午夜人工门禁 Pending。除此之外，当前代码没有新的 P2 BLOCKER/MAJOR；排序服务上下文、复合排序原子性和后续单写者架构按 P2.5 后置处理。
