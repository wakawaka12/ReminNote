# P3-04 Reminder notification delivery

状态：Agent 生命周期接线与 durable committed-trigger 对账已完成，等待总成窗口
审查与实际桌面环境验收。

本窗口完成 Reminder notification delivery 的 Agent 生产闭环接缝：核心
`ReminderInstance` 仍由 P3-03 的 durable due transaction 产生；P3-04 只消费
提交后的 `NotificationTriggerFact`，并把渠道投递作为 Agent 单写入器内的追加式
子事实保存。没有改写 P3-03 的 Rule/Schedule/Instance owner，也没有在 UI 或
通道 adapter 中增加第二个数据库写入口。

## 持久化投递尝试

`src/windows/ReminNote.Core/Reminders/Notifications/NotificationDeliveryDurability.cs`
定义了 `NotificationDeliveryAttemptRecord` 和
`INotificationDeliveryAttemptJournalStore`。一条投递尝试的事件链为：

1. `PENDING`：写入外部 channel 前提交，带 Instance/Schedule/Rule/Occurrence/
   LogicalReminder 快照、channel、correlation、RequestId、idempotency key、
   attempt ordinal、priority/pin/purpose 和时间事实；
2. terminal event：外部 channel 返回后追加 `DELIVERED`、`BLOCKED`、
   `UNAVAILABLE`、`FAILED`、`SUPPRESSED_QUIET_HOURS` 或 `NOT_ATTEMPTED`，并记录
   stable error、retryable 和 next-attempt time。

同一 `attemptId` 使用递增 `eventOrdinal`，事件行从不 UPDATE。每次追加都经过
P2.5 `P25StorageWriter`，因此 attempt event、全局 revision、change journal 和
command receipt 在同一事务中提交。相同 effect idempotency key 重放时不调用
channel；新的重试保持同一 Instance/LogicalReminderId/channel intent，生成新的
AttemptId、idempotency key 和 RequestId。发生进程崩溃时，未完成的 `PENDING` 与
已到期的 retryable terminal event 都可从 durable projection 重新发现。

新增的 `notification_delivery_attempt_events` migration 是纯追加 P3-04 migration，
只创建投递尝试事件表和索引，不重写 P3-03 表或 P3-03 migration。Agent 启动在
P2.75 promote/verify 后只读核对该表；缺失 migration 会 fail-closed，不会对 Active
数据库做 DDL fallback。

## Agent 分发与恢复

`src/windows/ReminNote.Agent/Notifications/NotificationDeliveryRuntime.cs`
提供：

- `NotificationDeliveryDispatcher`：单进程 effect gate + durable pending/outcome
  两阶段分发；channel 缺失、能力不符、blocked、unavailable、policy suppression
  和异常都会变成明确 attempt outcome；
- `NotificationRetryPolicy`：有界次数和指数退避，区分可重试的 unavailable/
  transient failure、Quiet Hours 与不可重试的 capability/idempotency/channel
  contract failure；
- `RecoverAsync`：重启时恢复 pending，用原 effect key 做安全 replay；已到期的
  retryable failure 用新 key/new RequestId 开新 attempt；
- Agent 的每个正常 scheduler tick 也会先处理已到期的 notification retry，避免
  retry 只在进程启动时被消费；scheduler 的 due wakeup 仍只由 durable schedule 驱动；
- `INotificationCommittedTriggerRecoverySource`：由 P3-03 durable store 提供已提交但
  尚未形成 attempt 的 core trigger 对账源，覆盖“core commit 后、PENDING append 前”
  的进程崩溃窗口；对账仍走同一 dispatcher 的逻辑去重，不会创建第二个 Instance；
- `NotificationDeliverySafetyGate`：安全模式下不写 attempt、不调用外部 channel，
  也不把安全模式当作 core trigger 失败；
- `ReminderNotificationRuntime`：只处理
  `ReminderDueResult.ShouldDispatch == true` 且 scheduler 的
  `IReminderDueTransaction.CommitAsync` 已返回之后的 `TriggerFact`。因此通道失败
  不能再次创建 `ReminderInstance`。

## P3-05 宿主 channel 注入

`INotificationChannelCatalogSnapshot` 和
`CompositeNotificationChannelCatalog` 是宿主到 Agent Runtime 的明确注入 seam；
`INotificationCommittedTriggerRecoverySource` 是 scheduler 到通知恢复的只读对账 seam。
`AgentRuntime` 现在正式实例化 `AgentNotificationRuntimeComposition.Create`，把它绑定到
已打开的 Agent `P25StorageStore`；attempt store、scheduler 和 recovery source 共用
同一 Agent writer，不会从 Agent 另开写连接或绕过单写入器。

`AgentCommittedReminderTriggerRecoverySource` 使用 P3-03 concrete schema 的只读
SQLite/query-only connection 查询已提交 `ReminderInstance`，再按注入 catalog 的每个
channel 查询 durable current attempt。所有 channel 都已是不可重试 terminal event 时
不再返回该 core fact；缺一条、PENDING 或 retryable 时才交给统一 dispatcher 对账。

P3-05 的能力接线保持原 owner：

- Windows catalog：`TOAST`、`TRAY`、`SOUND`、`WAKE_TIMER`；
- Widget catalog：`WIDGET`，实际 effect 是 Widget HWND 的 flash；
- taskbar flash 复用冻结的 `TRAY` channel identity，不新增数据库 channel；
- 两个宿主 catalog 可用 `CompositeNotificationChannelCatalog` 合并为完整 P3
  catalog，并拒绝重复 channel owner。

宿主默认仍然 fail-closed：Toast registration 未验证、窗口不可见、系统能力不可用
或 wake timer 建立失败时，adapter 返回明确的 unavailable/blocked/failed，而不伪报
成功。Agent 自身没有 Main/Widget 的进程内对象；没有受控 host bridge 时使用
`AgentNotificationChannelComposition.CreateFailClosed` 注册五个已知 adapter 形状，
并由 durable dispatcher 写出 `UNAVAILABLE`（或能力检查产生的 `NOT_ATTEMPTED`），
不会绕过 writer。Toast close、Widget action 和窗口激活仍必须回到 Agent business
command，不能由 UI callback 直接改 Reminder lifecycle。

## 测试与验证

`tests/ReminNote.Tests/P3_04/` 新增了隔离 SQLite/P2.5 writer 和 Agent runtime
测试，覆盖：

- commit-before-dispatch；
- attempt append、terminal append、receipt/journal/revision；
- effect key replay、logical dedup、new-key retry；
- channel failure 不重复 core Instance；
- 退避、到期恢复、不可重试失败和安全模式；
- 进程重启后的 pending recovery；
- 既有 P3-04 contract/coordinator、P3-05 adapter 和旧 P2 测试。

验证使用直接 SDK 10.0.400、locked restore，并在仓库外临时 clone/root 中执行。测试
只使用临时 SQLite 文件；未访问 `D:\Anime\.devdata\reminnote.sqlite`。

## 未决集成点与人工项

当前仍有未决集成点：AgentRuntime 需要由总成接入实际 host catalog/受控 bridge，
并将 P3-03 concrete scheduler 的 committed trigger recovery source 绑定到通知
runtime；这些接线完成后再运行 P3-09 的真实进程门禁。

- P3-05 的真实 Windows API 可见性、Toast AUMID/Start-menu registration、音频听感、
  Widget HWND 和 sleep/wake timer 需要 P3-09 real-process/desktop 验收；本窗口不改
  P3-09 脚本。
- Windows/Widget host catalog 仍只存在各自宿主进程；当前总成接入点保留
  `INotificationChannelCatalog`/`INotificationChannelCatalogSnapshot`，但没有伪造一个
  跨进程共享的 Main/Widget 对象。后续若要启用真实效果，需要单独的受控 IPC/bridge
  证据，把 host-owned effect port 注入 `AgentNotificationRuntimeComposition.Create`；
  本窗口不改 UI 行为，也不把 fail-closed 默认误报为桌面成功。
