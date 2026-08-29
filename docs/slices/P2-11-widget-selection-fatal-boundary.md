# P2-11 Widget 选中失效与致命异常边界

## 范围

本 Slice 只处理 Widget 的两条失败/刷新边界：

1. 普通或外部刷新时，如果当前选中的开放 Task 已因其他宿主或外部写入离开开放队列，Widget 清除选中态并保留新的主任务展示，同时给出“已清除选择、请重新选择”的稳定反馈。
2. Widget 自己成功写入 DONE、PARTIAL 或 MISSED 后，刷新使用独立的 `OwnResultRecorded` 原因，明确转到下一开放 Task；没有下一项时明确提示，不伪造已转移。

写入成功但刷新失败仍保留旧列表，写入失败与刷新失败继续使用不同反馈。取消继续传播，致命异常不由 Widget 或 App 的非致命反馈边界吞掉。

## 实现边界

- 仅在 `src/windows/ReminNote.Widget/` 与 `tests/ReminNote.Tests/WidgetInteractionTests.cs` 内调整 Widget 行为。
- `App.xaml.cs` 仅捕获由 `WidgetViewModel.IsFatalException` 判定为非致命的异常；生命周期取消仍按关闭语义处理，其他取消继续传播。
- 不改变 Parser、Core、Infrastructure、Task 结果持久化、跨午夜或 Main 行为。

## 验收场景

- 外部完成当前选中 Task 后刷新：列表只显示开放项，主任务可更新，但选中标题、状态和结果操作不会伪装成新的主任务；用户可点击列表项重新选择。
- Widget 自身完成当前 Task 后刷新：下一开放 Task 成为明确选中项；若没有下一项，反馈明确说明“当前没有下一开放 Task”。
- 以上行为均由 Widget 交互测试覆盖，并通过 Release build、全量测试和 P0-07 资源/项目约束校验。
