# P3-04 通知抽象与渠道投递契约

状态：本窗口实现完成，等待总成窗口审查与接入。本文只描述 P3-04 owner 范围，不改变 P3-00/P3-01/P3-02/P3-07 的文件或持久化实现。

## 交付范围

实现位于 `src/windows/ReminNote.Core/Reminders/Notifications/`，测试位于
`tests/ReminNote.Tests/P3_04/`。没有接入 Agent runtime、Named Pipe、Windows Toast/UI、DbContext/Migrations 或真实系统通知，也没有增加依赖。

核心类型分为四组：

- `NotificationChannelId`、`NotificationCapability`、`NotificationChannelHealth`、`NotificationChannelStatus`：稳定的渠道身份、能力和健康快照。P3-00 冻结的 `TOAST`、`TRAY`、`WIDGET`、`SOUND`、`WAKE_TIMER` 是状态/投递允许的渠道；能力与健康正交，健康渠道仍可能缺少 `PRESENT`。
- `NotificationTriggerFact`、`NotificationDeliveryRequest`：Agent 已记录的 `ReminderInstance` 核心触发快照和渠道请求。请求不包含自由文本；`logicalReminderId` 只作为 adapter 的 replace/update key 输入。
- `NotificationDeliveryAttempt`、`INotificationDeliveryAttemptStore`、`NotificationDeliveryCoordinator`：渠道尝试的不可变 append-only 子事实。唯一性由 `(instanceId, channel, idempotencyKey)` seam 表达；重复键按 intent 校验后返回 `REPLAYED`，不同 intent 稳定返回 `notification.idempotency.conflict`。
- `NotificationLifecycleState` 与 `INotificationPresentationPolicy`：ReminderInstance 的 `UNREAD → READ → RESOLVED` 单向生命周期，以及不复制 Quiet Hours 规则的策略输入/结果 seam。Toast close 只能调用 `ApplyToastClose` 产生 READ，不能直接 RESOLVED。

## 结果语义

`NotificationDispatchResult` 同时携带核心事实和渠道事实：只要 caller 已提供 `NotificationTriggerFact`，结果的 `CoreTriggerWasRecorded` 就为 true；渠道缺少 `PRESENT` 能力时是 `NOT_ATTEMPTED/capability_missing`，渠道 `BLOCKED` 是 `BLOCKED`，`UNAVAILABLE/UNKNOWN` 是 `UNAVAILABLE`，adapter 自身失败是 `FAILED`。这些结果都追加 delivery attempt，不能把渠道失败改写成“没有触发”。没有核心 Instance 时由上游返回 `NotificationDispatchResult.NotTriggered(reasonCode)`，该结果不带 attempt。

成功、失败、阻塞、不可用、Quiet Hours 抑制和未尝试均可独立追加；重试必须使用新的 idempotency key，原记录不覆盖。相同 key 的重放不再次调用渠道，重启后的 store adapter 可从 append-only 记录恢复并返回 `REPLAYED`。`DELIVERED` 不允许携带 error code，所有可持久化 code 均为有限长度小写稳定码。

`NotificationContractJson` 提供 status、attempt、lifecycle 的严格有限 JSON 编解码：字段集合固定、未知字段/重复字段/BOM/超限/非法 enum 或时间均拒绝；UUID 采用小写 D 格式 UUID v7，Instant 采用九位小数 UTC 格式。JSON 只含身份、快照和稳定结果码，不含用户文案。

## 未来最小适配接口

Agent 侧最小接入只需实现 `INotificationChannelCatalog`、`INotificationDeliveryAttemptStore`，并在 Agent 单写事务/串行 gate 内保证 `(instanceId, channel, idempotencyKey)` 的原子唯一约束；due 事务先提交 ReminderInstance，再把 `NotificationDeliveryRequest` 交给 `NotificationDeliveryCoordinator`，不能由渠道 adapter 写 Rule/Schedule/Instance。

Windows adapter 只需实现 `INotificationChannel`：返回自身 `ChannelId`、可缓存或刷新 `GetStatus()`，并在 `DeliverAsync` 中执行一次外部呈现后返回 `NotificationChannelDeliveryResponse`。adapter 不实现 Quiet Hours；策略由 `INotificationPresentationPolicy` 决定。Toast 回调只能转成 Agent 的 READ command，不能直接改 Core lifecycle；UI/Toast API、真实文案和 replace 行为留在后续 Windows slice。

## 验证

在隔离临时目录下的 `P3_04` 测试覆盖：能力缺失、渠道 blocked/unavailable、成功/失败/新 key 重试、重复投递、重启重放、idempotency 冲突、Quiet Hours 策略抑制、生命周期非法状态与幂等、Toast close 边界，以及 JSON round-trip、未知/重复字段、BOM、超限和非法值。

验证命令（使用仓库现有 .NET 10 SDK 的绝对路径）：

```text
"C:\Program Files\dotnet\dotnet.exe" restore tests/ReminNote.Tests/ReminNote.Tests.csproj --locked-mode
"C:\Program Files\dotnet\dotnet.exe" build tests/ReminNote.Tests/ReminNote.Tests.csproj --configuration Release --no-restore --nologo
tests/ReminNote.Tests/bin/Release/net10.0/ReminNote.Tests.exe -noLogo -noColor -class "ReminNote.Tests.P3_04.*"
```

结果：构建 0 警告/0 错误；P3-04 测试 10/10 通过。测试只写入 `%TEMP%/ReminNote.P3_04/<guid>/`，未访问受保护数据库。

## 未决集成点

- P3-01 需要把 `NotificationDeliveryAttempt` 映射为 append-only child storage，并在 Agent writer gate 中实现原子唯一键和启动恢复加载。
- P3-02/P3-07 负责把 ReminderInstance 的真实 core snapshot、Quiet Hours/DST 策略结果接入本 seam；本 Slice 未复制这些规则。
- 后续 Agent/Windows slice 负责文案 read model、Toast/Tray/Widget/Sound/WakeTimer adapter、READ command 和外部错误码映射；本 Slice 不承诺系统通知可见性。
