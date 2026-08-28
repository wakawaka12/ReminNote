# P1-05 文档与验收窗口说明

- 阶段：P1 Real Task domain + SQLite persistence
- 窗口：P1-05 文档与验收
- 状态：文档材料交付窗口
- 依据：P1 阶段预报告、主计划 Manual test format、仓库治理与安全文档

## 目标

为 P1-01 至 P1-06 提供可执行、可回溯的范围说明、手动验收步骤、开发数据库安全规则和 migration/升级/回滚草案。文档必须把当前 P0 基线、P1 目标实现和待集成证据区分开，不能把未来行为写成当前仓库事实。

## 本窗口交付

- `P1-01-task-core-implementation-brief.md`：领域模型与不变量；
- `P1-02-persistence-implementation-brief.md`：SQLite、schema、migration 和数据路径；
- `P1-03-tests-implementation-brief.md`：测试矩阵、夹具和数据安全；
- `P1-04-application-boundary-implementation-brief.md`：CRUD/application/query 边界；
- 本文件：P1-05 文档交付边界；
- `P1-06-integration-and-acceptance-brief.md`：串行集成与最终验收门槛；
- `P1-manual-test.md`：启动、CRUD、重启、非法输入和失败判定；
- `P1-data-safety-and-migration-draft.md`：开发数据、升级和恢复草案。

P1-00 简报与其决策由主线维护，本窗口不新建、不覆盖 P1-00 文件，也不修改 `reviews/`、`second-review/` 下的既有记录。

## 文档一致性规则

- 数据库路径统一写为仓库根目录下 `.devdata/reminnote.sqlite`；
- 只有显式 `-PurgeDevData` 才可以重置开发数据；
- P0 Mock 展示行为保留，但不能作为真实持久化证据；
- P1 不创建 Reminder、Anime、Sync 表或服务；
- 当前脚本 `scripts/run.ps1` 在未合入 P1 代码时仍启动 P0 Mock，文档必须保留这一事实；
- 所有真实 migration、CRUD、重启和失败结果由 P1-06 以实际命令/输出补证；
- 未冻结的公共契约、标题约束、删除策略和具体 CRUD 入口不能被本窗口擅自定为产品规则。

## 完成判定

本窗口的文档交付只有在下列条件均满足时才算完成：

1. 每个 P1 模块都有独立范围/验收说明；
2. 手动验收不要求用户猜测启动命令、数据路径、预期或失败标准；
3. migration 草案同时写明 forward upgrade、备份、失败停写、保留原库和恢复路径；
4. 文档没有覆盖 P0 review 记录或公共代码文件；
5. 文档与当前真实仓库的脚本、项目、P0 Mock 状态一致，并显式标出尚未实现的 P1 证据。
