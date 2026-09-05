# Third-party notices

ReminNote 的源代码和发布包按仓库根目录 `LICENSE` 的 GPL-3.0-or-later 条款提供。本清单只记录随当前 Alpha 构建分发的第三方组件及其上游许可证；它不构成法律意见。

| 组件 | 版本 | 用途 | 许可证/来源 |
| --- | --- | --- | --- |
| .NET Runtime / SDK | 10.x（构建机以 `global.json` 与 SDK 输出为准） | 运行时与构建 | MIT，Microsoft；<https://github.com/dotnet/runtime> |
| .NET Generic Host | 10.0.11 | 进程生命周期与依赖注入 | MIT，Microsoft；<https://github.com/dotnet/runtime> |
| Entity Framework Core / SQLite provider | 10.0.11 | 本地 SQLite 持久化与迁移 | MIT，Microsoft；<https://github.com/dotnet/efcore> |
| Noda Time | 3.3.3 | Core 时间和时区语义 | Apache-2.0；<https://github.com/nodatime/nodatime> |
| CommunityToolkit.Mvvm | 8.4.2 | WPF MVVM 基础类型 | MIT；<https://github.com/CommunityToolkit/dotnet> |
| xUnit.net v3 | 4.0.0 | 测试执行 | Apache-2.0；<https://github.com/xunit/xunit> |
| Microsoft Testing Platform / Test SDK | 18.9.0 | 测试宿主兼容 | MIT；<https://github.com/microsoft/testfx> |

实际依赖版本以 `Directory.Packages.props`、各项目 lock 文件和构建产物为准。发布前如有依赖更新，必须同步复核本表及其上游 NOTICE/COPYING 文件；本 Alpha 包不声称完成独立法律许可证审计。
