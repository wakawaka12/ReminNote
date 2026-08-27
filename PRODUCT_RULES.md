# ReminNote 产品规则

完整产品规则以 `ReminNote_MASTER_DEVELOPMENT_PLAN.md` 为准。本文件记录当前 Slice 实际采用的规则。

## 当前阶段

当前执行 P0-03 ReminNote Design System foundation。本阶段保留 P0-02 的可启动 WPF 应用壳、Host 组合和最小 MVVM 导航；本阶段只整理可复用视觉资源，不接入真实任务、数据库、提醒或动画数据。

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
