# P0-04 TODAY 高保真 Mock Implementation Brief

日期：2026-08-27
分支：`codex/p0-04-today`

## Goal

在现有 WPF Shell 内交付可真实运行、可手动验证的 TODAY 高保真 Mock，用内存 Mock 数据呈现主计划冻结的 TODAY 信息层级，并通过 ViewModel 命令验证主要交互状态。

## 当前基线

- `Features/Today` 目前只有 Shell 占位页、资源字典和 DI 注册入口。
- App 已合并 `Features/Today/TodayResources.xaml`，因此本 Slice 无需修改 `App.xaml`。
- 当前 worktree 在 `codex/p0-04-today`，开始编辑前状态干净。
- 共享 Design System 位于 `Resources/DesignSystem/`，属于集成窗口所有权，本 Slice 只读取和复用。

## In Scope

- TODAY 页面高保真布局：日期摘要、主焦点卡、NEEDS REVIEW 提示、分组任务列表、选中任务详情和内存状态提示。
- 默认分组：`OVERDUE`、`MORNING`、`AFTERNOON`、`EVENING`、`ANYTIME`、`COMPLETED`。
- 内存 Mock 数据服务和 Today 专属 ViewModel；不写入磁盘。
- 任务状态交互：完成/恢复、置顶/取消置顶、选择任务查看详情、展开/折叠分组。
- Mock Quick Add：展开输入、添加一个内存 ANYTIME 任务、取消输入，并显示明确的 Mock 边界提示。
- `RANGE`/`AWAITING RESULT` 与 `NEEDS REVIEW` 的静态演示状态；保留原计划时间文案，不表达实际工作时长。
- 在 `TodayResources.xaml` 中复用共享 Design System 资源，并补充仅属于 Today 的布局/控件模板。
- 本 Slice 文档中的验收、手动测试、已知限制和集成请求。

## Out of Scope

- SQLite、EF Core、真实 Task 领域模型或持久化。
- Reminder Scheduler、Toast、Snooze、Agent、IPC、Change Journal、网络访问和生产数据。
- P1/P2 的确定性 TaskParser、真实工作日边界、真实 RANGE 结果写入、历史/复盘持久化和跨日规则实现。
- 修改 `App.xaml`、`App.xaml.cs`、`MainWindow.xaml`、共享 ViewModels、`Resources/DesignSystem`、Solution、Directory 文件、脚本、CI 或 ANIME/Widget 目录。
- 新增 NuGet 包或公共设计令牌。

## Product Rules

- TODAY 是计划视图，不是时间追踪器；Mock 不记录开始、结束、暂停或耗时。
- 任务时间形状只以 `ANYTIME`、`TIME`、`RANGE` 三种概念呈现；提醒时间不会被模拟成任务时间。
- RANGE 结束但无结果时显示 `AWAITING RESULT`，同时计入顶部 `NEEDS REVIEW`；不自动改成 `MISSED`。
- 历史计划文案保持原样，例如 `14:00–16:00`，不改写为实际完成时段。
- 所有状态只存在于当前进程内；页面会明确标识 `MOCK · 仅内存`，避免用户误以为已经保存。
- 用户可见文本集中在 Today 资源/页面中；C# 只保留状态和值，避免把业务规则放入 Shell。

## Implementation Shape

- `TodayMockDataService.cs`：提供固定、可重复的 TODAY 快照和任务样例。
- `TodayPageViewModel.cs`：持有任务集合、分组、Quick Add、选择和状态命令，负责重建内存分组视图。
- `TodayResources.xaml`：提供页面 DataTemplate、任务/分组/详情模板和 Today 专属控件样式；颜色、间距、字体优先使用现有 DynamicResource。
- `TodayFeatureRegistration.cs`：注册 Mock 服务和 Today 页面 ViewModel，保持现有稳定模块入口。

## Reuse Research

本 Slice 不需要新增依赖。已检查并复用现有原生 WPF Design System：

- `CardBorderStyle`、`HeaderSurfaceStyle`、`FooterSurfaceStyle`；
- `ShellBackgroundBrush`、`CardBackgroundBrush`、`BorderBrush`、`AccentBrush`、内容文字颜色；
- `PageTitleTextStyle`、`PageDescriptionTextStyle`、`CardTitleTextStyle`、`CardBodyTextStyle`；
- 现有 `CommunityToolkit.Mvvm`，继续使用 `ObservableObject`/`RelayCommand`。

不引入第三方 UI 框架、图标包、网络库或持久化依赖，因此没有额外许可证、维护或安全审查项。

## Acceptance

1. 从现有 `scripts/run.ps1` 启动后，默认 TODAY 页面显示完整 Mock，而非 P0-02 占位卡。
2. 页面能清晰呈现主焦点、Needs Review、时间分组、完成分组和内存 Mock 边界。
3. 点击任务的完成按钮后，任务在对应未完成/已完成分组间移动，统计和 Needs Review 数量同步更新；再次点击可恢复。
4. 点击任务行能更新右侧详情；置顶按钮能更新视觉状态和详情文案。
5. 分组标题能展开/折叠，任务内容不会修改数据源。
6. Quick Add 能展开、接受非空标题、添加内存 ANYTIME 任务并关闭；空标题不会添加任务并给出提示；取消不会添加任务。
7. RANGE 示例显示 `AWAITING RESULT` 和 Needs Review，不自动标记为 Missed，也不显示真实完成时间。
8. locked restore、Release 构建和测试通过，且不引入未解释的新警告。
9. `git diff --name-only` 只包含 `src/windows/ReminNote.Windows/Features/Today/**` 与 `docs/slices/P0-04-*`。

## Manual Test

### Environment

- Windows 10 22H2 x64 或 Windows 11 x64。
- 仓库根目录：当前 worktree 根目录。
- 构建配置：Release。
- 数据路径：无；本 Slice 只使用进程内 Mock，不创建 `.devdata` 数据文件。

### Test 1：启动并确认 TODAY 层级

1. 在仓库根目录运行 `./scripts/run.ps1`。
2. 确认默认打开“今天”页面。
3. 观察页面顶部、主焦点卡、`NEEDS REVIEW` 提示和任务分组。

预期：页面显示日期摘要、`TODAY` 小标题、主焦点任务、`NEEDS REVIEW · 1`（或对应当前 Mock 数量）、`OVERDUE` 至 `COMPLETED` 分组；页面角落明确显示 `MOCK · 仅内存`。没有数据库、网络或真实用户任务。

失败判定：窗口启动异常、仍显示 Shell 占位卡、分组/主焦点缺失、出现真实数据、出现数据库/网络错误，或页面声明已保存任务。

### Test 2：选中任务和完成状态

1. 点击任意未完成任务行，例如 `提交周报`。
2. 确认右侧详情显示该任务标题、时间形状/时间、优先级和说明。
3. 点击任务行左侧的完成圆形按钮。
4. 观察任务分组和顶部统计，再次点击已完成任务的恢复按钮。

预期：选中任务有清晰视觉反馈；完成后任务移动到 `COMPLETED`，开放任务数减少、完成数增加；恢复后回到原分组。整个过程不弹出保存提示，也不记录完成时间。

失败判定：点击无响应、详情不随选中项变化、任务消失、统计不同步、任务错误地进入 `MISSED`，或出现耗时/真实完成时间。

### Test 3：RANGE 与 NEEDS REVIEW

1. 点击 `整理桌面文件`（带 `14:00–16:00` 的 RANGE Mock）。
2. 查看任务行和右侧详情中的状态。
3. 点击顶部 `NEEDS REVIEW` 提示（如页面将其实现为可交互区域，则应能定位到该任务；否则确认其静态摘要存在）。

预期：任务显示 `AWAITING RESULT`，顶部 Needs Review 数量包含它，原计划范围保持 `14:00–16:00`；不会自动显示 `MISSED` 或实际完成区间。

失败判定：RANGE 被当作 ANYTIME/TIME、被自动标记为 Missed、Needs Review 数量不一致或原计划被重写。

### Test 4：Quick Add

1. 点击 `QUICK ADD`。
2. 输入 `准备明天的会议材料`，点击 `添加到今天`。
3. 确认新任务出现在 `ANYTIME` 分组，并观察页面反馈。
4. 再次打开 Quick Add，不输入内容直接点击添加；然后打开后点击取消。

预期：非空标题创建一个带 `ANYTIME` 标签的内存任务并更新统计；空标题不会创建任务且显示提示；取消不会创建任务；提示始终说明状态仅在当前运行中有效。

失败判定：输入框无法展开、空标题也被添加、任务被错误解析为 TIME/RANGE、任务未出现在 ANYTIME、应用崩溃或生成持久化文件。

### Test 5：分组和置顶交互

1. 点击一个分组标题，确认任务列表折叠，再次点击确认展开。
2. 选中任意任务，点击任务行/详情中的置顶按钮。
3. 再次点击取消置顶。

预期：分组只改变当前视图展开状态；置顶状态在任务行和详情同步更新，不改变任务时间或优先级规则。

失败判定：折叠影响其他分组、任务数据被清空、置顶按钮无反馈或置顶改变了计划时间/优先级。

### Test 6：数据与边界检查

1. 关闭应用。
2. 检查本次 `git diff --name-only` 的文件范围。
3. 检查仓库中没有因运行 Mock 新增 SQLite、Token、网络缓存或生产配置。

预期：只存在 Today 模块和 P0-04 slice 文档改动；无新增真实数据或凭据；ANIME 页面仍可通过左侧导航打开且未被本 Slice 改写。

失败判定：越界修改、创建数据库/Token/缓存、发生网络访问或 ANIME/Shell 行为被意外改变。

## Risks / Known Limitations

- 这是单进程、非持久化 Mock；重启后所有完成、置顶、Quick Add 状态恢复为固定样例。
- Quick Add 在 P0 只创建 ANYTIME Mock，不实现 P2 的确定性日期/时间/标签/优先级解析。
- `NEEDS REVIEW`、RANGE、OVERDUE 等状态由固定 Mock 快照演示，不由当前时钟或真实工作日边界计算。
- TodayResources 只能复用当前 Design System；如果集成窗口希望统一新的共享 token，需要另行提出公共文件集成请求。
- Shell 顶部/底部仍显示 P0-03 文案，本 Slice 不越界修改；集成窗口应在合并时更新为 P0-04 或更通用的产品状态文案。

## Integration Requests

1. 集成窗口在合并后更新 `MainWindow.xaml` 的 Header/Footer 版本文案，移除 `P0-03` 专属措辞并反映 TODAY Mock。
2. 若希望 Today 专属视觉 token 上提至共享 `Resources/DesignSystem`，请由集成窗口审查后在公共文件中统一修改；本 Slice 不越界处理。
3. 集成后请串行执行整仓库的 locked restore、Release 构建、测试和手动 WPF 验收。
