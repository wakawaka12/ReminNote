# P3-09 最终总成 Gate、稳定性与验收骨架

状态：**当前为 BLOCKED；不是 P3 Alpha，也没有用户 sign-off。**

本 Slice 只提供总成审查、隔离执行器和报告结构，不伪造 P3-03、P3-05、P3-06
尚未接入的功能。执行入口为 `scripts/verify-p3-09.ps1`；它会在系统临时目录创建
新的 artifact/run root，生成 Markdown/JSON 报告，并以非零退出码表示
`BLOCKED`/`PENDING`。

## 1. 当前结论

当前基线是 `codex/p3-integration` 指向的整合提交 `45e5451`。已有 P3-01、P3-02、
P3-04、P3-07、P3-08 只证明契约、纯计算、策略、通知 seam 和受控恢复计划；它们
不能证明 Agent scheduler、真实 Windows channel、Main/Widget Reminder UI 或
用户可见行为。

P3-09 将下列依赖全部视为硬门槛：

| 依赖 | 当前状态 | 当前静态事实 | 解除条件 |
| --- | --- | --- | --- |
| P3-03 Agent scheduler | **BLOCKED** | 尚无 Reminder DbSet/configuration 的正式 DbContext 接线、Agent due scheduler/transaction 组合或 P3-03 测试目录；Agent migration target 仍为 P2.5 | 唯一 Agent writer 内完成 due consume、Instance append、幂等、restart/recovery、migration/model snapshot 和隔离测试 |
| P3-05 channel adapters | **BLOCKED** | Windows/Widget 生产源码没有实际 `INotificationChannel`/`DeliverAsync` adapter，也没有 P3-05 测试目录 | Toast/Tray/Widget/Sound/WakeTimer adapter、health/result/replace 和真实进程隔离证据完成 |
| P3-06 Reminder UI | **BLOCKED** | Main/Widget 没有 Reminder read-only query/read model、Drawer/Center action 接线，也没有 P3-06 测试目录 | Agent query/command 接线、READ/RESOLVED/Snooze/DONE、stale/unavailable 反馈和桌面用例完成 |

因此本轮可以通过的只是已有契约检查和隔离 harness；总成状态仍必须是
`BLOCKED`。任何“编译通过”“fake channel 通过”“CLI 通过”都不能解除上述硬门槛。

## 2. 执行入口与退出码

在当前仓库根目录运行：

```powershell
pwsh -NoProfile -File .\scripts\verify-p3-09.ps1
```

默认行为：

- 使用 `Release` 配置；
- 将白名单源码树复制到新的 `run/contract-source` 临时副本（明确排除 `bin/obj/.devdata/reviews/second-review`），在该副本内执行 locked restore/build；
- 将测试输出、日志、marker 和报告写入新的系统临时 artifact/run root；
- 连续运行已有 P3 contract/isolated 测试 3 次；
- 做静态契约、依赖和受保护材料边界检查；
- 发现 P3-03/P3-05/P3-06 未 READY 时不启动任何真实产品进程；
- 始终把真实进程、桌面人工和用户 sign-off 分别记录为 `BLOCKED` 或 `PENDING`。

可选参数：

```powershell
pwsh -NoProfile -File .\scripts\verify-p3-09.ps1 `
  -StabilityIterations 5 `
  -ArtifactRoot C:\Users\Public\Temp\reminnote-p3-09-artifacts
```

`ArtifactRoot` 必须是当前仓库之外的绝对路径；脚本还会在其中创建唯一的
`run-<guid>` 子目录，不覆盖已有报告。不要把它指向任何正式数据目录。

退出码约定：

| 退出码 | 含义 | 是否可称 P3 Alpha |
| ---: | --- | --- |
| 0 | 所有自动门禁通过，且没有待决证据 | 仍需确认报告中的人工/sign-off 条件 |
| 1 | 自动检查失败或发现安全/契约矛盾 | 否 |
| 2 | `BLOCKED` 或 `PENDING`；依赖或证据不足 | 否 |
| 3 | gate 执行环境错误 | 否 |

当前基线预期为退出码 `2`。这是 fail-closed 的预期结果，不是脚本失败。

## 3. 证据级别

报告必须保留以下五类证据，不能合并命名：

| 级别 | 能证明什么 | 不能证明什么 |
| --- | --- | --- |
| `contract/unit/fake` | 值对象、状态机、错误码、接口和纯策略语义 | 真实进程、OS 通知、用户体验 |
| `CLI/harness/temporary clone` | 隔离 root、fixture、命令和文件 artifact | 正常桌面行为或用户签署 |
| `real-process isolated` | 指定 PID/二进制/隔离 root 下的真实进程启动、存活、日志和可复核状态 | 完整用户体验，除非另有桌面证据 |
| `normal desktop manual` | 用户在正常 Windows 桌面看到并操作 Main/Widget/通知 | 自动化测试或 CLI 代签 |
| `user sign-off` | 用户明确确认的版本、范围、时间和未覆盖项 | 任何未明确记录的推断 |

P3-04 的 fake/file store 结果即使通过，也只能落在第一类或第二类。P3-09 脚本的
真实进程模式只负责隔离启动/存活 smoke；IPC、重启、睡眠唤醒、通道失败保留事实和
桌面交互仍须在报告中逐项提供证据。

## 4. 隔离与数据安全约束

脚本只把产品运行所需的可选真实进程 data root 放在本次新建的 artifact/run root
下；默认执行不会创建产品数据库。测试中的 SQLite 若有需要，也必须由测试自身在
系统临时目录创建独立 fixture。

脚本在启动前拒绝：

1. 当前工作树或其子目录作为 artifact root；
2. 受保护数据目录作为 artifact root；
3. 真实进程模式把当前工作树当作 isolated clone；
4. 未显式提供 Agent/Main/Widget 二进制或不含 `.git`、`ReminNote.sln` 的 clone。

本 Slice 不读取、写入、复制、锁定或连接受保护数据库，不运行会把当前仓库
`.devdata` 当作产品 root 的命令，不修改既有 `reviews/`、`second-review/` 或既有
报告/契约。报告中的保护边界检查只使用 Git 状态和计算后的输出路径。

## 5. 自动检查矩阵

### 5.1 当前可执行的检查

| 检查 ID | 级别 | 当前预期 | 说明 |
| --- | --- | --- | --- |
| `P3-09-BOUNDARY-REVIEWS` | static | PASS | 检查受保护评审材料没有工作树变更 |
| `P3-09-BOUNDARY-ARTIFACT` | temporary root | PASS | marker、build、test log 和报告位于新 run root |
| `P3-09-DOC-P300` … `P3-09-DOC-P308` | static/contract | PASS | 检查已有 Slice 契约和证据边界文件存在 |
| `P3-09-RESTORE-CONTRACT` | contract/unit/fake | PASS | 检查 P3-08 Active 写入拒绝契约仍在 |
| `P3-09-CONTRACT-TESTS` | contract + isolated | PASS 或 FAIL | 连续 N 次运行 P3-01/02/04/07/08 测试；失败即停止升级证据等级 |
| `P3-03-SUMMARY` | static | BLOCKED | DbContext、Agent scheduler/transaction、P3-03 tests 三项必须同时存在 |
| `P3-05-SUMMARY` | static | BLOCKED | 实际 channel adapter、delivery/health、P3-05 tests 三项必须同时存在 |
| `P3-06-SUMMARY` | static | BLOCKED | read-only query、actions、P3-06 tests 三项必须同时存在 |
| `P3-09-MIGRATION` | static/contract | BLOCKED | 当前 Agent target 尚未包含 P3 Reminder migration |
| `P3-09-REAL-PROCESS` | real-process isolated | BLOCKED | 当前硬依赖未满足，脚本不会启动进程 |
| `P3-09-STABILITY-AGENT` | real-process isolated | BLOCKED | 真实 restart/sleep-wake 仍未验证 |
| `P3-09-DESKTOP-MANUAL` | normal desktop manual | PENDING | 脚本不能代替用户操作 |
| `P3-09-USER-SIGNOFF` | user sign-off | PENDING | 没有用户签署记录 |

### 5.2 解除 P3-03/P3-05/P3-06 的最小证据

静态检查只是防止漏接线；`READY` 后仍需运行对应测试和真实进程。后续实现可使用
不同文件名，但必须让以下语义在生产组合中可定位：

- P3-03：正式 Reminder model 接入 DbContext；Agent scheduler 调用 P3-02 planner、
  在唯一 writer transaction 中消费 schedule 并追加 instance；restart/recovery/
  idempotency 有独立测试。
- P3-05：Windows/Widget 的 `INotificationChannel` 实现执行实际投递并返回
  capability/health/outcome；channel failure 不覆盖 core fact；有真实进程日志。
- P3-06：Main/Widget 只读读取 Reminder snapshot/query，通过 Agent command 完成
  READ/RESOLVED/Snooze/DONE；有 stale/unavailable 和失败反馈测试。

如果只有其中一部分接入，脚本必须保持 `BLOCKED`，不能手动改报告为 READY。

## 6. 真实进程检查骨架

只有 P3-03、P3-05、P3-06 全部 READY 后才允许显式执行：

```powershell
pwsh -NoProfile -File .\scripts\verify-p3-09.ps1 `
  -RunRealProcess `
  -AgentPath <absolute-agent-exe> `
  -MainPath <absolute-main-exe> `
  -WidgetPath <absolute-widget-exe> `
  -IsolatedRepositoryRoot <absolute-temporary-clone>
```

脚本会：

1. 检查 clone 含 `.git` 和 `ReminNote.sln`，且与当前工作树没有包含关系；
2. 在本次 run root 创建新的 `real-process-data`；
3. 启动指定 Agent，等待精确的 ready 输出；
4. 用相同 isolated clone 启动 Main 和 Widget，记录 PID/路径/stdout/stderr；
5. 在固定观察窗口内检查进程是否退出；
6. 只关闭本次脚本自己启动的进程，并保留日志。

这只是“真实进程隔离启动/存活”骨架，不会把启动成功写成完整 P3 Alpha。仍需另行
补齐 due、同 key 重试、Agent restart、sleep/wake、channel unavailable、Candidate
migration 和用户处理动作的证据。

## 7. 桌面人工验收骨架

人工验收必须使用新的隔离 clone/data root，并在正常 Windows 桌面由用户执行。每项
都要填入实际时间、版本、进程 PID、隔离路径和观察结果：

1. 创建/更新至少一个 `TIME`、一个跨午夜 `RANGE` reminder，确认 UI 显示的时间与
   Task 语义一致。
2. 等待 due，分别检查 Main Drawer/Center、Widget、Toast/Tray 的可见状态；关闭
   Toast 只变成 READ，不伪造 RESOLVED。
3. 对同一实例执行 DONE、Snooze、重复 action 和冲突 action，确认 receipt/错误码、
   历史不可变和 future occurrence 范围。
4. 在 Agent 正常运行、重启、异常退出后确认 pending/due/core instance 不丢失且不重复。
5. 执行 Windows sleep/wake；检查 recovery 的原始 trigger、RecoveredAt 和 summary/
   trigger-once 语义。
6. 模拟 Toast/Tray/Widget blocked/unavailable；确认 delivery attempt 与 core fact
   分开保留。
7. 在隔离 Candidate 上执行 backup→migration→verify→promote；制造失败输入，确认
   Active 不被未验证 Candidate 覆盖。
8. 关闭进程后重启 Main/Widget，确认只读 query、stale/unavailable 反馈和历史保留。

CLI、fake、反射调用、旧 binary、UI Automation 单独运行结果都必须放在各自证据级别，
不能勾选本节的桌面人工项。

## 8. 完成规则

只有同时满足以下条件，P3-09 才能从 `BLOCKED/PENDING` 进入可供用户判断的候选状态：

- P3-03/P3-05/P3-06 静态和 contract gate 全部 READY；
- P3 migration/Candidate/verify/失败恢复在隔离 root 完成；
- Agent 真实进程稳定、重启和 sleep/wake 证据完成；
- channel failure 不破坏 Reminder truth；
- Main/Widget 正常桌面人工清单逐项完成；
- 没有未解释的 BLOCKER/MAJOR correctness issue；
- 报告单独记录用户 sign-off、版本、范围、时间和未覆盖项。

在这些条件完成前，不得开始以 P3 Alpha 为前提的 P4 工作，也不得以日历等待天数
替代稳定性证据。
