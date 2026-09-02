# P3-08 Reminder 导出与受控恢复隔离契约

## 目标与边界

本 Slice 只准备可版本化、可验证的 Reminder export/restore contract，供 P3-09 接入 P2.75 Candidate 流程。实现位于 `ReminNote.Core/Reminders/Export`，只包含窄 DTO、canonical JSON、纯验证器和无副作用 restore plan。它不打开或恢复真实数据库，也不执行 migration、merge/sync、网络或插件逻辑。

受 P3-00 `ReminderRule → ReminderSchedule → ReminderInstance` 最小契约约束：

- Rule 是业务真相（target、purpose、timing、重复/唤醒策略和修订）。
- Schedule 是可重建的派生投影；导出它是为了审计/校验，不是授予恢复时直接激活的权限。
- Instance 是不可变触发/生命周期历史；受控恢复只能追加不存在的历史，已有相同 ID 的事实必须完全一致。

P3-08 不复制 P3-01 根实体。`ReminderRuleExport.TargetId` 等引用字段是与 P3-01 的映射 seam；最终由 P3-09 Candidate adapter 映射到实际实体和 P2.75 的验证/提升管线。

## Envelope 与表示

当前唯一支持的 envelope 是：

| 字段 | 约束 |
| --- | --- |
| `schema` | `reminnote.reminders.export` |
| `schemaVersion` | 整数 `1`；未知版本 fail-closed |
| `rules` / `schedules` / `instances` | 必须存在的数组；元素按实体 ID 稳定排序 |
| `checksum` | `sha256:` 加 64 位小写十六进制；哈希输入是去掉 checksum 后的 canonical envelope |

canonical JSON 使用严格 UTF-8、无 BOM、无注释、无尾逗号、禁止重复字段；对象键按 RN-CJ-1 的 UTF-8 字节序规范化，数组按 ID 的 ordinal 顺序排序。序列化会重新计算 checksum，不使用调用方提供的旧值。

所有实体 ID 使用非空 UUID v7 的小写 `D` 格式（`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`）。所有绝对时间和 offset 结果使用 Noda Time `Instant`，UTC wire pattern 固定为 `uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'`；相对 Rule 的 `offsetSeconds` 是有符号 `int64`。`timeZoneId` 仅作为相对 Schedule 的可选 provenance，绝对 Rule 不携带该字段。

DTO 没有 secret、token、密码、机器标识、用户标识、profile 路径、标题/笔记或运行时瞬态字段。解析器对敏感未知字段（如 `token`、`machineId`、`runtimeState`）返回 `export.field.sensitive`；其他未知字段返回 `export.field.unknown`，不会静默保留。

## 三类实体

### Rule truth

`ReminderRuleExport` 固定包含 `id`、`targetKind`、`targetId`、`occurrenceId`、`purpose`、`timing`、`priority`、`pinned`、`repeatPolicy`、`wakePolicy`、`enabled`、`ruleRevision`、`createdAtUtc`、`updatedAtUtc`。P3-08 只接受 P3-00 当前 Task 目标 `TASK_INSTANCE` 和 `TASK_PRE_START`、`TASK_START`、`TASK_RANGE_END`、`TASK_CUSTOM` 用途；Anime 保留值或未知用途不会被激活。

`timing` 是严格二选一：

- `RELATIVE`：`anchor`（不在本 Slice 发明额外枚举）与 signed `offsetSeconds`；不得出现 `atUtc`。
- `ABSOLUTE_UTC`：`atUtc`；不得出现 `anchor` 或 `offsetSeconds`。

开启重复策略时 interval 必须为正，maxCount（如有）也必须为正；关闭时这些可选值仅作为不生效的来源字段保留。Rule 修订从 1 开始，更新时间不能早于创建时间。

### Schedule projection

`ReminderScheduleExport` 保留 `id`、Rule/occurrence/logical 引用、`originScheduleId`、`cause`、修订、`triggerAtUtc`、可选 `timeZoneId`、`state`、终态原因/替换引用和时间戳。相对 Rule 可带 provenance，也可以缺省；绝对 Rule 不应带该字段。`RULE` cause 不得有 origin；`SNOOZE`/`REPEAT` 必须有 origin。每个 `(ruleId, occurrenceId, scheduleRevision)` 唯一。

`PENDING` 不得携带终态字段；`CONSUMED`、`SUPERSEDED`、`CANCELLED`、`EXPIRED` 必须携带合法 `terminalReason` 与 `terminalAtUtc`。其中 `CONSUMED` 固定使用 `DUE_CONSUMED` 且不得有 replacement，`SUPERSEDED` 必须有 replacement，`CANCELLED`/`EXPIRED` 不得有 replacement。Instance 只能引用 `CONSUMED` schedule。历史终态不删除。

### Instance history

`ReminderInstanceExport` 保留 schedule/rule/occurrence/logical 引用、`attemptOrdinal`、purpose/priority/pin 快照、`triggeredAtUtc` 和 `UNREAD → READ → RESOLVED` 生命周期。READ 必须有 read 时间且没有解决动作；RESOLVED 必须有 read/resolved 时间和 Task 允许的 `DONE`、`SNOOZE`、`SKIP`、`IGNORE` 动作。时间顺序和引用不一致均拒绝。相同 Instance ID 的不同事实属于不可变历史冲突，绝不覆盖。

## 验证、错误与 restore plan

`ReminderExportValidator.Validate` 返回稳定的 `ReminderExportIssue` 列表；`ReminderExportJson.Parse` 对格式错误抛出带 `Code`/`Field` 的 `ReminderExportContractException`。主要错误分类如下：

| 分类 | 代码示例 | 确定性策略 |
| --- | --- | --- |
| JSON/字段 | `export.invalid_json`、`export.field.missing`、`export.field.type` | 严格 UTF-8/类型；不接受未知字段 |
| 敏感/版本 | `export.field.sensitive`、`export.schema.unsupported` | 敏感未知字段拒绝；未知 schema/version fail-closed |
| 标识/时间 | `export.id.invalid`、`export.id.duplicate`、`export.time.invalid` | UUID v7、小写 D、固定 Instant；重复 ID 或非法 offset 拒绝 |
| 关系/历史 | `export.relation.invalid`、`export.state.invalid`、`export.history.immutable` | 引用、终态、生命周期和不可变事实不一致即拒绝 |
| 完整性 | `export.checksum.missing`、`export.checksum.mismatch` | checksum 缺失或不匹配不得进入 restore |
| 受控恢复 | `restore.candidate.required`、`restore.active.write_forbidden`、`restore.conflict.*` | 只允许 Candidate；冲突和 Active 写入请求 fail-closed |

`ReminderRestorePlanner.CreatePlan` 输入 `ReminderRestorePlanRequest`：已解析且带 checksum 的文档、目标（Candidate/Active）、`DryRun`、显式确认、Candidate pipeline 就绪标记、可选 global revision 预期值，以及由集成层提供的既有 Rule/Instance 快照字典。输出 `ReminderRestorePlan`：状态（Ready/RequiresConfirmation/Conflict/Blocked）、动作、计数、错误、source checksum、`CanApply` 和安全布尔值。

- dry-run 只生成计划，不写文件/数据库；没有确认时也可用于审阅。
- `Destination.Active` 永远返回 `restore.active.write_forbidden`；计划的 `WritesActiveDatabase` 恒为 false。
- 非 dry-run 必须声明 Candidate pipeline 就绪并显式确认；否则不能 `CanApply`。
- 新 Rule 生成 `CREATE_RULE`；同 ID 且字段/修订完全一致仅 `SKIP_IDENTICAL_RULE`；任何差异都生成 `restore.conflict.rule_revision`，不覆盖。
- Schedule 只生成 `REBUILD_SCHEDULES_FROM_RULES`；所有导出的 pending 行生成 `DEFER_PENDING_SCHEDULES`，`ActivatesPendingSchedules` 恒为 false。真正排程由 Agent/P2.75 Candidate 验证后决定。
- 新 Instance 生成 `IMPORT_INSTANCE_HISTORY`；同 ID 完全一致可跳过；任何事实差异生成 `restore.conflict.instance_immutable`。
- global revision 预期值与快照不一致生成 `restore.conflict.global_revision`。

计划不包含“应用”方法，也不接受数据库连接、路径或 profile 句柄，避免在本 Slice 误引入真实恢复。

## 隔离测试

`tests/ReminNote.Tests/P3_08/ReminderExportContractTests.cs` 只在系统临时目录创建带 UUID 的独立 root，并在测试结束删除。覆盖：

1. 文件 artifact round-trip、checksum 和 canonical 再序列化；
2. 输入数组重排仍产生字节级相同输出；
3. 敏感字段拒绝与默认输出不含敏感字段；
4. 未知字段、未知版本、重复 ID、非 UTC 时间的确定性错误；
5. dry-run Candidate 计划、pending 延后、Active 写入拒绝；
6. Rule 修订冲突和 Instance 不可变历史冲突不覆盖；
7. 终态 schedule shape 与 Instance→CONSUMED schedule 关系拒绝。

测试不连接仓库数据库，不读取 `D:\Anime\.devdata\reminnote.sqlite`，不执行 migration。

## P3-09 最终集成清单

- [ ] 将 `ReminderRuleExport` 映射到 P3-01 Rule 根实体；确认 target/occurrence 存在性和缺失目标策略。
- [ ] 将 Rule revision、Schedule revision、Instance attempt/lifecycle 映射到正式 schema；补 EF 配置、唯一约束和 migration（本 Slice 未修改）。
- [ ] 通过 P2.75 Backup → Candidate → Verify → Atomic Promote 管线接入 restore plan；确认启动前 marker、fingerprint 和 fail-closed 错误码一致。
- [ ] 明确 Schedule 重建算法、pending 激活闸门、snooze/repeat origin 和 timezone provenance；禁止把 export Schedule 当作直接写入命令。
- [ ] 以 Agent/IPC 单写者边界完成 Candidate adapter，不允许 UI/插件/网络路径绕过 Agent。
- [ ] 完成 schema/version/checksum 兼容策略、未知字段策略和向前 migration 评审；不引入 merge/sync。
- [ ] 安全审查 secret/token/机器与用户隐私剥离、日志脱敏、artifact 权限和临时文件清理。
- [ ] 在隔离临时 root 执行 round-trip、冲突、失败恢复、重启和重复执行测试；不得使用真实用户数据库。
