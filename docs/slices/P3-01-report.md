# P3-01 实现与验证报告

日期：2026-09-02
基线：`a871e06cfdeb6c9d6121aacfbf341a9a217713e3`
契约：P3-00 ReminderRule → ReminderSchedule → ReminderInstance 最小契约

## 已交付

- `ReminNote.Core.Reminders.Domain`：UUID v7 身份、受限 timing/repeat 值对象、Rule/ Schedule/Instance 三层领域模型，以及 append-only `ReminderDeliveryAttempt` 子记录。
- Rule 负责业务真相和 `RuleRevision` 单调更新；完全相同更新是 no-op。
- Schedule 只允许 PENDING → terminal 单向转换；Snooze/Repeat 生成新 ID、origin 和 logical chain，旧行保留。
- Instance 事实字段只读，生命周期单向推进，ResolutionAction 冲突拒绝、相同动作幂等；Task 仅启用 DONE/SNOOZE/SKIP/IGNORE。
- `Persistence/Reminders`：四个关系实体、FromDomain/ToDomain 映射、UTC Instant/UUID v7/uppercase enum 文本约束、allow-list check、唯一索引、due/occurrence/logical 索引、Restrict FK 和并发 token。
- 映射配置不实现 `IEntityTypeConfiguration<T>`，通过 `ApplyReminderConfigurations(ModelBuilder)` 显式接入，避免现有 `ReminNoteDbContext` 的 assembly scan 提前改变 P2 model。
- 新建 `tests/ReminNote.Tests/P3_01`：领域合法/非法构造、修订与历史不可变、Snooze 派生、生命周期/动作幂等、映射 round-trip、模型索引和重复 revision 约束；SQLite 使用随机 TEMP root，测试结束先释放 context/connection 再删除目录。

## 验证

使用本机 `C:\Program Files\dotnet\dotnet.exe` 10.0.400（未修改 `global.json`）：

```text
dotnet restore tests/ReminNote.Tests/ReminNote.Tests.csproj --locked-mode  PASS
dotnet build tests/ReminNote.Tests/ReminNote.Tests.csproj --configuration Release --no-restore  PASS (0 warning, 0 error)
ReminNote.Tests.exe -class ReminderDomainTests -class ReminderPersistenceMappingTests  PASS (8/8)
ReminNote.Tests.exe  PASS (340/340, 0 failed)
```

验证未读取、打开或写入 `D:\Anime\.devdata\reminnote.sqlite`，未修改 DbContext、迁移、csproj 或共享审查材料。

## 后续接入点与风险

- P3-03 需在总成迁移窗口将 `ReminderModelBuilderExtensions.ApplyReminderConfigurations()` 显式接到目标 DbContext，并生成 forward migration/model snapshot；本窗口不生成迁移。
- P3-03/P3-06 需在 Agent writer transaction 中实现 due consume、Instance append、Snooze/Repeat 派生和 terminal cancellation；不要在 UI 或第二 DbContext 写入。
- P3-02/P3-07 仍需冻结 Task 时间形状到 UTC 的计算、迟到恢复窗口、repeat 上限和 wake/Quiet Hours 策略；本实现保留 timing/chain seam，不读取隐式默认时钟。
- 迁移接入时应复核现有 P2 enum INTEGER 与 P3 uppercase TEXT 的列隔离、Restrict FK、SQLite check expression 和并发 token 生成策略。
