# Contributing to ReminNote

ReminNote 当前按 `ReminNote_MASTER_DEVELOPMENT_PLAN.md` 的 Slice 方式开发。提交前请先阅读主计划、`PRODUCT_RULES.md`、`ARCHITECTURE.md` 和对应的 `docs/slices/` 文档。

## 开发边界

- 每个 Slice 使用独立的 `codex/<slice>` 分支和 Git worktree。
- 先确认文件所有权，再修改文件；不要在其他窗口正在工作的目录中写入。
- P0 只使用进程内 Mock；未进入当前 Slice 的 SQLite、Reminder、IPC、网络或生产数据功能不得提前实现。
- 中高风险变更（schema、migration、IPC、加密、Core 架构或大范围删除）必须先说明范围并得到明确批准。

## 验证

在交付前至少运行对应 Slice 文档中的构建、测试和静态门禁，并提供可复现的手工验收步骤、预期结果和失败判定。当前没有测试项目时，不要把空的 `dotnet test` 结果写成业务覆盖。

## 分支与提交

- 分支命名示例：`codex/p1-task-core`、`codex/p1-task-storage`。
- 提交按有意义的逻辑拆分，避免 `WIP`、`try fix` 和无信息的重复修复提交。
- Contributor commits 必须包含 DCO sign-off：

```text
git commit -s -m "feat: add task time model"
```

- 未经用户明确授权，不 push、merge、rebase、tag 或创建 Release。
- Pull Request 必须说明改动范围、测试结果、数据安全影响和未完成事项。

## Pull Request 清单

- [ ] 只修改了声明的 Slice 所有权范围。
- [ ] 已更新必要的 living docs / ADR / 手工验收说明。
- [ ] 已运行构建、测试和适用的门禁。
- [ ] 没有提交 token、数据库、生产配置或个人数据。
- [ ] 已填写 `Signed-off-by`，并说明 schema/migration 是否变化。

