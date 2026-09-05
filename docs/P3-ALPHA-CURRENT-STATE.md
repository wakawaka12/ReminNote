# ReminNote P3 Alpha 当前总成状态

更新日期：2026-09-05  
修复基线：`a871e06cfdeb6c9d6121aacfbf341a9a217713e3`（P2.75）  
审核依据：`ReminNote_Alpha_Review_2026-09-05.md`  
发布身份：后续构建必须使用新的 alpha 版本号；不得覆盖历史 `v0.3.0-alpha.1`。

本文是 living document，描述审核修复候选的实现边界。它不是用户 sign-off，也不替代
历史 `reviews/`、`second-review/` 或审核报告；那些材料保持原样。

## 修复范围

| 发现 | 当前处理 | 自动化证据 | 仍需人工/外部证据 |
| --- | --- | --- | --- |
| R01 首条 Rule/Schedule | Agent Task create/update/continue 与显式 Rule upsert 接入同一事务；TIME/RANGE 生成首条计划，改期 rebuild | P3-03/P3-04 契约、全量测试、P25 writer receipt/journal/revision | Main/Widget 正常 UI 创建和真实 IPC 端到端 |
| R02 通知宿主 | Agent 通过 profile+宿主隔离命名管道调用 Main/Widget catalog；连接失败保持 unavailable/blocked | 通知 contract/adapter 测试、Release build | Windows 通知权限、Toast registration、真实桌面效果 |
| R03 DONE 联动 | 同一事务 resolve Instance、记录 Task `COMPLETED`/history、取消同 occurrence pending；其他 occurrence 不选中 | Agent/P25 事务测试与重复请求 contract | Reminder Center 刷新、真实 UI 操作 |
| R04 恢复分类 | 启动、回拨或超过间隔阈值的 scheduler tick 进入 recovery，并重新读取 durable state | scheduler/recovery contract tests | 真实睡眠/唤醒和系统时钟异常 |
| R05 Quiet Hours | data-root 侧车配置、多区间/跨午夜、override、HIGH/PIN 例外；抑制只记录投递事实 | policy/notification tests | 用户设置入口、结束后摘要视觉体验 |
| R06 备份路径 | migration head 超长时使用有界摘要 token，manifest 保留完整历史 | P275 定向长路径测试、全量测试 | Windows runner 长用户名/长目录复跑 |
| R07 导出/恢复 | Active 只读导出、checksum、dry-run、确认后隔离 Candidate 导入；Candidate 独立 verify、迁移锁/writer 写门、Active 备份、原子 promotion 和 post-promote verify 已接入；默认不激活旧 pending | `ReminNote.Tests.P308.UserDataExportServiceTests`、`P309.AgentUserDataCommandSurfaceTests` | Windows 真实数据升级、Candidate promotion 的桌面停机/回退演练 |
| R08 数据根 | checkout `.devdata`、Portable `UserData`、显式 `--data-root` 分离；Bootstrap 将同一 root 传给三进程 | startup/path contract、隔离 gate | 从 alpha.1 复制数据后的 Windows 进程验证 |

## 证据边界

推荐使用仓库外的新临时根运行自动化检查，例如：

```powershell
./scripts/test.ps1 -Configuration Release
./scripts/verify-p3-09.ps1 -Configuration Release -ArtifactRoot C:\Temp\ReminNote-P3-09
./scripts/package-alpha.ps1 -Version 0.3.0-alpha.2 -OutputRoot C:\Temp\ReminNote-Alpha
```

脚本产生的数据库、Candidate、migration marker、日志和 ZIP 必须位于这些临时目录，
不得指向 `D:\Anime\.devdata\reminnote.sqlite`，不得复制或连接该文件。真实进程 gate
必须显式传隔离 clone、二进制和 `--data-root`；真实进程探针会在同一隔离 root
预置 P2.5 fixture，未执行的人工项保持 `PENDING`。

## 用户数据 CLI

导出要求仓库外的绝对 artifact 路径，Active 连接只读：

```powershell
ReminNote.Bootstrap.exe --user-data-export C:\Temp\reminnote-export.json --data-root C:\Temp\reminnote-userdata
```

恢复默认只做 dry-run；只有显式 `--confirm --import-candidate` 才会在单独 Candidate 根创建候选库：

```powershell
ReminNote.Bootstrap.exe --user-data-restore C:\Temp\reminnote-export.json --candidate-root C:\Temp\reminnote-candidate --data-root C:\Temp\reminnote-userdata --confirm --import-candidate
```

Candidate 导入完成后先独立 verify：

导入输出中的 `candidateRoot` 是包含 `candidate.sqlite`、marker 和
`structured-export.json` 的目录；`stagedArtifact` 仅是其中的 JSON 文件，不能直接作为
verify/promote 的目录参数。

```powershell
ReminNote.Bootstrap.exe --user-data-verify <candidateRoot> --data-root C:\Temp\reminnote-userdata
```

操作者确认所有 Agent/宿主已退出后，才可显式 promotion：

```powershell
ReminNote.Bootstrap.exe --user-data-promote <candidateRoot> --data-root C:\Temp\reminnote-userdata --confirm
```

promotion 取得迁移锁和 writer-quiescence lease，保留 Active 安全备份，并在原子切换后重新验证；任何校验、锁、备份或状态写入失败都 fail closed，绝不自动激活旧 pending Schedule。

## 数据迁移与回退

Portable 包的 `run-alpha.cmd` 默认使用包内 `UserData/`。从 alpha.1 升级时，先退出旧
进程并把旧包 `.devdata/` 的全部内容复制到新包 `UserData/`，保留旧目录作为回退副本。
不要删除、合并覆盖或提交个人数据。若 migration 进入非 READY 状态，保留 backup、
Candidate、`runtime/migration-state.json` 和 recovery history，按状态输出处理；不在
Active 上执行 EF Down、删库重建或手动写表。

## 发布状态

自动化门禁通过后，发布清单应记录构建 commit、SDK、测试结果、ZIP SHA-256 和
`humanAcceptance=NOT_PERFORMED`（除非另有独立签署证据）。这表示可以交给会议试用，
不表示 P3 稳定完成；新的公开构建必须使用新的 alpha 版本身份。
