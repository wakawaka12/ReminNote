# P0-05 ANIME 高保真 Mock 实施简报

## Goal

在现有 WPF 导航壳和 ReminNote Design System 之上，建立可真实运行、可交互验证的 ANIME 本地 Mock 页面，展示动画库的信息层级和核心用户动作，为后续 P4 本地 Anime domain 保留清晰的 UI 边界。

## In Scope

- ANIME 页面真实 WPF `DataTemplate`、ViewModel、Mock model 和本地状态服务边界。
- `NEXT AIRING` 主视觉，以及 `WATCH LATER`、`WATCHING`、`PLAN TO WATCH`、`THIS SEASON` 信息层级。
- 固定本地 Mock 数据，不访问网络、不读取数据库、不使用真实 Token。
- 本地搜索、栏目筛选、动画选择、上层详情对话框、标记已看、加入/移出 WATCH LATER、开启/关闭追番和模拟提醒状态。
- 使用现有 Design System 的颜色、字体、间距、圆角和卡片资源，并在 ANIME 资源字典中补充仅限模块的装饰/控件样式。
- 本简报中的手动验收步骤、预期结果、失败判定、风险和集成说明。

## Out of Scope

- Bangumi、bangumi-data 或任何其他网络 Provider。
- PAT、Credential Manager、DPAPI、真实图片下载和远程缓存。
- SQLite、EF Core、业务持久化、Agent IPC、Reminder Scheduler 和真实提醒。
- 播放、下载、视频源、弹幕、同步、真实 Anime domain、Anime -> Task 关系。
- 修改 App.xaml、共享 ViewModels、Resources/DesignSystem、Solution、Directory 文件、脚本、CI、TODAY 或 Widget；MainWindow 仅允许接入本 Slice 的拥有者详情对话框生命周期。

## Product Rules

- ANIME 仍是 P0 的高保真 Mock，不宣称已经接入真实动画数据。
- 页面展示的本地追番、已看、待看和提醒动作只改变当前进程内的 Mock 状态；关闭程序后不保证保留。
- `WATCH LATER` 用于表现已播但尚未观看的本地队列；下一集即将播出时，未观看项可在队列中保持较高优先级。
- airing 时间和倒计时是静态 Mock 文案，不代表当前系统时钟，也不触发真实通知。
- 页面不访问生产数据、`.devdata`、Token、SQLite 或网络 Provider。
- 继续使用现有亮色、干净的日式游戏/JRPG 信息面板视觉方向，不引入第三方 UI 框架或新的 NuGet 依赖。

## Reuse Research

- 复用原生 WPF `ResourceDictionary`、`DataTemplate`、`ItemsControl`、`ScrollViewer` 和命令绑定，不增加 UI 框架。
- 复用 `Resources/DesignSystem` 中现有的 `DynamicResource` 颜色画刷、Typography、间距、圆角、卡片和按钮语义。
- 复用已有 `CommunityToolkit.Mvvm` 的 `ObservableObject` 与 `RelayCommand`；不引入额外状态管理依赖。
- 本 Slice 不需要新的第三方依赖，因此不产生新的许可证、维护或安全风险。

## Acceptance

- 从主窗口导航到“动画”后，页面能够显示 ANIME 高保真 Mock，而非 P0-02 占位内容。
- 页面首屏清晰呈现 `NEXT AIRING` 主视觉，并能继续浏览 `WATCH LATER`、`WATCHING`、`PLAN TO WATCH`、`THIS SEASON`。
- 搜索框和栏目筛选能即时改变本地 Mock 卡片列表；点击“查看详情”后上层对话框显示所选动画，且可明确关闭。
- “标记已看”“加入/移出待看”“开启/关闭追番”和“模拟提醒”按钮有明确的文本/计数反馈，窗口不崩溃。
- 所有状态仅存在内存；实现中没有网络客户端、SQLite、真实 Token、Reminder 或 IPC 调用。
- 仅增加 `Features/Anime/**`、MainWindow 的对话框接入、测试与 `docs/slices/P0-05-*`，不越过并行开发文件所有权。
- 通过 locked restore、Release build 和适合当前仓库的自动化测试，且无新增未解释警告。

## Manual Test

### 环境

- Windows 10 22H2 或 Windows 11 x64。
- 仓库根目录：`C:\Users\EMT\.codex\worktrees\8392\Anime`（或集成后的实际仓库根目录）。
- 构建配置：Release。
- 数据路径：本 Slice 不创建或读取数据文件。

### Test 1：进入 ANIME 并确认信息层级

1. 在仓库根目录执行 `./scripts/run.ps1`。
2. 点击左侧“动画”。
3. 观察页面首屏，并向下滚动。

预期：

- 页面标题为“动画”，并有明确的 `NEXT AIRING` 主卡片、倒计时、剧集信息和本地 Mock 标识。
- 向下可以看到 `WATCH LATER`、`WATCHING`、`PLAN TO WATCH`、`THIS SEASON` 对应的栏目/筛选入口和卡片。
- 页面保持亮色信息面板风格，中文可读，未出现数据库或网络错误。

失败：页面仍显示旧的“ANIME Shell 占位”、出现空白/XAML 异常、主要栏目不可辨认、窗口崩溃或出现真实网络数据。

### Test 2：栏目筛选与搜索

1. 点击“待看”筛选按钮，确认列表变化。
2. 点击“追番中”，确认列表再次变化。
3. 在搜索框输入 `星`，再输入一个不存在的词。
4. 清空搜索框并点击“本季”。

预期：

- 筛选按钮有选中反馈，列表只显示对应本地 Mock 条目。
- 搜索会即时缩小列表；无匹配时显示清晰的空结果说明。
- 清空搜索后列表恢复；不会发起网络请求或改变主窗口导航。

失败：筛选无效、搜索需要提交后才更新、无结果时页面重叠/崩溃，或出现真实数据。

### Test 3：本地状态交互

1. 先向下滚动 ANIME 页面到任意位置，选择一张动画卡片并点击“查看详情”。
2. 确认上层详情对话框显示明确标题、动画名称和状态，再点击“加入 WATCH LATER”或“移出 WATCH LATER”。
3. 点击右上角 `×` 或“取消”。
4. 点击“标记已看”，观察状态和计数。
5. 点击“开启追番/关闭追番”和“模拟提醒”各一次。

预期：

- 详情对话框位于页面上层，不与底层卡片/面板互相遮挡；标题和状态与所选卡片一致。
- 对话框打开后焦点进入关闭入口；关闭后回到原页面滚动位置和原触发控件，不强制跳到底部详情区。
- 待看、已看、追番和模拟提醒动作立即更新按钮文案、卡片状态或栏目计数。
- 操作只影响当前运行中的 Mock 状态；不创建数据库、Token、缓存或提醒实例。

失败：按钮无响应、状态更新到错误条目、应用崩溃、关闭重启后声称已持久化，或产生越界数据文件。

### Test 4：响应式与键盘可用性

1. 将窗口调整到接近最小尺寸（约 960x640）。
2. 使用 Tab 在搜索框、筛选按钮和操作按钮间移动，并用 Enter 激活一个按钮。
3. 向下滚动检查卡片和空结果文案；用 Tab/Enter 打开并关闭详情对话框。

预期：

- 页面可滚动，卡片/文字不被严重裁切；详情对话框焦点可见且可用 X/取消关闭；Enter 能触发当前按钮。
- 不因缩小窗口或键盘操作发生异常。

失败：控件无法获得焦点、焦点不可见、页面布局重叠、滚动失效或窗口崩溃。

## Risks

- 当前共享壳的 Header/Footer 文案仍属于 P0-03，ANIME 页面无法在本 Slice 越界修改；集成时可由公共文件负责人统一更新 Slice 标识。
- Mock 卡片使用矢量色块和文字占位，不代表最终远程封面资源的裁切、版权或缓存方案。
- 倒计时和 airing 文案固定在 ViewModel 中，仅用于验证视觉层级；真实时间语义留给 P4/P5。
- 页面状态不持久化，重启后恢复默认 Mock 数据。

## Integration Requests

- 无必须的公共文件改动请求；现有 App 已合并 `Features/Anime/AnimeResources.xaml`，可直接加载本 Slice 资源。
- 集成后请在 `codex/p0-integration` 分支重新执行完整 WPF 启动冒烟，并将 TODAY/ANIME 合并后的公共壳文案按整体 P0 状态统一调整。
