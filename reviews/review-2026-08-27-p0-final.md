# ReminNote P0 终审 — 2026-08-28（一审 vs 二审 对比裁决）

> 终审职责：对一审（`reviews/review-2026-08-27-p0.md`）与
> 二审（`second-review/review-2026-08-27-p0-second-review.md`）做三方对比，
> 核实争议点后给出最终裁决与拍板项。终审未发现二审遗漏的新缺陷。

## 终审验证证据

- 二审陈述"只读边界内未复跑 build/冒烟"——**终审在本次会话已实跑**（一审阶段，同一 HEAD
  `90d4886`）：`build.ps1 -Configuration Release` 0 警告 0 错误；`test.ps1` 通过；
  verify-p0-07 通过（213 键 / 6 项目）；MainWindow 冒烟（标题 ReminNote）；
  Widget 冒烟（标题 ReminNote Widget Mock）。故「构建+冒烟通过」已有本会话复现证据，
  二审仅保留其"未独立复现"的措辞，不影响结论。
- 终审亲自复核二审的全部事实纠正（见下节），均已确认正文。

## 二审事实纠正的终审核验（一审两处描述不准确，接受纠正）

| 争议点 | 一审描述 | 二审纠正 | 终审核验（实读代码） |
|---|---|---|---|
| Today Quick Add 输入框焦点 | 「Today/Widget 均已用 AccessibleFocusBrush」 | Today 焦点用固定 `AccentBrush`（:443），不随高对比度变化 | ✅ 属实（TodayResources.xaml:440-447） |
| ANIME 搜索框焦点策略 | 「无显式触发器，建议补齐或验证」 | 同左；且按条件性必须处理 | ✅ 属实（AnimeResources.xaml 搜索框样式无任何焦点触发器；仅 AnimeButtonStyle :142 有 AccessibleFocusBrush） |
| widget fixture 一致性 | 「Today mock 主任务是整理桌面文件」 | Main App 主焦点实为「准备设计评审材料」；「整理桌面文件」是 RANGE 示例；「整理桌面资料」是 Widget 自有 fixture，未规定必须同一数据 | ✅ 属实（TodayMockDataService.cs:13,44,57） |
| verify 脚本双向保证 | 「213 键=resx 1:1（脚本反向查缺）」 | 脚本只做 C# 常量→resx 单向（verify-p0-07.ps1:39-44）；当前 213/213 是人工统计，非门禁保证 | ✅ 属实 |

综上：**二审结论可靠度高于一审**，这些纠正全部采纳，相应内容以二审为准。

## 三方对比与终审裁决

| 编号 | 一审 | 二审 | 终审 |
|---|---|---|---|
| IMP-1 Widget 状态标签不更新 | Important | 接受；MINOR；必须修复；阻断 P0 完成声明 | **确认**。字段写入无通知是确定性缺陷；最小修复=3 行通知；修复后重跑 P0-06 手测 4 |
| IMP-2 标记已读弹 Quick Add | Important | 接受；MINOR；必须修复；阻断 P0 完成声明 | **确认**。复核 `MarkReminderRead`（WidgetViewModel.cs:338-343）：把提醒反馈写入 QuickAddFeedback 并打开面板，属状态错位；最小修复=独立反馈字段+不设 IsQuickAddOpen；修复后重跑手测 7 |
| NIT-2 输入框焦点/高对比度 | Nit | MINOR（条件性必须）：人工 Tab+高对比度验证；不通过则 Today 改 AccessibleFocusBrush + Anime 补触发器 | **从二审**。建议直接按二审条件执行：**先做一行级代码对齐（见拍板项 P-2），再做实机验证**，一次闭环 |
| FUNC-01/02/05/06/07 | 通过 | 通过/部分接受（门禁宽泛≠逐控件证明） | 确认：功能正确性无新问题；门禁强化属可选 |
| FUNC-03 P0-06 总评 | 通过 | 部分接受（被 IMP-1/2 拉低为"必须修复"） | 确认 |
| FUNC-04/08/09 门禁/测试边界 | 无提及 | 门禁单边、无测试项目、冒烟属一审自述 | 确认措辞校正：今后报告写「无测试程序集；dotnet test 流程/门禁通过」 |
| NIT-1/3/4/5、FUNC-10、FUNC-11 | 记录/Nit | 接受/部分接受/驳回（clean.ps1 归后续 backlog） | 确认；无争议 |
| ARCH-01 架构/安全/性能 | 通过 | 接受 | 确认 |

## 终审总裁决

**P0 实现「有条件批准」**：
- 无 BLOCKER/MAJOR/安全/数据/架构问题（一审+二审+终审三方一致）；
- **P0 完成声明目前被 3 件事阻断**：IMP-1、IMP-2（代码缺陷）+ 用户手工验收（主计划 :2947-2951，不可由 AI 替代）。

## 需你拍板（3 项）

**P-1（建议：立即修）IMP-1 + IMP-2**：两处共约 10 行（WidgetViewModel.cs），
修复后重跑 Widget「完成→延后→改期」与「抽屉标记已读」手测。建议与 P-2 合并一次交付。

**P-2（建议：顺手修 + 实测）NIT-2 焦点/高对比度**：
方案 A（推荐）——把 `TodayTextBoxStyle` 焦点边框由 `AccentBrush` 改为 `AccessibleFocusBrush`
（一行），给 `AnimeSearchTextBoxStyle` 补 `IsKeyboardFocused → AccessibleFocusBrush` 触发器
（与 Widget 输入框一致）；然后在默认/高对比度下 Tab 验证三个输入框。
方案 B——不动代码，先做人工验证，有不通过再改。
（AccessibleFocusBrush 已存在，A 版改动零风险，且 P0-07 本就承诺"主要输入框焦点可验证"。）

**P-3（必须你执行）三端人工验收**：按我上一轮给的验收指南做第 2/3 层
（主窗口 TODAY/ANIME、Widget 三档尺寸、Tab/UIA、125-150% 缩放、高对比度、数据边界），
结果填表回传；通过后 P0 正式宣告完成。

## 校正记录（正式信源修正）

- 一审报告 `review-2026-08-27-p0.md` 中「Today 输入框已用 AccessibleFocusBrush」
  「Today mock 主任务是整理桌面文件」「verify 脚本反向查缺」三处表述不准确，
  以本文与二审报告为准。
