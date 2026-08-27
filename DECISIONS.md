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
