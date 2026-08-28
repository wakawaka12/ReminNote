# ReminNote 产品规则

完整产品规则以 `ReminNote_MASTER_DEVELOPMENT_PLAN.md` 为准。本文件记录当前 Slice 实际采用的规则。

## 当前阶段

P0 已完成并通过用户确认；当前执行 P1 Task core、SQLite persistence、application boundary 和集成验收。本阶段保留已完成的 WPF/TODAY/ANIME/Widget Mock 展示，不把 P0 Mock Quick Add 当作真实持久化入口；真实 Task UI、提醒、Agent IPC、网络和 P2 TODAY/Widget 体验仍不在 P1。

## 已冻结的核心边界

- ReminNote 是 Windows 桌面端的提醒与个人记录应用。
- TASK 是计划，不是时间追踪；时间形状只有 `ANYTIME`、`TIME`、`RANGE`。
- 计划时间与提醒时间是两个独立概念。
- 生产数据、真实 Token 和开发数据必须隔离。
- P0-P3 范围已冻结；未到对应 Slice 的功能进入 Backlog，不提前实现。

## P0-02 规则

- TODAY 与 ANIME 只作为导航入口和占位页面存在。
- 当前页面不读取 SQLite、开发数据库、网络 Provider 或真实用户数据。
- 页面状态由 ViewModel 持有，窗口通过 Generic Host 和 DI 创建。
- 导航命令只负责切换壳内页面，不承载业务写入。

## Slice 规则

每个 Slice 必须保持小而可验证，完成后报告改动、构建/测试结果、手动测试步骤与已知限制。没有用户明确指示，不自动进入下一个 Slice。
## P0-03 规则

- Design System 使用原生 WPF 资源字典、样式和控件模板。
- 颜色、文字层级、间距和圆角以资源键集中定义，页面不重复散落同类视觉常量。
- 当前只提供亮色基线；完整主题、用户背景、对比度和动画设置不在本 Slice 内。
- 导航按钮必须保留鼠标悬停、按下、禁用和键盘焦点反馈。

## P0-07 规则

- Shell、TODAY、ANIME 和 Widget 的用户可见文案必须通过稳定资源键或资源格式化边界提供，默认显示简体中文；本 Slice 不实现运行时语言切换。
- 可交互控件必须保留可见键盘焦点，并为关键按钮、输入框、页面区域提供 `AutomationProperties.Name` 或 `HelpText`。
- P0 Mock 仍只使用进程内示例数据；P0-07 不引入数据库、网络、IPC、提醒调度、真实国际化切换或 P1+ 功能。
- P1 的真实 Task 写入经过 application boundary 和 Infrastructure SQLite；P0 Mock 仍只使用进程内示例数据。
- `scripts/verify-p0-07.ps1` 是 `scripts/test.ps1` 之外的静态/配置门禁；P1 业务测试必须以真实 xUnit v3 executable 的非零退出码检查为证据。
