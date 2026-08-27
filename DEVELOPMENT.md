# ReminNote 开发说明

## 前置条件

- Windows 10 22H2 x64 或 Windows 11 x64
- .NET 10 LTS SDK
- Git
- PowerShell

SDK 版本由 `global.json` 固定到 .NET 10.0.100，并允许同一 LTS 小版本内的最新 Feature Band。当前验证使用 .NET 10.0.400。

## 常用命令

在仓库根目录执行：

```powershell
./scripts/bootstrap.ps1
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release
./scripts/verify-p0-07.ps1
./scripts/run.ps1
./scripts/clean.ps1
```

脚本使用 NuGet lock 文件的 locked mode；依赖变更必须经过审查并更新 lock 文件。脚本会先检查标准 x64 安装路径 `%ProgramFiles%\dotnet\dotnet.exe`，且只有确认该路径提供 .NET 10 SDK 时才会直接使用；否则再按 PATH 中的 dotnet 命令顺序寻找提供 .NET 10 SDK 的可用宿主。若 PATH 中排在前面的 dotnet 没有 SDK，裸 `dotnet` 命令可能失败，但仓库脚本仍会继续解析可用 SDK。

`verify-p0-07.ps1` 检查 `global.json`、解决方案 Widget 登记、所有项目 lock 文件、默认资源键/非空值、Shell/TODAY/ANIME/Widget 的基础 UI Automation 标记和开发数据清理边界。它不能替代真实 UI 操作测试。

## P0-02 依赖

- `CommunityToolkit.Mvvm` 8.4.2：用于 ObservableObject 和 RelayCommand；MIT；Microsoft/.NET Foundation 维护。
- `Microsoft.Extensions.Hosting` 10.0.11：用于 Generic Host、DI 和生命周期组合；MIT；Microsoft 维护。

本 Slice 没有引入 EF Core、Serilog、Noda Time、SQLite 或网络库。

## 数据安全

Codex 和开发运行默认使用 `.devdata/`。不得读取生产数据库、真实 Token 或生产 Widget 配置来完成普通开发测试。`clean.ps1` 默认只清理 `src` 下的 `bin/obj`，只有显式传入 `-PurgeDevData` 才会清空开发数据。

## Git 规则

允许本地编辑和验证；未经用户明确要求，不 commit、push、tag、release、rebase 或 force-push。

## P0-03 Design System

- 设计系统使用 WPF 原生 ResourceDictionary、Style、ControlTemplate 和 DynamicResource。
- 资源按颜色、间距、文字和控件分层，位于 src/windows/ReminNote.Windows/Resources/DesignSystem/。
- 不新增第三方 UI 框架或 NuGet 包；修改视觉令牌时应同步检查 Main App 和未来 Widget 的复用关系。

run.ps1 会在当前启动进程缺少 WINDIR 但存在 SystemRoot 时使用进程级回退，避免 WPF 字体缓存初始化失败；不会修改系统环境变量。

## P0-07 稳定化

- P0 默认语言是简体中文；当前只提供默认资源，不提供运行时语言切换。
- `dotnet test ReminNote.sln` 当前没有测试项目，因此 CI 同时运行 P0-07 仓库验证脚本；不得将该空测试结果写成业务覆盖率。
- CI 输出实际 SDK 版本、SDK 信息和解决方案项目清单，并在构建前执行 P0-07 仓库验证。

## 并行开发

独立 Codex 任务必须使用独立 Git worktree，并遵守 docs/PARALLEL_DEVELOPMENT.md 的文件所有权。功能分支可以本地 commit，但不得自行 push 或合并；公共文件由集成分支统一修改。
