# P3-03 Agent durable Reminder 接线

状态：P3-03 核心实现已落地，等待总成窗口在隔离 Candidate 中完成迁移与真实宿主验收。本文不改变 P3-09 gate，也不宣告 P3 Alpha 或桌面人工验收完成。

## 已完成

- `ReminNoteDbContext` 的正式 options 接入 `ReminderRule`、`ReminderSchedule`、`ReminderInstance` 和 delivery attempt 配置；旧 P2/P2.5 in-memory fixture 保持独立的 P2-only model/迁移前缀。
- 新增 `P3ReminderPersistence` forward migration、Designer 和 model snapshot。正式文件型数据库由 Agent migration plan 从 P2.5 前进到该 migration。
- `AgentReminderSchedulerStore` 通过 P2.5 `P25StorageWriter` 完成 due re-check、schedule consume、Instance append、repeat derived schedule、terminal cancellation，以及 revision/journal/receipt/idempotency。
- Agent 启动后先执行 durable recovery，再按持久化 pending schedule 计算唤醒；无内存 schedule cache，也没有第二 Task writer。
- `TaskRecordResult` 在相同 Agent writer transaction 内取消 occurrence 的未来 pending schedule；Reminder mark-read/resolve 也走现有 business pipe 与同一 writer。

## 边界

读路径使用 SQLite read-only/query-only connection；所有 Reminder 写入必须绑定 P2.5 writer transaction。通知 channel、WPF/Widget UI 和 P3-09 gate 脚本仍由各自 owner 负责。

## 本窗口验证

- 直接 SDK 10.0.400 locked restore/build。
- P3_03 namespace tests：13/13。
- 全量 ReminNote.Tests MTP executable：429/429。
- P3_03 测试使用 `%TEMP%` 下隔离 SQLite 文件，覆盖 forward migration、rollback/reapply、due consume+Instance append、restart recovery、writer revision/journal/receipt 和 Task-result cancellation。

## 未覆盖的真实项

尚未在真实用户桌面数据库或受保护开发数据库上运行；尚未执行 Named Pipe/WPF/系统通知、sleep/wake 和 P3-09 最终 gate。上述项目不属于本窗口的安全验证范围。
