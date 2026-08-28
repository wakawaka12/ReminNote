# P1 手动验收步骤

## 状态与前提

本文件是 P1-05 准备、P1-06 集成后校对的验收稿。当前已合入真实 P1 Core、SQLite migration、repository 和 application/query boundary，但 `scripts/run.ps1` 仍启动 P0 WPF Mock，尚未把真实 CRUD 接入用户界面。不得使用 P0 TODAY 的 Quick Add 代替真实 Task Create；在真实 CRUD UI 尚未接入前，Test 2–6 的入口只能使用报告中明确记录的受控测试/harness。

环境：

- Windows 10 22H2 x64 或 Windows 11 x64；
- PowerShell；
- 仓库根目录为当前 worktree 根目录；
- .NET SDK 由 `scripts/resolve-dotnet.ps1` 解析，版本遵循 `global.json`；
- 配置：Release；
- 开发数据库：`<仓库根目录>/.devdata/reminnote.sqlite`；SQLite sidecar 文件也必须留在 `.devdata/`。

## 启动与准备

在仓库根目录执行：

```powershell
./scripts/bootstrap.ps1
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release
./scripts/verify-p0-07.ps1
./scripts/run.ps1 -Configuration Release
```

预期：

- restore 使用 locked mode；Release 构建无错误且无新增未解释警告；
- P1 集成后的启动路径能执行真实 migration/数据库检查，并仍保留 P0 Mock 展示行为；
- 开发数据库只解析到 `.devdata/reminnote.sqlite`；
- 不读取生产数据库、Token、生产 Widget 配置或网络数据。

当前实现注意：P1 自动化测试使用 `:memory:` 数据库，证明 migration、repository 和 application/query 代码路径；它不证明 P0 WPF Mock 已经成为真实 Task UI，也不替代下面要求的 `.devdata` 人工数据安全检查。

## P1 真实 CRUD 验收入口

P1 已合入一个仅供开发验收的命令行入口：`tools/ReminNote.P1ManualHarness`。它必须显式接收仓库根目录，启动每个命令前应用真实 migration，并只使用该根目录下的 `.devdata/reminnote.sqlite`；它没有 purge 命令。先构建：

```powershell
./scripts/build.ps1 -Configuration Release
$harness = (Resolve-Path './tools/ReminNote.P1ManualHarness/bin/Release/net10.0/ReminNote.P1ManualHarness.exe').Path
$root = (Get-Location).Path
& $harness --repo-root $root schema
```

预期只看到 `tasks`、`__EFMigrationsHistory` 和 `__EFMigrationsLock`。确认当前 `.devdata` 没有需要保留的数据后，才执行下面的写入命令；标题建议使用本次验收专用值：

```powershell
& $harness --repo-root $root create 'P1验收-跨午夜' '2026-08-28' RANGE '23:00' '01:00'
& $harness --repo-root $root list '2026-08-28'
```

从 Create 输出复制 UUID v7 ID，再执行：

```powershell
& $harness --repo-root $root update '<ID>' 'P1验收-已更新' '2026-08-28' RANGE '22:00' '00:30'
& $harness --repo-root $root result '<ID>' PARTIAL '人工验收备注'
& $harness --repo-root $root list '2026-08-28'
& $harness --repo-root $root delete '<ID>'
& $harness --repo-root $root list '2026-08-28'
```

预期：跨午夜项仍归属计划日 `2026-08-28`；更新保持 ID；PARTIAL、记录时间和备注可读回；删除后列表为空。命令返回非零或输出“执行失败”即失败。该入口不是 P0 UI，也不代表已实现 P2 TODAY/Widget 体验。

### Test 6 的 harness 输入失败检查

在确认已有验收数据没有被修改的前提下，执行以下命令，验证失败不会写入：

```powershell
& $harness --repo-root $root create 'P1验收-非法空范围' '2026-08-28' RANGE '14:00' '14:00'
& $harness --repo-root $root create 'P1验收-非法时间参数' '2026-08-28' TIME
& $harness --repo-root $root result '0191f6a4-3b25-4c12-8d34-56789abcde01' COMPLETED
```

预期：三条命令均返回非零并报告输入/领域错误；随后用 `list` 确认没有新增非法行，其他合法 Task 不受影响。不要对未知仓库根目录运行 harness；它会要求根目录同时包含 Git 元数据和 `ReminNote.sln`。

失败：

- 任何命令失败、窗口无法启动、出现未处理异常/XAML 错误；
- 启动路径无法说明真实数据库位置，或指向仓库外/生产路径；
- P0 Mock 被删除/改成未授权的 P1 大 UI，或页面声称 Mock 已持久化；
- 启动产生 Token、网络缓存、生产配置或 `.devdata` 之外的数据库。

## Test 1 — 空开发库 migration

仅在确认当前路径是本仓库 `.devdata` 且不含真实用户数据后执行：

```powershell
./scripts/clean.ps1 -PurgeDevData
./scripts/run.ps1 -Configuration Release
```

预期：

- 仅清理开发数据；生产目录完全不受影响；
- 启动/指定的 P1 migration 入口从空路径建立当前 schema；
- `.devdata/reminnote.sqlite` 出现，可能有相邻 SQLite sidecar 文件；
- schema 只包含 P1 Task 所需对象，不包含 Reminder、Anime、Sync 表；
- 再次启动或再次执行 migration 不报重复对象错误、不丢数据。

失败：

- migration 依赖手工建表、删除数据库作为正常升级，或没有真实 migration；
- 数据库出现在 `.devdata` 以外，或清理命令能触碰生产/不明确路径；
- 创建任何 Reminder/Anime/Sync 空表；
- 重复 migration 改变/删除已有 Task。

## Test 2 — Create / Read 合法 Task

使用 P1-06 报告中记录的真实 Task application 入口（最小 UI、受控 harness 或等价已合入入口），不要使用 P0 Mock Quick Add。使用唯一标题，例如 `P1验收-20260828-CRUD`。

依次创建并读取：

1. `ANYTIME`：本地日期 `2026-08-28`，无时间点/范围字段；
2. `TIME`：本地日期 `2026-08-28`、`14:00`；
3. `RANGE`：本地日期 `2026-08-28`、`14:00–17:00`；
4. 跨午夜 `RANGE`：计划日期 `2026-08-27`、`23:00–01:00`。

预期：

- 每条合法 Task 创建成功并获得稳定 ID；
- 读取结果保留标题、时间形状、本地日期和对应时间值；
- 跨午夜项显示/返回计划归属日期 `2026-08-27`，不是自动改成 `2026-08-28`；
- `TIME` 被表达为计划/提醒锚点，`RANGE` 不暗示应用知道实际工作时长；
- 写入只发生在 `.devdata/reminnote.sqlite`。

失败：

- 任一合法形状被拒绝、字段被静默改写或 ID 不稳定；
- 跨午夜归属错误、出现实际开始/结束/耗时字段语义；
- 结果只在内存中存在，或写到错误路径。

## Test 3 — Update

1. 读取 Test 2 中的一条 Task，记录其 ID 和原始时间形状。
2. 只修改允许修改的字段，例如标题或未来计划的本地时间。
3. 重新读取同一 ID。
4. 对一个已发生/已有结果的范围任务确认其历史计划没有被改写成所谓实际完成区间。

预期：

- 更新只影响指定 Task；ID 保持不变；
- 新值通过相同的领域/数据库约束；
- 已发生计划的历史语义保留，未来计划编辑不凭空产生 `MISSED` 历史；
- 更新失败时没有部分写入。

失败：

- 修改导致时间形状和字段混合、其他 Task 被修改、ID 改变；
- 失败响应后数据库处于半更新状态；
- 计划编辑被错误呈现为实际工作时段或自动失败。

## Test 4 — Delete

1. 创建一条专用于删除的合法 Task，例如 `P1验收-待删除`。
2. 记录 ID，并通过真实入口执行明确删除。
3. 重新读取该 ID，并读取其他验收 Task。

预期：

- 指定 Task 删除成功且重新读取为未找到；
- 其他 Task 保持不变；
- 删除结果不是把不存在项伪装成成功，也不是误删整个数据库。

失败：

- 删除错误 ID 的 Task、清空整库、删除失败仍报告成功，或通过删除数据库实现操作；
- 删除行为与最终产品的 trash/硬删除决策不一致且没有记录。

## Test 5 — 重启后持久化

1. 创建一条带有日期/时间形状的验收 Task，记录 ID、标题和全部时间字段。
2. 正常关闭 P1 应用，确认进程结束。
3. 再次执行 `./scripts/run.ps1 -Configuration Release`。
4. 通过同一真实 Read 入口读取该 ID。

预期：

- 重启后 Task 仍存在，ID、标题、时间形状、本地日期/时间和结果语义一致；
- P0 Mock 页面仍按已冻结决策保留，但不能覆盖或伪造真实 Task 数据；
- 没有因正常启动而重建/清空数据库。

失败：

- Task 丢失、字段改变、ID 重生、启动自动删库或把数据库当成进程内 Mock；
- 应用启动前后出现两个未说明的业务写入路径。

## Test 6 — 非法输入与不变量

通过真实 application/domain 入口逐项提交，并在每项失败后重新读取数据库：

1. 时间形状和字段混用，例如 `ANYTIME` 同时携带 `RangeStart`；
2. `TIME` 缺少本地日期或时间点；
3. `RANGE` 缺少开始/结束时间；
4. 非法/未知时间形状值；
5. `RangeEnd == RangeStart`、空标题等边界项，按 P1-01 最终冻结规则执行，不由手测人员自行猜测；
6. 更新一个不存在的 ID。

预期：

- 每个最终定义为非法的输入被确定性拒绝，错误可识别且不写入非法行；
- 数据库约束能阻止绕过应用服务的非法时间组合；
- 更新不存在项返回未找到，不修改其他数据；
- 合法跨午夜 `23:00–01:00` 不因跨日而被误判为非法。

失败：

- 非法数据进入 SQLite、异常被吞掉后显示成功、错误结果依赖当前时间/语言；
- 对尚未冻结的边界规则擅自作出产品结论；
- 错误输入污染已有合法 Task。

## Test 7 — Migration 升级与失败恢复演练

按 [P1 数据安全与 migration 草案](P1-data-safety-and-migration-draft.md) 执行，不在真实用户目录演练：

1. 停止应用，确认使用的确实是 `.devdata/reminnote.sqlite`。
2. 生成带时间标记的安全副本，保留原始库。
3. 应用 forward migration，执行 schema、行数、CRUD 和重启检查。
4. 使用受控失败条件（例如指向无权限的测试副本或预检失败）验证应用停止正常写入，并保留原库/失败产物。
5. 从安全副本恢复到开发路径，再验证可读和可写。

预期：

- 升级顺序为 Backup → Migration → Verify → Startup；
- 失败不执行破坏性原地修复，不覆盖唯一原始文件；
- 恢复后原有 Task 可读取，验证通过后才恢复写入；
- 普通升级不要求删除数据库。

失败：

- migration 失败后仍允许正常业务写入/启动；
- 原始库被覆盖或无法恢复；
- 只能通过删除数据库“恢复”；
- 失败过程触碰生产数据或泄露标题、笔记、Token。

## Test 8 — P0 保留与数据安全

1. 启动 Main App，确认既有 P0 TODAY/ANIME Mock 展示仍可见。
2. 关闭应用，检查 `.devdata`、项目目录、Git 状态和网络/Token 相关文件。
3. 确认变更清单不包含 `reviews/` 或 `second-review/` 的覆盖/重写。

预期：

- P0 Mock 展示行为保留；P0 Mock 操作不会被误报为真实持久化；
- 只有 `.devdata/reminnote.sqlite` 及其 sidecar 属于本地开发数据库；
- 无 Token、生产配置、网络缓存、生产数据库或个人数据；
- P1 没有 Reminder/Anime/Sync 表、IPC、同步或网络功能。

失败：

- P0 UI 被无授权重构、出现 P2/P3 功能、真实网络/Token/生产数据路径；
- 数据库或日志落在 `.devdata` 外；
- 既有 review 记录被修改或删除。

## 手动验收记录模板

| 项目 | 结果（通过/失败） | 证据/备注 |
|---|---|---|
| 启动与 locked restore |  |  |
| 空库 migration |  |  |
| Create / Read |  |  |
| Update |  |  |
| Delete |  |  |
| 重启保留 |  |  |
| 非法输入 |  |  |
| 升级/失败/恢复 |  |  |
| P0 保留与数据安全 |  |  |
