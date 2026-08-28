# P1 开发数据安全与 migration / 升级 / 回滚草案

- 状态：草案，供 P1-02/P1-06 集成核对
- 数据范围：仅 P1 Task 开发数据
- 不适用：生产用户数据、Reminder/Anime/Sync、P2.5 Agent 迁移和 P2.75 完整恢复系统

## 已冻结的开发数据规则

开发库固定为：

```text
<仓库根目录>/.devdata/reminnote.sqlite
```

这条路径是 P1-00 的主线决策。`.devdata` 与生产数据严格分离；开发运行不得读取生产数据库、真实 Token、生产 Widget 配置或个人数据。SQLite 主文件及可能的 `-wal`、`-shm`、journal sidecar 都属于开发数据。

只有显式命令允许清理开发数据：

```powershell
./scripts/clean.ps1 -PurgeDevData
```

默认 `./scripts/clean.ps1` 只清理 `src` 下的 `bin/obj`，不得清理 `.devdata`。执行 `-PurgeDevData` 前必须人工确认当前仓库根目录和 `.devdata` 不含真实数据；该命令不是生产恢复、用户迁移或常规 schema 升级方案。

数据库、Token、生产配置、网络缓存和个人数据不能提交。现有 `.gitignore` 已将 `.devdata/*` 排除，仅保留目录说明/占位文件；P1 实现不得绕过该边界。

## 正常启动与数据库生命周期

在仓库根目录运行：

```powershell
./scripts/bootstrap.ps1
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release
./scripts/run.ps1 -Configuration Release
```

P1-06 必须证明最终启动入口：

1. 解析的数据库就是 `.devdata/reminnote.sqlite`；
2. 首次空库通过真实 migration 建库；
3. 已有库启动不删除、不重建、不静默覆盖；
4. 迁移失败时停止正常写入/启动，并给出恢复提示；
5. P0 Mock 展示保留，但不得把内存 Mock 当作数据库成功证据。

当前 checkout 仍是 P0 基线，现有 `run.ps1` 只启动 P0 WPF Mock；在 P1-06 记录真实入口和输出前，本草案的启动命令不能被误写成 P1 已实现证据。

## Migration 原则

P1 从第一天使用真实 migration。修改实体或 schema 时，必须创建并审查 forward migration；“编辑实体 → 删除数据库 → 重跑”不能作为正常开发升级流程。

兼容边界前，P1 开发库在必要时可以重置，但必须说明原因、确认只针对 `.devdata`、记录数据丢失范围，并使用显式 `-PurgeDevData`。这不等于产品升级允许删除用户数据库。

推荐流程：

```text
确认开发库
  -> 停止写入
  -> 生成安全备份
  -> 应用 forward migration
  -> 校验 schema/数据/CRUD
  -> 启动应用
```

即：`Backup -> Migration -> Verify -> Startup`。migration 是高风险操作；P1-06 仍应按该顺序验收，即使完整自动备份保留策略属于后续 P2.75。

## 升级草案

1. 停止 Main App/测试 harness，确认没有正在进行的业务写入。
2. 解析并显示待操作的绝对路径，必须落在当前仓库 `.devdata` 下；路径不明确则中止。
3. 将原始 SQLite 文件及必要 sidecar 复制到明确标记的备份位置；原文件保持不变。
4. 检查当前 migration history 和目标 migration，应用 forward migration。
5. 做最小验证：schema 对象/版本、Task 行数、关键字段、合法查询、CRUD 和重启读取。
6. 只有验证成功才启动正常业务；记录 migration 名称、结果和备份位置。

备份目录/命名、保留数量和用户可见提示在 P1 尚未形成长期产品契约；本草案只要求 P1-06 使用一个可定位、不与原库混淆的安全副本，并在报告中记录实际位置。未来不能以静默覆盖安全副本替代正式备份策略。

## 失败、回滚与恢复草案

### Migration 失败

- 立即停止正常写入；不要继续启动为可写状态的业务路径；
- 保留原始库、失败后的文件和日志证据，不在唯一原文件上做破坏性原地修复；
- 不自动删除数据库、不自动执行未知 down migration、不吞掉错误；
- 向操作者显示失败状态、受影响路径和恢复副本位置，但日志不得包含 Task 标题、笔记、Token 或 Authorization header；
- 通过集成报告中记录的安全副本恢复开发库，恢复后先做 schema/数据/CRUD 检查，再允许写入。

### P1 开发库回滚

本阶段“回滚”优先指恢复 migration 前的安全副本，而不是依赖 EF down migration 或重写唯一原始文件：

1. 停止应用和所有写入者；
2. 将当前失败库改名/移出工作路径以保留证据；
3. 将安全副本恢复到 `.devdata/reminnote.sqlite`；
4. 执行只读 schema、行数和已知 Task 验证；
5. 验证通过后再启动应用并做一次 CRUD/重启检查。

若只是一次性开发库且没有需要保留的数据，可以在确认范围后使用显式 `-PurgeDevData` 重新建库；这必须记录为“开发数据重置”，不能称为用户数据回滚或常规升级。

### 不可恢复/疑似真实数据

如果路径不在当前 `.devdata`、发现真实用户数据、Token 或生产配置，立即停止操作，不执行 purge、migration 或恢复覆盖；保留现状并升级给主线/维护者处理。不得把真实数据复制到仓库进行调试。

## Schema 演进边界

- P1 只维护长期 Task schema 所需对象；不预建 Reminder、Anime、Sync 表；
- schema 演进遵循 `Expand -> Migrate -> Contract` 方向；删除/重命名字段前必须有兼容和数据证明；
- 兼容边界前的 migration history 可以在有理由且有记录时 squash/reset，但不能作为没有证据的快捷修复；
- 从 P3 daily-use Alpha 起，删除数据库不再是可接受的例行升级路径；P1 草案不应与该长期规则冲突；
- P2.5 的 WAL、Agent single writer、IPC 和产品级恢复不属于本草案的实现范围。

## 安全验收清单

- [ ] 所有数据库文件只在仓库 `.devdata` 或明确的临时测试目录。
- [ ] 默认清理不触碰 `.devdata`；只有显式 `-PurgeDevData` 重置开发数据。
- [ ] 空库/升级使用真实 migration，不使用删库代替正常升级。
- [ ] migration 前有安全副本，原始库保留。
- [ ] 失败后停止正常写入，不做破坏性原地修复。
- [ ] 恢复后先验证再恢复写入。
- [ ] 日志和报告不含标题、笔记、Token、Authorization header 或个人数据。
- [ ] schema 清单没有 Reminder、Anime、Sync 表。
- [ ] 未修改 `reviews/`、`second-review/`，未提交数据库/Token/生产配置。
