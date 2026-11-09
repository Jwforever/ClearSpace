# 清屿 ClearSpace

[![Release](https://img.shields.io/github/v/release/Jwforever/ClearSpace?display_name=tag)](https://github.com/Jwforever/ClearSpace/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/Jwforever/ClearSpace/build.yml?branch=main)](https://github.com/Jwforever/ClearSpace/actions/workflows/build.yml)

![清屿 ClearSpace](docs/clearspace-banner.png)

一个 Windows 10 / 11 软件管理与空间整理工具。中文界面，本地运行，无需账号。

**[下载最新版 Windows 便携包](https://github.com/Jwforever/ClearSpace/releases/latest)** · [查看源码](https://github.com/Jwforever/ClearSpace)

## 使用

从 Releases 下载 `ClearSpace-0.2.0-win-x64.zip`，解压后双击 `ClearSpace.exe`。适用于 Windows 10 / 11 x64，自带 .NET 运行时，无需安装。

在“软件管理”中搜索、排序和查看安装记录，选中软件后可依次启动其自带的卸载程序。在“空间清理”中先扫描，再核对具体文件，最后确认将选中文件移入系统回收站。

## 功能

- 读取 HKLM / HKCU 中 32 位和 64 位安装记录，支持搜索、排序和批量卸载队列。
- 每个卸载器结束后自动刷新；卸载期间每 2 秒检查安装记录，队列结束后继续检查 3 分钟，之后返回窗口时也会再次检查待确认的软件。刷新保留搜索、排序和其余软件的勾选状态。
- 显示“上次使用”，可按最近优先或最久优先排序；没有记录的应用在两种排序中都排在末尾。
- 显示系统盘可用空间与软件提供的安装大小。
- 扫描超过 7 天的用户临时文件、崩溃转储，以及 Chrome / Edge 文件缓存。
- 清理前预览文件名、完整路径、大小、最后修改时间和来源分类，可逐项取消选择。
- 清理时再次检查允许的根目录、文件属性、大小与修改时间，只移入系统回收站。
- 浏览器运行时跳过其缓存；跳过链接、系统文件、只读文件和独占使用中的文件。
- 在 `%LOCALAPPDATA%\ClearSpace\history.json` 保存最近 500 条操作记录。每条记录最多保存 100 个文件路径与 100 条跳过原因。
- 应用本身不联网，不收集遥测，不上传软件列表或文件路径。

## 界面

下图使用虚构应用与文件，不包含个人软件列表。

![软件管理，演示数据](docs/apps.png)

![空间清理，演示数据](docs/cleanup.png)

## 0.2.0 更新

- 卸载后逐个自动刷新，并处理卸载器启动子进程、延迟移除安装记录的情况。
- 新增上次使用时间列，以及最近优先、最久优先两种排序。
- 从当前用户的 Windows UserAssist 运行记录读取最近启动时间，解析本地快捷方式目标，以完整程序路径及明确的安装目录关联应用。安装器、卸载器及无法区分的软件共用目录不用于推断使用时间。
- 没有记录时显示“无记录”，不使用安装日期、文件修改时间或文件访问时间代替。
- 增加延迟卸载刷新、排序、刷新状态保留和使用记录解析的检查。

## 0.1.1 更新

- 增加原创叶片图标：SVG 源文件、多尺寸 Windows ICO、EXE、窗口标题栏与应用内标志。
- 修复磁盘使用率进度条错误使用双向绑定造成的启动报错。
- 增加窗口启动、图标加载、四个页面和搜索筛选的自动检查。
- 增加 GitHub Actions 构建检查与 Windows 发布流程。

## 功能边界

安装列表不包含 Microsoft Store 应用或未登记的便携软件；软件自报的占用大小可能缺失或不完整。卸载需要完成原软件的向导，列表根据 Windows 安装记录自动更新，只有记录实际移除后才移除对应行。停止队列只会停止等待或后续任务，已经启动的卸载程序由其自身控制。

“上次使用”是 Windows 为当前用户保留的最近启动记录，属于尽力获取的数据，不是完整的活动审计。关闭 Windows 应用启动跟踪、历史记录被清除、应用未由资源管理器/开始菜单启动、快捷方式已经删除，或安装记录不足以关联程序时，可能显示“无记录”。这不表示从未使用；应用不会开启系统跟踪或要求管理员权限，也不将读取的运行历史写入文件。

当前版本不清理注册表，不修改服务或启动项，不自动删除卸载残留，也不提升权限清理系统目录。清理规则只覆盖指定缓存与临时目录，不扫描下载、文档等个人目录。

移入回收站后可通过系统回收站恢复，清空回收站后才实际释放空间。清理失败或跳过的文件会记录原因；任务中断后应重新扫描核对结果。

## 开发和验证

需要 Windows 10 / 11 与 .NET 8 SDK。

```powershell
# 构建桌面应用
dotnet build src/ClearSpace.App/ClearSpace.App.csproj -c Release

# 运行隔离检查、发布便携包、执行界面检查
.\check.ps1

# 只发布
.\publish.ps1
```

发布结果位于 `dist/`，包括自带运行时的单文件 EXE、分发 ZIP 和 SHA256 校验文件。

核心检查使用唯一的 `%TEMP%\ClearSpace.Checks.<random>` 目录。回收站集成检查仅操作在该目录中新建的测试文件。界面检查使用虚构数据，不执行卸载或清理，验证 5 种排序、无记录置后、定时刷新识别延迟移除以及搜索/勾选/排序状态保留，结果输出到 `dist/ui-smoke/`。

图标源文件位于 `assets/clearspace-mark.svg`；`tools/generate_brand.py` 使用 Pillow 生成配套 PNG、ICO 与横幅。

## 项目说明

维护者：[Jwforever](https://github.com/Jwforever)。应用使用 .NET / WPF 与 Windows 系统功能，没有复制 BCUninstaller 或 BleachBit 的源码。导航字形使用 Windows 自带的 Segoe MDL2 Assets，应用标志为项目原创。

仓库目前未指定开源许可证。
