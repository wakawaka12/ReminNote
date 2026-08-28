# P0-07 P0 polish / accessibility / i18n / CI stabilization 实施简报

日期：2026-08-27
实施分支：`codex/p0-07-stabilization-impl`
实施基线：`codex/p0-integration@4bca2cb`

## Goal

在 P0-04 TODAY、P0-05 ANIME 和 P0-06 Widget 已合入并登记到 Solution 的真实基线上，完成 P0 收尾所需的最小稳定化：

- 为 Shell、TODAY、ANIME、Widget 建立稳定的简体中文资源键边界；
- 让主要键盘路径、焦点反馈和 UI Automation 名称可验证；
- 清理残留的 P0-03 阶段文案，保留已有 Mock 交互语义；
- 加强 locked restore、Release 构建、静态/配置校验及 CI 诊断，避免空 `dotnet test` 被误当作测试覆盖。

## Current Baseline / Audit Facts

- 当前 `HEAD` 为 `4bca2cb`，Widget 已存在于 `ReminNote.sln`，四个 DesignSystem 字典通过 Link 复用；
- 初始 worktree 曾是 detached HEAD，已切换到本命名分支；当前工作树干净；
- `reviews/`、`second-review/` 在当前 worktree 不存在；可见前置审计分支已指出 Shell 过时文案、文案散落、AutomationProperties 缺口、Widget 无辅助技术命名、CI 空测试门禁和 SDK/架构诊断缺口；
- 不把 mock 标题、动画名和示例内容强行当作翻译键；它们是当前 P0 的演示数据夹具。

## In Scope

### Resource / i18n foundation

- 在 Windows 资源目录建立简体中文默认资源表和稳定 key 常量/格式化入口；缺失键有确定的非空回退；
- Shell、TODAY、ANIME、Widget 的 UI chrome、按钮、状态、反馈、空状态、ToolTip 和 AutomationProperties 使用资源键；
- Widget Link 该资源表和轻量访问入口，不复制一份字典；
- C# ViewModel 不再拼接完整 UI 句子，动态数据只作为资源格式化参数；
- 本 Slice 不实现运行时语言切换，不新增英语/日语资源，不把领域模型提前国际化。

### Accessibility / polish

- MainWindow 导航、主内容、TODAY/ANIME 主要动作、筛选/搜索、Quick Add、Widget 页签、抽屉和 Alert 提供稳定 AutomationProperties.Name/HelpText；
- 动态动作名称随完成/恢复、置顶/取消置顶、待看状态等变化同步；
- 统一主要 Button/TextBox 的键盘焦点可见性、Tab 可达性和 Enter/Space 默认行为；
- 为高对比度、较大文本、减少透明度/动画的手测保留清晰边框/文本语义；不实现完整主题编辑器或系统级锁定策略。

### CI / verification

- 让本地脚本和 CI 输出实际 SDK 版本、宿主架构和 Solution 项目；
- 保持 restore `--locked-mode`、build/test `--no-restore` 的边界；
- 增加窄的 `scripts/verify-p0-07.ps1`，验证资源键、项目/Link/lock 文件、Core 边界、Solution Widget 登记、脚本安全和禁止越界能力；CI 将其作为独立硬门禁；
- 明确 `dotnet test` 当前没有测试程序集的限制，不能将其退出码当作业务测试覆盖证明；资源和仓库边界由独立验证脚本实际检查。

## Out of Scope

- SQLite、真实 Task/Anime 数据、Reminder、Toast、IPC、网络 Provider、Bangumi/PAT、同步、生产数据或 P0-08；
- 真实语言切换、完整用户设置、主题编辑器、动态字体系统、屏幕阅读器专用 UI 和像素快照测试；
- 改变 TODAY/ANIME/Widget 既有 Mock 数据、导航层级和交互含义；
- 删除保留令牌、修改 `reviews/` 或 `second-review/` 原件、push/merge/rebase/tag/release。

## Implementation Shape

1. `Resources/Localization/UiText.resx` 是默认简体中文资源源，`UiText.cs` 暴露稳定 key、非空回退和格式化 API；Widget 以 Link 方式复用两者。
2. 先替换 Shell 和四个页面的静态 UI chrome，再替换 ViewModel 的状态/反馈/空状态文案；Mock 标题/说明保留为 fixture。
3. 以现有原生 WPF ResourceDictionary、`AutomationProperties` 和 Button 模板为基础补齐焦点、命名和系统高对比度兼容点，不引入第三方 UI 框架或新增运行时包。
4. 通过 `verify-p0-07.ps1` 做可重复的仓库级检查；CI 先记录 SDK/架构，再执行 restore、Release build、test 和 P0-07 verification。

## Acceptance

- Main App 和 Widget 的用户可见 chrome 无原始资源键、空白名称和 P0-03 专属文案，默认显示简体中文；
- 主要控件可由 Tab 到达并有可见焦点，Enter/Space 与鼠标动作一致；辅助技术名称不是空白、控件类型名或单独图标字符；
- TODAY 的 Quick Add、完成/恢复、置顶、RANGE/NEEDS REVIEW；ANIME 的搜索/栏目/详情/状态动作；Widget 的分页、Quick Add、Drawer、Alert 均保留原有 Mock 行为；
- Solution 含 Widget，四个 DesignSystem/本地化 Link 来源存在且唯一，lock 文件仍在，Core 无 WPF/Windows/EF/Serilog 引用；
- locked restore、Release 全量 build、测试脚本和 P0-07 静态/配置验证在可访问 Windows SDK 环境通过；
- 构建/运行不创建 SQLite、Token、网络缓存、生产配置或真实用户数据。

## Manual Acceptance

1. 主窗口：运行 `./scripts/run.ps1 -Configuration Release`，确认默认 TODAY，导航到 ANIME 再返回；确认 Shell 不再出现 P0-03 文案。
2. 键盘/辅助功能：在主窗口和 Widget 用 Tab 依次经过导航、输入、筛选、详情、页签、抽屉和 Alert 动作，用 Enter/Space 激活；用 Narrator 或 UIA 检查器确认动态按钮名称会随状态改变。
3. TODAY：打开 Quick Add，添加 `准备明天的会议材料`，完成/恢复并置顶/取消置顶；确认 RANGE 显示 `AWAITING RESULT` 并计入 `NEEDS REVIEW`，不出现真实完成时间。
4. ANIME：切换“待看/追番中/本季”，搜索 `星` 和不存在的词，触发已看、待看、追番和模拟提醒；确认只改变进程内 Mock。
5. Widget：独立启动并切换 TODAY/ANIME；调整 Compact（约 400 DIP）、Standard（约 500 DIP）、Expanded（约 680 DIP），测试 Quick Add、提醒抽屉和 Alert；确认 Alert 不切页。
6. 系统设置：在 125%/150% 缩放、高对比度及减少透明度/动画可用设置下重启并滚动页面；焦点、文本和关键动作仍可辨认。
7. 数据安全：关闭应用后检查 `.devdata`、项目目录和工作树，确认没有数据库、Token、网络缓存或生产配置。
8. 启动生命周期：连续启动两次 Main App，确认第二次以 0 退出并唤起已有窗口且不重置页面状态；先启动 Widget 再启动 Main App，确认两个应用可以并行运行，且各自只约束自己的实例。
9. TODAY/ANIME 回归：Quick Add 添加内容后确认顶部有成功反馈、ANYTIME 新条目被选中并自动定位可见；点击 ANIME“查看详情”后确认详情标题、选中反馈和详情卡自动可见。

失败判定：窗口崩溃/XAML 资源错误；Tab 跳过关键控件或焦点不可见；辅助技术读到空名、资源 key 或单独图标；出现 P0-03 误导文案；Mock 被宣称为已保存/真实提醒；发生未授权文件、网络或越界项目改动。

## Risks / Backlog

- 当前字号仍是固定 WPF 样式，P0-07 只能保证在规定手测下主要信息可用；完整字体缩放/用户偏好持久化列入后续设置 Slice。
- 当前没有真实测试项目；`dotnet test` 不提供业务用例覆盖，已用独立静态/配置门禁避免“命令成功即测试覆盖”的假绿。P1/P2 添加领域逻辑后应建立真正的单元测试项目。
- WPF 高对比度、Narrator、透明无边框 Widget 仍需 Windows 桌面人工验收；非桌面环境不能替代该验收。
- SDK feature band 继续遵循 `global.json` 的受控 roll-forward；CI 记录实际版本/架构，但不在本 Slice 擅自改变全局 SDK 政策。
- `clean.ps1 -PurgeDevData` 的链接/重解析点防护如需完整实现，列为后续安全加固；本 Slice 验证默认清理不触及 `.devdata`，并拒绝明显的越界目标。

## Validation Commands

```powershell
./scripts/resolve-dotnet.ps1
./scripts/bootstrap.ps1
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release
./scripts/verify-p0-07.ps1
```

CI 还应执行等价的 `dotnet restore ReminNote.sln --locked-mode`、`dotnet build ... --no-restore`、`dotnet test ... --no-build --no-restore`，并保留 SDK/架构诊断输出。
