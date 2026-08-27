# ReminNote 架构基线

## 当前项目

```text
src/windows/
  ReminNote.Core
  ReminNote.Infrastructure
  ReminNote.Windows
  ReminNote.Agent
  ReminNote.Bootstrap
```

## 引用边界

```text
ReminNote.Core                 (无项目引用)
ReminNote.Infrastructure  ->  ReminNote.Core
ReminNote.Windows          ->  ReminNote.Core, ReminNote.Infrastructure
ReminNote.Agent            ->  ReminNote.Core, ReminNote.Infrastructure
ReminNote.Bootstrap        ->  (无业务项目引用)
```

Core 必须独立于 WPF、EF Core、Serilog 和 Windows API。Infrastructure 承担持久化、网络和 Provider 实现。Bootstrap 只负责进程启动/激活、崩溃恢复、安全模式及未来的版本切换，不承载业务逻辑，也不直接操作业务数据库。

## P0-02 Host 组合

- WPF Main App 使用 `Microsoft.Extensions.Hosting` 创建并管理 Host 生命周期。
- `MainWindow`、`MainWindowViewModel` 和两个壳页面 ViewModel 通过 DI 注册。
- Agent 和 Bootstrap 建立可启动、可停止的 Host 组合骨架，但当前不启动长驻后台循环。
- Host 负责组合对象；业务规则仍应进入 Core 或明确的应用服务，不放入 `App.xaml.cs`。

## P0-02 MVVM

- Windows UI 使用 `CommunityToolkit.Mvvm` 的 `ObservableObject` 和 `RelayCommand`。
- ViewModel 不直接操作 WPF 控件。
- 当前导航模型只服务于壳验证，不代表最终 Task/Anime 领域模型。

## 后续架构约束

P2.5 完成后，Agent 是唯一业务写入者；Main App 通过 IPC 发命令并使用安全的只读查询路径。

当前 Slice 不加入 SQLite、Reminder、IPC、Anime 网络或业务领域实现。

## P0-03 Design System

Windows UI 的共享视觉资源位于：
src/windows/ReminNote.Windows/Resources/DesignSystem/
  Colors.xaml
  Spacing.xaml
  Typography.xaml
  Controls.xaml

App.xaml 只负责按顺序合并这些资源字典。页面通过资源键使用颜色、文字层级、间距、圆角和壳层样式；资源不包含业务数据或业务规则。控件模板负责导航按钮的基本交互反馈和键盘焦点可见性。

本 Slice 使用 DynamicResource 连接可替换的颜色/控件资源，为未来主题能力保留替换点，但当前不实现主题切换。
