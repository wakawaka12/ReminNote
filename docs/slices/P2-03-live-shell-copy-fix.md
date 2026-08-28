# P2-03 Main live 壳层文案修复 Slice

- 日期：2026-08-28
- 范围：正式 Main live 组合的共享 Shell 文案与资源回归测试
- 状态：代码修复与自动化门禁已完成；真实 Main GUI 人工验收待用户执行

## 目标与评估结论

P2-00 和当前 P2 阶段报告已经确认，正式 Main 入口会在显示窗口前初始化经过仓库根目录校验的 `TaskWorkspace`，TODAY ViewModel 通过 Today/application/query 边界读取和写入本地 SQLite；ANIME 仍是本地 Mock。原 Shell 的 `Shell.StageLabel`、`Shell.HeaderSubtitle`、`Shell.Footer` 仍保留 P0 文案，把正式入口描述为进程内 Mock，和实际 live 行为冲突。

本 Slice 只修正共享资源值，不改变业务接线：

| 资源键 | 正式 live 默认文案 |
|---|---|
| `Shell.StageLabel` | `P2 LIVE · TODAY 本地持久化 Task / ANIME 本地 Mock` |
| `Shell.HeaderSubtitle` | `本地优先 · TODAY 使用本地持久化 Task · ANIME 仍为本地 Mock` |
| `Shell.Footer` | `P2 正式入口已启动 · TODAY Task 持久化于本地 SQLite · ANIME 仍为本地 Mock` |

## 改动与边界

- 更新 `src/windows/ReminNote.Windows/Resources/Localization/UiText.resx` 中已有的三个 Shell 键；没有改键名、`UiText.cs` 访问边界或 `MainWindow.xaml` 的 `x:Static` 绑定。
- 在 `StartupAndMainUiInteractionTests` 增加正式 live 文案回归：要求 TODAY 明确指向本地持久化 Task、ANIME 明确保留本地 Mock，并拒绝旧的 `P0`、`进程内`、`业务数据尚未接入` 和“仅在本次运行有效”壳层表述。
- `Today.PageDescription`、`Today.MockBadge`、无参 `TodayPageViewModel` 的 `TodayMockDataService` 路径未改；既有 P0 Mock 兼容测试继续覆盖该路径。
- `Anime.PageDescription`、`Anime.MockBadge`、Anime Mock catalog 和其现有 Mock 交互未改。
- 未实现或接入 Reminder、Agent、业务 IPC、Sync；未修改 `reviews/`、`second-review/`。

## 命令证据

在 `C:\Users\EMT\.codex\worktrees\c438\Anime` 执行：

| 命令 | 结果 |
|---|---|
| `scripts/build.ps1 -Configuration Release` | 退出码 0；解决方案 8 个项目成功构建；无 warning/error |
| `scripts/test.ps1 -Configuration Release` | 退出码 0；`ReminNote.Tests` `Total: 161, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`；脚本内 P0-07 通过 |
| `scripts/verify-p0-07.ps1` | 退出码 0；213 个资源键、7 个锁定项目、1 个测试项目及 Shell/TODAY/ANIME/Widget 标记通过 |
| `git diff --check` | 退出码 0；无 whitespace errors |

## 人工验收步骤

使用当前 Release 输出，以正式 live 参数启动 Main：

```powershell
$repo = 'C:\Users\EMT\.codex\worktrees\c438\Anime'
Start-Process "$repo\src\windows\ReminNote.Windows\bin\Release\net10.0-windows\ReminNote.Windows.exe" -WorkingDirectory $repo -ArgumentList @('--repo-root', $repo)
```

1. 查看 Main 左侧阶段标签、顶部副标题和底部状态栏。三处都应明确看到 TODAY 的本地持久化 Task 与 ANIME 的本地 Mock；不应出现 `P0 · 本地 Mock`、`TODAY / ANIME Mock`、`业务数据尚未接入` 或“所有状态仅在本次运行有效”。
2. 在 TODAY 执行一次 Quick Add，关闭并重新启动同一 `--repo-root` 的 Main，确认 Task 仍可读；TODAY 页面不得出现 `MOCK · 仅内存` 徽标。
3. 导航到 ANIME，确认页面仍显示 `LOCAL MOCK`/本地 Mock 说明，固定本地目录和现有 Mock 交互仍可用，不把 ANIME 误报为持久化业务数据。
4. 用辅助功能检查 Shell 的主导航、页面标题区域、主要内容区域和应用状态区域仍有原有 `AutomationProperties` 名称；本次只换资源值，不应丢失可访问性标记。

## 失败判定

出现以下任一情况即判定本 Slice 失败：

- 正式 live Shell 仍出现 P0 Mock、进程内 Mock、业务数据未接入或“仅本次运行有效”的 TODAY 归因；
- Shell 未同时明确 TODAY 使用本地持久化 Task 和 ANIME 仍为本地 Mock，或 ANIME 的既有 Mock 语义被删除；
- 无参 `TodayPageViewModel` 不再使用 P0 Mock 夹具、Mock 徽标/交互发生回归，或正式 live TODAY 再次挂回 Mock 服务；
- 资源键被改名/删除、Shell 的 AutomationProperties 标记丢失，或 Release 构建、161 项测试、P0-07 任一门禁失败；
- 本 Slice 引入 Reminder、Agent 业务 IPC、Sync 等未授权范围。

真实 GUI 点击、重启保留和双宿主同库仍属于 P2 阶段人工验收，以上自动化证据不替代用户签字。
