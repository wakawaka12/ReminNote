# ReminNote

ReminNote 是面向 Windows 的本地优先提醒与个人记录应用，目标是提供可验证、可恢复且严格开源的 Task 工作流。

## 当前版本：首个 P3 Alpha

`0.3.0-alpha.1` 是首个 P3 Alpha 构建包，适合 Alpha 会议前试用和问题收集，不代表稳定版或最终验收结论。本轮按计划跳过正常桌面人工验收。

已包含：

- Task 与 Reminder 的持久化、调度和历史事实边界；
- Agent/Main/Widget 的自包含运行骨架；
- P2.75 数据迁移安全边界（备份、校验、失败闭锁和恢复入口）；
- 可追溯的版本标识与 `win-x64` Portable 打包。

### 下载与启动

从 [GitHub Releases](https://github.com/wakawaka12/ReminNote/releases/tag/v0.3.0-alpha.1) 下载：

`ReminNote-0.3.0-alpha.1-win-x64.zip`

解压到独立目录后运行 `run-alpha.cmd`，或直接运行 `ReminNote.Bootstrap.exe`。这是自包含 x64 包，不需要另行安装 .NET Runtime；数据默认写入包目录下的 `.devdata`。请勿把个人数据目录提交到仓库或上传到公开位置。

## Alpha 已知限制

- Toast/Tray/Widget/Sound/WakeTimer 的跨进程通知宿主桥接尚未启用；
- Windows 睡眠/唤醒、真实桌面通知效果和用户 sign-off 尚未完成人工验收；
- P3-08 生产级结构化导出与受控恢复 Candidate 流程仍待后续迭代；
- 本包用于会议前试用和收集问题，不建议承载唯一生产数据。

## 开发与打包

开发构建需要 .NET 10 LTS SDK。常用门禁命令：

```powershell
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release
```

生成首个 Alpha Portable 包：

```powershell
./scripts/package-alpha.ps1 -Version 0.3.0-alpha.1 -OutputRoot C:\RNAlpha
```

产物目录必须位于工作树之外；脚本会生成 ZIP、SHA-256 sidecar 和版本清单。构建、测试、架构和安全约束详见 `DEVELOPMENT.md`、`ARCHITECTURE.md` 与 `SECURITY.md`。

## 项目文档

- [许可证](LICENSE)
- [贡献指南](CONTRIBUTING.md)
- [开发说明](DEVELOPMENT.md)
- [产品规则](PRODUCT_RULES.md)
- [架构说明](ARCHITECTURE.md)
- [安全说明](SECURITY.md)
- [路线图](ReminNote_MASTER_DEVELOPMENT_PLAN.md)
