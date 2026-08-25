# dsh-launcher-dev —— DSH Launcher 本地开发仓库

基于 **121103qwq/DSH-Launcher v1.0.7**（commit `846c44c1`，官方 main）的本地开发分支：
上游源码 + 修复与增强变更集。本仓库是之后所有修改的基准（git 管理）。

## 仓库结构

```
dsh-launcher-dev/
├─ src/DshLauncher/        # 完整 C# 源码（WPF + WebView2，.NET 8）
├─ dist/DSH Launcher.exe   # 发布版（自包含单文件 72.5MB，.gitignore 排除不进 git）
├─ README.md               # 本文档
└─ .gitignore              # bin/obj/dist 产物/WebView2 缓存 等
```

## 变更集（相对上游 v1.0.7）

| # | 文件 | 变更 |
|---|---|---|
| 1 | `Services/ExtensionService.ProfileRepair.cs` | **新增**：方案 A 插件依赖自愈（`EnsureProfileDependenciesAsync`：bundle/依赖不可解析 → profile 目录 `pnpm install` → 复检）+ `FindMissingBundlePackages` + `BuildPnpmStartInfo`（Windows .cmd 经 cmd.exe 包装） |
| 2 | `Services/ExtensionService.cs` | `class` → `partial class`（配合新文件） |
| 3 | `Services/DshInstanceRunner.cs` | 注入 `ExtensionService?`；`StartAsync` 导入实例自愈调用（失败即中止并给可操作错误）；`openBrowser` 参数 + `SupportsNoOpen` 版本守卫（≥0.1.0-rc.8 才传 `--no-open`，0.1.1-rc.2 正确支持） |
| 4 | `MainWindow.xaml.cs` | 共享 ExtensionService 接线；托盘（关窗驻留/双击恢复/菜单退出）；启动分流（Web→浏览器 / Desktop→Chat / 运行中重开同样分流）；按钮文案（运行中→「打开实例」）；关闭行为两选项（最小化到托盘/关闭启动器与实例）；最大化 `WM_GETMINMAXINFO`（占满工作区）+ `UpdateMaximizeVisuals`（直角化） |
| 5 | `MainWindow.xaml` | 标题栏：DeepSeek 图标 Image（后恢复 DSHLauncher）、三个窗口控制按钮统一圆角方形（最大化 50×50）、根元素命名（AppRootBorder/TitleBarBorder） |
| 6 | `Services/TrayIconService.cs` | **新增**：WinForms NotifyIcon（零新依赖） |
| 7 | `Models/VersionSettingsModels.cs` | `VersionOpenMode` 重定义 `{Web, Desktop, Custom}` + 旧值转换器；`CloseBehavior` 两选项 + `CloseBehaviorConverter`（旧 Exit → ExitAndStopInstances） |
| 8 | `VersionSettingsWindow.xaml(.cs)` | 启动方式三选项 + 关闭行为相关文案；移除 DSH Desktop 校验 |
| 9 | `Services/VersionPackageService.cs` | 旧 `VersionOpenMode.Launcher` → `Desktop` |
| 10 | `ExtensionWindow.xaml` | 市场卡片强制撑满整行（`HorizontalContentAlignment=Stretch`，插件市场 + Skill 市场） |
| 11 | `ChatWindow.xaml` | Chat（Desktop 启动）窗口图标 → `DeepSeekOpenPlatform.ico`（用户提供的"DeepSeek 开放平台.ico"） |
| 12 | `DshLauncher.csproj` | 图标资源声明（DeepSeekOpenPlatform.ico 入 `<Resource>`）；ApplicationIcon 维持 `DSHLauncher.ico`（启动器原样） |
| 13 | `Services/MarketplaceService.cs` | **插件市场修复**：① 明确 DeepSeek 官方（`@deepseek-ai/*` 包 / `deepseek-ai` 仓库）条目标记为 DSh 官方来源且保留原来源（“DSh 官方”筛选不再恒空；“精选”/社区目录筛选仍可见），合并以官方为主来源；② `NormalizeCategory` 未命中关键词时归一为「未分类」，不再出现无法筛选的孤儿类别 |
| 14 | `ExtensionWindow.xaml` | 插件市场分类新增「未分类」Tab（与 13 的类别封口配套） |
| 15 | `ExtensionWindow.xaml.cs` | 运行中 dsh-market 热加载「更新」增加与安装对称的前检：插件不在 dsh-market 目录时轻提示“停止实例后普通更新”，不再触发回档+诊断报告重流程 |
| 16 | `ExtensionWindow.xaml(.cs)` | 页面高度固定为视口可用高度（`RootLayout.Height`）：插件数量多时滚动只发生在内层列表，不再出现内外两级滚动条；**同时移除** 4dd3644 加入的“插件市场自定义目录”配置口（用户反馈目录格式不兼容暂缓；对应代码仅在历史提交中） |

其余文件与上游逐字节一致。构建 0 警告 0 错误；功能全部实测通过（见 work-log 12-15、17）。

## 行为变化（相对上游）

1. **插件依赖自愈**：导入/扫描已有实例启动前自动 `pnpm install` 恢复插件（修复"健康检查前退出"）
2. **任务栏托盘**：关闭主窗口 → 按设置（默认隐藏到托盘，实例继续）；菜单「退出」停 Managed 实例后退出
3. **启动方式三选项**（版本设置 → 个性化 → 绑定打开方式）：Web 启动（dsh 原生浏览器）/ Desktop 启动（启动器 Chat 窗口，`--no-open` 防双开）/ 自定义；运行中重开按模式分流；主按钮文案运行中→「打开实例」
4. **关闭主窗口时**（设置 / 诊断）：最小化到托盘 / 关闭启动器与实例（两选项）
5. **窗口打磨**：标题栏三按钮统一（最大化 50×50 圆角方形）；最大化占满工作区 + 直角，还原恢复圆角；市场卡片撑满整行
6. 旧设置值自动迁移（Launcher/Desktop→Desktop；Exit→ExitAndStopInstances）

## 构建与发布（SOP）

```powershell
# 需 .NET 8 SDK（本机：..\dotnet-sdk）
$env:DOTNET_ROOT = 'D:\Program Files (x86)\dsh_from_github\dotnet-sdk'
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
cd src\DshLauncher

# 开发构建
dotnet build -c Release

# 发布（单文件，输出到仓库 dist\）
# ⚠️ 前置：先停止运行中的 DSH Launcher（dist exe 被锁定 → MSB4018）
dotnet publish DshLauncher.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -o ..\..\dist
```

**重要教训（踩过的坑）**：
- csproj 资源清单（`<Resource Include>`）变更后，必须 **`rm -rf bin/obj` 全量重建**——增量构建的 BAML 资源清单不刷新，会致主窗口 XAML 解析崩溃（无窗口、仅 crash.log）
- publish 前先停 Launcher；移动/删除 SDK 前先 `dotnet build-server shutdown`（Roslyn 目录占用）
- 开发期 `dotnet "DSH Launcher.dll"` 会带 conhost 黑窗口且关黑窗口=杀进程——正式使用一律 `dist\DSH Launcher.exe`

## 上游同步

- 上游基线仓库：`..\repo-review-2`（121103qwq/DSH-Launcher 完整 git，origin=官方，mirror=ghproxy.net 备用）
- 上游发布新版本时：对照 `..\repo-review-2` diff，把本仓库变更集逐项移植（上面表格即移植清单）
