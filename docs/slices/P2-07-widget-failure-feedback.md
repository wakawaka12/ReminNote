# P2-07 Widget 失败路径反馈

- 阶段：P2 Real TODAY / Widget task loop
- 日期：2026-08-29
- 范围：Widget live Quick Add、DONE/PARTIAL/MISSED 结果写入及写入后的 Today 刷新失败路径
- 状态：实现与自动化验证完成；真实桌面 GUI 验收仍由用户执行

## 目标

让 Widget 在 live Task 写入链路中稳定说明实际发生的事情：

- 写入失败时，不显示成功，不清空旧列表，并保留领域校验错误和写门繁忙语义；
- 写入成功但 Today 刷新失败时，明确说明两件事分别成功/失败，不伪造“已刷新”；
- 刷新失败时保留旧的 Widget 队列和计数，不因异常清空旧 read model；
- 取消继续向调用方传播；致命进程错误不被通用反馈 catch 吞掉。

## 实现范围

- `WidgetViewModel` 将刷新拆为可复用的内部刷新路径：先查询并构建下一份队列，准备完成后才提交到当前列表。
- Quick Add 与 DONE/PARTIAL/MISSED 结果写入分别捕获领域/写门异常、取消和非致命通用异常；写入后的刷新失败使用独立反馈。
- live 异步命令向 application service 和刷新路径传递取消 token。
- Widget 现有 Task/Quick Add 反馈文本增加 `AutomationProperties.LiveSetting=Polite`；没有引入 UI 重做。
- 不修改 Main、Core、Infrastructure、既有报告/审查材料或开发数据。

## 反馈契约

| 情况 | Widget 反馈 | 列表/数据库语义 |
|---|---|---|
| Quick Add 写入失败 | `无法创建 Task：写入失败 · 当前 TODAY 列表保持不变` | 不显示成功；旧列表保持；输入保留 |
| 结果写入失败 | `无法记录结果：写入失败 · 当前 TODAY 列表保持不变` | 不显示成功；旧列表和结果保持 |
| Quick Add 写入成功、刷新失败 | `已写入本地 Task：...，但 TODAY 刷新失败 · 当前列表保持不变 · 请点击刷新重试` | 数据库写入保留；旧列表保持；不显示 `TODAY 已刷新` |
| 结果写入成功、刷新失败 | `已记录「...」的 ... 结果，但 TODAY 刷新失败 · 当前列表保持不变 · 请点击刷新重试` | 结果写入保留；旧列表保持；不显示成功刷新 |
| 直接刷新失败 | `TODAY 刷新失败 · 当前列表保持不变 · 请点击刷新重试` | 旧列表保持；非致命异常继续交由宿主记录 |

`DomainValidationException` 仍显示其原有稳定错误信息，`TaskWriteGateBusyException` 仍显示原有写门繁忙信息；`OperationCanceledException` 不转换为失败反馈。OOM、栈溢出、访问冲突、SEH 及其包装异常不进入通用反馈分支。

## 验证证据

- `scripts/build.ps1 -Configuration Release`：8 个项目构建成功，0 warning、0 error。
- `scripts/test.ps1 -Configuration Release`：真实 `ReminNote.Tests.exe` 执行 180/180，0 error、0 failed、0 skipped、0 not run；脚本内 P0-07 通过。
- 新增 Widget 测试覆盖 Quick Add 通用写入失败、结果通用写入失败、两类写入成功后刷新失败、直接刷新失败保留旧队列和取消传播。
- 未启动或控制桌面 GUI；真实 Main/Widget 双宿主、重启和人工提示可见性仍待用户验收。

## 人工验收

1. 使用同一个显式 `--repo-root` 启动 Main 与 Widget，在 Widget 中先确认已有 Task，再执行 Quick Add；成功时应看到写入成功和刷新成功提示。
2. 让 Widget 的写入路径遇到写门占用或数据库不可用的非致命错误；应看到“写入失败”，旧队列仍在，不能出现“已写入/已刷新”。
3. 让写入完成后暂时使 Today 查询不可用；应看到“已写入/已记录，但 TODAY 刷新失败”，旧队列仍在，点击后续刷新后才收敛到新状态。
4. 关闭宿主或取消异步操作；不应伪造成功，取消不应变成普通写入失败提示。

任一失败：旧列表被清空、写入失败仍显示成功、刷新失败被报告为完整成功、取消被吞掉，或 Widget 写入致命进程错误后静默继续。

## 剩余风险

- 本 Slice 只解决 Widget 当前 P2 本地 application/query 链路的失败反馈；P2.5 Agent/IPC、命令幂等和跨进程变更通知仍未实现。
- 自动轮询的宿主会记录非致命刷新异常；用户仍需通过下一次刷新重试，未引入自动重试策略。
- 真实桌面 GUI、双宿主重启和实际数据库故障注入不在本窗口执行范围内。
