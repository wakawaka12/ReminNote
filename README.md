# ReminNote

ReminNote 是面向 Windows 的本地优先提醒与个人记录应用，目标是提供可验证、可恢复且严格开源的 Task 工作流。

## 当前版本：P3 Alpha 修复候选

`0.3.0-alpha.1` 是历史首个 P3 Alpha 构建包；当前分支包含审核报告后的修复候选，下一
个对外版本使用新的 `0.3.0-alpha.2` 身份。修复候选适合在新建隔离目录中试用和收集
问题，不代表稳定版或最终验收结论。本轮仍未执行正常桌面人工验收。

已包含：

- Task 与 Reminder 的持久化、调度和历史事实边界；
- Agent/Main/Widget 的自包含运行骨架；
- P2.75 数据迁移安全边界（备份、校验、失败闭锁和恢复入口）；
- Agent 到 Main/Widget 的受控通知宿主桥、Quiet Hours 配置和时钟间隔恢复；
- 只读结构化用户数据导出、Candidate 验证与受控恢复切换；
- 可追溯的版本标识与 `win-x64` Portable 打包。

### 下载与启动

历史首个包仍可从 [GitHub Releases](https://github.com/wakawaka12/ReminNote/releases/tag/v0.3.0-alpha.1) 下载：

`ReminNote-0.3.0-alpha.1-win-x64.zip`

审核修复候选尚未覆盖历史 Release；需要试用当前工作树时，请按下方命令在仓库外生成
新的、独立的 Portable 包。

解压到独立目录后运行 `run-alpha.cmd`，或直接运行 `ReminNote.Bootstrap.exe --data-root <数据目录>`。这是自包含 x64 包，不需要另行安装 .NET Runtime；`run-alpha.cmd` 默认把数据写入包目录下的 `UserData/`。请勿把个人数据目录提交到仓库或上传到公开位置。

### 用户数据导出与 Candidate 恢复

导出必须指定仓库外的绝对 artifact 路径；Agent 只读 Active：

```powershell
ReminNote.Bootstrap.exe --user-data-export C:\Temp\reminnote-export.json --data-root C:\Path\To\UserData
```

恢复默认是 dry-run。只有同时提供 `--confirm --import-candidate` 才会在独立 Candidate 根创建候选库，仍不会切换或覆盖 Active：

```powershell
ReminNote.Bootstrap.exe --user-data-restore C:\Temp\reminnote-export.json --candidate-root C:\Temp\reminnote-candidate --data-root C:\Path\To\UserData --confirm --import-candidate
```

Candidate stage 目录中生成 `candidate-restore.json` 后，先做只读验证：

导入命令会同时输出 `stagedArtifact`（结构化 JSON 文件）和 `candidateRoot`（包含
`candidate.sqlite`、marker 与 artifact 的目录）；后续 verify/promote 使用 `candidateRoot`。

```powershell
ReminNote.Bootstrap.exe --user-data-verify <candidateRoot> --data-root C:\Path\To\UserData
```

确认停掉 Agent/宿主并准备切换时，才显式执行受控 promotion；它会取得 profile 迁移锁和 writer-quiescence lease，先保留 Active 安全备份，再进行原子切换和 post-promote verify：

```powershell
ReminNote.Bootstrap.exe --user-data-promote <candidateRoot> --data-root C:\Path\To\UserData --confirm
```

验证失败、锁被占用、checksum/schema/profile 不匹配或切换结果不确定都会 fail closed；禁止把 Active 数据库路径作为 artifact 或 Candidate 路径。

## Alpha 已知限制

- 通知宿主桥已接入，但 Toast registration、Windows 睡眠/唤醒和真实桌面通知效果尚未完成人工验收；
- 结构化导出可生成独立 artifact；恢复默认 dry-run，Candidate 需要独立 verify，只有显式 `--confirm` promotion 才会写入 Active；
- 用户 sign-off 尚未完成，本包仍不建议承载唯一生产数据；
- 本包用于会议前试用和收集问题，不建议承载唯一生产数据。

## 开发与打包

开发构建需要 .NET 10 LTS SDK。常用门禁命令：

```powershell
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release
```

生成下一个 Alpha Portable 修复候选包：

```powershell
./scripts/package-alpha.ps1 -Version 0.3.0-alpha.2 -OutputRoot C:\RNAlpha
```

产物目录必须位于工作树之外；脚本会生成 ZIP、SHA-256 sidecar 和版本清单。构建、测试、架构和安全约束详见 `DEVELOPMENT.md`、`ARCHITECTURE.md` 与 `SECURITY.md`。

## 项目文档

- [许可证](LICENSE)
- [贡献指南](CONTRIBUTING.md)
- [开发说明](DEVELOPMENT.md)
- [产品规则](PRODUCT_RULES.md)
- [架构说明](ARCHITECTURE.md)
- [安全说明](SECURITY.md)
- [P3 Alpha 当前总成状态](docs/P3-ALPHA-CURRENT-STATE.md)
- [变更记录](CHANGELOG.md)
- [第三方声明](THIRD-PARTY-NOTICES.md)
- [路线图](ReminNote_MASTER_DEVELOPMENT_PLAN.md)
