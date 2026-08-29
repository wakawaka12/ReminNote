# P2-10 Main TODAY 刷新并发收敛

- 阶段：P2 Main / TODAY refresh concurrency hardening
- 日期：2026-08-29
- 基线：`7ac72ba`（P2-06 Main 交互加固）
- 状态：代码与自动化验证已完成；未启动桌面 GUI

## 目标

修复终审更正报告 `db18a10` 指出的 Main TODAY 刷新竞态：宿主的启动、激活、2 秒 timer 刷新，以及 TodayPageViewModel 各写入完成后的无参 `RefreshAsync`，必须经过同一个异步串行边界，避免旧查询结果在较新的查询之后覆盖列表。

本 Slice 不改变 TaskId、Task 结果、计划字段、跨午夜分类或历史语义，也不引入 Agent、业务 IPC、Single Writer、WAL 或 Change Journal。

## 实现

- `TodayPageViewModel.RefreshAsync` 是 Main TODAY 的共同刷新入口；`SemaphoreSlim` 覆盖查询和 `ApplyReadModel`，因此宿主刷新与写入后刷新不会并发访问 Today 查询/应用段。
- 每个请求在等待串行门前取得递增代次。查询返回后只有仍为最新代次的请求才能应用 Read Model；被更新请求取代的旧结果会丢弃，旧异常也不会覆盖更新请求的反馈。
- 每次刷新使用调用方 token 与 Today 生命周期 token 的链接 token；等待门、查询返回后的应用前都检查取消。查询失败或取消不调用 `ApplyReadModel`，现有列表和选择保持不变。
- `TodayPageViewModel.Dispose` 拒绝新的刷新请求，取消进行中的刷新，并在活动请求退出后释放串行门和生命周期 token。`MainWindow.Dispose` 在停止 timer、取消宿主 token 时同时释放 Today VM；已有 Loaded/Activated/timer/二次实例激活路径保持不变。

## 回归覆盖

- `ConcurrentRefreshesAreSerializedAndTheNewestReadModelWins`：控制旧查询先开始、替换为新快照后再发起刷新；断言查询最大并发数为 1，最终只应用新快照。
- `FailedRefreshKeepsTheExistingTodayState`：查询失败时保留原 Task 实例、选择和失败提示。
- `FailedRefreshReleasesTheGateForTheFollowingRefresh`：失败后下一次刷新仍可进入串行门并成功读取，列表未被失败清空。
- `CancelledQueuedRefreshReleasesTheGate`：等待门的请求取消后不进入查询、不持有门，前一个请求退出后后续刷新仍可执行。
- `DisposingDuringRefreshCancelsItAndRejectsLaterRefreshes`：窗口生命周期释放会取消进行中的查询、保留现有列表，并拒绝后续刷新。
- `MainTodayRefreshLifecycleCoversStartupActivationAndShutdown`：静态检查 Main 的 Loaded、Activated、短周期 timer、宿主 token、Today 释放，以及 VM 的串行门和代次收敛标记。

## 最终验证

- `scripts/build.ps1 -Configuration Release`：通过；8 个项目，0 warning、0 error。
- `scripts/test.ps1 -Configuration Release`：通过；`ReminNote.Tests` 184/184，Errors 0、Failed 0、Skipped 0、Not Run 0；脚本内 P0-07 通过。
- `scripts/verify-p0-07.ps1`：通过；213 resource keys、7 locked projects、1 test project，并完成 Shell/TODAY/ANIME/Widget 标记检查。
- `git diff --check`：通过；无 whitespace errors。
- 使用仓库脚本解析到的 `C:\Program Files\dotnet\dotnet.exe`（x64，SDK `10.0.400`）执行；直接使用 PATH 中的 x86 .NET 6 host 会因 `global.json` 要求 .NET 10 而失败，这是环境选择问题，未修改门禁文件。
- 未访问 `D:\Anime\.devdata`，未启动或控制桌面 GUI，未修改 Widget/Core/Infrastructure、既有 reports、`reviews/` 或 `second-review/` 文件。

## 剩余风险

- 本 Slice 的自动化测试覆盖了刷新门的时序、失败保留、取消和释放；WPF Dispatcher 的真实事件调度仍依赖既有 Main 手工验收，不能由无 GUI 的单元测试替代。
- 写入业务本身仍遵循既有 application service 语义；本 Slice 只收敛刷新查询/应用，不提升为跨宿主 Single Writer 或事务日志方案。
