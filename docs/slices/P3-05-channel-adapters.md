# P3-05 Windows / Widget Channel Adapters

状态：完成可验证隔离 seam，等待 P3-03 Agent trigger/due 接线、P3-06 UI 接线和 P3-09
真实桌面验收。本文是本 Slice 的交接说明，不宣称生产通知或 Alpha 通过。

## 范围与边界

实现位于 `src/windows/ReminNote.Core/Reminders/Notifications/Adapters/`，Main 与
Widget 的组合入口分别位于：

- `src/windows/ReminNote.Windows/Notifications/WindowsNotificationChannelComposition.cs`
- `src/windows/ReminNote.Widget/Notifications/WidgetNotificationChannelComposition.cs`

所有 adapter 只消费 P3-04 的 `NotificationDeliveryRequest`。它们不读取或写入
Rule、Schedule、Instance，不打开数据库，也不调用 Agent control pipe。核心 trigger
必须先由 Agent 记录；channel 只返回/追加 delivery attempt 所需的 channel fact。

`NotificationChannelEffectRequest` 不含标题、正文或其他自由文本，只传递 instance、
correlation/idempotency、purpose、priority、PIN snapshot 和 `logicalReminderId`。
Toast、Tray、Widget 的替换键是 `logicalReminderId` 的小写 UUID D 格式。

## Frozen channel / capability matrix

| Channel | Surface effect | Capabilities |
| --- | --- | --- |
| `TOAST` | Windows Toast effect sink | `PRESENT`, logical replace, read-on-close |
| `TRAY` | Tray state/attention effect sink | `PRESENT`, logical replace |
| `WIDGET` | Widget alert effect sink | `PRESENT`, logical replace, read-on-close |
| `SOUND` | Sound effect sink | `PRESENT` |
| `WAKE_TIMER` | Wake timer effect sink | `PRESENT`, `WAKE` |

taskbar flash 是 `TRAY` 的 `TASKBAR_FLASH` surface effect，不新增 `TASKBAR` channel
ID，避免突破 P3-04/P3-00 的稳定 allow-list。`TrayNotificationChannel` 与
`TaskbarFlashNotificationChannel` 是互斥的 catalog 选择。

health 由 `INotificationChannelStatusSource` 提供，和 capability 正交：

- 缺少 `PRESENT` → `NOT_ATTEMPTED / capability_missing`；
- `WAKE_TIMER` 还必须同时具备 `WAKE`，否则同样是 `NOT_ATTEMPTED / capability_missing`；
- `BLOCKED` → `BLOCKED`；
- `UNAVAILABLE` 或 `UNKNOWN` → `UNAVAILABLE`；
- 健康/降级才调用 effect sink；
- sink 异常 → `FAILED`，取消 → `FAILED / notification.delivery.cancelled`。

adapter 不实现 Quiet Hours。coordinator 先执行 `INotificationPresentationPolicy`；
`SUPPRESSED_QUIET_HOURS` 由 policy 产生，adapter 拒绝把它伪造成外部成功。priority
与 PIN 只作为不可变 snapshot 传递，`HIGH`、`PIN + HIGH` 的升级/summary 规则仍由
P3-07 policy 决定。

## 安全默认与后续接线

`UnavailableNotificationChannelEffectSink` 是未验证 OS 环境的默认实现，不执行任何
Windows API、WinRT Toast、任务栏、音频或 wake 调用，固定返回 `UNAVAILABLE`。宿主组合
入口默认使用该 sink；提供 `DelegateNotificationChannelEffectSink` 供隔离 fake 或经
审批的宿主 presenter 注入。

后续接线必须满足：

1. P3-03 在 Agent 单 writer 完成 core trigger/due transaction 后才创建并派发 request；
2. 对每个 channel 使用 append-only `(instanceId, channel, idempotencyKey)` 记录，不能
   用 adapter 覆盖或回写 ReminderInstance；
3. 真实 OS presenter 必须由宿主在具备安全验证、权限和退出清理条件后注入；当前
   Slice 的 fake/safe sink 结果不能作为桌面可见性或用户 sign-off 证据；
4. Toast close/read、Widget alert action 和 Tray/Main 激活必须通过 Agent business
   command，不能由 callback 直接改变 Core lifecycle；
5. 不把 `TRAY` 的 taskbar effect 或 `WAKE_TIMER` 的 OS 不支持解释为 core trigger
   成功。

## 隔离验证

`tests/ReminNote.Tests/P3_05/NotificationChannelAdapterTests.cs` 使用每个测试独立的
系统临时 `ReminNote.P3_05/<guid>` artifact，仅保存 fake delivery-attempt JSONL；不
使用仓库数据目录或真实生产数据库。覆盖矩阵、logical replacement key、健康/能力
分类、status probe fail-closed、sink failure/cancellation、Quiet Hours suppression
边界以及 coordinator replay 幂等。

真实 Toast、Tray、Widget、taskbar、sound、wake timer 的可见性、权限和 sleep/wake
行为留给 P3-09 的 real-process + desktop manual 验收。
