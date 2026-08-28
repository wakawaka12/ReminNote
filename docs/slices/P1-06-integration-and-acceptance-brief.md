# P1-06 集成与人工验收说明

- 阶段：P1 Real Task domain + SQLite persistence
- 窗口：P1-06 Integration and acceptance
- 性质：串行集成窗口的验收 brief 与当前状态记录
- 前置：P1-01 至 P1-05 交付，P1-00 决策已冻结

## 目标

把 Task Core、SQLite/migration、测试、application boundary 和本验收材料串行集成，完成一次从空开发库到当前 schema 的可复现验证，并由用户完成重要用户行为验收后再决定是否合并或进入 P2。

## 集成顺序

1. 核对 P1-01 的 Core contract 与依赖边界；
2. 核对 P1-02 的 DbContext、migration、schema constraints 和 `.devdata/reminnote.sqlite` 路径；
3. 核对 P1-03 测试项目、锁定依赖和 fixture 数据安全；
4. 核对 P1-04 的 application/query/CRUD 接线，确认 P0 Mock 展示仍保留；
5. 使用锁定 restore、Release build、测试和 P0-07 门禁；
6. 对空库建库、CRUD、重启、非法输入、升级失败和恢复路径执行 [P1 手动验收](P1-manual-test.md)；
7. 记录实际证据、未完成项和是否进入 P2 的用户判断。

## 必须串行核对的事项

- `ReminNote.sln`、`Directory.Packages.props`、lock 文件、DI 接线和根脚本的修改只由集成窗口完成；
- migration 从空库到当前库可重复，正常升级不依赖删除数据库；
- schema 只包含 P1 Task 所需对象，不创建 Reminder/Anime/Sync 表；
- Core 不引用 WPF、Windows API、EF Core、Serilog 或 SQLite；
- 真实写入只经过 application boundary；P0 Mock 操作不被冒充为真实持久化；
- 无数据库、Token、缓存、生产配置或个人数据被提交。

## 统一命令

在仓库根目录运行：

```powershell
./scripts/bootstrap.ps1
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release
./scripts/verify-p0-07.ps1
./scripts/run.ps1 -Configuration Release
```

当前 checkout 的 `run.ps1` 仍启动 P0 `ReminNote.Windows` Mock；只有集成 P1 代码并记录真实 CRUD 入口后，最后一条命令才可以作为 P1 手测启动证据。P1-06 必须在报告中补充实际入口，不得只引用命令本身。

## P1-06 通过条件

- 空库真实 migration 成功，重复执行幂等；
- 合法 ANYTIME/TIME/RANGE（含跨午夜）可以经真实 CRUD 路径保存和读取；
- 更新、删除、重启后读取与非法输入均有证据；
- migration/升级失败不会继续正常写入，原始数据库被保留并有可执行恢复路径；
- locked restore、Release build、真实测试项目、现有 P0-07 门禁均通过；
- 用户完成手动验收并明确记录通过/失败；
- 没有提前引入 Reminder、Anime、Sync、IPC、生产数据或大范围 UI 改造。

## 不通过条件

窗口崩溃、未处理异常、真实库路径不明确、删库被当作常规升级、非法数据进入数据库、重启丢数据、失败后仍可写入、越界文件改动，或仅以 P0 Mock/空 `dotnet test` 结果代替 P1 证据，均不得宣告 P1 通过。

## 当前集成证据

当前集成分支已合入 P1-00 至 P1-04 实现和 P1-05 验收材料。已执行并通过：

- `dotnet restore ReminNote.sln --locked-mode`；
- `dotnet build ReminNote.sln --configuration Release --no-restore`，8 个项目，0 警告、0 错误；
- `scripts/test.ps1 -Configuration Release`，此前 61/61 测试通过；本次补充已结果 Task 改期保护的 Core、Application、Persistence 三类回归测试后，应以本次 Release 测试结果为准（目标总数 64 项）；
- `scripts/verify-p0-07.ps1`，213 个资源键、7 个锁定项目和 P0 标记检查通过。

自动化证据使用临时 SQLite，尚未替代用户对真实 `.devdata/reminnote.sqlite` 的手动备份、migration、重启和数据安全验收。已合入开发验收入口 `tools/ReminNote.P1ManualHarness`，可对显式指定的仓库根目录执行真实 schema/CRUD；当前仍没有把该路径接入 P0 WPF UI，不得把 P0 Mock Quick Add 当作真实持久化入口。
