# P0-02 Host + MVVM + App Shell 实施简报

## Goal

建立真正可启动的 ReminNote WPF 主程序、Generic Host 组合和最小 MVVM 导航壳，为后续设计系统与高保真 Mock 提供稳定入口。

## In Scope

- WPF Application 和 MainWindow。
- Windows 主程序的 Generic Host 生命周期。
- Agent/Bootstrap 的最小 Host composition smoke path。
- CommunityToolkit.Mvvm 的 ViewModel 和导航命令。
- TODAY/ANIME 两个占位导航页。
- 中央包版本和 lock 文件更新。
- 活文档和 P0-02 手动测试说明更新。

## Out of Scope

- SQLite、EF Core、真实 Task、Reminder、Agent IPC。
- Bangumi、网络 Provider、Anime 数据。
- P0-03 Design System、P0-04 TODAY 高保真 Mock。
- 真实生产数据、开发数据库或 Token 读取。

## Product Rules

- 导航壳不代表业务功能已实现。
- TODAY 和 ANIME 只提供产品信息架构的入口。
- Core 仍不得依赖 WPF、EF Core、Serilog 或 Windows API。
- 所有新增依赖必须集中管理并锁定版本。

## Reuse Research

- `CommunityToolkit.Mvvm` 8.4.2：MIT，Microsoft/.NET Foundation 维护，提供 ObservableObject 与 RelayCommand。
- `Microsoft.Extensions.Hosting` 10.0.11：MIT，Microsoft 维护，提供 Generic Host、DI 与生命周期。
- 本 Slice 没有找到需要引入其他依赖的理由。

## Acceptance

- WPF 主窗口可启动。
- 默认显示 TODAY，占位导航可切换至 ANIME 并返回。
- Agent/Bootstrap Host 项目可构建并能完成启动/停止 smoke path。
- locked restore、Release build、test 命令通过且无新警告。
- Core 没有 UI、Windows 或基础设施依赖。
- 不创建真实数据文件或网络访问。

## Manual Test

详见 `TESTING.md` 中的 P0-02 手动测试 1-4。

## Risks

- 当前窗口样式是壳级基础布局，视觉系统留给 P0-03。
- 当前页面是占位内容，不能作为 Task/Anime 业务可用性的验收依据。
- WPF 运行仍依赖 Windows 桌面环境；CI 只负责构建和测试。
