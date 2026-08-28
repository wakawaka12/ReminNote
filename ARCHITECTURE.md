# ReminNote 架构基线

## 当前项目

```text
src/windows/
  ReminNote.Core
  ReminNote.Infrastructure
  ReminNote.Windows
  ReminNote.Agent
  ReminNote.Bootstrap
  ReminNote.Widget
```

## 引用边界

```text
ReminNote.Core                 (无项目引用)
ReminNote.Infrastructure  ->  ReminNote.Core
ReminNote.Windows          ->  ReminNote.Core, ReminNote.Infrastructure
ReminNote.Agent            ->  ReminNote.Core, ReminNote.Infrastructure
ReminNote.Bootstrap        ->  (无业务项目引用)
ReminNote.Widget            ->  (独立 WPF Widget；仅 Link 共享 Design System 与默认 UI 文案资源)
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

## P2 本地 Task loop 边界

P2 在 Agent 迁移前允许 Main 和 Widget 通过同一组
`ITaskApplicationService`、`ITaskQueryService` 与 `ITodayQueryService` 访问开发
SQLite。`TaskWorkspace` 为每个操作创建自己的 DbContext；ViewModel 只能经过
这些应用/查询边界，不得直接访问 EF entity、DbContext、SQL 或数据库路径。

完整本地读-改-写由命名跨进程 `Semaphore(1, 1)` 保护，名称为
`Local\\ReminNote.P2.TaskWrite`；等待超时返回稳定的 busy 失败，不静默覆盖原数据。
实现使用 Semaphore 而不是线程归属型 Mutex，是因为写租约会跨越 `await` 并可能由
后续 continuation 线程释放；这不改变 P2 的跨进程写保护契约。该本地写门仍只是
P2 临时边界，P2.5 必须替换为 Agent 命令串行化、版本/冲突控制和 Change Journal。

P2 的持久化对象限于 `task_history`、单例 `app_settings`、`tasks.sort_order` 和
`tasks.continued_from_task_id`；不加入 Reminder、Anime、Sync、WAL、Change Journal、
Agent 或 IPC。Widget 正式入口已接入 `TaskWorkspace` 和短周期 Today 刷新；Main 的
正式 `App.xaml.cs`/Today DI 也已在总成接管期间接通 live 组合（真实 `TaskWorkspace`
加校验 `--repo-root`，见提交 `da2fc4d`），TODAY 页面具备改期与同日期同分组排序的
用户入口；剩余总成复核项为 Main/Widget 双宿主真实进程人工验收，不是新的业务边界。

## P0-03 Design System

Windows UI 的共享视觉资源位于：
src/windows/ReminNote.Windows/Resources/DesignSystem/
  Colors.xaml
  Spacing.xaml
  Typography.xaml
  Controls.xaml

App.xaml 只负责按顺序合并这些资源字典。页面通过资源键使用颜色、文字层级、间距、圆角和壳层样式；资源不包含业务数据或业务规则。控件模板负责导航按钮的基本交互反馈和键盘焦点可见性。

本 Slice 使用 DynamicResource 连接可替换的颜色/控件资源，为未来主题能力保留替换点，但当前不实现主题切换。

## P0-07 稳定化边界

- 默认 UI 文案位于 `src/windows/ReminNote.Windows/Resources/Localization/UiText.resx`，稳定键与读取/格式化边界位于同目录的 `UiText.cs`；Widget 通过项目文件 Link 复用同一份资源源，不复制字典。
- ViewModel 可以使用 `UiText.Get`/`UiText.Format` 生成动态状态文案，但不应继续新增散落在 View/XAML 外的用户可见文本。
- Shell、TODAY、ANIME 和 Widget 的关键交互控件使用 `AutomationProperties`；共享焦点令牌为 `AccessibleFocusBrush`。这是 P0 的基础可访问性门槛，不等同于完整无障碍认证。
- P0-07 不改变 P0 Mock 的业务边界，不新增数据库、网络、IPC 或真实语言切换。

## P0 并行模块边界

TODAY 与 ANIME 的 ViewModel、资源字典和 DI 注册入口分别位于 Features/Today 与 Features/Anime。App 只调用稳定的模块注册入口并合并固定资源字典路径，MainWindow 只保留共享导航壳。

独立任务的文件所有权与合并规则见 docs/PARALLEL_DEVELOPMENT.md。该边界只用于 P0 模块协作，不是通用插件系统。
