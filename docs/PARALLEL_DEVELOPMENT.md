# ReminNote P0 并行开发约定

本文件约束独立 Codex 任务窗口的文件所有权。每个开发窗口使用独立 Git worktree；禁止多个窗口同时写同一工作目录。

## 集成窗口

分支：codex/p0-integration

P2 继续由该总成窗口承接；P2 的具体所有权以 `docs/slices/P2-00-integration-and-contract-brief.md` 为准。

独占公共文件：

- App.xaml 与 App.xaml.cs
- MainWindow.xaml 与 MainWindow.xaml.cs
- ViewModels 下的共享导航壳
- Resources/DesignSystem
- ReminNote.sln
- Directory.Build.props 与 Directory.Packages.props
- 根级活文档、脚本和 CI
- 本地分支合并与最终综合验证

## TODAY 窗口

分支：codex/p0-04-today

允许修改：

- src/windows/ReminNote.Windows/Features/Today/**
- docs/slices/P0-04-*

禁止修改集成窗口独占文件。需要新依赖或公共设计令牌时，记录请求并交给集成窗口。

## ANIME 窗口

分支：codex/p0-05-anime

允许修改：

- src/windows/ReminNote.Windows/Features/Anime/**
- docs/slices/P0-05-*

本阶段只使用 Mock 数据，不访问 Bangumi 或其他网络 Provider。禁止修改集成窗口独占文件。

## Widget 窗口

分支：codex/p0-06-widget

允许修改：

- src/windows/ReminNote.Widget/**
- docs/slices/P0-06-*

Widget 项目先使用项目文件直接构建；由集成窗口统一加入 Solution。禁止修改集中包版本和现有 Windows 壳层。

## 交付规则

- 开始前阅读主计划和现有活文档。
- 只实现自己的 P0 Slice，不提前实现真实 SQLite、Reminder、IPC 或网络功能。
- 每个窗口独立构建、测试并提供精确手动验收步骤。
- 每个窗口允许创建本地提交；禁止 push、merge、rebase、tag 或发布。
- 完成后把提交哈希、改动文件、验证结果、限制和集成请求报告给集成窗口。
- 集成窗口按 TODAY、ANIME、Widget 顺序审查和本地合并。
- P0-07 必须在三个功能分支合并后串行执行。

## P2 窗口覆盖

P2-01 至 P2-05 的窗口、分支和文件所有权如下；它们覆盖本文件中历史 P0 功能窗口的对应范围：

| 窗口 | 分支 | 允许修改 |
|---|---|---|
| P2-01 Today Read Model | `codex/p2-01-today` | `src/windows/ReminNote.Core/Today/**`、Today query service、新增 Today 测试 |
| P2-02 Parser | `codex/p2-02-parser` | `src/windows/ReminNote.Core/Tasks/Parsing/**`、新增 Parser 测试 |
| P2-03 Main TODAY | `codex/p2-03-main-today` | `src/windows/ReminNote.Windows/Features/Today/**`、新增 Main Today 测试 |
| P2-04 Widget | `codex/p2-04-widget` | `src/windows/ReminNote.Widget/**`、新增 Widget 测试 |
| P2-05 验证 | `codex/p2-05-verification` | 新增测试矩阵、`docs/slices/P2-05-*`、P2 阶段报告 |

P2-01/P2-02 和 P2-03/P2-04 可分别并行。公共契约、Solution、包锁、项目引用、migration、DI、根脚本、`App.xaml.cs`、`MainWindow.*`、根级文档以及所有 merge/build/test/人工验收由 P2-00 总成窗口串行处理。所有窗口只做本地提交，不得 push、merge、rebase、tag 或发布；不得修改 `reviews/`、`second-review/`。

当前合并树已包含 P2 持久化、Today、Parser、Main Today ViewModel、Widget 和 Widget lock 文件的模块提交；Widget live 组合已在 Widget App 中，Main live ViewModel 的正式 App/DI 接线仍属于 P2-00 总成审查，不得用独立 ViewModel 测试代替。
