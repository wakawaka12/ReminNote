# Changelog

本文件记录对外可见的 ReminNote 版本变化。未列出的历史提交仍保留在 Git 历史中。

## 0.3.0-alpha.2（待发布）

- 修复 TIME/RANGE Task 经 Agent 命令创建时缺少首条 ReminderRule/Schedule 的问题。
- 接通 Main/Widget 与 Agent 的受控通知宿主桥；通道不可用时保留核心提醒事实和投递回执。
- DONE 在同一 Agent 事务中记录 Task `COMPLETED`、历史并取消同 occurrence 的待处理计划。
- 增加 Agent 时钟间隔恢复检测、Quiet Hours 持久化策略和 HIGH/PIN 例外。
- 修复长路径下 P2.75 备份文件名超出安全预算的问题；完整 migration history 继续写入 manifest。
- 增加只读结构化用户数据导出、Candidate 独立 verify 和受控 promotion；promotion 先保留 Active 安全备份，校验失败或结果不确定时 fail closed。
- Portable 包统一使用 `UserData/`；补充 alpha.1 `.devdata/` 到 `UserData/` 的手动迁移说明。
- 发布脚本在打包前执行 Release 测试门禁，并区分自动化结果与人工验收状态。

限制：本版本仍未完成正常桌面人工验收、Toast registration、Windows 睡眠/唤醒和用户 sign-off；请将其视为 Alpha 预览。

## 0.3.0-alpha.1（2026-09-05）

- 首个 P3 Alpha Portable 预览包。
- 提供 P2.75 候选迁移安全边界、Task/Reminder 持久化骨架和 Agent/Main/Widget 启动骨架。
- 该预览包明确跳过正常桌面人工验收；通知宿主桥和结构化导出/恢复尚未接入。
