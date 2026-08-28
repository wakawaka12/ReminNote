# ReminNote 测试说明

## P0-02 自动验证

```powershell
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release
```

重点验证：NuGet locked restore、WPF XAML 编译、Host 依赖组合、MVVM 类型可解析、项目引用边界、P1 Core/SQLite/application 测试和零警告构建。P0 UI 仍以手动可访问性验收为主，不建立大规模 UI 快照测试。

## P0-02 手动验证

环境：Windows 10 22H2 或 Windows 11，仓库根目录为 `D:\Anime`。

### 测试 1：启动 App Shell

1. 打开 PowerShell，进入 `D:\Anime`。
2. 执行 `./scripts/run.ps1`。
3. 预期：出现 ReminNote WPF 主窗口，左侧有“今天”和“动画”两个导航项，默认显示“今天”。
4. 失败：窗口无法启动、出现未处理异常、页面显示数据库/网络数据，或控制台构建失败。

### 测试 2：导航到动画

1. 在主窗口左侧点击“动画”。
2. 预期：右侧标题切换为“动画”，显示 ANIME Shell 占位内容。
3. 失败：按钮无响应、窗口崩溃、导航后仍显示“今天”，或出现真实动画数据。

### 测试 3：返回今天

1. 点击左侧“今天”。
2. 预期：右侧标题和占位内容回到“今天”。
3. 失败：页面无法切换或 ViewModel 状态异常。

### 测试 4：开发数据隔离

1. 关闭应用。
2. 确认仓库内没有被创建生产数据库或 Token 文件。
3. 预期：当前 Slice 不创建 SQLite、Token、网络缓存或生产配置。

每个后续 Slice 都必须补充具体的启动、点击、输入、预期结果和失败判定。

## P2 自动化与手工验收边界

P2 必须自动化验证 Noda Time 时区/工作日边界、Today 状态和分组、RANGE 跨午夜、确定性 Parser、结果/计划历史、改期保护、继续关系、排序、迁移保留和本地写门失败保持不变。WPF 布局仍以人工验收为主，不以 P0 Mock 交互代替真实 SQLite 证据。

已合并 Slice 的独立证据必须和整仓证据分开记录：持久化边界曾以隔离 xUnit executable 77/77 通过，Today 查询以隔离 executable 75/75 通过，Main Today ViewModel 的裁剪测试为 6/6，Widget Slice 的 Release executable 为 82/82。上述证据覆盖各自边界，不代表正式 Main/Widget 双宿主已经完成总成验收；整仓门禁仍需在当前 lock 文件和 App/DI 组合上复跑。

标准整仓门禁：

```powershell
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release
./scripts/verify-p0-07.ps1
```

P2 手工验收必须在同一仓库根目录分别启动 Main 和 Widget，确认二者使用同一个 `.devdata/reminnote.sqlite`：Main 创建/刷新后 Widget 在短轮询窗口内显示，Widget 的 DONE、RANGE 结果和 Quick Add 写入后 Main 能读回；必须检查重启保留、RANGE NEEDS REVIEW、跨午夜、Parser 非法输入、migration 不删库、未知路径不写入，以及 `reviews/`/`second-review/` 未被修改。Widget 的 `--repo-root` 路径必须通过 `.git` 与 `ReminNote.sln` 校验；当前 Main 正式组合仍为总成复核项，未接上 live `TaskWorkspace` 前不得报告该双宿主步骤通过。

自动化或手工失败包括：未来任务进入 Today、已完成历史任务误显示、跨午夜换日丢失或换组、RANGE 自动变 `MISSED`、Parser 静默丢弃标签/优先级、结果或计划历史缺失、已有结果仍可改时间、继续关系错误、排序改写计划时间、写门超时静默覆盖、ViewModel 直写数据库、migration 删除原库、Main 仍以 Mock 作为 P2 真实入口，或审查材料被修改。

## P0-07 自动验证补充

```powershell
./scripts/verify-p0-07.ps1
```

该门禁验证稳定资源键、默认资源非空、Widget 解决方案登记、项目 lock 文件、当前 UI 的 AutomationProperties 标记和开发数据清理边界。P1 业务测试由 `scripts/test.ps1` 直接运行构建后的 xUnit v3 executable；不能以旧 `dotnet test` 的零发现结果替代真实测试执行证据。

## P0-03 Design System 手动验证

环境：Windows 10 22H2 或 Windows 11，仓库根目录为 `D:\Anime`。

### 测试 1：启动并确认资源加载

1. 打开底部 PowerShell，进入 `D:\Anime`。
2. 执行 ./scripts/run.ps1。
3. 预期：ReminNote 主窗口正常打开；背景、侧栏、卡片、页标题和底部栏保持统一亮色信息面板风格。
4. 失败：窗口启动异常、出现 XAML/资源字典错误、界面缺少颜色或控件样式。

### 测试 2：导航悬停和按下反馈

1. 将鼠标移动到左侧“今天”和“动画”按钮上。
2. 分别按住鼠标左键，再松开。
3. 预期：悬停和按下时按钮背景有可见变化；松开后仍能完成对应页面切换。
4. 失败：按钮没有任何反馈、点击无效、窗口崩溃或页面内容错乱。

### 测试 3：键盘焦点反馈

1. 点击窗口空白区域后按 Tab，直到左侧导航按钮获得焦点。
2. 使用方向键或继续按 Tab 在导航项间移动，按 Enter 激活当前按钮。
3. 预期：当前焦点按钮出现清晰的浅蓝色焦点边框；按 Enter 可切换页面。
4. 失败：焦点不可见、Tab 无法到达导航按钮、Enter 不能触发导航或发生崩溃。

### 测试 4：窗口尺寸与文字回退

1. 将窗口调整到接近最小尺寸（宽约 960、高约 640）。
2. 在 Windows 设置中临时切换系统显示缩放到 125%（如方便），重新启动应用。
3. 预期：窗口仍可使用，中文文字可显示，卡片内容不会被裁剪到无法辨认。
4. 失败：启动失败、文字消失、导航区域不可点击或布局严重重叠。

### 测试 5：数据与网络边界

1. 关闭应用。
2. 检查 `D:\Anime\.devdata` 和项目目录。
3. 预期：本 Slice 不新增 SQLite、Token、网络缓存或生产配置文件。
4. 失败：出现未授权的数据/Token 文件或应用产生网络访问。

## P0-07 手动验收

环境：Windows 10 22H2 x64 或 Windows 11 x64，仓库根目录，使用默认简体中文系统语言。

### 主窗口与默认中文

1. 执行 `./scripts/run.ps1`，确认主窗口标题、侧栏、页眉和底部状态区可见。
2. 预期：默认显示简体中文 Shell 文案；不出现旧的 P0-03 阶段文案、数据库、网络或生产数据提示。
3. 失败：窗口无法启动、资源缺失、出现 `[Missing resource:`、文案为空或显示旧阶段事实。

### TODAY

1. 默认进入“今天”，使用 Tab 依次移动到导航、Quick Add、任务操作和待复盘操作；按 Enter 或 Space 激活可见按钮。
2. 打开 Quick Add，输入任意标题并添加；再切换任务完成、置顶和待复盘入口。
3. 预期：焦点有清晰边框，关键控件可由读屏/UI Automation 识别；分组、状态和反馈文案正常显示；变化只存在于本次进程。
4. 失败：Tab 无法到达控件、焦点不可见、按钮无名称/帮助文本、页面崩溃或创建持久数据。

### ANIME

1. 切换到“动画”，用 Tab 访问搜索框、清除、栏目、卡片和详情操作；输入不存在的关键词后清除。
2. 选择一个 Mock 条目，执行加入待看、标记已看、追番/模拟提醒和重新载入。
3. 预期：默认简体中文文案稳定，搜索/筛选/操作反馈正确，焦点可见，关键控件有可读 Automation 名称。
4. 失败：输入框或按钮不可键盘访问、无焦点反馈、出现真实网络/数据请求或状态写入磁盘。

### Widget

1. 启动 Widget 项目或从主开发入口打开 Widget，使用 Tab 访问状态、关闭、TODAY/ANIME 标签、主操作、Quick Add 和提醒抽屉。
2. 切换 Widget Mock 状态，执行一个主操作，打开并关闭提醒抽屉。
3. 预期：标题栏、状态按钮、关闭按钮、标签和抽屉操作均有可见焦点和 Automation 名称；Mock 反馈明确说明仅当前窗口有效。
4. 失败：关闭/状态按钮不可聚焦、标签无名称、抽屉无法关闭、出现真实 Reminder/IPC/持久化数据。

### 构建、CI 与数据安全

1. 执行 `./scripts/build.ps1 -Configuration Release`、`./scripts/test.ps1 -Configuration Release` 和 `./scripts/verify-p0-07.ps1`。
2. 预期：locked restore、Release 构建、测试流程和 P0-07 门禁成功；CI 日志包含实际 SDK 信息与解决方案项目清单。
3. 检查 `.devdata` 和项目目录；预期没有 SQLite、Token、网络缓存、IPC 或生产配置文件。
4. 失败：依赖未锁定、构建有错误/警告、门禁漏报资源缺失、或产生超出开发目录的数据。
