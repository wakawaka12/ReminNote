# ReminNote P3-07 Policy / Recovery Contract

状态：总成窗口补齐的纯策略契约，待 P3-03/P3-05/P3-06 接入审查。

基线：P2.75 `a871e06cfdeb6c9d6121aacfbf341a9a217713e3`；上游契约为
`docs/slices/P3-00-contract-freeze.md`（只读引用）。本 Slice 不创建 Reminder
数据库表，不改写 P2.75 存储、Agent 启动门禁、UI 或通知适配器，也不访问
`D:\Anime\.devdata\reminnote.sqlite`。

## 1. 边界

P3-07 只提供四个无副作用的策略 seam：

- `QuietHoursPolicy`：把已发生的 Core trigger 映射为展示决策；Quiet Hours 不改变
  `ReminderSchedule.TriggerAtUtc`、`ReminderInstance` 或 Rule 真相。
- `ReminderRepeatPolicyEvaluator`：验证 repeat 的 interval、maxCount 与 ordinal；不
  创建行、不推进 revision、不改变 logical reminder key。
- `ReminderWakePolicy`：结合 Rule 的 `WakePolicy`、profile 和 OS capability 产生
  wake 请求决策；失败只产生 `DENIED`/`UNAVAILABLE`，不伪造 reminder 成功。
- `ReminderRecoveryPolicy`：把重启/睡眠恢复时的 overdue schedule 分类为 defer、
  trigger-once、summary、expire、skip 或 reject；原始 trigger instant 只读保留，
  核心事实时间使用 `RecoveredAtUtc`。

所有结果均为稳定 code + 结构化字段，文案由上层本地化。Policy 不执行 I/O、OS
调用、通知发送或数据库写入。

## 2. 冻结语义

### Quiet Hours

`QuietHoursWindow` 是半开区间 `[Start, End)`；`Start > End` 表示跨午夜，`Start == End`
拒绝，避免把空区间误解为全天。多个区间取并集。临时 override 使用 UTC 半开区间，
重叠时按最近开始时间取胜，支持 `BYPASS` 与 `FORCE_QUIET`。

`Evaluate` 的优先级是：Core 未触发 → channel `BLOCKED`/`UNAVAILABLE` → 正常投递 →
Quiet Hours 展示策略。普通提醒可以 `SUMMARY` 或
`SUPPRESSED_QUIET_HOURS`；`HIGH`，以及策略允许的 `PIN + HIGH`，可以
`DELIVER`/`QUIET_HOURS_ESCALATED`。channel availability 与 Quiet Hours 不是同一个
状态。

### Repeat 与 wake

- `maxCount` 包含第一次核心触发；`null` 受 profile 默认安全上限约束，不表示无限。
- 默认上限为 64 次、interval 10 年；当前测试使用更小 profile 复核边界。
- `WakePolicy.NO` 不请求；`DEFAULT` 服从 profile；`YES` 仍须通过 Safe Mode 与 OS
  capability/health。最近 wakeup 只从 `REQUEST` 候选中求最小值，已 due 候选映射为
  当前时间。

### Recovery

决策输入必须显式带 occurrence cancellation、task result、已有 Core instance、
purpose、priority、pin 和必要的 task start 时间：

| purpose / 条件 | 决策 | 事实语义 |
| --- | --- | --- |
| 未到 trigger | `DEFER` | schedule 保持 `PENDING` |
| 已开始的 `TASK_PRE_START` | `EXPIRE` | `RECOVERY_OBSOLETE`，不造 instance |
| 未开始的 `TASK_PRE_START` | `TRIGGER_ONCE` | 以恢复时刻记录核心事实 |
| 普通 `TASK_START` | `SUMMARY` | 记录核心事实，展示可后聚合 |
| `HIGH` / pinned `TASK_START` | `TRIGGER_ONCE` | 以恢复时刻触发一次 |
| `TASK_RANGE_END` | `TRIGGER_ONCE` | 不重写原始 trigger |
| 有意义且不超过 24 小时的 `TASK_CUSTOM` | `TRIGGER_ONCE` | 以恢复时刻触发一次 |
| 超过窗口或无意义 `TASK_CUSTOM` | `EXPIRE` / `SKIP` | 不产生伪造的旧时间事实 |
| occurrence cancelled / task result 已记录 / Core instance 已存在 | `SKIP` | 分别为取消、结果已记录、重复保护 |
| Anime purpose | `REJECT` | 保留 reserved 值，但 P3 Task path 不执行 |

`TRIGGER_ONCE` 和 `SUMMARY` 均只产生最多一个核心事实；`UsesOriginalTriggerAtUtc`
固定为 false，确保恢复不会把“现在处理”伪装成旧时间发生。

## 3. 稳定 code 与接入约束

稳定 code 集中于 `ReminderPolicyCodes`。P3-03 可将 recovery/wake 结果转换为
schedule/instance 的一次性写入计划，但必须继续由 Agent 单 writer 串行落库。P3-05
只能消费 Quiet Hours 的展示结果；P3-04 的 DeliveryAttempt 必须继续区分
`SUPPRESSED_QUIET_HOURS`、`BLOCKED`、`UNAVAILABLE`、`FAILED`。

本 Slice 不允许：

1. 用 Quiet Hours 修改 schedule 时间或 Rule revision；
2. 用 wake capability 失败改写 Core trigger truth；
3. 把 repeat evaluator 当成无限生成器；
4. 在 recovery 中复用原 trigger 时间伪造迟到触发；
5. 添加第二 writer、直接调用 SQLite、接入真实 Toast/OS wake。

## 4. 验证矩阵

测试文件：`tests/ReminNote.Tests/P3_07/ReminderPolicyTests.cs`。

| 类别 | 覆盖 |
| --- | --- |
| Quiet Hours | 跨午夜半开边界、临时 bypass/force、summary/suppression、HIGH 与 PIN+HIGH 升级 |
| 状态隔离 | Core 未触发、channel blocked、channel unavailable 的稳定 code 区分 |
| Repeat | allowed、disabled、null maxCount profile cap、interval/count 越界、limit、确定性重放 |
| Wake | NO、DEFAULT profile、YES、Safe Mode、OS unsupported/unhealthy、nearest due wakeup |
| Recovery | future defer、pre-start obsolete、task-start summary/trigger、range end、custom 24h 窗口、cancel/result/duplicate、Anime reject |
| 输入门禁 | 空 Quiet window、无效 override、未知 WakePolicy/Purpose 的稳定验证 code |
| 安全边界 | 使用随机隔离临时 root 写入 marker；不访问受保护数据库 |

最终接入前仍需在 P3-09 运行真实 Agent restart/sleep-wake、实际 channel health 和
桌面人工验收；纯策略单测不能替代这些门禁。
