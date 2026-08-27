# P0-03 ReminNote Design System 基础实施简报

## Goal

把 P0-02 的壳级视觉常量整理为可复用的原生 WPF Design System 资源，为 TODAY、ANIME 和后续 Widget Mock 提供同一套视觉基础。

## In Scope

- 颜色与画刷令牌。
- 页面、卡片、导航和壳层的间距/圆角令牌。
- 中英文 UI 文字层级的基础样式。
- 导航按钮的鼠标悬停、按下、禁用和键盘焦点状态。
- App Shell 接入资源字典，保持 P0-02 导航行为。
- 更新活文档和 P0-03 手动验收说明。

## Out of Scope

- TODAY/ANIME 业务 Mock 和真实数据。
- Widget、Quick Add、任务状态交互。
- SQLite、网络 Provider、Reminder、IPC。
- 完整主题切换、用户背景、动画策略和国际化运行时。
- 引入 Material/HandyControl 等大型 UI 框架。

## Product Rules

- 视觉方向保持明亮、干净、日式游戏/JRPG 信息面板风格。
- Main App 与未来 Widget 使用同一套令牌命名。
- P0-03 仍然是本地 UI 壳，不读取生产数据、开发数据库、Token 或网络。
- 设计系统的可访问性基础必须在控件层预留，不能依赖后续重写整个 UI。

## Reuse Research

- 继续使用 WPF 原生 ResourceDictionary、Style、ControlTemplate 和 DynamicResource。
- 本 Slice 不增加 NuGet 依赖；当前 CommunityToolkit.Mvvm 和 Generic Host 与设计系统无直接耦合。
- 不采用大型第三方 UI 框架，遵守主计划的原生 WPF 约束。

## Acceptance

- App.xaml 只负责合并 Design System 资源，不再持有壳层颜色和导航按钮模板。
- MainWindow 的壳层和占位页使用统一颜色、文字、间距、卡片资源。
- 导航仍可在“今天”和“动画”之间切换。
- 导航按钮可通过 Tab 聚焦，并显示明显焦点边框；悬停和按下状态可见。
- locked restore、Release build、测试和 WPF 启动冒烟通过，且无新警告。
- 不创建真实数据文件，不访问网络，不越过项目目录修改外部文件。

## Manual Test

详见 TESTING.md 中的 P0-03 手动测试 1-5。

## Risks

- 当前资源为单一亮色基线，完整主题/背景/高对比度切换留给后续 P0 Slice。
- WPF 资源字典能否加载由构建和启动冒烟验证；不同 Windows 字体安装情况可能影响字体回退，但不应影响布局功能。
