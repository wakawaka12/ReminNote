# ReminNote 产品规则

完整产品规则以 `ReminNote_MASTER_DEVELOPMENT_PLAN.md` 为准。本文件记录当前 Slice 实际采用的规则。

## 当前阶段

P0/P1 已完成并通过用户确认；当前执行 P2 Real TODAY / Widget Task loop。P2 的持久化、Today、Parser、Main Today ViewModel、Widget 及 Widget lock 文件已合并到当前分支。P2 将真实 Task 接入 Main TODAY 和 Widget，但不实现 Reminder、Anime 网络、Agent IPC、WAL、Change Journal 或 P2.5 单写者迁移。当前 Widget 正式入口已接入本地工作区；Main 的正式 live 组合与真实 SQLite 全量查询已在总成接管期间接通并修复（含 TODAY 改期/排序入口补齐）；Main/Widget 双宿主真实进程人工验收仍未执行，不能把独立测试当作该步骤的验收证据。

## 已冻结的核心边界

- ReminNote 是 Windows 桌面端的提醒与个人记录应用。
- TASK 是计划，不是时间追踪；时间形状只有 `ANYTIME`、`TIME`、`RANGE`。
- 计划时间与提醒时间是两个独立概念。
- 生产数据、真实 Token 和开发数据必须隔离。
- P0-P3 范围已冻结；未到对应 Slice 的功能进入 Backlog，不提前实现。

## P2 已冻结规则

- 逻辑工作日默认边界为 `00:00`，使用系统时区，边界以整分钟配置并持久化到单行 `app_settings`；它只影响 TODAY/复盘/ANYTIME 归属，不改变 Task 的日历日期或绝对时间。
- Today 默认展示逻辑今天的计划及未完成历史计划；未来日期不进入 Today。默认分组为 `OVERDUE`、`MORNING`、`AFTERNOON`、`EVENING`、`ANYTIME`、`COMPLETED`。
- `TIME` 按本地计划时间判断；RANGE 结束无结果显示 `AWAITING RESULT` 并计入 `NEEDS REVIEW`，仍保留原计划时段，不自动写入 `MISSED`。跨午夜 RANGE 由 `RangeEnd < RangeStart` 推导，始终归属开始日期/开始时段，换日后仍可见。
- Quick Add 只接受确定性日期/时间/范围语法：`今天`、`明天`、`后天`、`yyyy-MM-dd`、`HH:mm`、`HH:mm-HH:mm` 或同义的半角/短横线范围；P2 不实现 `#标签`、`!优先级`，遇到未支持语法直接拒绝；标题上限为 500 字符，超长拒绝且不截断。
- `DONE` 等价于记录 `COMPLETED`；`RecordedAt` 只表示用户记录动作的时间。当前结果允许覆盖式保存，但每次结果、计划改期、排序变化和继续关系都追加不可变 `task_history` 快照。
- 未来/尚未开始 Task 以及用户明确改期的无结果过时 Task 可改计划；计划改期保留旧计划快照。已有结果的 Task 禁止改时间，沿用 `task.time_spec.changed_after_result`；改名仍允许。P2 继续使用硬删除，不承诺删除后的历史审计保留。
- `PARTIAL` 只适用于 RANGE；继续操作创建新的 UUID v7 Task，并以 `continued_from_task_id` 关联原 Task，原计划和原结果保持不变。`sort_order` 只用于同一计划日期/Today 分组内的稳定排序，不等同于改期；当前 PIN 仍是页面状态，不持久化。
- Main 与 Widget 必须通过共同的 application/query service 访问已验证仓库根目录下的 `.devdata/reminnote.sqlite`，ViewModel 不得直连 DbContext、SQL 或 EF entity。完整本地读改写使用命名跨进程写门；当前实现为可跨 `await` 租约释放的 `Semaphore(1,1)`，超时失败而不静默覆盖。P2 不引入 Agent/IPC。
- Widget 的 live `Snooze`/`Reschedule` 不擅自改变计划，明确提示回 Main TODAY 编辑；Reminder Drawer、Anime 和既有 Mock 交互仍不等于 P2 业务实现。

## P0-02 规则（历史基线；P2 已更新 TODAY/Widget 入口）

- P0 阶段 TODAY 与 ANIME 只作为导航入口和占位页面存在；P2 保留无参 Mock 构造作为兼容夹具，并新增独立的 live Today/Widget 路径。
- 当前页面不读取 SQLite、开发数据库、网络 Provider 或真实用户数据。
- 页面状态由 ViewModel 持有，窗口通过 Generic Host 和 DI 创建。
- 导航命令只负责切换壳内页面，不承载业务写入。

## Slice 规则

每个 Slice 必须保持小而可验证，完成后报告改动、构建/测试结果、手动测试步骤与已知限制。没有用户明确指示，不自动进入下一个 Slice。
## P0-03 规则

- Design System 使用原生 WPF 资源字典、样式和控件模板。
- 颜色、文字层级、间距和圆角以资源键集中定义，页面不重复散落同类视觉常量。
- 当前只提供亮色基线；完整主题、用户背景、对比度和动画设置不在本 Slice 内。
- 导航按钮必须保留鼠标悬停、按下、禁用和键盘焦点反馈。

## P0-07 规则

- Shell、TODAY、ANIME 和 Widget 的用户可见文案必须通过稳定资源键或资源格式化边界提供，默认显示简体中文；本 Slice 不实现运行时语言切换。
- 可交互控件必须保留可见键盘焦点，并为关键按钮、输入框、页面区域提供 `AutomationProperties.Name` 或 `HelpText`。
- P0 Mock 仍只使用进程内示例数据；P0-07 本身不引入数据库、网络、IPC、提醒调度、真实国际化切换或 P1+ 功能。P2 的 live Task 路径不改变该 Mock 夹具的语义。
- P1 的真实 Task 写入经过 application boundary 和 Infrastructure SQLite；P0 Mock 仍只使用进程内示例数据。
- `scripts/verify-p0-07.ps1` 是 `scripts/test.ps1` 之外的静态/配置门禁；P1 业务测试必须以真实 xUnit v3 executable 的非零退出码检查为证据。
