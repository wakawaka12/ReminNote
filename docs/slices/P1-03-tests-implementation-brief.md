# P1-03 测试实施说明

- 阶段：P1 Real Task domain + SQLite persistence
- 窗口：P1-03 Tests
- 性质：测试范围、夹具生命周期和失败判定说明
- 依据：主计划 P1 required automated tests、P1 阶段预报告和仓库 `TESTING.md`

## 目标

建立真实测试项目，覆盖 Task 时间形状、值对象、领域不变量、SQLite migration 和基本持久化路径。P1 的 `dotnet test` 成功只有在测试项目真实执行测试后才可作为业务测试证据；不得沿用 P0 的空测试流程来声称领域覆盖。

## 测试分层

### Domain tests

- 合法 `ANYTIME`、`TIME`、同日 `RANGE`、跨午夜 `RANGE`；
- 时间形状与值对象不一致、缺少字段、非法枚举和其他最终冻结的非法输入；
- `RangeEnd < RangeStart` 的跨午夜检测；
- 跨午夜任务仍拥有开始日期；
- `AWAITING RESULT` 与 `COMPLETED`/`PARTIAL`/`MISSED` 的合法状态转换（仅测试 P1 已纳入的最小结果模型）；
- 值对象相等性、稳定 ID 和确定性错误结果；
- 计划时间与记录时间不被混为实际工作时间。

### Persistence/integration tests

- 空 SQLite 文件应用全部真实 migration；
- 重复应用 migration 不创建重复对象、不丢数据；
- 合法 Task 的 create/read/update/delete；
- 非法领域输入在应用层失败，数据库约束拒绝绕过应用层的非法组合；
- 写入后重新创建 DbContext/服务仍可读取；
- 进程停止再启动后，Task ID 和业务字段保留；
- schema 对象清单只包含 P1 Task 所需对象，不包含 Reminder、Anime、Sync。

### Architecture/data-safety tests

- Core 不引用 WPF、Windows API、EF Core、Serilog 或 SQLite；
- 测试只使用临时数据库或 `.devdata/` 开发数据库；
- 测试不会读取生产数据库、真实 Token、生产 Widget 配置或网络；
- 测试运行后不把 SQLite、日志、Token 或个人数据写入 Git 跟踪范围。

## 测试数据规则

- 每个测试创建自己的数据库/数据集，避免依赖测试执行顺序。
- 需要重置时，只能定位到已确认的开发/临时路径；不得调用生产路径或泛化删除命令。
- 夹具不得使用真实标题、笔记、Token 或个人数据。
- 数据库文件及 SQLite sidecar 文件不应进入提交；测试产物应落在现有忽略的临时/测试目录。

## 失败判定

- `dotnet test` 退出码为非零，测试未发现或测试被跳过但报告为通过；
- 只测试 happy path，未覆盖跨午夜、非法组合、migration、重启保留；
- 测试依赖本机当前时间、执行顺序或网络而导致结果不确定；
- 任何测试读写生产/未知路径，或在仓库跟踪范围生成数据库/Token/个人数据；
- 使用删除数据库代替 forward migration；
- 没有测试证据却将空 `test.ps1` 成功写成业务覆盖。

## 交付证据

交付测试项目路径、测试总数/通过数、关键场景名称、migration/CRUD 夹具路径策略、Core 边界检查和完整命令输出。若当前 checkout 仍未合入测试项目，应明确记录“当前仓库只有 P0 基线，尚无 P1 业务测试”，不可伪造结果。

## 当前实现与验证证据

测试项目为 `tests/ReminNote.Tests/ReminNote.Tests.csproj`，引用 Core 和 Infrastructure，依赖使用中央锁定版本。持久化夹具仅使用独立 `:memory:` SQLite 连接，不创建 `.devdata` 文件，不读取网络、Token 或生产路径。

集成分支当前通过既定 `scripts/test.ps1 -Configuration Release`：xUnit v3 executable 共 61 个测试，0 失败、0 跳过；其中包含 47 个 Core 领域测试、11 个 SQLite persistence 执行项和 3 个 application/query boundary 场景。由于当前 .NET 10/Microsoft Testing Platform 配置，测试脚本直接运行构建后的测试 executable，并检查其非零退出码；不能用旧 VSTest 的“未发现测试”输出替代测试证据。
