# P0-06 Widget 高保真 Mock 实施简报

## Goal

建立一个可独立构建、可手动验收的原生 WPF Widget Mock，用来验证 ReminNote 的 TODAY / ANIME 分页、`PRIMARY -> UPCOMING -> SUMMARY` 信息层级、响应式尺寸变化、Widget 状态和轻量交互。

## In Scope

- 独立的 `ReminNote.Widget` WPF WinExe 项目；
- 通过项目文件 Link 复用 `ReminNote.Windows/Resources/DesignSystem/` 的四个共享资源字典；
- TODAY 与 ANIME 两个独立 Widget 页面，不把两者合并到一个默认页面；
- `LOCKED`、`TEMP_INTERACTIVE`、`UNLOCKED`、`ALERT` 四种可视化 Mock 状态；
- 根据窗口 DIP 宽度切换 Compact / Standard / Expanded 布局；
- TODAY Mock：主任务、Upcoming 队列、完成 / 延后 / 改期反馈；
- ANIME Mock：最近一集、Upcoming 队列、WATCHED / WATCH LATER / SCHEDULE AS TASK 反馈；
- Mock Quick Add 内联流程；
- Reminder Drawer Mock 与手动触发高优先级 Alert；
- 独立项目的 locked restore、Release build 和可重复的基础验证说明；
- 本 Slice Implementation Brief 与 Widget 手动验收说明。

## Out of Scope

- 不加入 `ReminNote.sln`；
- 不修改现有 `ReminNote.Windows`、公共 Design System、集中包版本、根级活文档、脚本、CI 或其他项目；
- SQLite、EF Core、生产 Task 数据、Reminder Scheduler、Reminder 持久化；
- Agent IPC、Tray、Toast、网络 Provider、Bangumi、真实 Token、生产 Widget 配置；
- 真实 Quick Add Parser、真实 Task / Anime application service、跨进程状态同步；
- 多实例、拖拽吸附、多显示器持久化、点击穿透和真正的锁定输入策略；
- P1 及之后的任务领域、提醒领域、动画网络和数据安全迁移能力。

## Product Rules

- Widget 默认分开呈现 TODAY 与 ANIME；
- Widget 信息层级为 `PRIMARY -> UPCOMING -> SUMMARY`；
- 普通提醒只显示 Badge / Overlay / Attention Mock，不随机切换页面；当前仅有手动触发的 Alert Mock；
- UI 保持明亮、干净、日式游戏 / JRPG 信息面板风格，并与 Main App 使用同一套 Design System 源资源；
- 所有内容都是内存中的固定 Mock，不读取 `.devdata`、生产数据、Token，不访问网络；
- 快速添加和状态操作只产生可见的 Mock 反馈，不代表业务写入已实现。

## Reuse Research

- 复用仓库已有原生 WPF `Colors.xaml`、`Spacing.xaml`、`Typography.xaml` 与 `Controls.xaml`，不复制、不修改共享令牌；
- 复用 `CommunityToolkit.Mvvm` 8.4.2 的 `ObservableObject` / `RelayCommand`，版本已由仓库的集中包管理和锁文件覆盖；
- 不引入新的 UI 框架、图标包、网络库、数据库包或动画库；
- Widget 自有视觉变体只放在 Widget 项目资源中，并通过 `DynamicResource` 连接共享颜色、间距、文字和圆角键。

## Acceptance

- `src/windows/ReminNote.Widget/ReminNote.Widget.csproj` 可不依赖 Solution 独立 restore / build；
- Widget 启动后默认显示 TODAY，能切换到 ANIME 并返回；
- Compact / Standard / Expanded 窗口宽度下均保持可辨认的主信息与操作；
- 可手动验证锁定状态切换、任务 / 动画 Mock 操作、Quick Add、Reminder Drawer 与 Alert Overlay；
- Alert 不会自动把页面从 TODAY 切到 ANIME；
- Release 构建无新增警告，locked restore 能通过；
- 不创建 SQLite、Token、网络缓存或生产配置文件；
- 不改变 Solution、现有 Windows 项目、公共 Design System 或其他所有权范围文件。

## Manual Test

详见 `docs/slices/P0-06-manual-test.md`。测试覆盖启动、分页、尺寸响应、Mock 操作、Quick Add、提醒抽屉、Alert 和数据安全边界；每项都给出预期与失败判定。

## Risks

- 当前 `LOCKED` 只提供视觉与状态 Mock，不实现真正的点击穿透或禁止编辑；
- 共享资源以 Link 方式编译，若未来公共字典路径或资源键改动，集成窗口需同步检查 Widget；
- Widget 暂不加入 Solution，主程序集成和 Agent 宿主关系由集成窗口后续处理；
- WPF 运行需要 Windows 桌面环境，Linux / 非 Windows CI 只能验证项目文件和构建条件，不能替代人工视觉验收。

## Integration Requests

- 将 `src/windows/ReminNote.Widget/ReminNote.Widget.csproj` 加入 `ReminNote.sln`；
- 集成时确认 Solution 构建仍加载 Widget 项目，并在 Windows 主机执行一次端到端启动验收；
- 后续若要让 Widget 接入 Agent、Reminder 或真实应用服务，应在对应 Slice 重新设计边界，不沿用本 Mock 的内存交互。
