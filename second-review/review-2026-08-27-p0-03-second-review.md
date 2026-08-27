# ReminNote P0-03 Design System 二审裁决

- 审查日期：2026-08-27
- 审查范围：`codex/p0-integration` 当前 P0-03 Design System 实现、指定一审报告列出的全部主发现及偏差项；只核对代码、XAML、项目配置、脚本、活文档、开发计划和测试证据。
- 一审源文件：`D:\Anime\reviews\review-2026-08-27-p0-03.md`
- 被审代码对应集成提交：`54db574ea7961df33dfd3e2ca934c26bfd25e955`（`chore: prepare parallel P0 workstreams`）
- 被审提交链：`098ff23afef31ef2d088a2dc5dfe0125d18983e5` → `54db574ea7961df33dfd3e2ca934c26bfd25e955`
- 审查依据：`ReminNote_MASTER_DEVELOPMENT_PLAN.md`、`ARCHITECTURE.md`、`PRODUCT_RULES.md`、`DEVELOPMENT.md`、`TESTING.md`、`DECISIONS.md`、`docs/slices/P0-03-implementation-brief.md`、`docs/PARALLEL_DEVELOPMENT.md`及当前提交树。

## 总体裁决

一审“批准、无 Critical/阻塞项”的方向基本成立，但应改读为“实现可有条件通过代码二审”，不能据此宣告整个 P0 已完成。二审没有发现 BLOCKER 或 MAJOR，也没有发现安全、数据丢失或数据损坏风险。

需要交回集成窗口处理的事项只有两类：

- `DEVELOPMENT.md` 和 `TESTING.md` 的事实/路径错误应修正，否则会误导手工验收和排障。
- i18n foundation 可按计划推迟实现到 P0-07，但应在 P0-07 前明确记录并落实资源键边界；主计划把 i18n 列为 P0 范围，不能长期以“完整国际化运行时不在当前 Slice”为由遗漏。

其余项目是非阻断的可选改进或下一 Slice 的前置决策。主计划要求用户手工验证视觉层级和主要交互后才能完成 P0（`ReminNote_MASTER_DEVELOPMENT_PLAN.md:2947-2951`）；一审报告提供了启动冒烟声明，但没有可复核的手工交互记录，因此本报告不把“代码二审通过”升级为“P0 完成”。

## 裁决汇总

| 编号 | 对应一审项 | 二审裁决 | 校正严重级别 | 处理属性 | 阻断当前 P0-03 |
|---|---|---|---|---|---|
| F-01 | 1. 未使用令牌、`TextBlockBaseStyle` | 部分接受 | MINOR（令牌清理部分为 STYLE/NIT） | 可选改进 | 否 |
| F-02 | 2. Widget 复用策略；偏差项 3 | 部分接受 | MINOR | P0-06 前置决策 | 否 |
| F-03 | 3. `ActiveTitle` 通知 | 部分接受 | MINOR | 可选改进 | 否 |
| F-04 | 4. 标题硬编码重复 | 部分接受 | MINOR | 可选改进，i18n 时统一处理 | 否 |
| F-05 | 5. 文档与实现不一致 | 接受 | MINOR | 必须修复文档 | 否（但应在手工验收交付前修正） |
| F-06 | 6. PATH 环境问题 | 接受 | MINOR | 可选人工环境修复 | 否 |
| F-07 | 7. 过时注释 | 接受 | STYLE/NIT | 可选改进 | 否 |
| F-08 | 偏差项 1. i18n foundation 未实施 | 部分接受 | MINOR | 必须在 P0-07/P0 完成前处理或正式记录 | 否（不阻断 P0-03 实现） |
| F-09 | 偏差项 2. 页面模板重复 | 部分接受 | STYLE/NIT | 可选改进/Backlog | 否 |

严重级别沿用主计划 `ReminNote_MASTER_DEVELOPMENT_PLAN.md:2649-2674` 的四级定义。以上“必须修复”指阶段交付约束，不表示当前实现存在阻断性运行时故障。

## 逐项复核

### F-01：未使用颜色令牌与未收编的基础文字样式

- 发现编号：F-01
- 一审主张：`Colors.xaml` 中 Accent、Focus、Disabled 令牌及其画刷没有实际消费者；`TextBlockBaseStyle` 定义后没有 `BasedOn` 使用，其他文字样式重复 `FontFamily` 和 `SnapsToDevicePixels`。
- 证据（文件与精确行号）：
  - `src/windows/ReminNote.Windows/Resources/DesignSystem/Colors.xaml:11-13` 定义 Accent 颜色，`:18-19` 定义 Focus/Disabled 颜色，`:28-30` 和 `:35-36` 定义对应画刷。当前提交树搜索不到这些画刷在 `Colors.xaml` 之外的消费者；颜色键本身仍被同文件中的画刷内部引用，因此“一律 0 引用”应更准确地表述为“无外部消费者”。
  - `src/windows/ReminNote.Windows/Resources/DesignSystem/Controls.xaml:45-64` 实际使用的是 `SidebarFocusBrush`、`SidebarPressedBrush` 和禁用态 `Opacity=0.55`，没有使用 `FocusBrush` 或 `DisabledBrush`。
  - `src/windows/ReminNote.Windows/Resources/DesignSystem/Typography.xaml:3-9` 定义 `TextBlockBaseStyle`；`:11-23`、`:25-35`、`:37-49`、`:51-63`、`:65-75`、`:77-89`、`:91-103`、`:105-117`、`:119-133`、`:135-147`、`:149-163`、`:165-175` 的 12 个子样式均未声明 `BasedOn`，并重复了这两个 Setter。一审所称“11 个”少算了一个。
- 二审裁决：部分接受
- 校正后的严重级别：MINOR；其中“未使用令牌”本身为 STYLE/NIT
- 裁决理由：一审观察到的“无外部消费者”和文字样式重复均成立，但“死代码”结论过强。Accent 令牌与主计划未来的全局 accent 方向（`ReminNote_MASTER_DEVELOPMENT_PLAN.md:1554-1559`）相容，保留未使用的基础令牌未造成运行时错误；删除它们还可能改变未来资源键契约。`TextBlockBaseStyle` 确实是当前未使用的重复抽象，但只影响维护成本，不影响 P0-03 验收或启动。
- 建议的最小处理方案：不强制删除 Accent/Focus/Disabled 键；若团队确认它们不是预留契约，可在一次小型清理中删除整组键及只由其引用的画刷。对 `TextBlockBaseStyle`，可给上述 12 个样式添加 `BasedOn="{StaticResource TextBlockBaseStyle}"` 并删除重复的两个 Setter，随后做一次 Release 构建和启动验证；若采用源生成/样式改法出现编译问题，应保持当前实现并记录，不要改变视觉行为。
- 受影响文件：`src/windows/ReminNote.Windows/Resources/DesignSystem/Colors.xaml`、`src/windows/ReminNote.Windows/Resources/DesignSystem/Typography.xaml`；`Controls.xaml`仅为现状证据。
- 是否阻断当前阶段：否。
- 处理属性：可选改进；`TextBlockBaseStyle` 收编适合在下次触及 Typography 时顺手完成，令牌删除应先确认资源键契约。

### F-02：Widget 复用 Design System 的物理承载/引用策略未决

- 发现编号：F-02
- 一审主张：P0-03 声称服务后续 Widget，但资源实际位于 Windows 项目；未来若 Widget 反向引用 Windows、复制字典或抽离资源，都涉及架构问题，建议现在记录 ADR-0008。
- 证据（文件与精确行号）：
  - `docs/slices/P0-03-implementation-brief.md:3-5` 将 Widget 写入目标，`:16-22` 又明确把 Widget 列为当前 Slice 的 Out of Scope，`:24-29` 要求 Main App 与未来 Widget 使用同一套令牌命名。
  - `ARCHITECTURE.md:45-56` 将当前 Design System 物理位置定为 `ReminNote.Windows/Resources/DesignSystem/`，并要求保留可替换资源连接点；`DECISIONS.md:38-44` 只记录原生 ResourceDictionary 方案和未来复用意图，没有确定 Widget 的程序集/文件共享策略。
  - `docs/PARALLEL_DEVELOPMENT.md:42-51` 把 Widget 项目和 `src/windows/ReminNote.Widget/**` 定义为未来独立窗口，并要求先用项目文件直接构建、由集成窗口统一加入 Solution。当前提交树没有 `src/windows/ReminNote.Widget` 路径，也没有已经发生的反向项目引用或复制字典证据；当前项目清单见 `ARCHITECTURE.md:5-12`。
- 二审裁决：部分接受
- 校正后的严重级别：MINOR
- 裁决理由：缺少明确的未来共享策略这一事实成立，且应在 P0-06 开工前解决；但一审把未来可能发生的反向引用/复制行为写成了当前架构缺陷，证据不足。P0-03 明确不实现 Widget，因此该缺口不阻断当前 Slice。
- 建议的最小处理方案：在 P0-06 开始前补一条 ADR 或 P0-06 brief，明确共享资源的所有权、访问方式、禁止反向引用/复制的验证规则，并定义一个最小共享资源验证。当前不提前拆分项目、不复制字典。
- 受影响文件：当前为 `ARCHITECTURE.md`、`DECISIONS.md`、`docs/slices/P0-06-*` 的决策记录；未来涉及 `src/windows/ReminNote.Widget/**` 与 Design System 资源。
- 是否阻断当前阶段：否；但应作为 P0-06 实施前置条件。
- 处理属性：阶段性必须项（当前只需记录/决策，不要求修改 P0-03 代码）。

### F-03：`ActiveTitle` 的通知依赖 `Navigate()` 手动补发

- 发现编号：F-03
- 一审主张：`ActiveTitle` 是 `CurrentPage` 的派生值，当前在 `Navigate()` 末尾手动通知；未来其他路径赋值可能忘记通知，建议使用 `[NotifyPropertyChangedFor]` 或把通知放入 setter。
- 证据（文件与精确行号）：
  - `src/windows/ReminNote.Windows/ViewModels/MainWindowViewModel.cs:33-37` 的 `CurrentPage` setter 为 `private`，通过 `SetProperty` 更新；`:39-44` 的 `ActiveTitle` 派生自 `CurrentPage`；`:46-60` 显示当前唯一导航路径在赋值后手动调用 `OnPropertyChanged(nameof(ActiveTitle))`。
  - `src/windows/ReminNote.Windows/MainWindow.xaml:60-61` 绑定 `ActiveTitle`，因此当前导航切换的通知链是完整的。
  - `src/windows/ReminNote.Windows/MainWindow.xaml.cs:8-12` 和 `App.xaml.cs:21-29` 显示窗口在构造后绑定 ViewModel；构造函数中的 `:26` 直接设置初始 Today 页面不会漏掉首次绑定所需的通知。
- 二审裁决：部分接受
- 校正后的严重级别：MINOR
- 裁决理由：当前行为正确；`private set` 使外部路径无法直接绕过 setter，一审描述的风险实际上限于未来在同一类中新增赋值路径时忘记补通知。它是可维护性风险，不是当前 P0-03 的功能缺陷。
- 建议的最小处理方案：优先把通知放入当前 setter，在 `SetProperty` 成功改变后调用 `OnPropertyChanged(nameof(ActiveTitle))`，并删除 `Navigate()` 中的手动通知；或在确认当前语言/Toolkit 配置支持后采用源生成属性。不要为此改动导航模型或 Features。
- 受影响文件：`src/windows/ReminNote.Windows/ViewModels/MainWindowViewModel.cs`。
- 是否阻断当前阶段：否。
- 处理属性：可选改进；若本文件继续变更，建议一并处理。

### F-04：导航标题与占位页标题重复硬编码

- 发现编号：F-04
- 一审主张：Today/Anime 标题分别出现在导航项、`ActiveTitle` switch 和两个占位页资源中，建议至少在共享壳内从 `NavigationItems` 查询。
- 证据（文件与精确行号）：
  - `src/windows/ReminNote.Windows/ViewModels/MainWindowViewModel.cs:20-24` 在导航项中定义“今天/动画”，`:39-44` 在 `ActiveTitle` 中再次定义。
  - `src/windows/ReminNote.Windows/Features/Today/TodayResources.xaml:4-15` 与 `src/windows/ReminNote.Windows/Features/Anime/AnimeResources.xaml:4-15` 分别再次定义页标题和占位文案；这两个目录受 `docs/PARALLEL_DEVELOPMENT.md:20-40` 的 TODAY/ANIME 窗口所有权约束。
  - 一审称“三处”不精确：按文件位置至少是 ViewModel 两处加两个资源文件四处；按每个标题字符串出现次数则各有三次。
- 二审裁决：部分接受
- 校正后的严重级别：MINOR
- 裁决理由：重复确实存在，但 P0-03 的当前页面仍是占位页，且 i18n 运行时不在本 Slice 内（`docs/slices/P0-03-implementation-brief.md:16-22`）。只修改共享壳不能消除 Features 资源中的重复。另，一审建议的 `First(...)` 若直接使用，元素缺失时会抛异常，并不能实现所称“兜底 ReminNote”。
- 建议的最小处理方案：如要立即去掉共享壳内重复，使用 `FirstOrDefault(...)?.Title ?? "ReminNote"` 等真正有兜底的查询，只改 `MainWindowViewModel.cs`。Feature 页面标题待 i18n foundation 或共享模板决策时统一处理，不跨越当前并行所有权。
- 受影响文件：`src/windows/ReminNote.Windows/ViewModels/MainWindowViewModel.cs`；未来涉及 `Features/Today/TodayResources.xaml`、`Features/Anime/AnimeResources.xaml`。
- 是否阻断当前阶段：否。
- 处理属性：可选改进；与 F-08 的资源化工作合并更合适。

### F-05：开发说明和测试说明与脚本/真实路径不一致

- 发现编号：F-05
- 一审主张：`DEVELOPMENT.md` 描述的 SDK 解析顺序与 `resolve-dotnet.ps1` 相反；`TESTING.md` 的 `D:Anime`、`D:Anime.devdata` 路径写法错误。
- 证据（文件与精确行号）：
  - `DEVELOPMENT.md:24` 声称先用 PATH、再回退标准 x64；实际 `scripts/resolve-dotnet.ps1:5-11` 先检查 `%ProgramFiles%\dotnet\dotnet.exe` 且只有检测到 .NET 10 SDK 才返回，随后 `:13-19` 才遍历 PATH。`scripts/build.ps1:9-17` 和 `scripts/test.ps1:9-17` 均使用这个解析器。
  - `TESTING.md:14` 已正确使用 ``D:\Anime``，但 P0-03 段的 `:45`、`:49` 仍写成 `D:Anime`，`:78` 写成 `D:Anime.devdata`；后一处既缺少反斜杠也缺少 `.devdata` 前的路径分隔。
  - `ReminNote_MASTER_DEVELOPMENT_PLAN.md:3278-3287` 要求 Slice 交付提供准确的构建/测试命令和手工步骤，因此这不是纯排版问题。
- 二审裁决：接受
- 校正后的严重级别：MINOR
- 裁决理由：一审定位准确，且遗漏了 `TESTING.md:49` 的同类路径错误。仓库脚本本身按正确顺序解析 SDK，故不会把该文档错误升级为代码阻断；但错误路径会误导手工验收，错误的 resolver 描述会误导排障。
- 建议的最小处理方案：只修正 `DEVELOPMENT.md:24` 的 resolver 顺序和 PATH 说明；把 `TESTING.md:45`、`:49`、`:78` 统一为带反斜杠的完整路径。无需修改脚本。
- 受影响文件：`DEVELOPMENT.md`、`TESTING.md`。
- 是否阻断当前阶段：否（不阻断代码合入）；应在把 TESTING.md 作为正式交付手册前修正。
- 处理属性：必须修复（文档准确性）。

### F-06：当前主机 PATH 中 x86 dotnet 位于 x64 之前

- 发现编号：F-06
- 一审主张：PATH 中 `C:\Program Files (x86)\dotnet` 排在 `C:\Program Files\dotnet` 前，x86 安装没有 SDK，导致裸 `dotnet` 命令失败；建议人工调整 PATH。
- 证据（文件与精确行号）：
  - 只读环境探测显示 `Get-Command dotnet -All` 的顺序为 `C:\Program Files (x86)\dotnet\dotnet.exe`、`C:\Program Files\dotnet\dotnet.exe`；前者 `--list-sdks` 无输出，后者有 `10.0.300` 和 `10.0.400`。裸 `dotnet --version` 因 `global.json` 要求 `10.0.100` 且当前命令选中无 SDK 的 x86 host 而失败。
  - `global.json:1-6` 固定 `10.0.100`、`rollForward: latestFeature`；`scripts/resolve-dotnet.ps1:5-19` 会优先选择实际存在 .NET 10 SDK 的 x64 路径。二审只读调用 `Resolve-DotNetPath` 返回 `C:\Program Files\dotnet\dotnet.exe`，与一审判断一致。
- 二审裁决：接受
- 校正后的严重级别：MINOR
- 裁决理由：环境事实成立，但它不是当前仓库代码的失败路径：构建、测试和运行脚本都调用 resolver。它影响裸 `dotnet`、IDE 或直接命令行体验，不影响脚本设计的预期行为；也没有证据要求修改 `global.json`。
- 建议的最小处理方案：由用户把 x64 dotnet 放到 x86 之前或移除无 SDK 的 x86 条目；在环境未调整前使用仓库脚本。不要为此改动项目文件或 `global.json`。
- 受影响文件：主机 PATH（外部环境）；`scripts/resolve-dotnet.ps1` 仅作为现有缓解证据，无需修改。
- 是否阻断当前阶段：否。
- 处理属性：可选人工环境修复。

### F-07：Windows `AssemblyMarker` 过时注释

- 发现编号：F-07
- 一审主张：`ReminNote.Windows/AssemblyMarker.cs` 仍写着 App Shell 在 P0-02 引入之前，注释已过时。
- 证据（文件与精确行号）：
  - `src/windows/ReminNote.Windows/AssemblyMarker.cs:3-5` 明确写着“before the App Shell is introduced in P0-02”。
  - `src/windows/ReminNote.Windows/App.xaml.cs:18-29` 已创建 Host、解析 `MainWindow` 并显示窗口；`src/windows/ReminNote.Windows/MainWindow.xaml.cs:6-12` 已有实际窗口构造和 DataContext 绑定。
- 二审裁决：接受
- 校正后的严重级别：STYLE/NIT
- 裁决理由：注释与当前事实不符，但只影响阅读，不影响程序集、资源加载或运行行为。当前没有理由为清理一条注释而删除 Marker 文件。
- 建议的最小处理方案：改写为说明该类仅用于标记 Windows UI 程序集；若将来要删除文件，应先确认程序集标记确无工具链/打包用途，并保留全仓搜索证据。
- 受影响文件：`src/windows/ReminNote.Windows/AssemblyMarker.cs`。
- 是否阻断当前阶段：否。
- 处理属性：可选改进。

### F-08：i18n foundation 尚未实施，推迟说明需要校准

- 发现编号：F-08（对应一审“偏差项 1”）
- 一审主张：主计划把 `resource/i18n foundation` 放在 P0 In Scope；当前 UI 文案仍是硬编码中文，建议记录 ADR-0009，推迟到 P0-07 与 stabilization 一并处理。
- 证据（文件与精确行号）：
  - `ReminNote_MASTER_DEVELOPMENT_PLAN.md:2249-2262` 要求从第一天支持 i18n、使用稳定资源键、不要在 ViewModel/业务逻辑散落用户文案；`:2897-2918` 把 `resource/i18n foundation` 列为 P0 In Scope；`:3430-3438` 明确建议 P0-07 名称包含 `i18n`。
  - 当前文案仍散落在 `src/windows/ReminNote.Windows/ViewModels/MainWindowViewModel.cs:22-23`、`:41-43`，`src/windows/ReminNote.Windows/MainWindow.xaml:24-34`、`:62-75`，以及 `Features/Today/TodayResources.xaml:7-15`、`Features/Anime/AnimeResources.xaml:7-15`。`DECISIONS.md:46-52` 以 ADR-0007 结束，没有记录该推迟策略。
  - `docs/slices/P0-03-implementation-brief.md:16-22` 将“完整主题、用户背景、动画策略和国际化运行时”列为 Out of Scope；这支持“当前不做完整运行时”，但不能把 foundation 永久排除。
- 二审裁决：部分接受
- 校正后的严重级别：MINOR
- 裁决理由：缺少 foundation 的事实成立；但一审把 P0-07 概括成“仅 stabilization”不准确，主计划的 P0-07 标题本身包含 i18n。因此“推迟到 P0-07”与主计划建议的切片顺序相容，问题在于当前没有清晰的记录和完成门槛，而不是必须在 P0-03 立即实现完整本地化。
- 建议的最小处理方案：在 `DECISIONS.md` 或 P0-07 brief 中记录推迟范围，并把“建立稳定资源键、提取当前壳与 Features 的硬编码文案、保持当前简体中文默认显示”列为 P0-07 验收项。当前不引入大型本地化框架，不扩大 P0-03 实现范围。
- 受影响文件：`DECISIONS.md`、`docs/slices/P0-07-*`（未来）；最终会涉及当前 `MainWindow.xaml`、共享 ViewModel 和 Features 文案资源。
- 是否阻断当前阶段：否（不阻断 P0-03 实现）；若要宣告整个 P0 完成而未建立资源键边界，则会阻断 P0 完成门槛。
- 处理属性：阶段性必须项；实现可以推迟，但不能无记录地遗漏。

### F-09：Today/Anime 占位页模板重复

- 发现编号：F-09（对应一审“偏差项 2”）
- 一审主张：Today 与 Anime 占位资源模板约 95% 相同；这是并行边界下的暂时结果，当前不修，未来出现 Widget 第三个消费者时再抽取。
- 证据（文件与精确行号）：
  - `src/windows/ReminNote.Windows/Features/Today/TodayResources.xaml:4-21` 与 `src/windows/ReminNote.Windows/Features/Anime/AnimeResources.xaml:4-21` 的 DataTemplate、布局、Margin、样式和 Border 结构相同，主要差异在 `:7`、`:13`、`:15` 的文案。精确“95%”没有独立度量，但“结构高度重复”成立。
  - `docs/PARALLEL_DEVELOPMENT.md:20-40` 将两套 Features 资源分别交给 TODAY/ANIME 窗口；`DECISIONS.md:46-52` 记录 ADR-0007 的独立资源与 DI 注册入口。
  - `docs/slices/P0-03-implementation-brief.md:16-22` 把 TODAY/ANIME 业务 Mock 和 Widget 排除在本 Slice 外，因此当前没有第三个实际消费者。
- 二审裁决：部分接受
- 校正后的严重级别：STYLE/NIT
- 裁决理由：重复结构真实存在，具体百分比不充分；但当前是并行窗口所有权和占位页面阶段的可预期代价。现在抽取共享模板会增加跨窗口耦合，不能作为 P0-03 必修项。
- 建议的最小处理方案：当前保留两份模板，并在 P0-06 或 P0-07 的复用研究中重新评估；当出现第三个真实消费者时，再抽取只包含布局/视觉的共享模板或资源程序集，不把业务 ViewModel 合并进共享层。
- 受影响文件：`src/windows/ReminNote.Windows/Features/Today/TodayResources.xaml`、`src/windows/ReminNote.Windows/Features/Anime/AnimeResources.xaml`；未来可能新增共享占位模板。
- 是否阻断当前阶段：否。
- 处理属性：可选改进/Backlog；当前不得越过并行窗口所有权修改。

## 一审“排除项”复核（不构成新增发现）

### `CornerRadius` 动态资源

`src/windows/ReminNote.Windows/Resources/DesignSystem/Controls.xaml:31-43` 的模板使用 `CornerRadius="{DynamicResource NavigationCornerRadius}"`，`src/windows/ReminNote.Windows/Resources/DesignSystem/Spacing.xaml:15-16` 提供该键；`Controls.xaml:79-90` 也以同样方式使用卡片圆角。当前代码没有显示出资源键缺失或 XAML 语义错误，确认不构成二审问题。

### CI 存在性

`.github/workflows/ci.yml:23-30` 明确执行 locked restore、Release build 和 test，故一审排除“无 CI”正确。需要限定的是，`ReminNote.sln:5-14` 当前只列出五个产品项目，没有测试项目；因此一审的 `test.ps1` “通过”最多证明命令成功退出，不能证明存在自动化测试覆盖。P0-03 当前没有必须自动化的业务逻辑，这一限制不阻断本 Slice。

### 间距令牌的 `DynamicResource` 说法

一审结论“不是问题”可以保留，但其证据描述不准确：`Controls.xaml:18-20`、`:81-90`、`:101-114` 在样式中使用 `DynamicResource`，而 `MainWindow.xaml:23`、`:32`、`:37` 以及 `TodayResources.xaml:5`、`AnimeResources.xaml:5` 使用的是 `StaticResource`。主架构只要求对可替换的颜色/控件资源保留动态连接点（`ARCHITECTURE.md:54-56`），没有要求所有固定间距都必须动态化；因此这里不是当前缺陷。

## 验证证据与限制

- 已执行只读分支/提交核对：当前分支为 `codex/p0-integration`，HEAD 为上述 `54db574...`；`git diff --check 098ff23... 54db574...` 无输出。
- 已执行资源键静态搜索：Accent/Focus/Disabled 只有 Design System 内部定义/依赖，没有外部消费者；`TextBlockBaseStyle` 没有 `BasedOn` 使用。
- 已执行只读 SDK 解析核对：仓库 `Resolve-DotNetPath` 返回 `C:\Program Files\dotnet\dotnet.exe`；当前环境裸 `dotnet` 仍会选中无 SDK 的 x86 host。
- 未在二审中重新运行 `build.ps1`、`test.ps1` 或启动应用。原因是本窗口严格限定只能写入 `D:\Anime\second-review`，而构建/启动会产生或更新 `src/**/bin`、`src/**/obj` 及进程状态。现有 Release 构建产物只能作为存在性线索，不能替代本次独立复跑；一审报告中的“0 警告 0 错误、test 通过、启动冒烟通过”按其已提供的声明记录，但不额外背书为二审复跑结果。
- 没有单列“二审额外发现”：在限定范围内未发现符合“安全、数据丢失或架构阻断”标准的新增问题。

## 交回集成窗口的最小执行顺序

1. 修正 `DEVELOPMENT.md` 和 `TESTING.md` 的文案/路径错误。
2. 决定是否在本次顺手收编 `TextBlockBaseStyle`；不要未经确认删除可能是未来契约的 Accent 令牌。
3. 记录 i18n 推迟范围和 P0-07 验收项；P0-06 开工前再记录 Widget 共享资源策略。
4. 完成主计划要求的用户手工视觉层级、导航、焦点、悬停/按下和缩放验证后，再判断 P0 的整体完成状态。
