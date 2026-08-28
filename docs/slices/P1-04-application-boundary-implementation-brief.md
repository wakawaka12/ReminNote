# P1-04 Application Boundary 实施说明

- 阶段：P1 Real Task domain + SQLite persistence
- 窗口：P1-04 Application boundary
- 性质：应用服务、查询和写入边界说明；不代表公共 API 已在当前 checkout 实现
- 依据：主计划 Application service style、Single Writer 迁移约束、P1 阶段预报告

## 目标

以显式的 Task application/query 边界连接 P1-01 Core 与 P1-02 Infrastructure，使创建、读取、更新、删除可以被 UI、测试或未来 Agent 适配，而不让 WPF ViewModel 直接操作 DbContext。P1 可以有本地应用服务直接写 SQLite；P2.5 才迁移为 Agent 唯一业务写入者和 Named Pipe 路径。

## 允许的边界形状

- 优先使用明确的 Task application service/query service；不为 P1 强行引入 MediatR 或“handler ocean”。
- 输入使用可验证的 command/request，输出使用领域结果或查询模型；具体公共命名由 P1-00/P1-01/P1-04 冻结并记录。
- create/update 先经过 Core 验证，再由 Infrastructure 持久化；read/query 不把 EF entity 泄露到 WPF。
- 删除必须是明确的 Task 操作，并返回可判断的成功/不存在/验证失败结果；不得将异常吞成“已删除”。
- 依赖注入由集成窗口接线；Windows 的 P0 Mock 展示行为保留，不得将 P0 Quick Add 误标为已持久化。

## CRUD 最小验收语义

| 操作 | 最小行为 | 失败时 |
|---|---|---|
| Create | 接收合法标题/时间形状，生成稳定 Task ID，写入 `.devdata/reminnote.sqlite`。 | 非法领域输入不写入；错误可识别。 |
| Read | 按 ID 和必要查询条件读取真实 Task；返回本地日期/时间和结果语义。 | 不存在项明确返回未找到，不伪造对象。 |
| Update | 只更新允许变更的字段，保留历史/时间语义，重新验证组合。 | 验证失败或不存在时不产生部分写入。 |
| Delete | 只删除明确指定的 Task（若最终产品使用 trash，则需另行决策）。 | 非法 ID/不存在项不删除其他数据。 |

`Task` 标题的最小长度、删除是否为硬删除、结果字段集合和并发策略若尚未冻结，必须在实际 API 与测试中明确，而不是由手测人员猜测。

## P1/P2.5 迁移约束

- P1 不引入 IPC、Named Pipe 或 Agent 业务写入；
- P1 的写入路径必须集中在应用服务边界，便于 P2.5 替换为 `Command -> Named Pipe -> Agent`；
- 不提供两个同等合法的业务写入路径；P0 Mock 操作仍是演示路径，不得与真实 Task 混称；
- 不把 Reminder、Anime 或 Sync 的服务接口/空表作为 P1 的“预留实现”。

## 手动验收入口要求

当前仓库只有 P0 WPF Mock，尚没有可执行的 P1 CRUD 入口。P1-06 必须在集成报告中记录实际使用的入口（最小 Task UI、受控验证 harness 或其他已合入的应用入口）及其启动方式。未记录前，不能把 P0 TODAY 的 Quick Add 当作本测试的 Create。

## 失败判定

- ViewModel 直接 new DbContext、拼 SQL 或决定 schema；
- 非法输入先写库后报错，或更新失败留下半条数据；
- 读取返回 EF entity、数据库异常或内部堆栈作为用户文案；
- P0 Mock 反馈声称“已保存”，但没有真实数据库证据；
- P1 引入 Agent/IPC/Reminder/Anime/Sync 逻辑，或形成无法迁移到 P2.5 的双写入口。
