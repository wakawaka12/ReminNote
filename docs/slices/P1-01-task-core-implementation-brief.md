# P1-01 Task Core 实施说明

- 阶段：P1 Real Task domain + SQLite persistence
- 窗口：P1-01 Task Core
- 性质：P1-05 文档窗口预先整理的实施与验收边界；不是代码已完成证明
- 依据：`ReminNote_MASTER_DEVELOPMENT_PLAN.md` 的 Task domain/P1 章节，以及 P1 阶段预报告

## 目标

建立不依赖 WPF、EF Core、Windows API 或 Serilog 的真实 Task 核心，替换 P0 中仅用于展示的 `TodayMockTask` 语义。P1-01 只负责领域模型、值对象、不变量和 Core 层边界抽象；持久化和 UI 适配分别由 P1-02、P1-04 负责。

P1-00 已由主线冻结开发数据库与数据安全决策：开发数据库为仓库相对路径 `.devdata/reminnote.sqlite`，仅显式 `-PurgeDevData` 可以重置开发数据，保留 P0 Mock 展示行为。P1-01 不重复定义这些决策，也不把它们写入 Core 的硬编码路径。

## 范围

必须覆盖：

- Task 稳定本地 ID，采用主计划要求的 UUID v7 方向；
- `TaskTimeType` 的且仅有 `ANYTIME`、`TIME`、`RANGE` 三种值；
- `TimeSpec` 值对象族：`AnytimeSpec`、`TimePointSpec`、`TimeRangeSpec`；
- 本地日期与本地民用时间，不把计划值误当成 UTC instant；
- 跨午夜范围的确定性判断和起始日期归属；
- P1 所需的最小结果状态/值模型（不扩展为 Reminder 或时间追踪）；
- 可由自动化测试稳定复现的领域验证结果。

不属于本窗口：

- EF Core、SQLite、数据库实体映射、migration 或 repository 实现；
- WPF ViewModel、TODAY/Widget 真实任务界面或 P0 Mock 展示改造；
- Reminder、Anime、Sync、网络、IPC、Named Pipe、Single Writer 迁移；
- 实际开始/结束时间、暂停、耗时、秒表或工作会话记录；
- 为未来功能添加空实体或空表。

## 必须保持的不变量

| 规则 | 验收要求 |
|---|---|
| 时间形状 | 只允许 `ANYTIME`、`TIME`、`RANGE`，不得出现第四种状态。 |
| 形状与字段 | `TimeSpec` 与时间形状匹配；混合字段、缺少所需值或无法表示的组合必须被拒绝。 |
| RANGE | `RangeEnd < RangeStart` 表示跨午夜；不得添加独立 `IsCrossMidnight` 字段来复制该事实。 |
| 日期归属 | `2026-08-27 23:00 -> 2026-08-28 01:00` 仍归属于 2026-08-27 的计划。 |
| 计划语义 | `TIME` 是提醒/计划锚点，不是硬截止时间；`RANGE` 是计划区间，不表示应用知道用户正在工作。 |
| 结果语义 | RANGE 结束且未确认结果时为 `AWAITING RESULT`，不能自动改成 `MISSED`。 |
| 记录时间 | `RecordedAt` 只表示用户在 ReminNote 记录结果的时间，不得展示为现实世界完成时间。 |
| 历史 | 已发生计划的编辑不能抹除历史；未来计划的日期/时间编辑不能凭空制造失败历史。 |

具体的标题长度、可选字段集合、`RangeStart == RangeEnd` 是否允许等尚未在当前仓库中冻结的契约，必须由 P1-01 以领域测试和决策记录明确；本说明不擅自替产品拍板。

## Core 边界检查

- `ReminNote.Core.csproj` 不得新增 WPF、Windows API、EF Core、Serilog 或 SQLite 引用。
- 领域验证不得依赖当前系统时钟、当前 UI 语言或数据库连接才能得到不同结果。
- 错误结果应可被应用层识别；Core 不返回只适用于某个 WPF 页面布局的完整用户文案。
- ID、日期、时间和结果值对象应具有稳定的相等性语义；不得用 SQLite 自增整数作为跨设备身份。

## 自动化验证矩阵

P1-03 至少应覆盖以下样例，并将实际测试项目/命名记录在集成报告中：

1. 合法 `ANYTIME`、`TIME`、同日 `RANGE` 和跨午夜 `RANGE`；
2. 时间形状与字段混用、缺失字段和非法枚举值；
3. 跨午夜检测及起始日期归属；
4. RANGE 结束后无结果保持 `AWAITING RESULT`；
5. 值对象相等性、不同值不相等、边界值和确定性错误结果；
6. `RecordedAt` 不被解释为实际完成时间；
7. Core 项目依赖边界。

## 交付证据

P1-01 交付给 P1-06 的证据应包括：Core 变更文件清单、公共类型最终名称/命名空间、领域不变量测试结果、未冻结契约清单，以及 Core 依赖检查结果。没有这些证据时，不得在 P1 报告中声称时间语义已通过验收。
