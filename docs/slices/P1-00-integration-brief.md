# P1-00 集成与决策冻结简报

- 日期：2026-08-28
- 状态：已接受，作为 P1 编码起点
- 负责窗口：主线集成窗口
- 前置基线：`fb74398`（P0 已完成并通过用户确认）

## 目标

冻结 P1 的公共边界，使 Task domain、SQLite/EF Core 持久化、测试和 application service 可以在独立 worktree 中协作，且最终能够安全串行集成。

## 已冻结决策

1. 开发数据库路径为仓库相对路径 `.devdata/reminnote.sqlite`。
2. 开发期间允许通过显式 `-PurgeDevData` 重置开发数据；默认清理不触碰 `.devdata`，任何生产数据路径都不在 P1 测试范围内。
3. 保留 P0 TODAY/ANIME/Widget 的 Mock 展示行为。P1 不把真实 Task 写入路径接入 P0 UI，真实 TODAY/Widget 任务体验属于 P2。
4. P1 只建立 Task 相关 schema，不预建 Reminder、Anime、Sync 空表。
5. Core 只包含领域类型、值对象、校验和应用边界抽象；不得引用 WPF、EF Core、Serilog 或 Windows API。
6. Infrastructure 使用 SQLite + EF Core，采用真实 migrations；禁止把删除数据库重建作为正常升级策略。
7. 新依赖必须记录用途、许可证、维护性和安全影响；中央包版本、lock 文件和 Solution 登记由主线串行处理。
8. Core 使用 Noda Time 3.3.3；Infrastructure 使用 EF Core 10.0.11、SQLite provider 10.0.11 和同版本 Design tooling；UUID v7 使用 .NET 10 内置 `Guid.CreateVersion7()`。
9. P1 测试使用 xUnit.net v3 4.0.0、`xunit.runner.visualstudio` 4.0.0 与 `Microsoft.NET.Test.Sdk` 18.9.0；当前 .NET 10/Microsoft Testing Platform 配置由 `scripts/test.ps1` 直接运行构建后的测试 executable，避免旧 VSTest “未发现测试”假通过。

## P1 工作边界

### 包含

- UUID v7 Task ID；
- `TaskTimeType`：`ANYTIME`、`TIME`、`RANGE`；
- `TimeSpec` 及三种合法时间形状；
- 本地 civil date/time 语义与跨午夜范围规则；
- 当前 Slice 所需的 Task 结果模型；
- SQLite、EF Core、真实 migration 和 schema constraints；
- repository、query、application service 的基本 CRUD；
- Noda Time abstraction；
- 确定性领域校验和高价值自动化测试；
- 每个模块对应的 Slice 简报、测试证据和手动验收说明。

### 不包含

- ReminderRule、ReminderSchedule、ReminderInstance 和真实提醒调度；
- Toast、Tray、Widget Alert 生产化；
- Agent IPC、Named Pipe、Single Writer 迁移；
- Anime 网络、Bangumi、同步、下载、播放；
- P1 对 P0 UI 的真实 Task 写入接线。

## 集成顺序

```text
P1-01 Task Core
       ↓
P1-02 Persistence + P1-03 Tests + P1-05 Docs
       ↓
P1-04 Application Boundary
       ↓
P1-06 主线集成、全量验证与人工验收
```

公共依赖、Solution、项目引用、数据库路径、DI 组合和 migration 顺序由主线窗口处理。子窗口只在自己的所有权范围内修改，并以 `git commit -s` 交付本地提交，不自行 push、merge、rebase、tag 或 release。

## P1-00 验收证据

- 本简报与 `DECISIONS.md` 的 ADR-0012 记录已冻结的数据库和兼容边界；
- 5 个独立开发窗口已按依赖关系启动；
- P1-01 至 P1-05 的提交、Slice 说明和验证结果将在主线集成时逐项登记；
- 最终必须通过 locked restore、Release build、P1 全量测试、现有 P0-07 门禁和人工验收。
