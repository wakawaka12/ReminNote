# P0-06 Widget 手动验收步骤

## Environment

- Windows 10 22H2 x64 或 Windows 11 x64；
- PowerShell；
- 仓库根目录：当前 worktree 根目录；
- SDK：由 `scripts/resolve-dotnet.ps1` 解析的 .NET 10 SDK；当前验证为 10.0.400；
- 配置：Release；
- 数据路径：无。Widget 只使用内存 Mock，不读取或写入 `.devdata`、SQLite、Token 或生产配置。

## 启动

在仓库根目录执行：

```powershell
$repo = (Get-Location).Path
. "$repo\scripts\resolve-dotnet.ps1"
$dotnetPath = Resolve-DotNetPath
Push-Location $env:ProgramFiles
& $dotnetPath run --project "$repo\src\windows\ReminNote.Widget\ReminNote.Widget.csproj" --configuration Release --no-restore
Pop-Location
```

若已完成 Release 构建，也可以直接双击：

```text
src/windows/ReminNote.Widget/bin/Release/net10.0-windows/ReminNote.Widget.exe
```

### 启动预期

- 出现居中的无系统边框 Widget 窗口；
- 默认页面为“今天”，显示 `PRIMARY · NEXT UP`、主任务、Upcoming 队列和 `SUMMARY`；
- 顶部显示 `LOCKED` 状态芯片，底部显示 `LOCAL MOCK · NO DATA CONNECTION`；
- 不应出现未处理异常、XAML 资源错误或控制台构建错误。

### 启动失败判定

- 进程立即退出或窗口无法显示；
- 窗口出现 XAML/ResourceDictionary/绑定异常；
- 页面读取数据库、网络或生产用户内容；
- 启动过程中创建 SQLite、Token、网络缓存或生产配置文件。

## Test 1 — TODAY / ANIME 分页

1. 启动 Widget，确认默认页面为“今天”。
2. 点击右上方 `ANIME` 页签。
3. 点击 `TODAY` 页签返回。

预期：

- 默认显示 TODAY；
- ANIME 页显示独立的动画主卡片（集数占位、播出时间、Upcoming、Summary）；
- 返回后重新显示 TODAY；
- 页面切换不创建数据，不触发网络访问。

失败：

- 默认进入 ANIME；
- 两个页面内容混在一起；
- 页签不响应、窗口崩溃或出现真实动画/任务数据。

## Test 2 — 三档响应式尺寸

1. 拖动窗口边缘，将宽度调到约 400 DIP（Compact）。
2. 观察 TODAY 和 ANIME 主卡片，再分别点击两个页签。
3. 将宽度调回约 500 DIP（Standard）。
4. 将宽度调到约 680 DIP（Expanded）。
5. 在接近最小高度时，将鼠标移到 TODAY / ANIME 正文区域滚动滚轮或使用触控板上下滚动；再用 Tab 检查正文内的操作控件仍可聚焦。

预期：

- Compact：主任务/最近一集、当前时间和完成/WATCHED 主操作仍可见；Upcoming 队列、长 Summary 和次要操作收起；内容不重叠、不被裁切到无法辨认；
- 窗口高度不足时，正文区域出现可用的纵向滚动，滚轮/触控板可以看到位于下方的内容；顶部主信息、底部触发栏和焦点控件不被窗口圆角裁切；
- Quick Add 打开时，输入框和“添加”按钮完整位于正文面板内，不被底部触发栏覆盖；调整高度或滚动正文后仍可输入、添加和关闭；
- Widget 使用窗口边缘/右下角调整大小后仍保持圆角裁剪，内容随可用高度重排且不遮挡输入框、按钮或关闭入口；
- Standard：Upcoming 队列、Summary 和次要操作恢复；
- Expanded：窗口可继续放宽，主信息保持稳定且显示宽屏布局标识；
- TODAY 与 ANIME 三档尺寸均能正常切换。

失败：

- 任一尺寸下按钮或主信息不可点击；
- 文本、卡片重叠或窗口内容溢出；
- 鼠标滚轮/触控板无法滚动正文，或滚动后关键内容仍被裁切；
- Quick Add 输入框/添加按钮下半部被 footer 遮挡，或滚动后无法回到面板继续操作；
- Widget 无法通过窗口边缘/右下角调整大小，或调整后圆角、边框和焦点区域异常；
- 调整尺寸导致异常、黑屏或页面状态丢失。

## Test 3 — Widget 状态 Mock

1. 点击顶部 `◆ LOCKED` 状态芯片。
2. 重复点击观察状态依次变为 `TEMP`、`UNLOCKED`、`LOCKED`。
3. 点击底部“模拟提醒”，观察 `ALERT` 状态；确认 Alert 面板右上角有可操作的 `×`，且底部“模拟提醒”具有再次触发即关闭的 toggle 语义。
4. 在 Alert 面板中点击右上角 `×` 或“已处理”。

预期：

- 状态芯片按顺序显示 `LOCKED → TEMP → UNLOCKED → LOCKED`；
- 手动模拟提醒后显示明显的高优先级 Alert Overlay 和 `ALERT` 芯片；
- Alert 保持当前页面，不自动在 TODAY / ANIME 间跳转；
- 点击关闭入口后恢复模拟前的状态；Alert 打开时不会与 Quick Add / Reminder Drawer 叠层。

失败：

- 状态标签不变化或窗口崩溃；
- Alert 无 Overlay、无法关闭或自动切页；
- Alert 没有清晰的关闭入口，或能与其他面板同时覆盖；
- 产生真实通知、后台进程、网络请求或持久化数据。

## Test 4 — TODAY Mock 操作

1. 回到 TODAY。
2. 点击“完成”，再点击“延后”，再点击“改期”。

预期：

- “完成”显示 `COMPLETED` Mock 反馈；
- “延后”显示 30 分钟 Mock 反馈，并说明原计划未改变；
- “改期”显示已模拟移动到明天 09:00；
- 反馈仅在当前窗口内存在，不写入数据库。

失败：

- 按钮无响应、反馈与操作不符、窗口崩溃；
- 出现真实 Task、真实提醒或任何磁盘写入。

## Test 5 — ANIME Mock 操作

1. 点击 `ANIME`。
2. 点击 `WATCHED`、`稍后看`、`排任务`。

预期：

- 分别显示 `WATCHED`、`WATCH LATER` 和“已模拟排入明天任务”的反馈；
- 不修改 Bangumi、动画库或真实 Task；
- 不访问网络、不读取 Token。

失败：

- 操作无反馈、页面崩溃或出现真实动画数据；
- 发生网络访问、Token 读取或持久化写入。

## Test 6 — Quick Add Mock

1. 点击底部 `QUICK ADD`。
2. 在输入框输入 `明天 18:00 买东西 #生活`。
3. 点击“添加”。
4. 点击 Quick Add 面板右上角 `×` 关闭；再次点击底部 `QUICK ADD` 打开，再次点击底部按钮关闭。

预期：

- Quick Add 面板在 Widget 内联展开；
- 面板右上角始终有不被内容遮挡的可操作 `×`；底部 `QUICK ADD` 按钮可 toggle 打开/关闭；
- 输入为空时“添加”不可执行；
- 输入后点击“添加”显示“已加入 Mock 队列”，输入框清空；
- 关闭面板后回到原页面；数据只保留在当前进程内。

失败：

- 面板不展开、输入框不可用或添加按钮在空输入时仍执行；
- Quick Add 没有可操作关闭入口，或关闭入口被自身内容遮挡；
- 发生真实解析、任务创建、数据库写入或网络访问。

## Test 7 — Reminder Drawer 与 Alert

1. 点击底部 `REMINDERS · 2`。
2. 确认抽屉展示两个最近提醒条目。
3. 点击右上角 `×` 关闭抽屉；再次点击底部 `REMINDERS · 2` 打开，再次点击该底部按钮关闭。
4. 再打开抽屉，点击“标记当前提醒已读 (Mock)”。
5. 分别打开 Quick Add、Reminder Drawer，再点击底部“模拟提醒”，确认只显示 Alert；从 Alert 点击“打开提醒”后确认 Alert 关闭且只显示 Reminder Drawer。

预期：

- 右侧轻量 Reminder Drawer 覆盖当前 Widget 内容；
- 两条条目分别展示 Task 与 Anime 提醒语义；
- `×` 可关闭抽屉；
- 底部 `REMINDERS · 2` 具有 toggle 关闭语义；Quick Add、Reminder Drawer、Alert 不会同时打开或互相覆盖；
- 标记操作关闭抽屉并显示 Mock 反馈，不写入 ReminderInstance。

失败：

- 抽屉无法打开/关闭、内容溢出或导致主窗口崩溃；
- 底部触发按钮无法关闭对应面板，或出现多个面板同时叠层；
- 标记操作修改真实提醒历史或删除提醒规则。

## Test 8 — 数据与所有权边界

1. 关闭 Widget。
2. 检查 `src/windows/ReminNote.Widget/`、`.devdata/` 和项目目录的文件变更。
3. 检查当前分支的修改范围。

预期：

- 不新增 SQLite、Token、网络缓存、生产配置或用户内容文件；
- 代码变更只位于本次 P0 UI 范围内的 `src/windows/ReminNote.Widget/**`、`src/windows/ReminNote.Windows/{App.xaml.cs,MainWindow.xaml,MainWindow.xaml.cs,Features/Today/**,Features/Anime/**,Startup/**}`、相关测试与 `docs/slices/P0-06-*` / P0-07 验收说明；
- `ReminNote.Core`、SQLite/Infrastructure、Agent IPC 与 `reviews/` / `second-review/` 审查记录未被修改；
- `ReminNote.sln`、现有 `ReminNote.Windows`、公共 Design System、集中包版本、脚本、CI 和根级活文档未被修改。

失败：

- 出现任何未授权数据/Token/网络文件；
- 发现授权范围外文件被修改；
- Widget 依赖 Agent IPC、Reminder Scheduler 或真实业务服务才能启动。

## 补充回归 — Main Quick Add 连续添加

1. 启动 Main App，进入 TODAY 并打开 `QUICK ADD`。
2. 输入第一条任务并点击“添加”；确认输入框仍在原处且已清空。
3. 输入第二条任务并再次点击“添加”，随后点击“取消”。

预期：

- 每次添加后面板不关闭，输入框和“添加”按钮持续可用；
- 成功反馈靠近输入区清晰显示，并明确写出已加入 `ANYTIME`；反馈在下一次操作前保持可见；
- 每次输入均被清空，两个新条目都出现在/定位到 `ANYTIME`，取消仍可关闭面板。

失败：添加后输入框消失、面板自动关闭、反馈只在不可见的页面底部出现，或未明确说明 `ANYTIME`。

## 补充回归 — Main App / Widget 单实例与并行启动

1. 先启动 Release Main App，再用第二个终端启动同一个 Main App（可直接运行 `src/windows/ReminNote.Windows/bin/Release/net10.0-windows/ReminNote.Windows.exe`）。
2. 观察第二次启动的退出码和桌面窗口；在第一次窗口中切换到 ANIME 或打开一个 Mock 面板后再重复启动，确认状态没有被第二个进程重置。
3. 关闭 Main App，先启动 Release Widget，再启动 Main App；随后再启动一次 Widget。

预期：

- Main App 第二次启动不创建第二个窗口，进程以 0 退出并唤起已有 Main 窗口；已有页面/Mock 状态保持不变；
- Widget 与 Main App 可以同时运行；Widget 只与同身份的 Widget 实例互斥，不阻塞 Main App；
- Widget 第二次启动不创建第二个 Widget 窗口，并以 0 退出后唤起已有 Widget；
- 关闭主窗口后 `scripts/run.ps1 -Configuration Release` 的前台 `dotnet run` 和 PowerShell 命令都返回提示符、退出码为 0；`ReminNote.Windows.exe` 与 `dotnet.exe` 不留下该次启动的后台进程，Mutex/NamedPipe 可被下一次启动重新取得；直接 EXE 启动与脚本启动行为一致；

失败：

- 第二次 Main App/Widget 启动出现重复窗口、悬挂不退出、退出码非 0 或没有唤起已有窗口；窗口关闭后 exe/dotnet 仍驻留、命令行不返回或下一次启动仍被锁住；
- 先启动 Widget 导致 Main App 无法启动，或两个应用共享同一个互斥锁；

## Known Limitations

- `LOCKED` / `TEMP_INTERACTIVE` / `UNLOCKED` / `ALERT` 是可视化 Mock，当前不实现真正的点击穿透、输入拦截或系统级锁定；
- Quick Add、Task、Anime、Reminder 操作都是内存反馈，关闭进程后丢失；
- 面板不会因全局点击自动关闭，避免误关闭 Quick Add 输入框；关闭通过对应面板的 `×`、底部 toggle 或明确操作按钮完成；
- Widget 已作为独立项目登记到 Solution，但仍未由 Agent 托管；Main App / Widget 的启动互斥仅用于本地 UI 生命周期，不代表 Agent IPC 或生产 Single Writer；
- 当前没有像素快照测试，视觉层级仍需要 Windows 桌面人工验收。
