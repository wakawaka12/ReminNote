# ReminNote P0 全阶段二审裁决 — 2026-08-28

- 审查日期：2026-08-28
- 审查范围：`codex/p0-integration` 当前集成结果，覆盖 P0-02 至 P0-07，重点复核 P0-03 Design System、P0-04 TODAY、P0-05 ANIME、P0-06 Widget、P0-07 稳定化，以及一审报告列出的全部发现和结论。
- 一审源文件：`D:\Anime\reviews\review-2026-08-27-p0.md`
- 被审代码对应的集成提交：`90d488660ed598706b653eddd1cb074d56d1ecb5`
- 当前分支：`codex/p0-integration`
- 权威依据：`D:\Anime\ReminNote_MASTER_DEVELOPMENT_PLAN.md`、`D:\Anime\PRODUCT_RULES.md`、`D:\Anime\ARCHITECTURE.md`、`D:\Anime\DEVELOPMENT.md`、`D:\Anime\TESTING.md`、`D:\Anime\DECISIONS.md`、`D:\Anime\docs\PARALLEL_DEVELOPMENT.md` 及 P0-02 至 P0-07 Slice 文档。

## 二审总裁决

**部分接受一审结论；当前不能宣告 P0 完成。**

一审指出的 `IMP-1`、`IMP-2` 均可由当前代码直接确认，且分别违反 Widget 任务操作反馈和 Reminder Drawer 手动验收语义。二审将其校正为 **MINOR**：它们不是数据、安全或架构阻断，修复范围很小，但必须在 P0 完成声明前修复并重新执行对应手测。

此外，一审的 `NIT-2` 证据描述有一处事实错误：Today 输入框虽然存在焦点触发器，但使用的是固定 `AccentBrush`，不是可随系统高对比度变化的 `AccessibleFocusBrush`。在 Windows 高对比度和 Tab 焦点手测完成前，P0-07 的无障碍验收仍未闭环。

当前没有发现 BLOCKER、MAJOR、数据丢失、真实数据接入或架构越界问题。主计划仍要求用户亲自验证窗口视觉层级和主要交互（`D:\Anime\ReminNote_MASTER_DEVELOPMENT_PLAN.md:2947-2951`），本二审未把静态门禁或一审自述的启动冒烟当作该人工验收的替代品。

### 处理分级

- **必须修复/闭环**：`IMP-1`、`IMP-2`；并完成 P0-07 Windows Tab/高对比度人工验收，按验收结果处理 `NIT-2`。
- **可选改进**：资源门禁反向检查、UIA 门禁精度、`NIT-1`、`NIT-3`、`NIT-4`、`NIT-5`、Typography 重复 setter 整理。
- **不计为当前缺项**：`clean.ps1` 链接/重解析点防护，P0-07 brief 已明确将其列为后续安全加固。

## 复核证据边界

- 本二审读取了当前 HEAD、源代码、XAML、项目文件、资源、脚本、CI 和活文档；只读运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-p0-07.ps1`，结果为 **213 resource keys、6 locked projects，门禁通过**。
- 独立统计 `UiText.cs` 常量键与 `UiText.resx` 数据项均为 213，且当前没有 cs-only 或 resx-only 键。
- 只读 `git diff --check` 通过；当前工作树可见状态只有用户已有的 `reviews/`、`second-review/` 未跟踪目录。本次没有执行任何 Git 写操作，也没有修改源代码、计划、活文档或一审报告。
- 为遵守本窗口只读验证边界，本二审没有再次执行 Release build、`test.ps1` 或启动 WPF 窗口；因此一审报告中 build/test/startup smoke 的“已执行”记录只能作为一审自述，不能作为本窗口独立复现的证据。当前没有测试项目，`dotnet test` 通过也不等于业务测试覆盖，仓库文档已明确这一限制。

## 逐条裁决

### IMP-1 — Widget 任务状态标签不随完成/延后/改期更新

- **一审主张**：`CompleteTask`、`SnoozeTask`、`RescheduleTask` 修改 `_isTaskCompleted`，但没有通知 `TaskStatusLabel`，因此右上角会继续显示初始 `ANYTIME`；一审建议在三个方法中补 `OnPropertyChanged(nameof(TaskStatusLabel))`。
- **证据（文件与精确行号）**：
  - 一审定位与现象：`D:\Anime\reviews\review-2026-08-27-p0.md:20-33`。
  - `TaskStatusLabel` 是根据私有字段派生的只读属性：`D:\Anime\src\windows\ReminNote.Widget\ViewModels\WidgetViewModel.cs:150-152`。
  - 三个命令直接写字段并只更新 `TaskFeedback`，没有状态通知：`D:\Anime\src\windows\ReminNote.Widget\ViewModels\WidgetViewModel.cs:295-311`。
  - XAML 右上角绑定该属性：`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml:131-138`。
  - P0-06 手测明确要求完成后显示 `COMPLETED`、延后/改期显示相应反馈：`D:\Anime\docs\slices\P0-06-manual-test.md:104-119`。
- **二审裁决**：**接受**。
- **校正后的严重级别**：**MINOR**。
- **裁决理由**：字段值确实变了，但 `ObservableObject` 不会因为私有字段直接写入而重新计算绑定；`TaskFeedback` 的通知不能替代 `TaskStatusLabel` 的通知。该问题是确定的用户可见行为缺陷，但不涉及持久化、数据安全或架构边界。
- **建议的最小处理方案**：在三个方法写入 `_isTaskCompleted` 后通知 `TaskStatusLabel`；或将完成状态改为带通知的属性。完成后重新执行“完成→延后→改期”手测，并逐项检查右上角标签。
- **受影响文件**：实现修复至少涉及 `D:\Anime\src\windows\ReminNote.Widget\ViewModels\WidgetViewModel.cs`；`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml` 是现有绑定点，无需为最小修复改动。
- **处理分类**：必须修复。
- **是否阻断当前阶段**：**是**，阻断 P0 完成声明；不构成编译、数据或架构阻断。

### IMP-2 — 标记提醒已读会打开 Quick Add

- **一审主张**：`MarkReminderRead` 关闭抽屉后把反馈写入 `QuickAddFeedback`，并设置 `IsQuickAddOpen = true`，导致“标记已读”突然打开 Quick Add 面板。
- **证据（文件与精确行号）**：
  - 一审定位、现象和手测预期：`D:\Anime\reviews\review-2026-08-27-p0.md:35-45`。
  - 当前实现明确设置了 Quick Add 反馈和打开状态：`D:\Anime\src\windows\ReminNote.Widget\ViewModels\WidgetViewModel.cs:338-343`。
  - Quick Add 面板只由 `IsQuickAddOpen` 控制，且反馈文本只在该面板中显示：`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml:417-448`。
  - Reminder Drawer 有独立可见区域和独立标记按钮：`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml:449-518`。
  - P0-06 手测要求标记操作关闭抽屉并显示 Mock 反馈，不写入 `ReminderInstance`：`D:\Anime\docs\slices\P0-06-manual-test.md:156-173`；该 Slice 将 Quick Add 与 Reminder Drawer 作为两个独立交互：`D:\Anime\docs\slices\P0-06-implementation-brief.md:14-17、47-56`。
- **二审裁决**：**接受**。
- **校正后的严重级别**：**MINOR**。
- **裁决理由**：实现把提醒反馈绑定到了不相关的 Quick Add UI 状态。抽屉关闭后，用户会看到一个未主动请求的输入面板；这不代表真实数据写入，但违反了当前 Mock 的交互语义和手测预期。
- **建议的最小处理方案**：保留关闭抽屉；新增独立 `ReminderFeedback`/`HasReminderFeedback` 并放到当前 Widget 的公共反馈位置，或使用在两个页面都可见的页面级反馈；不要设置 `IsQuickAddOpen`，也不要复用 Quick Add 专属反馈字段。
- **受影响文件**：实现修复至少涉及 `D:\Anime\src\windows\ReminNote.Widget\ViewModels\WidgetViewModel.cs` 和 `D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml`。
- **处理分类**：必须修复。
- **是否阻断当前阶段**：**是**，阻断 P0 完成声明；不构成数据或架构阻断。

### FUNC-01 — P0-04 TODAY 功能正确性

- **一审主张**：分组、完成/恢复、置顶、折叠、Quick Add、Needs Review 和 MOCK 标识通过；完成/置顶后的派生属性通知齐全。
- **证据（文件与精确行号）**：
  - 一审功能表：`D:\Anime\reviews\review-2026-08-27-p0.md:49-54`。
  - P0-04 acceptance 明确要求这些交互：`D:\Anime\docs\slices\P0-04-implementation-brief.md:63-73`。
  - Quick Add、Needs Review、完成/恢复、置顶及汇总刷新：`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayPageViewModel.cs:133-242`。
  - 分组重建与任务派生属性通知：`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayPageViewModel.cs:226-242`、`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayPageViewModel.cs:376-465`。
- **二审裁决**：**接受**。
- **校正后的严重级别**：无（通过）。
- **裁决理由**：当前逻辑在完成状态变化后重建分组并通知 `CurrentGroup`、状态、Needs Review、完成按钮和详情相关属性；Quick Add 空值分支和内存边界也与 brief 一致。没有发现一审所述之外的 P0-04 缺陷。
- **建议的最小处理方案**：无需代码处理；按 P0-04/P0-07 手动步骤保留人工验证。
- **受影响文件**：核验对象为 `D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayPageViewModel.cs`、`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayResources.xaml`。
- **处理分类**：无需处理；人工验收为必需流程。
- **是否阻断当前阶段**：否（但仍受总体验收规则约束）。

### FUNC-02 — P0-05 ANIME 功能正确性

- **一审主张**：栏目筛选、搜索、详情、已看、待看、追番、模拟提醒和计数刷新通过；筛选后 `SelectedEntry` 保持在匹配集内。
- **证据（文件与精确行号）**：
  - 一审功能表：`D:\Anime\reviews\review-2026-08-27-p0.md:55-56`。
  - P0-05 acceptance 与本地 Mock 边界：`D:\Anime\docs\slices\P0-05-implementation-brief.md:40-48`。
  - 搜索即时刷新、各项动作、计数和筛选后选中项回退：`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimePageViewModel.cs:57-67`、`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimePageViewModel.cs:174-313`。
  - 条目状态及进度派生属性通知：`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeMockModels.cs:129-203`。
  - 搜索输入、详情状态和进度条绑定：`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml:282-292`、`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml:532-535`、`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml:597-604`。
- **二审裁决**：**接受**。
- **校正后的严重级别**：无（通过）。
- **裁决理由**：`RefreshVisibleEntries` 先生成匹配列表，再在选中项不属于匹配集时回退到首项；状态动作会刷新集合和计数，进度/状态由条目自身通知。没有发现数据、网络或跨边界行为。
- **建议的最小处理方案**：无需代码处理；保留 P0-07 的键盘和人工交互验收。
- **受影响文件**：核验对象为 `D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimePageViewModel.cs`、`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeMockModels.cs`、`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml`。
- **处理分类**：无需处理；人工验收为必需流程。
- **是否阻断当前阶段**：否（但仍受总体验收规则约束）。

### FUNC-03 — P0-06 Widget 响应式、状态、Quick Add 和 Alert 总结

- **一审主张**：响应式分档、状态循环、Alert 不切页、Quick Add 空输入禁用均通过。
- **证据（文件与精确行号）**：
  - 一审功能表：`D:\Anime\reviews\review-2026-08-27-p0.md:57-59`。
  - P0-06 产品规则与 acceptance：`D:\Anime\docs\slices\P0-06-implementation-brief.md:31-38`、`D:\Anime\docs\slices\P0-06-implementation-brief.md:47-56`。
  - Compact/Standard/Expanded 阈值及通知：`D:\Anime\src\windows\ReminNote.Widget\ViewModels\WidgetViewModel.cs:222-245`。
  - 页面状态循环、Quick Add、Alert 保存/恢复状态：`D:\Anime\src\windows\ReminNote.Widget\ViewModels\WidgetViewModel.cs:247-293`、`D:\Anime\src\windows\ReminNote.Widget\ViewModels\WidgetViewModel.cs:345-375`。
  - 窗口尺寸事件与 400/500/680 DIP 手测要求：`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml.cs:7-22`、`D:\Anime\docs\slices\P0-06-manual-test.md:64-82`。
  - Quick Add 与 Alert/Drawer 的独立叠层：`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml:372-452`。
- **二审裁决**：**部分接受**。
- **校正后的严重级别**：**MINOR**（由 `IMP-1`、`IMP-2` 共同造成）。
- **裁决理由**：响应式阈值、状态循环、Alert 不切换页、Quick Add 空值命令条件本身成立；但将整个 P0-06 概括为“全部通过”不准确，任务状态显示和提醒已读行为分别存在 `IMP-1`、`IMP-2`。
- **建议的最小处理方案**：无需新增第三个修复项；按 `IMP-1`、`IMP-2` 修复并重跑 P0-06 手测 2、3、4、6、7。
- **受影响文件**：`D:\Anime\src\windows\ReminNote.Widget\ViewModels\WidgetViewModel.cs`、`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml`。
- **处理分类**：必须修复（仅限上述两个已确认行为问题）。
- **是否阻断当前阶段**：**是**，在两项修复和手测完成前不能宣告 P0 完成。

### FUNC-04 — P0-07 资源键数量与一对一关系

- **一审主张**：213 个稳定资源键与 `UiText.resx` 条目 1:1，且 verify 脚本会反向查缺。
- **证据（文件与精确行号）**：
  - 一审功能表：`D:\Anime\reviews\review-2026-08-27-p0.md:60`。
  - C# 稳定键范围：`D:\Anime\src\windows\ReminNote.Windows\Resources\Localization\UiText.cs:19-234`；资源数据范围：`D:\Anime\src\windows\ReminNote.Windows\Resources\Localization\UiText.resx:8-223`。
  - 本二审只读统计结果为 cs key 213、resx data 213、双方差集均为 0。
  - verify 脚本建立 resx 名称表，但只遍历 C# 常量检查其是否存在于 resx：`D:\Anime\scripts\verify-p0-07.ps1:29-44`；脚本没有遍历 resx 名称反查 C# 常量。
- **二审裁决**：**部分接受**。
- **校正后的严重级别**：**STYLE/NIT**（当前状态正确，门禁覆盖范围不足）。
- **裁决理由**：当前提交的实际资源集合确实是 213/213 且无差集，因此一对一结果成立；但“一审说 verify 脚本反向查缺”不成立，脚本只能防止 C# 新增键漏进 resx，不能防止孤立 resx 条目。该差异不会造成当前运行时缺陷。
- **建议的最小处理方案**：可选地在 `verify-p0-07.ps1` 增加 resx-only 反向断言，并保留当前独立计数；报告中应区分“当前数据一对一”和“门禁具备双向保证”。
- **受影响文件**：可选改进涉及 `D:\Anime\scripts\verify-p0-07.ps1`。
- **处理分类**：可选改进。
- **是否阻断当前阶段**：否；当前 HEAD 的实际键集合已通过独立双向统计。

### FUNC-05 — P0-07 缺失键回退与旧 P0-03 文案

- **一审主张**：缺失资源键会回退为 `[Missing resource: …]`，当前 UI 没有 P0-03 阶段文案残留。
- **证据（文件与精确行号）**：
  - 一审功能表：`D:\Anime\reviews\review-2026-08-27-p0.md:61`。
  - `UiText.Get` 的非空回退：`D:\Anime\src\windows\ReminNote.Windows\Resources\Localization\UiText.cs:334-339`。
  - 门禁检查 Shell 不含旧阶段标签并读取四个 UI 文件：`D:\Anime\scripts\verify-p0-07.ps1:52-60`。
  - P0-07 acceptance 要求无空名称、原始资源键和 P0-03 专属文案：`D:\Anime\docs\slices\P0-07-implementation-brief.md:61-68`。
- **二审裁决**：**接受**。
- **校正后的严重级别**：无（当前代码/静态检查通过）。
- **裁决理由**：回退逻辑明确且非空；当前源代码检索未发现 P0-03 阶段标签，剩余的 P0-03 文本只出现在计划、决策和验收文档中，属于文档历史语境，不是当前 UI 文案。
- **建议的最小处理方案**：无需代码处理；保留启动和默认中文人工验收。
- **受影响文件**：核验对象为 `D:\Anime\src\windows\ReminNote.Windows\Resources\Localization\UiText.cs`、`D:\Anime\scripts\verify-p0-07.ps1`。
- **处理分类**：无需处理；人工验收为必需流程。
- **是否阻断当前阶段**：否。

### FUNC-06 — P0-07 AutomationProperties 覆盖结论

- **一审主张**：Shell、TODAY、ANIME、Widget 的 AutomationProperties 覆盖通过。
- **证据（文件与精确行号）**：
  - 一审功能表：`D:\Anime\reviews\review-2026-08-27-p0.md:61`。
  - 当前关键控件存在可读名称/帮助文本，例如 Shell：`D:\Anime\src\windows\ReminNote.Windows\MainWindow.xaml:65-70`；TODAY：`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayResources.xaml:460-466`；ANIME 搜索：`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml:282-286`；Widget 抽屉操作：`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml:511-516`。
  - verify 脚本只检查每个文件是否包含至少一个 `AutomationProperties.Name`：`D:\Anime\scripts\verify-p0-07.ps1:52-60`。
  - P0-07 要求关键控件可 Tab 到达且有可见焦点/可读名称：`D:\Anime\docs\slices\P0-07-implementation-brief.md:63-68`、`D:\Anime\docs\slices\P0-07-implementation-brief.md:70-80`。
- **二审裁决**：**部分接受**。
- **校正后的严重级别**：**STYLE/NIT**（门禁断言过宽，不等同于当前控件缺名）。
- **裁决理由**：代码中关键交互确实有多个 `Name`/`HelpText`，因此不能驳回实现本身；但 verify 的文件级 `Contains` 不能证明所有关键控件均有正确名称，也不能验证名称随状态更新。该结论必须依赖 P0-07 的人工 Tab/UIA/Narrator 验收。
- **建议的最小处理方案**：当前不必为此扩大 P0；可在后续把门禁收窄到明确控件清单，或保留现状并在报告中明确“静态存在性检查”而非“逐控件覆盖证明”。
- **受影响文件**：可选改进涉及 `D:\Anime\scripts\verify-p0-07.ps1`；当前 UI 证据分布在上述 XAML 文件。
- **处理分类**：可选改进；人工验收为必须流程。
- **是否阻断当前阶段**：否（但未完成人工验收时，整体 P0 仍受 `FUNC-08` 阻断）。

### FUNC-07 — P0-07 Widget Link、CI 诊断和只读绑定

- **一审主张**：Widget 通过 Link 复用 Design System/UiText；verify 门禁、CI 诊断以及 `ProgressValue`/`ActivePageTitle` OneWay 保护通过。
- **证据（文件与精确行号）**：
  - 一审功能表：`D:\Anime\reviews\review-2026-08-27-p0.md:62-63`。
  - Widget Link 四个 DesignSystem 字典、UiText.cs 和资源 LogicalName：`D:\Anime\src\windows\ReminNote.Widget\ReminNote.Widget.csproj:15-32`。
  - Widget 已登记到 Solution：`D:\Anime\ReminNote.sln:20-21`；且 Widget 项目没有 `ReminNote.Windows` ProjectReference：`D:\Anime\src\windows\ReminNote.Widget\ReminNote.Widget.csproj:11-33`。
  - 页面标题/副标题与 ANIME 进度条明确 OneWay：`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml:77-80`、`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml:532-534`。
  - CI 诊断、P0-07 门禁、locked restore、Release build/test 顺序：`D:\Anime\.github\workflows\ci.yml:23-41`。
- **二审裁决**：**接受**。
- **校正后的严重级别**：无（通过）。
- **裁决理由**：项目文件和 Solution 登记与 ADR-0008、架构基线一致；Widget 只通过源文件 Link 复用，没有反向引用主 Windows 程序集。只读绑定保护和 CI 步骤均存在。CI 的 `dotnet test` 覆盖限制另列于 `FUNC-08`，不改变本项 Link/配置结论。
- **建议的最小处理方案**：无需处理。
- **受影响文件**：核验对象为上述 `ReminNote.Widget.csproj`、`ReminNote.sln`、`WidgetWindow.xaml`、`AnimeResources.xaml`、`.github\workflows\ci.yml`。
- **处理分类**：无需处理。
- **是否阻断当前阶段**：否。

### FUNC-08 — `test.ps1` 通过与自动化测试证据边界

- **一审主张**：一审把 `test.ps1` 通过列为验证结果，并在功能表中概括 P0-07 验证通过。
- **证据（文件与精确行号）**：
  - 一审执行记录：`D:\Anime\reviews\review-2026-08-27-p0.md:5-9`。
  - `test.ps1` 先 restore、运行 solution test，再运行 P0-07 门禁：`D:\Anime\scripts\test.ps1:12-22`。
  - 当前仓库实际没有 `*Tests.csproj`；verify 脚本对该情况只输出说明：`D:\Anime\scripts\verify-p0-07.ps1:73-78`。
  - 活文档明确规定空 `dotnet test` 不能描述为业务测试覆盖：`D:\Anime\DEVELOPMENT.md:52-56`、`D:\Anime\TESTING.md:43-49`、`D:\Anime\DECISIONS.md:78-84`。
- **二审裁决**：**部分接受**。
- **校正后的严重级别**：**STYLE/NIT**（验证措辞边界）。
- **裁决理由**：如果把“通过”理解为命令退出码和后续静态门禁通过，一审记录可以成立；但当前没有测试项目，不能把它理解为 TODAY/ANIME/Widget 业务测试通过。该限制是已记录的 P0-07 设计决策，不构成当前 P0 缺项。
- **建议的最小处理方案**：后续集成摘要和验收记录明确写成“无测试程序集；`dotnet test` 流程/门禁通过”，不要写成业务覆盖；按文档在 P1/P2 建立领域逻辑单元测试。
- **受影响文件**：当前无需修改；未来测试涉及 `D:\Anime\scripts\test.ps1` 及新的测试项目。
- **处理分类**：可选改进/报告措辞校正。
- **是否阻断当前阶段**：否。

### FUNC-09 — 一审 build、test、MainWindow/Widget 启动冒烟的独立复现

- **一审主张**：一审窗口实际执行了 Release build、test、MainWindow 标题冒烟和 Widget 标题冒烟，并全部通过。
- **证据（文件与精确行号）**：
  - 一审自述记录：`D:\Anime\reviews\review-2026-08-27-p0.md:5-9`。
  - 当前主计划要求 clean build、app launch、Widget 可交互和 CI 成功：`D:\Anime\ReminNote_MASTER_DEVELOPMENT_PLAN.md:2931-2945`。
  - P0 完成不能只凭窗口渲染，必须由用户手动验证主要交互：`D:\Anime\ReminNote_MASTER_DEVELOPMENT_PLAN.md:2947-2951`。
  - 本二审只读复核了静态门禁；没有重新运行会产生 build 输出或启动进程的命令。
- **二审裁决**：**信息不足**（仅针对本窗口的独立复现，不否定一审执行记录）。
- **校正后的严重级别**：**STYLE/NIT**（证据来源限制，不是代码缺陷）。
- **裁决理由**：静态检查不能证明 WPF 在目标 Windows 环境中的资源加载、焦点、缩放、读屏和主要点击路径；这些必须由用户按手册完成。当前不能把一审自述升级为二审独立验证结果。
- **建议的最小处理方案**：集成窗口或用户按 `D:\Anime\TESTING.md:90-126`、`D:\Anime\docs\slices\P0-06-manual-test.md:64-191` 完成三端手测，记录 Release build、主窗口、Widget、键盘/UIA、高对比度和数据边界结果。
- **受影响文件**：无代码文件；验收依据为 `D:\Anime\TESTING.md` 和 `D:\Anime\docs\slices\P0-06-manual-test.md`。
- **处理分类**：必须完成验收；不是要求本二审窗口代替用户运行。
- **是否阻断当前阶段**：**是**，在用户手动验收完成前不能宣告 P0 完成。

### NIT-1 — AnimePageViewModel 的死包装属性

- **一审主张**：`SelectedEntryStatus`、`SelectedEntryProgress` 未被 XAML 使用，相关通知冗余。
- **证据（文件与精确行号）**：
  - 一审定位：`D:\Anime\reviews\review-2026-08-27-p0.md:72-74`。
  - 包装属性和通知：`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimePageViewModel.cs:82-106`。
  - 实际 XAML 使用链式绑定：`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml:597-604`。
  - 条目自身会通知 `StatusLabel`、`ProgressLabel`：`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeMockModels.cs:158-203`。
- **二审裁决**：**接受**。
- **校正后的严重级别**：**STYLE/NIT**。
- **裁决理由**：在当前代码搜索范围内，两个包装属性只有声明和通知，没有绑定引用；实际绑定由 `SelectedEntry` 链接到条目属性。删除它们不会改变当前 UI 绑定，但属于可选清理。
- **建议的最小处理方案**：删除两个包装属性及 `SelectedEntry` setter 中对应的两条通知；修改前确认没有外部调用者依赖。
- **受影响文件**：`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimePageViewModel.cs`。
- **处理分类**：可选改进。
- **是否阻断当前阶段**：否。

### NIT-2 — Anime/Today 输入框的焦点可见性与高对比度

- **一审主张**：Anime 搜索框没有显式 `IsKeyboardFocused` 触发器；Today 和 Widget 已使用 `AccessibleFocusBrush`，建议补齐 Anime 或做人工高对比度验证。
- **证据（文件与精确行号）**：
  - 一审主张：`D:\Anime\reviews\review-2026-08-27-p0.md:75-78`。
  - Anime 搜索样式确实没有 `Style.Triggers`：`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml:188-199`。
  - **校正一审事实**：Today 输入样式有触发器，但焦点边框使用的是 `AccentBrush`，不是 `AccessibleFocusBrush`：`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayResources.xaml:421-447`。
  - Widget Quick Add 使用 `AccessibleFocusBrush`：`D:\Anime\src\windows\ReminNote.Widget\WidgetResources.xaml:519-535`；共享可访问焦点画刷才绑定系统高亮色：`D:\Anime\src\windows\ReminNote.Windows\Resources\DesignSystem\Colors.xaml:35-38`。
  - P0-07 要求焦点可见并要求高对比度/缩放人工验收：`D:\Anime\docs\slices\P0-07-implementation-brief.md:63-80`、`D:\Anime\TESTING.md:107-119`。
- **二审裁决**：**部分接受**。
- **校正后的严重级别**：**MINOR**（若 Windows 实机确认焦点始终清晰可见，可降为 STYLE/NIT）。
- **裁决理由**：Anime 缺少显式焦点策略的事实成立；Today 的“已使用 AccessibleFocusBrush”则是错误描述。仅凭静态代码还不能断言默认 WPF TextBox 焦点一定不可见，但 Today 的固定 `AccentBrush` 不能证明具备系统高对比度适配，因此一审的高对比度正面结论过强。
- **建议的最小处理方案**：P0 完成前必须在 Windows 默认/高对比度环境使用 Tab 验证 Anime 搜索和 Today Quick Add 的焦点可见性；若有不可见、对比度不足或不随系统变化，最小代码处理是给 Anime 搜索增加焦点触发器，并把 Today 焦点边框改为 `AccessibleFocusBrush`。
- **受影响文件**：`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml`、`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayResources.xaml`；共享 token 已存在于 `D:\Anime\src\windows\ReminNote.Windows\Resources\DesignSystem\Colors.xaml`。
- **处理分类**：人工验证为必须；代码调整为条件性必须，不能以一审静态结论替代。
- **是否阻断当前阶段**：**条件性为是**：在人工焦点/高对比度验收未完成前，P0-07 完成证据不闭环；若实机验收通过，则本条不单独阻断。

### NIT-3 — Widget 中文 fixture 使用数字实体及与 TODAY fixture 的文字差异

- **一审主张**：WidgetWindow 的三处中文 fixture 使用 `&#x…;` 数字实体，且 Widget 的“整理桌面资料”与 Main App 的 TODAY fixture “整理桌面文件”不一致。
- **证据（文件与精确行号）**：
  - 一审定位：`D:\Anime\reviews\review-2026-08-27-p0.md:79-83`。
  - 数字实体确实存在于 Widget 主卡、ANIME 主卡和提醒条目：`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml:122-123`、`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml:244-245`、`D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml:481-485`。
  - Main App 中“整理桌面文件”是 RANGE/Needs Review fixture：`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayMockDataService.cs:55-67`；真正的 TODAY `PrimaryTask` 是“准备设计评审材料”：`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayMockDataService.cs:10-14、42-54`。
  - P0-07 明确允许 Mock 标题/说明作为 fixture 保留：`D:\Anime\docs\slices\P0-07-implementation-brief.md:54-59`；P0-06 明确所有内容为独立内存 Mock：`D:\Anime\docs\slices\P0-06-implementation-brief.md:31-38`。
- **二审裁决**：**部分接受**。
- **校正后的严重级别**：**STYLE/NIT**。
- **裁决理由**：数字实体降低源文件可读性，这一建议成立；但“Main App 主任务不一致”的表述不准确，Widget 与 Main App 的 fixture 没有被当前 brief 规定为同一数据集，而且 Main App 的主焦点本来也不是“整理桌面文件”。不应把独立 Mock 的文字差异升级为功能缺陷。
- **建议的最小处理方案**：可选地将三处数字实体改成 UTF-8 中文；只有产品明确要求跨窗口展示同一 fixture 时，才另行统一文字，不在本 P0 二审中强制改数据。
- **受影响文件**：可选改进涉及 `D:\Anime\src\windows\ReminNote.Widget\WidgetWindow.xaml`；若未来统一 fixture，另涉及 `D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayMockDataService.cs`。
- **处理分类**：可选改进。
- **是否阻断当前阶段**：否。

### NIT-4 — 模块 CornerRadius 未全部令牌化

- **一审主张**：Today、Anime、Widget 仍有若干硬编码圆角；模块局部值可接受，建议 P1 前统一认知。
- **证据（文件与精确行号）**：
  - 一审定位：`D:\Anime\reviews\review-2026-08-27-p0.md:84-86`。
  - 共享 Design System 当前提供 `CardCornerRadius` 和 `NavigationCornerRadius`：`D:\Anime\src\windows\ReminNote.Windows\Resources\DesignSystem\Spacing.xaml:15-16`。
  - Today 仍有局部 `CornerRadius="8"`：`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayResources.xaml:614`、`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayResources.xaml:811`；Anime 有局部 7/9/11/14 等值：`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml:127-232`。
  - Widget 已建立自己的窗口/卡片/控件令牌并在多数样式中引用：`D:\Anime\src\windows\ReminNote.Widget\WidgetResources.xaml:13-15`、`D:\Anime\src\windows\ReminNote.Widget\WidgetResources.xaml:25-26`、`D:\Anime\src\windows\ReminNote.Widget\WidgetResources.xaml:546-573`。
  - P0-05 允许在模块资源字典中补充仅限模块的装饰/控件样式：`D:\Anime\docs\slices\P0-05-implementation-brief.md:7-14`。
- **二审裁决**：**部分接受**。
- **校正后的严重级别**：**STYLE/NIT**。
- **裁决理由**：硬编码事实成立，且 P0-03 产品规则确实偏好集中令牌；但这些值位于功能模块的局部装饰样式，部分具有与共享 token 不同的视觉意图，Widget 也已经使用了模块 token。当前没有证明其违反 P0 acceptance 或造成运行时问题。
- **建议的最小处理方案**：记录模块级圆角的允许边界；后续若需要统一主题/令牌治理，再将重复值收编为模块 token，不为本项扩大 P0 重构。
- **受影响文件**：`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayResources.xaml`、`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml`、`D:\Anime\src\windows\ReminNote.Widget\WidgetResources.xaml`。
- **处理分类**：可选改进。
- **是否阻断当前阶段**：否。

### NIT-5 — MainWindowViewModel 手动通知 ActiveTitle

- **一审主张**：`MainWindowViewModel.cs:60` 仍在导航方法中手动通知 `ActiveTitle`，建议以后改到 `CurrentPage` setter。
- **证据（文件与精确行号）**：
  - 一审定位：`D:\Anime\reviews\review-2026-08-27-p0.md:87-89`。
  - `CurrentPage` 是私有 setter，当前唯一赋值点在 `Navigate`，随后手动通知 `ActiveTitle`：`D:\Anime\src\windows\ReminNote.Windows\ViewModels\MainWindowViewModel.cs:34-60`。
  - Header 绑定 `ActiveTitle`：`D:\Anime\src\windows\ReminNote.Windows\MainWindow.xaml:63-70`。
- **二审裁决**：**接受**。
- **校正后的严重级别**：**STYLE/NIT**。
- **裁决理由**：写法可以改进，但在当前封装下通知位置正确且没有观察到漏通知路径；把它升级为行为问题没有依据。
- **建议的最小处理方案**：下次触碰该文件时可将通知移入 setter，并避免重复通知；当前无需为此单独改动。
- **受影响文件**：`D:\Anime\src\windows\ReminNote.Windows\ViewModels\MainWindowViewModel.cs`。
- **处理分类**：可选改进。
- **是否阻断当前阶段**：否。

### FUNC-10 — Design System Typography 重复 setter / TextBlockBaseStyle 记录项

- **一审主张**：`TextBlockBaseStyle` 已被 Today/Anime/Widget 使用；DesignSystem 中旧的多个 TextBlock 样式仍重复声明 `FontFamily`/`SnapsToDevicePixels`，属于未处理的可选项。
- **证据（文件与精确行号）**：
  - 一审记录：`D:\Anime\reviews\review-2026-08-27-p0.md:95-107`。
  - `TextBlockBaseStyle` 与旧样式重复 setter：`D:\Anime\src\windows\ReminNote.Windows\Resources\DesignSystem\Typography.xaml:3-175`。
  - Today、Anime、Widget 模块样式均有 `BasedOn="{StaticResource TextBlockBaseStyle}"`，例如：`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayResources.xaml:9-20`、`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimeResources.xaml:31-39`、`D:\Anime\src\windows\ReminNote.Widget\WidgetResources.xaml:63-71`。
- **二审裁决**：**部分接受**。
- **校正后的严重级别**：**STYLE/NIT**。
- **裁决理由**：重复 setter 的记录成立，但 `TextBlockBaseStyle` 并非死样式，当前三个功能模块确实依赖它；旧 Typography 样式也仍被 MainWindow 或页面直接使用，不能笼统称为死代码。属于可读性/维护性整理，不影响 P0 行为。
- **建议的最小处理方案**：后续可将仍然直接声明基础字体属性的 Typography 样式改为 `BasedOn` 基础样式并做视觉回归；当前不要求重构。
- **受影响文件**：`D:\Anime\src\windows\ReminNote.Windows\Resources\DesignSystem\Typography.xaml` 及其使用方。
- **处理分类**：可选改进。
- **是否阻断当前阶段**：否。

### FUNC-11 — clean.ps1 链接/重解析点防护

- **一审主张**：该项不是当前缺项，因为 P0-07 brief 已将其列入后续安全 Backlog。
- **证据（文件与精确行号）**：
  - 一审说明：`D:\Anime\reviews\review-2026-08-27-p0.md:90-91`。
  - P0-07 风险项明确将 `-PurgeDevData` 的链接/重解析点防护列为后续安全加固：`D:\Anime\docs\slices\P0-07-implementation-brief.md:82-88`。
  - 当前脚本默认只清理 `src` 下的 `bin/obj`，显式 purge 时清理 `.devdata` 子项：`D:\Anime\scripts\clean.ps1:9-26`。
- **二审裁决**：**驳回**（驳回“属于当前 P0 缺项”的指控；不是驳回后续安全工作本身）。
- **校正后的严重级别**：无（按当前范围不计入 P0 缺陷）。
- **裁决理由**：文档已经明确范围和后续 backlog，当前 P0-07 的 acceptance 是保持默认清理边界，不要求在本 Slice 完成完整重解析点防护。
- **建议的最小处理方案**：当前不处理；将完整重解析点/链接安全防护保留到已授权的后续安全 Slice。
- **受影响文件**：后续安全工作可能涉及 `D:\Anime\scripts\clean.ps1`；本 P0 不要求修改。
- **处理分类**：范围内无需处理；后续安全 backlog。
- **是否阻断当前阶段**：否。

### ARCH-01 — 架构边界、数据安全与性能结论

- **一审主张**：Widget Link 复用无反向引用或复制字典；Core 保持干净；没有网络/SQLite/Token/DPAPI；`.devdata` 边界正常；Mock 循环为可接受的 O(n)，无 N+1 或无界循环。
- **证据（文件与精确行号）**：
  - 一审架构/安全/性能结论：`D:\Anime\reviews\review-2026-08-27-p0.md:95-107`。
  - 架构基线明确 Widget 仅 Link Design System/默认文案，Core 无 WPF/EF/Windows 依赖，当前 Slice 不接 SQLite/Reminder/IPC/网络：`D:\Anime\ARCHITECTURE.md:15-26`、`D:\Anime\ARCHITECTURE.md:41-65`。
  - Widget 项目只引用 CommunityToolkit，不引用主 Windows 项目：`D:\Anime\src\windows\ReminNote.Widget\ReminNote.Widget.csproj:11-33`；Core 项目仅为 `net10.0` 且无项目/包引用：`D:\Anime\src\windows\ReminNote.Core\ReminNote.Core.csproj:1-6`。
  - 只读源代码检索未发现 `HttpClient`、SQLite、DPAPI/ProtectedData、Credential 或 Token API；P0-05/P0-06 brief 也明确禁止这些接入：`D:\Anime\docs\slices\P0-05-implementation-brief.md:16-22`、`D:\Anime\docs\slices\P0-06-implementation-brief.md:21-29`。
  - TODAY 分组重建、汇总刷新和 ANIME 筛选/计数均为固定小集合上的有限遍历：`D:\Anime\src\windows\ReminNote.Windows\Features\Today\TodayPageViewModel.cs:226-242`、`D:\Anime\src\windows\ReminNote.Windows\Features\Anime\AnimePageViewModel.cs:254-313`。
  - 数据安全规则与 clean 默认边界：`D:\Anime\PRODUCT_RULES.md:9-15`、`D:\Anime\DEVELOPMENT.md:36-38`、`D:\Anime\scripts\clean.ps1:9-26`。
- **二审裁决**：**接受**。
- **校正后的严重级别**：无（当前 P0 范围内通过）。
- **裁决理由**：代码、项目引用和文档边界相互一致；未发现一审所述之外的安全、数据丢失或架构阻断。性能结论适用于当前固定 Mock 规模，不应外推到 P1 真实数据。
- **建议的最小处理方案**：无需当前处理；进入真实数据/持久化 Slice 时重新审查边界和性能假设。
- **受影响文件**：核验对象为上述架构、项目文件、脚本和两个 Mock ViewModel；无待修复文件。
- **处理分类**：无需处理。
- **是否阻断当前阶段**：否。

## 最终交付判断

1. 先修复 `IMP-1`、`IMP-2`，并补做对应 Widget 手测；这两项是当前最小代码闭环。
2. 必须完成 P0-07 主窗口、TODAY、ANIME、Widget、Tab/UIA、缩放/高对比度和数据安全人工验收；尤其不能沿用一审关于 Today `AccessibleFocusBrush` 的错误描述。
3. 资源键、Link、Core 边界、CI 配置和当前 Mock 数据安全结论可以保留；资源/UIA 门禁的双向或逐控件强化属于可选改进。
4. 在上述必须项完成前，向集成窗口的状态应写为：**P0 实现基本可用，但完成声明被两个 Widget 行为缺陷和未闭环的用户人工验收阻断**。
