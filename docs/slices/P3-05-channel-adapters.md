# P3-05 Windows / Widget Channel Adapters

状态：已完成 Core adapter + Windows/Widget host-owned effect sink 接线；仍等待
P3-03 Agent trigger/due 接线、P3-06 UI action 接线和 P3-09 真实桌面验收。本文是
本 Slice 的交接说明，不宣称 Alpha 通过。

## 范围与边界

实现位于 `src/windows/ReminNote.Core/Reminders/Notifications/Adapters/`，Main 与
Widget 的组合入口分别位于：

- `src/windows/ReminNote.Windows/Notifications/WindowsNotificationChannelComposition.cs`
- `src/windows/ReminNote.Widget/Notifications/WidgetNotificationChannelComposition.cs`
- `src/windows/ReminNote.Windows/Notifications/WindowsNotificationEffectSinks.cs`
- `src/windows/ReminNote.Widget/Notifications/WidgetNotificationEffectSink.cs`

所有 adapter 只消费 P3-04 的 `NotificationDeliveryRequest`。它们不读取或写入
Rule、Schedule、Instance，不打开数据库，也不调用 Agent control pipe。核心 trigger
必须先由 Agent 记录；channel 只返回/追加 delivery attempt 所需的 channel fact。

`NotificationChannelEffectRequest` 不含标题、正文或其他自由文本，只传递 instance、
correlation/idempotency、purpose、priority、PIN snapshot 和 `logicalReminderId`。
Toast、Tray、Widget 的替换键是 `logicalReminderId` 的小写 UUID D 格式。WakeTimer
若由 Agent 提供 schedule instant，还会收到独立的 `ScheduledTriggerAtUtc`；它不改写
`TriggeredAtUtc`，避免把已记录的 core trigger 时间误当成下一次 OS wake 时间。

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

## Host-owned 真实接线与安全默认

`UnavailableNotificationChannelEffectSink` 是未验证 OS 环境的默认实现，不执行任何
Windows API、WinRT Toast、任务栏、音频或 wake 调用，固定返回 `UNAVAILABLE`。宿主组合
入口仍保留该 safe default；同时提供 `CreateHostOwned` 真实宿主入口。Main `App` 将
`WindowsNotificationChannelHost.Catalog` 注册为 Core `INotificationChannelCatalog`，
Widget `App` 在窗口创建后注册 `WidgetNotificationChannelHost`。宿主对象退出时清理
tray icon 和 waitable timer。

真实 effect 只存在宿主项目：

- Toast 通过 `combase.dll` 的 WinRT ABI 创建 notifier/document/notification，使用
  常量展示文本和 logical UUID 的 launch/tag/group；默认 AUMID registration 未验证，
  因而健康为 `UNAVAILABLE`，不会伪报 `DELIVERED`。当前未接 read-on-close callback，
  所以真实 status 不广告 `READ_ON_CLOSE`；
- Tray 通过 `Shell_NotifyIconW` 操作宿主窗口的稳定 icon slot，重复请求走 modify；
  无可见 Main HWND 时为 `UNAVAILABLE`；
- Widget 通过 `FlashWindowEx` 操作可见 Widget HWND；无 HWND 或 API 异常时为
  `UNAVAILABLE`/`FAILED`，不直接写 Reminder lifecycle；
- Sound 通过 `SystemSounds.Exclamation.Play()`；这只证明 OS API 接受播放请求，不能
  代替人工确认扬声器实际可听；
- WakeTimer 通过 `CreateWaitableTimerExW` + `SetWaitableTimer(fResume=true)`，以
  `ScheduledTriggerAtUtc` 计算绝对 FILETIME；同一 logical key 至多保留一个 live
  timer，替换成功后才关闭旧 timer，容量和 OS 错误均 fail-closed。

所有 host status 都是动态/可审计结果，adapter 只在 `HEALTHY`/`DEGRADED` 且具备
`PRESENT`（WakeTimer 还需 `WAKE`）时调用 sink。真实 API 的返回/异常被映射为
`DELIVERED`、`BLOCKED`、`UNAVAILABLE` 或 `FAILED`；Core trigger 和 ReminderInstance
始终不由通道改写。

后续接线必须满足：

1. P3-03 在 Agent 单 writer 完成 core trigger/due transaction 后才创建并派发 request；
2. 对每个 channel 使用 append-only `(instanceId, channel, idempotencyKey)` 记录，不能
   用 adapter 覆盖或回写 ReminderInstance；
3. Toast 只有在宿主完成 AUMID/Start-menu shortcut/activator 注册核验后才能将
   `ToastRegistrationVerified` 设为 true；当前默认配置不满足该核验；
4. 真实 OS presenter 必须由宿主在具备安全验证、权限和退出清理条件后使用；当前
   Slice 的 fake/safe sink 结果不能作为桌面可见性或用户 sign-off 证据；
5. Toast close/read、Widget alert action 和 Tray/Main 激活必须通过 Agent business
   command，不能由 callback 直接改变 Core lifecycle；
6. 不把 `TRAY` 的 taskbar effect 或 `WAKE_TIMER` 的 OS 不支持解释为 core trigger
   成功。

## 隔离验证

`tests/ReminNote.Tests/P3_05/NotificationChannelAdapterTests.cs` 使用每个测试独立的
系统临时 `ReminNote.P3_05/<guid>` artifact，仅保存 fake delivery-attempt JSONL；不
使用仓库数据目录或真实生产数据库。覆盖矩阵、logical replacement key、健康/能力
分类、scheduled instant 与 recorded trigger 分离、status probe fail-closed、sink
failure/cancellation、Quiet Hours suppression 边界以及 coordinator replay 幂等。

本窗口未启动现有桌面进程，也未触碰正式数据；因此真实 Toast、Tray、Widget、taskbar、
sound 的可见性/权限和 wake timer 的 sleep/wake 行为仍是 P3-09 的 real-process +
desktop manual 证据，不能由本次编译或 fake 测试替代。
