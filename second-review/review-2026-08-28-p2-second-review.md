# ReminNote P2 独立二审裁决 — 2026-08-29

- 审查日期：2026-08-29
- 审查仓库：`D:\Anime`
- 审查分支：`codex/p0-integration`
- 一审源文件：`D:\Anime\reviews\review-2026-08-28-p2.md`
- 二审规则：`D:\Anime\second-review\README.md`
- 一审代码/测试基线：`f9a8395d515feceb04ec71263e443befb5d7cc1e`（短哈希 `f9a8395`）
- 二审开始时本地 HEAD：`7a8ae8ad582ef995010eb6318303e194175565ae`（短哈希 `7a8ae8a`）

`f9a8395` 是一审报告所指的远程代码基线。`7a8ae8a` 只是本地领先的一份文档提交，
`git diff --name-status f9a8395..7a8ae8a` 仅显示新增
`docs/reports/P2-阶段总结与下一阶段开发约束-2026-08-29.md`；
`src/**` 与 `tests/**` 在两者之间无差异。因此本报告把 `f9a8395` 作为产品代码/测试裁决基线，
同时记录并不把 `7a8ae8a` 误写成产品代码变更。本次不 push。

## 二审总裁决

**结论：部分接受一审结论；有条件批准继续推进，但不批准 P2 完成声明。**

- 未发现 Critical、BLOCKER 或 MAJOR 级问题。
- `IMP-P2-1` **接受**：确有静默吞掉日期/时间语义的 Parser 边界缺陷；校正为
  **MINOR（契约门禁级）**。修复范围很小，但因 P2-00 明确把“Parser 静默丢字段”列为失败条件，
  必须修复并补测试后，才能宣告 P2 完成。
- `IMP-P2-2` **部分接受**：应用服务确实只校验非负排序值，没有表达或验证日期/分组/相邻上下文；
  但当前 P2 的真实排序入口由 Main UI 保护，Widget 没有排序入口，排序值本身也不能改变任务的
  计划日期或 Today 分组。校正为 **MINOR（防御性边界缺口）**，可留到 P2.5，不阻塞当前 P2 代码推进。
- 人工验收残留状态属实。条件性 UIA、自动化测试和空开发库 schema 证据不能替代用户在正常桌面上的
  双宿主、跨午夜和已有 P1 数据迁移验收；它们阻塞 P2 完成声明，但不是新增代码严重度。

### 必须修复与可后置总表

| 项目 | 二审裁决 | 校正严重度 | 处理属性 | 是否阻塞 P2 完成声明 |
|---|---|---|---|---|
| `IMP-P2-1` | 接受 | MINOR | 必须修复并补 Parser 测试 | 是 |
| `IMP-P2-2` | 部分接受 | MINOR | P2 保持 UI 守卫；服务/命令层强化后置 P2.5 | 否 |
| `P2-GATE-01` 人工验收 | 接受其“仍待用户执行”状态；实际功能证据不足 | MINOR 验收门槛 | 必须由用户完成并记录 | 是 |
| `P2-EXTRA-01` 两项排序写入非原子 | 接受为已知后置风险 | STYLE/NIT | P2.5 批量命令/事务化 | 否 |

## 审查范围与证据边界

本次读取并对照了二审目录规则（`second-review/README.md:1-21`）、主计划 P2 范围与严重度规则
（`ReminNote_MASTER_DEVELOPMENT_PLAN.md:2649-2676,3004-3030`）、P2-00 契约
（`docs/slices/P2-00-integration-and-contract-brief.md:22-87,142-174`）、P2-01
（`docs/slices/P2-01-today-query-implementation-brief.md:8-55`）、P2-03
（`docs/slices/P2-03-live-shell-copy-fix.md:3-25,38-62`）、P2-04
（`docs/slices/P2-04-widget-task-loop-implementation-brief.md:3-35`）、P2-05
（`docs/slices/P2-05-final-acceptance-and-documentation.md:28-56,71-127`）、P2 持久化边界
（`docs/slices/P2-Persistence-implementation-brief.md:6-60`），以及当前
`PRODUCT_RULES.md:5-27`、`ARCHITECTURE.md:41-63`、`DECISIONS.md:118-135`、
`DEVELOPMENT.md:38-68`、`TESTING.md:43-59`、`SECURITY.md:18-23`。
P2-02 Parser 的范围和冻结语法由 P2-00 与阶段总结记录
（`docs/reports/P2-阶段总结与下一阶段开发约束-2026-08-29.md:65-76,108-115`）提供；
当前树没有单独的 `docs/slices/P2-02-*.md`，本报告不把这一文件组织事实扩大为产品缺陷。

本次只读核对了 P2 代码、测试、契约和现有报告；没有修改产品源码、测试源码、根级活文档、
`reviews/` 或 `second-review/` 既有文件，没有启动 WPF GUI，没有调用会写入开发库的 harness，
也没有执行 purge、删除或覆盖数据库。现有测试夹具使用内存 SQLite
（`tests/ReminNote.Tests/SqliteTestDatabase.cs:7-24`）。

## 独立验证命令及结果

| 命令 | 二审结果 |
|---|---|
| `D:\Anime\scripts\build.ps1 -Configuration Release` | 通过；8 个项目构建，0 警告、0 错误 |
| `D:\Anime\scripts\test.ps1 -Configuration Release` | 通过；`ReminNote.Tests Total: 162, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`；脚本内 P0-07 通过 |
| `D:\Anime\scripts\verify-p0-07.ps1` | 通过；213 个资源键、7 个锁定项目、1 个测试项目及 UI 标记通过 |
| `git diff --check f9a8395..HEAD` | 通过；无 whitespace errors |
| `git diff --quiet f9a8395..HEAD -- src tests` | 通过；一审基线到本地文档 HEAD 无产品/测试源差异 |

### Parser 独立复现

在上述 Release 构建产物上用只读 PowerShell 直接调用
`ReminNote.Core.Tasks.Parsing.TaskParser.Parse`，逻辑日期为 `2026-08-28`，结果如下：

```text
[明天18:00 买东西]       => SUCCESS type=ANYTIME date=2026-08-28 title=[明天18:00 买东西]
[今天18:00 买东西]       => SUCCESS type=ANYTIME date=2026-08-28 title=[今天18:00 买东西]
[后天08:05 确定性任务]   => SUCCESS type=ANYTIME date=2026-08-28 title=[后天08:05 确定性任务]
[明天 18:00 买东西]      => SUCCESS type=TIME date=2026-08-29 title=[买东西]
[2026-09-0110:00 标题]   => FAILURE [task.parser.date.invalid]
[9:00 标题]              => FAILURE [task.parser.time.invalid]
```

## 逐项裁决

### IMP-P2-1 — 中文日期词与时间粘连时静默吞字段

- **一审发现编号与主张**：一审 `IMP-P2-1`（`reviews/review-2026-08-28-p2.md:30-42`）主张
  `明天18:00 买东西` 被成功解析成逻辑今天的 `ANYTIME`，完整输入落入标题，而不是返回稳定错误。
- **精确代码/测试证据**：
  - `src/windows/ReminNote.Core/Tasks/Parsing/TaskParser.cs:36-38` 仅按空白切 token；
    `:50-64` 只在整个 token 是受支持日期或显式日期形状时消费日期。
  - `TaskParser.cs:67-84` 只对下一个完整 token 判断合法/畸形时间，畸形时间守卫又要求
    ASCII 数字开头（`:250-259`）；`明天18:00` 既不是完整日期 token，也不满足该数字开头守卫。
  - `TaskParser.cs:93-118` 随后把剩余完整输入当标题，并默认 `TimeSpec.Anytime(planDate)`；
    因而日期和时间都未被识别，却返回成功。
  - `tests/ReminNote.Tests/TaskParserTests.cs:62-72` 只覆盖空格分隔的日期+时间成功路径；
    `:115-136` 的非法输入矩阵没有中文日期词粘连用例。
  - 两个 live Quick Add 在 Parser 成功后直接写入：Main 为
    `src/windows/ReminNote.Windows/Features/Today/TodayPageViewModel.cs:377-390`，Widget 为
    `src/windows/ReminNote.Widget/ViewModels/WidgetViewModel.cs:603-619`。因此该错误成功结果会进入
    `CreateAsync`，不是一个仅存在于单元测试的解析差异。
  - 契约要求“不识别的日期/时间形状”返回稳定错误且不写库
    （`docs/slices/P2-00-integration-and-contract-brief.md:60-87`），P2 失败判定也明确包含
    “Parser 静默丢字段”（`:154-162`）；同一规则在 `PRODUCT_RULES.md:17-27`、
    `TESTING.md:43-59` 和阶段总结 `docs/reports/P2-阶段总结与下一阶段开发约束-2026-08-29.md:108-115` 重申。
- **二审裁决**：**接受（accept）**。
- **校正后的严重度与理由**：**MINOR（契约门禁级）**。这是可复现的用户语义错误：日期/时间被忽略，
  任务可落到错误的工作日和 `ANYTIME`，且无稳定错误；但触发形态是未按冻结语法分隔的边界输入，
  没有发现数据结构损坏、凭据、安全或跨任务历史破坏，修复范围也限于 Parser 守卫和测试，不足以定为 MAJOR。
- **最小行动/文件范围**：在 `TaskParser.cs` 的日期/时间消费与标题回退前增加对
  `今天/明天/后天` 后紧跟 ASCII 数字、`:`、`-`、`–` 等候选粘连形状的稳定错误判定；统一采用已冻结的
  `task.parser.date.invalid`、`task.parser.time.invalid` 或 `task.parser.range.invalid` 之一，并在
  `tests/ReminNote.Tests/TaskParserTests.cs` 增加至少 `今天18:00`、`明天18:00`、`后天08:05` 和粘连范围回归。
  本二审不改 Parser，也不改测试。
- **是否阻塞 P2**：**是，阻塞 P2 完成声明**；不是构建、安全或 P2.5 启动阻断。修复后需重新运行 Parser
  测试，并至少确认失败结果不会进入 Main/Widget 的 `CreateAsync`。

### IMP-P2-2 — 排序语义守卫位于 UI，应用服务只校验非负值

- **一审发现编号与主张**：一审 `IMP-P2-2`（`reviews/review-2026-08-28-p2.md:44-55`）主张
  `ReorderAsync` 接受任意已有 `TaskId` 和任意非负 `SortOrder`，同日期/同分组/相邻约束只在 Main UI，
  因而服务层没有“越界、跨日期、跨分组拒绝写库”守卫。
- **精确代码/测试证据**：
  - 公共命令只有 `TaskId` 和 `SortOrder`：
    `src/windows/ReminNote.Core/Application/TaskApplicationContracts.cs:147-161`，没有计划日期、Today
    分组、邻接项或期望版本上下文。
  - `src/windows/ReminNote.Infrastructure/Application/TaskApplicationService.cs:135-169` 查找任意
    已有 ID，只有相同值 no-op，然后复制聚合、调用 `SetSortOrder` 并写入 `SortOrderChanged` 历史；
    `Task.cs:260-273` 只拒绝负值，`TaskEntityConfiguration.cs:82-84` 的 SQLite check 也只有
    `sort_order >= 0`。
  - `tests/ReminNote.Tests/P2PersistenceBoundaryTests.cs:138-206` 实际用 `ReorderTaskCommand(created.Id, 7)`
    并断言排序值 7 与历史成功；同文件 `:445-459` 只覆盖负排序拒绝。
  - Main UI 在写入前检查同组边界、相邻项和计划日期：
    `src/windows/ReminNote.Windows/Features/Today/TodayPageViewModel.cs:682-711`、`:713-758`；
    跨日期和分组边界测试在 `tests/ReminNote.Tests/TodayPageViewModelTests.cs:362-393`，断言没有
    `ReorderCommands`。实际写入调用为 `:760-766`。
  - `TodayQueryService` 的排序键先按分组/计划日期，再按 `SortOrder`
    （`src/windows/ReminNote.Infrastructure/Application/TodayQueryService.cs:62-71`），所以直接设置排序值
    不会把任务的计划日期或 Today 分组改成另一个值；它能绕过的是本组内的任意 rank/非相邻语义。
  - P2 明确把排序限制写为同一计划日期/Today 分组，跨组必须改计划时间
    （`docs/slices/P2-00-integration-and-contract-brief.md:50-58`），同时 P2-00 的实现核对明确描述
    当前 Main 入口的“越界/跨日期拒绝写库”（`:146-150`）。Widget 的 P2 行为仅包括 Quick Add、结果
    等允许操作，不提供排序入口（`docs/slices/P2-04-widget-task-loop-implementation-brief.md:7-13`）。
- **二审裁决**：**部分接受（partial）**。
- **校正后的严重度与理由**：**MINOR（防御性边界缺口）**。一审关于“服务接受任意非负 rank、没有
  上下文验证”的事实成立；但“会跨日期/跨分组移动”的表述过强，因为 `SortOrder` 不写计划日期/分组，
  当前 P2 真实排序入口确实由 Main UI 限制，Widget 没有排序入口。没有证据表明当前双宿主路径能通过排序
  改期或越组；风险是未来/绕过 UI 的调用者可以写入非邻接或任意 rank。
- **最小行动/文件范围**：P2 当前不强制改码；P2-00、架构和接管记录已足够明确“UI 层排序守卫”和
  P2.5 单写者边界，不需覆盖既有文档。进入 P2.5 或出现第二个合法命令调用者时，在
  `ReorderTaskCommand`/application service 中增加计划日期、分组、邻接或版本上下文，最好改成一次性
  批量交换并由服务/事务验证；补 `TaskApplicationService`/SQLite/并发回归测试。若产品决定 P2 就要求
  服务级拒绝，则受影响范围是上述 Core contract、Infrastructure service/repository 和测试，不能只改 UI。
- **是否阻塞 P2**：**否**。当前 P2 UI-only guard 在既有调用图和 Widget 范围内足够；该项是 P2.5 的服务
  边界前置门槛，不应把它升级成当前代码交付阻断。

## NIT 复核

### NIT-1 — Today 查询全表读取后内存过滤

- **一审主张**：`TodayQueryService` 使用空 `TaskQuery` 全表读取，再以内存过滤日期。
- **证据**：`src/windows/ReminNote.Infrastructure/Application/TodayQueryService.cs:40-60` 调用
  `ListAsync(new TaskQuery())`；`TaskQueryService.cs:40-55` 只有显式 `PlanDate` 才生成 SQL `WHERE`。
- **二审裁决**：**接受（accept）**；校正严重度 **STYLE/NIT**。事实成立，但 Today 契约还要显示未完成的历史计划
  （`docs/slices/P2-00-integration-and-contract-brief.md:40-48`），P2 当前规模没有性能证据或功能错误。
- **最小行动/文件范围**：可在 P2.5/P3 设计支持 `PlanDate <= workday` 的查询、分页/游标和索引；涉及
  `TaskQuery`、`TaskQueryService`、`TodayQueryService` 与查询测试。P2 不改。
- **是否阻塞 P2**：否，可后置。

### NIT-2 — Anime 详情窗口硬编码 `ANIME DETAILS · MOCK`

- **一审主张**：`AnimeDetailsWindow.xaml:40-41` 保留硬编码英文 Mock 标识。
- **证据与边界**：代码证据为 `src/windows/ReminNote.Windows/Features/Anime/AnimeDetailsWindow.xaml:39-44`；
  但当前 P2 明确保留 ANIME 本地 Mock（`docs/slices/P2-03-live-shell-copy-fix.md:7-25`，
  `PRODUCT_RULES.md:5-27`），P2-05 也明确不把 Anime Mock 当真实 P2 业务（`docs/slices/P2-05-final-acceptance-and-documentation.md:20-26`）。
- **二审裁决**：**驳回（reject，作为 P2 缺陷）**；校正严重度 **STYLE/NIT（P0/后续 UI 事项）**。
  这是已有 P0 Mock 的文案一致性问题，不是 P2 Task loop、数据、排序或安全问题。
- **最小行动/文件范围**：若继续做 P0-07 文案/i18n，再单独处理该 XAML 与资源键；本 P2 不改、不列为必须修复。
- **是否阻塞 P2**：否。

### NIT-3 — `TaskWorkspace` 每次操作建立独立 DbContext

- **一审主张**：每次查询/操作建立新 context、没有复用连接。
- **证据**：`src/windows/ReminNote.Infrastructure/Application/TaskWorkspace.cs:14-17` 说明每操作拥有 DbContext；
  `:186-208` 明确每次应用操作建 context，读路径 `:112-130` 也独立建立 context；架构基线允许该模式
  （`ARCHITECTURE.md:45-56`）。
- **二审裁决**：**驳回（reject，作为 P2 缺陷）**；校正严重度 **STYLE/NIT**。这是已写明的每操作 UoW
  设计，不是连接泄漏或数据一致性失败；P2 的 repository 方法还以事务保存。
- **最小行动/文件范围**：无 P2 行动；只有出现测量到的性能/连接管理问题时，才在 Infrastructure 和测试中重新设计。
- **是否阻塞 P2**：否。

### NIT-4 — `NeedsReview`/计数为派生属性、每次遍历计算

- **一审主张**：`TodayReadModel` 的 `NeedsReviewCount` 与 `NeedsReview` 每次读取都会遍历集合。
- **证据**：`src/windows/ReminNote.Core/Today/TodayContracts.cs:49-60` 为 LINQ 派生属性；查询返回小型只读
  数组（`src/windows/ReminNote.Infrastructure/Application/TodayQueryService.cs:62-73`）。
- **二审裁决**：**驳回（reject，作为 P2 缺陷）**；校正严重度 **STYLE/NIT**。这是透明的只读模型实现，
  没有发现重复读取导致的语义错误或 P2 规模性能问题。
- **最小行动/文件范围**：无 P2 行动；只有在模型变为大集合或性能数据证明必要时，才缓存或预计算并补读模型测试。
- **是否阻塞 P2**：否。

### NIT-5 — 排序修复后的条件性 UIA 验证

- **一审主张**：`8a86a00` 修复默认 `sort_order=0` 交换后有条件性 UIA 证据，而不是完整桌面 GUI 证据。
- **证据**：`docs/slices/P2-05-final-acceptance-and-documentation.md:119-127` 明确记录修复、排序结果、
  `SortOrderChanged` 历史、`WINDIR` 条件和“不是用户正常桌面签字”；阶段报告也把正常桌面验收与条件性 smoke 分开
  （`docs/reports/P2-阶段集成报告-2026-08-28.md:24-35,146-181`）。
- **二审裁决**：**接受（accept，作为证据限定）**；校正严重度 **STYLE/NIT**。一审没有把条件性 UIA
  夸大成完整人工验收，记录是准确的；排序功能本身的代码/测试门禁已通过。
- **最小行动/文件范围**：由用户按 P2-05 手册完成正常桌面排序/重启/双宿主验收并记录，不要求本二审修改代码。
- **是否阻塞 P2**：该 NIT 本身否；未完成人工验收由 `P2-GATE-01` 阻塞完成声明。

### NIT-6 — `P1ManualHarness` 仍在 Solution，建议未来升级通用 CLI

- **一审主张**：P1 harness 保留在 Solution，当前可输出 P2 schema/列，未来可评估通用化。
- **证据与边界**：`ReminNote.sln:20-28` 注册 `ReminNote.P1ManualHarness`；其 `schema`/`list` 命令在
  `tools/ReminNote.P1ManualHarness/Program.cs:24-80,102-117`；P2-05 明确复用该 harness 做 schema 检查
  （`docs/slices/P2-05-final-acceptance-and-documentation.md:37-51`）。
- **二审裁决**：**驳回（reject，作为 P2 缺陷）**；校正严重度 **STYLE/NIT**。保留它并不违反 P2，
  也没有发现其与 `sort_order`/新表冲突；通用 CLI 是 P2.5 工具演进建议。
- **最小行动/文件范围**：P2 不改；未来若需要通用 CLI，再单独评估 harness、命令契约和工具测试。
- **是否阻塞 P2**：否。

## 人工验收残留

### P2-GATE-01 — 条件性 smoke 不能替代用户桌面验收

- **一审发现/主张**：一审总裁决已说明自动化通过，但 P2 完成声明仍需用户执行双宿主 GUI、已有数据升级和
  跨午夜人工验收（`reviews/review-2026-08-28-p2.md:10-15`）。
- **精确证据**：
  - `TESTING.md:43-59` 要求 Main/Widget 同库、DONE/结果/Quick Add、重启、RANGE/NEEDS REVIEW、跨午夜、
    非法 Parser、migration、未知路径和审查文件不变，并明确完整双宿主人工验收尚未执行。
  - `docs/slices/P2-05-final-acceptance-and-documentation.md:54-56` 把人工验收标为“待用户执行”；
    `:71-110` 给出五组步骤；`:114-125` 明确条件性 UI smoke 不是用户正常桌面签字，跨午夜和真实已有 P1
    数据 forward migration 仍待执行。
  - 阶段集成报告 `docs/reports/P2-阶段集成报告-2026-08-28.md:24-35,146-181` 将条件性运行 smoke、
    用户桌面签字、跨午夜和已有 P1 数据迁移分别列出，未把它们合并成“人工完全验收”。
- **二审裁决**：**接受其“尚未完成”状态（accept）；对实际行为通过性为信息不足（info insufficient）**。
- **校正后的严重度与理由**：**MINOR（强制验收门槛，不是代码缺陷严重度）**。已有自动化和条件性进程
  证据可降低风险，但不证明正常用户桌面环境下的完整点击、重启、实际时序或已有数据迁移保留。
- **最小行动/文件范围**：用户在安全的开发数据库副本/指定开发库上按 P2-05:58-110 完成：Main/Widget
  同库双向 Quick Add/结果、重启保留、结束 RANGE 的 NEEDS REVIEW、跨午夜 `00:30/01:00`、改期/同组排序/
  继续、非法 Parser 不写库、无效根目录非零和已有 P1 数据 forward migration 保留。把实际路径、时间、结果和
  失败项记录在后续验收记录中；不 purge、删除或覆盖现有开发库，不改写 reviews/second-review 原件。
- **是否阻塞 P2**：**是，阻塞“人工完全验收/P2 完成声明”**；不等同于判定当前实现必然失败，也不要求本二审代替用户操作。

## 二审额外发现

### P2-EXTRA-01 — Main 的一次排序交换由多个独立 application 写入组成

- **发现主张**：Main UI 对一次上移/下移构造两个或多个目标后，逐一调用 `ReorderAsync`；写门和数据库事务
  保护每个调用，但没有把整次交换作为一个跨任务原子命令。
- **精确证据**：`src/windows/ReminNote.Windows/Features/Today/TodayPageViewModel.cs:760-772` 在循环中
  逐项调用 `ReorderAsync`；`src/windows/ReminNote.Infrastructure/Application/TaskApplicationService.cs:139-168`
  及 `CrossProcessTaskWriteGate.cs:30-51,62-71` 的租约范围是单次 service 调用；repository 的单次更新事务为
  `src/windows/ReminNote.Infrastructure/Persistence/TaskRepository.cs:92-121`。接管记录已经如实记录该窗口、
  最坏结果和 P2.5 方向（`docs/reports/P2-总成接管记录-2026-08-28.md:101-104`）。
- **二审裁决**：**接受（accept，作为后置已知限制）**。
- **校正后的严重度与理由**：**STYLE/NIT**。这是可证明的并发窗口，但当前 Widget 没有排序入口，Main UI 已做
  日期/分组/边界保护；已记录的最坏结果是重复排序值回落到稳定时间/ID 顺序，不是跨组或数据删除。不能把它夸大成
  当前 P2 数据损坏或安全阻断。
- **最小行动/文件范围**：P2.5 进入 Agent/命令层时，将排序交换改为服务级批量命令或单事务，并补交错写入/失败回滚
  测试；预计涉及 `ReorderTaskCommand`、application service/repository、Main VM 和测试。P2 不改。
- **是否阻塞 P2**：否；作为 P2.5 单写者/命令串行化的前置事项保留。

除上述已由代码/文档/测试证据支持的后置事项外，本二审没有发现应新增为 P2 阻断项的代码或文档问题。
P0 Mock 兼容路径、Anime Mock、Reminder、Agent/业务 IPC、WAL、Change Journal、Sync 和 Anime 网络均按
`P2-00:22-24`、`P2-03:9-25`、`P2-04:3-5`、`P2-05:20-26` 的明确范围外处理，不计为 P2 缺陷。

## 最终处理建议

### 必须修复/闭环

1. 修复 `IMP-P2-1` 的中文相对日期粘连守卫并增加 Parser 回归测试；本二审没有实施该修复。
2. 由用户完成并记录 `P2-GATE-01` 的正常桌面人工验收、跨午夜实际时序和已有 P1 数据 forward migration。

### 可后置

1. 将 `IMP-P2-2` 的排序上下文/邻接/版本验证和 `P2-EXTRA-01` 的批量原子交换纳入 P2.5。
2. 将 NIT-1 的查询范围下推/分页作为规模增长后的性能工作；NIT-2、NIT-3、NIT-4、NIT-6 不构成 P2 行动。

本报告只新增当前文件；既有一审报告、`second-review/README.md`、产品代码、测试代码和其他文档均不应被本报告覆盖或改写。
