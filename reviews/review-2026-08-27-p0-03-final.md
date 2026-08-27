# ReminNote P0-03 终审 — 2026-08-27（一审 vs 二审 对比裁决）

> 终审性质：对一审（`reviews/review-2026-08-27-p0-03.md`）与二审
> （`second-review/review-2026-08-27-p0-03-second-review.md`）进行三方比对。
> 终审无法替用户拍板的地方已单列为「需拍板问题」，其余项给出定稿裁决。

## 终审验证证据（二审未复跑，终审补做）

- 分支：`codex/p0-integration`，HEAD `54db574`，一审以来无代码提交。
- `build.ps1 -Configuration Release`：**0 警告 0 错误**（复跑）。
- WPF 启动冒烟：窗口正常创建，`MainWindowTitle = 'ReminNote'`（复跑）。
- **工作树状态**：`DECISIONS.md` / `DEVELOPMENT.md` / `TESTING.md` 存在**未提交修改**，
  另新增 `reviews/`、`second-review/` 未跟踪目录。详见「需拍板问题 P1」。

## 三方对比与终审裁决

| 编号 | 一审主张 | 二审裁决 | 终审裁决 | 终审理由 |
|---|---|---|---|---|
| F-01 | 死令牌 + TextBlockBaseStyle 死样式 | 部分接受（令牌=STYLE/NIT，文字样式收编有效） | **采纳二审** | 二审对：一审「0 引用」应为「无外部消费者」；Accent/Focus/Disabled 是主题预留契约（主计划 #50 全局 accent + ARCHITECTURE.md「为未来主题保留替换点」），删除反而破坏契约。**令牌不删。TextBlockBaseStyle 收编（BasedOn）改为可选项**，做时全量加 BasedOn 并删重复 Setter。 |
| F-02 | Widget 引用策略缺口，建议现在记 ADR-0008 | 部分接受（前置决策，非缺陷；P0-06 前记录） | **采纳二审，但见 P2** | Worktree 中已被写入 ADR-0008（Link 方案），**超出了二审报告自称的只读范围**，需用户确认归属与内容。 |
| F-03 | 通知手动补发，建议源生成 | 部分接受（建议 setter 内通知的最小方案） | **采纳二审方案 B** | 二审对：`private set` 已限缩风险至同类内未来写点；setter 内 `OnPropertyChanged` 是最小迁移，源生成需 class 改 partial，收益不成比例。 |
| F-04 | 标题三处硬编码，建议 `First(...)` 查询 | 部分接受（`First()` 会抛异常，须用 `FirstOrDefault` + 兜底；「三处」宜按 4 文件口径） | **采纳二审** | 技术纠错成立：`First()` 无兜底。页码口径终审从二审（「至少 4 文件/各有 3 次」）。 |
| F-05 | DEVELOPMENT.md/ TESTING.md 路径错误 | 接受（补出 TESTING.md:49 同类错误） | **采纳二审** | 二审补充到位。 |
| F-06 | PATH 环境问题 | 接受（人工处理） | **无分歧** | — |
| F-07 | AssemblyMarker 注释过时 | 接受（只改注释，不删文件） | **采纳二审** | 删文件无收益，注释改为如实描述。 |
| F-08 | i18n 缺失，建议记 ADR-0009 | 部分接受（P0-07 上还含 i18n；需记录推迟范围与验收项） | **采纳二审** | 二审引用主计划 #76（2259-2261「稳定资源键、ViewModel 不散落文案」）成立——该条与 P0-03 brief Out of Scope 存在**计划内部冲突**，需用户确认 ADR-0009 的处置（见 P3）。 |
| F-09 | 页面模板重复 | 部分接受（STYLE/NIT，Backlog） | **无分歧** | 保留至第三消费者出现。 |

### 一审「排除项」复核

- CornerRadius DynamicResource / ci.yml 存在 / 间距 DynamicResource：二审复核确认非问题，终审一致。
- 二审新增限定：`test.ps1`「通过」仅证明命令成功退出，**无自动化测试覆盖**（sln 无测试项目）。P0-03 无必须自动化的业务逻辑，不阻断，但终审同意该表述应写入交付说明。

## 需拍板问题（按优先级）

### P1. 工作树未提交修改的归属（必须先澄清）

`DECISIONS.md`/`DEVELOPMENT.md`/`TESTING.md` 已被修改（内容对应 F-05 建议 + ADR-0008/0009）。
二审报告声明「本窗口严格限定只能写入 `D:\Anime\second-review`」（报告 :200），**与工作树现状矛盾**。
若这些修改是 Codex 窗口所为，则其执行了修正性写操作却未在报告中声明；若为你批准的手动调整，
请在拍板时确认。终审立场：**修改内容本身可接受**（见 P2/P3 校验），但归属声明必须由你澄清。

### P2. ADR-0008 是否按现文本接受（Link 方案）

终审校验 ADR-0008 内容：Widget 项目通过 csproj `<Page/Resource>` Link 引入
`ReminNote.Windows/Resources/DesignSystem/**` 原始 XAML，不复制、不反向引用整个 Windows 程序集。
- 技术可行：DesignSystem 字典无 `x:Class`，Link 编译无类冲突；缺点是产生两份 BAML（每程序集一份），
  未来改键需重编译两个项目——属「单源双编译」的可接受模式，漂移风险低于复制文件。
- 与 PARALLEL_DEVELOPMENT.md（Widget 窗口先独立项目文件构建、集成窗口入 Solution）兼容。
- ADR 已写明「若未来需独立程序集再以新 ADR 取代」，这点优于一审原建议的「现在提前决策」。
- **终审意见**：按现文本接受（前提是 P1 澄清归属），无需修改。

### P3. i18n 计划内部冲突的处置主确认

主计划 #76（:2257-2261）要求「day one 支持 i18n、稳定资源键、ViewModel/业务逻辑不得散落用户文案」，
与 P0-03 brief Out of Scope（i18n 运行时不做）存在张力；`MainWindowViewModel.cs:22-23,41-43` 也确有
VM 内中文文案。ADR-0009 已将「P0-07 前提取 → 提取 Shell + Features 文案 → 验收含 VM 不散落文案」
记录为验收项。——**终审建议**：接受 ADR-0009 的推迟处理，不需要在 P0-03 改代码；
但如果你的预期是「P0 完成前就要资源化」，请明确推翻 ADR-0009。

## 定稿整改范围（与一审提示词的差异）

| 任务 | 一审提示词 | 定稿（本次修正） |
|---|---|---|
| T1 令牌 | 删除 Accent/Focus/Disabled 死令牌 | **删除取消**：令牌保留（主题预留契约）；只做 TextBlockBaseStyle BasedOn 收编（可选） |
| T3 通知 | 方案 A（partial 源生成）优先 | **方案 B**：CurrentPage setter 内 `OnPropertyChanged`，删除 Navigate 中的手动通知 |
| T3 标题 | `First(...)` | `FirstOrDefault(...)?.Title ?? "ReminNote"` |
| T4 ADR | 现在新增 ADR-0008/0009 | **改为核验**：ADR-0008/0009 已在工作树（待 P1 澄清），内容按 P2/P3 校验后保留；无需重写 |
| T5 文档 | TESTING.md :45、:78 | 追加 **:49**（二审补充） |
| T6 Marker | 删除或改注释 | 只改注释，不删文件 |
| 新增 | — | 交付说明中明确：`test.ps1` 通过 ≠ 有自动化测试（无测试项目）；P0 完成前需用户手工验证（主计划 :2947-2951） |

## 终审总裁决

**实现部分一致通过，条件是执行定稿范围内的整改（TextBlockBaseStyle 收编 + F-03/F-04 修正 +
F-05 文档 + F-07 注释）。令牌不删；ADR-0008/0009 按 P1-P3 你的拍板结果处理。P0-03 可通过，
但按主计划 :2947-2951，P0 整体完成仍需用户手工验收视觉层级与交互。**
