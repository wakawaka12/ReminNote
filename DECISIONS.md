# ReminNote 决策记录

## ADR-0001：P0-01 采用最小 Windows 单仓库基础

- 日期：2026-08-27
- 状态：已接受
- 决策：使用一个 Solution，建立 Core、Infrastructure、Windows、Agent、Bootstrap 五个项目；不创建额外的 Application 项目。
- 原因：与开发计划的 P0-01 和长期项目边界一致，避免在没有业务需求前增加层级和依赖。
- 结果：Core 无项目引用；Infrastructure 依赖 Core；Windows 和 Agent 依赖 Core/Infrastructure；Bootstrap 不依赖业务项目。

## ADR-0002：P0-01 不引入 NuGet 依赖

- 日期：2026-08-27
- 状态：已被 P0-02 局部更新
- 决策：P0-01 不引入依赖；需要 MVVM 和 Host 时在 P0-02 以集中版本和 lock 文件引入。

## ADR-0003：开发数据默认隔离

- 日期：2026-08-27
- 状态：已接受
- 决策：开发数据固定放在仓库 `.devdata/`，默认清理脚本只处理构建输出。

## ADR-0004：P0-02 使用 Microsoft CommunityToolkit.Mvvm

- 日期：2026-08-27
- 状态：已接受
- 决策：Windows UI 使用 `CommunityToolkit.Mvvm` 8.4.2 的 `ObservableObject` 和 `RelayCommand`。
- 原因：Microsoft 维护、MIT 许可证、轻量且直接覆盖当前 MVVM 基础需求，避免在项目内重复实现属性通知和命令。
- 限制：本 Slice 只使用基础类型，不提前引入消息总线或复杂状态管理。

## ADR-0005：P0-02 使用 Generic Host 组合桌面应用

- 日期：2026-08-27
- 状态：已接受
- 决策：WPF Main App、Agent、Bootstrap 都采用 `Microsoft.Extensions.Hosting` 进行依赖注入和生命周期组合；当前不引入长驻后台循环。
- 原因：与长期 Agent/Main App 架构一致，同时保持 P0-02 不接入业务基础设施。

## ADR-0006：P0-03 使用原生 WPF ResourceDictionary 作为 Design System

- 日期：2026-08-27
- 状态：已接受
- 决策：颜色、间距、文字层级、壳层表面和导航控件样式放入 Windows 项目的原生 WPF ResourceDictionary，由 App.xaml 合并。
- 原因：符合计划中“原生 WPF + 自定义 ReminNote Design System”的方向；不增加大型 UI 框架依赖，并可让 Main App 与后续 Widget 复用同一组资源键。
- 结果：当前只提供亮色基线和基础键盘焦点反馈；主题切换、用户背景和完整无障碍设置留到后续 Slice。

## ADR-0007：P0 功能模块使用独立资源与 DI 注册入口

- 日期：2026-08-27
- 状态：已接受
- 决策：TODAY 与 ANIME 各自拥有 Features 子目录、资源字典、ViewModel 和 DI 注册扩展；共享 App Shell 只引用稳定入口。
- 原因：支持独立 worktree 并行开发，减少 MainWindow、App 和共享 ViewModel 的合并冲突。
- 限制：这是 P0 的静态模块边界，不建立运行时插件发现或通用模块框架。

## ADR-0008：P0-06 Widget 复用 Design System 的资源所有权

- 日期：2026-08-27
- 状态：已接受
- 决策：P0-06 Widget 复用 `ReminNote.Windows/Resources/DesignSystem/**` 的原始 XAML 文件；Widget 项目通过项目文件 Link 引入这些文件，不复制令牌，也不反向引用整个 `ReminNote.Windows` 程序集。
- 原因：保持 Main App 与 Widget 使用同一份视觉令牌源，避免复制后发生漂移；同时维持 Widget 的独立构建边界，不把主窗口壳层拖入 Widget。
- 结果：P0-06 必须验证 Link 指向的源文件存在且没有复制的 Design System 副本；若未来需要独立程序集，再以新的 ADR 取代本决策，不在功能窗口内临时拆分公共资源。

## ADR-0009：i18n foundation 延后至 P0-07

- 日期：2026-08-27
- 状态：已接受
- 决策：P0-03 至 P0-06 的本地 Mock 保持简体中文默认显示，不在当前 Slice 引入完整国际化运行时；P0-07 必须建立稳定资源键，提取 Shell 与功能页面的用户可见文案，并将“简体中文默认显示、ViewModel/业务逻辑不散落用户文案”纳入验收。
- 原因：主计划将 `resource/i18n foundation` 列入 P0，且 P0-07 明确承担 stabilization 与 i18n；当前并行 Mock 阶段先保持功能边界和视觉验收稳定，避免现在引入超出 P0-03 的框架改造。
- 结果：当前 P0-03 不因该决策扩大实现范围；在宣告整个 P0 完成前，必须完成资源键边界和默认语言验收，并同步处理既有 Shell/Features 硬编码文案。

## ADR-0010：P0-07 使用稳定默认资源键，不引入运行时语言切换

- 日期：2026-08-28
- 状态：已接受
- 决策：Shell、TODAY、ANIME 和 Widget 的默认用户可见文案集中在 `UiText.resx`，通过 `UiText.Get`/`UiText.Format` 使用；默认语言为简体中文，本 Slice 不加入语言切换框架。
- 原因：满足 P0 的 resource/i18n foundation 与 Mock 稳定性要求，同时不扩大到 P1+ 的偏好设置或完整本地化系统。
- 结果：Widget Link 复用同一资源源；后续新增文案必须先增加稳定键和默认值。

## ADR-0011：空测试程序集由独立仓库门禁补充

- 日期：2026-08-28
- 状态：已接受
- 决策：保留现有 `dotnet test` 流程，但在 CI 和 `scripts/test.ps1` 中追加 `verify-p0-07.ps1`，检查资源、项目锁文件、解决方案登记、UI Automation 标记和数据安全边界。
- 原因：当前 P0 没有测试项目；不能凭空添加与产品行为无关的假测试，也不能把空测试成功描述为业务覆盖。
- 限制：该脚本是静态/配置门禁，不能替代真实 WPF UI、读屏和键盘人工验收；后续测试项目建立后应补充真实测试并保留门禁。
