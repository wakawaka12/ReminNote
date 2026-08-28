# P2-05 GitHub CI 测试门禁修复 Slice

- 日期：2026-08-28
- 范围：`.github/workflows/ci.yml` 的 Restore/Build/Test 门禁路径
- 状态：本地验证完成；待 GitHub Actions 在远端重新执行确认

## 根因

原 CI 最后执行：

```text
dotnet test ReminNote.sln --configuration Release --no-build --no-restore
```

当前仓库使用 xUnit v3/Microsoft Testing Platform。该 legacy `dotnet test` 路径没有按仓库测试脚本直接启动测试 executable，实际出现 0 个测试并以退出码 5 失败；它不能作为本仓库的真实测试证据。

## 改动

- 保留 `Environment diagnostics`，继续输出 SDK、环境和 solution 项目列表。
- 保留显式 `P0-07 repository verification` 步骤。
- 将 CI 的 Release 构建改为 `./scripts/build.ps1 -Configuration Release`。该脚本执行 locked restore，并完成 Release solution build。
- 将 CI 的测试改为 `./scripts/test.ps1 -Configuration Release`。该脚本执行 locked restore、构建测试项目、直接运行 `ReminNote.Tests.exe`，并在末尾执行 P0-07；不再调用 legacy `dotnet test` 零发现路径。
- 本 Slice 只修改 workflow 与本记录；没有修改产品源代码、测试源代码、`reviews/` 或 `second-review/`，也没有引入 Reminder、Agent/IPC 或 Sync。

## 本地证据

在 `C:\Users\EMT\.codex\worktrees\c438\Anime` 执行：

| 命令 | 预期/实际结果 |
|---|---|
| `scripts/build.ps1 -Configuration Release` | 退出码 0；8 个项目成功构建；无 warning/error |
| `scripts/test.ps1 -Configuration Release` | 退出码 0；真实 `ReminNote.Tests.exe` 执行 `161/161`，0 error、0 failed、0 skipped、0 not run；脚本内 P0-07 通过 |
| `scripts/verify-p0-07.ps1` | 退出码 0；213 个资源键、7 个锁定项目、1 个测试项目及 Shell/TODAY/ANIME/Widget 标记通过 |
| `git diff --check` | 退出码 0；无 whitespace errors |

## CI 预期

GitHub Actions 应依次完成环境诊断、显式 P0-07、`scripts/build.ps1` 的 locked restore/Release build，以及 `scripts/test.ps1` 的真实测试 executable 和 P0-07。测试摘要应显示非零测试数量（当前基线为 161），不应出现 `Total: 0` 或退出码 5。

## 人工验收步骤

1. 推送由总成窗口决定的包含本提交的分支后，在 GitHub Actions 打开对应 CI run。
2. 确认 `Environment diagnostics` 输出 .NET 10 SDK 和 solution 项目列表。
3. 确认 `P0-07 repository verification` 通过。
4. 确认 `Restore and Release build` 调用 `scripts/build.ps1` 并报告 locked restore、Release build 成功。
5. 确认 `Real tests and P0-07` 输出真实 xUnit executable 的非零测试摘要，且 P0-07 再次通过；不得以 `dotnet test` 的 0 测试输出作为成功证据。

## 失败判定与边界

出现以下任一情况即判定本 Slice 失败：

- workflow 仍调用 `dotnet test ReminNote.sln`，或测试步骤显示 0 个测试、退出码 5；
- locked restore、Release build、真实测试 executable 或 P0-07 任一步骤未执行/失败；
- CI 测试只构建不运行，或只依赖 legacy adapter/VSTest 零发现路径；
- 环境诊断或显式 P0-07 步骤被删除；
- 为修 CI 改动产品业务、Reminder、Agent/IPC、Sync、`reviews/` 或 `second-review/`；
- 本地四项命令证据不再满足上表结果，或工作树出现本 Slice 之外的变更。

本 Slice 只修 CI 门禁命令路径；GitHub Actions 的真实远端重跑仍需在提交同步后由总成窗口或用户执行，本地通过不替代远端 run 证据。
