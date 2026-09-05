# ReminNote

ReminNote 是面向 Windows 的提醒与个人记录应用，目标是提供本地优先、可验证且严格开源的 Task 工作流。

当前仓库已完成 P3 Alpha 第一版（`0.3.0-alpha.1`）：包含 Task + Reminder 持久化调度、Agent/Main/Widget 运行骨架、P2.75 数据迁移安全边界，以及可复现的自包含 x64 Portable 打包。此版本用于 Alpha 会议前试用和问题收集，正常桌面人工验收按本轮决定暂不执行；通知宿主桥接、睡眠/唤醒和生产导出恢复仍在后续迭代范围内。

开始贡献前请阅读：

- [许可证](LICENSE)
- [贡献指南](CONTRIBUTING.md)
- [开发说明](DEVELOPMENT.md)
- [产品规则](PRODUCT_RULES.md)
- [架构说明](ARCHITECTURE.md)
- [安全说明](SECURITY.md)
- [路线图](ReminNote_MASTER_DEVELOPMENT_PLAN.md)

开发构建前需要安装 .NET 10 LTS SDK。构建、测试和门禁命令见 `DEVELOPMENT.md`。生成 Alpha Portable 包可运行 `./scripts/package-alpha.ps1`；产物应输出到工作树之外的目录。
