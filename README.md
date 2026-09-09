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
| 17 | `Services/DshInstanceRunner.cs` 等 | **dsh 0.1.2-rc.1 浏览器 token 适配**：新版 web 应用要求 `?token=` 认证（裸地址 401），启动输出行 `dsh web: http://127.0.0.1:<port>/?token=…` 仅打印在进程 stdout。启动后从进程输出解析带 token 地址（事件捕获+缓冲扫描双通道，健康检查后最多等 5s），存入 `Instance.AuthenticatedWebUrl`（与 `WebUrl` 共存：后者保持裸地址语义，用于展示/校验/attach）；Chat 窗口与 Web 模式重开浏览器均优先使用带 token 地址（WebView2 首次导航即种 cookie，之后免 token）；停止/清空时同步清空；adopt 与 attach 场景无法取得 token 时回退裸地址（用户可自行粘贴带 token URL） |
| 18 | `MainWindow.xaml.cs` | **启动崩溃修复**：`HandleWmGetMinMaxInfo` / `ClampMaximizedWindowPos` 对 `lParam==NULL` 的早期 `WM_GETMINMAXINFO`/`WM_WINDOWPOSCHANGING` 未防护，窗口初始化阶段触发 `NullReferenceException`（crash.log 实测抓到的启动崩溃）；两处均早退。修复后干净启动无新崩溃日志 |
| 19 | `App.xaml` / `MainWindow.xaml(.cs)` | **PCL2 风 P0 打磨**：①配色升级为 PCL 式 10 级蓝渐变体系（`#0B5BCB→#1370F3→#4890F5→#96C0F9→#D5E6FD→#E0EAFD→#EAF2FE`）+ 灰阶/红绿强调色；②标题栏线性渐变（左深右亮的 PCL 式）+ 返回/实例胶囊改半透明白；③顶部导航胶囊化（圆角 10、hover `#55FFFFFF`、pressed `#B3FFFFFF`）；④窗控按钮改 34px 圆形（hover 半透明白、**关闭钮悬停红底**）；⑤启动面板右栏加 PCL 式渐变圆空态插画；⑥**窗口工作区自适应**（≤92% 工作区并居中，DPI 感知——修复 125% DPI 下窗控按钮被裁出屏幕右缘的长期问题）；⑦字体加雅黑回退 |
| 20 | `src/DshLauncher/Watchdog/`（新目录）+ `MainWindow.xaml.cs` + `App.xaml.cs` | **实例守护（进程内版）**：无独立 watchdog 进程——监控循环内建于 Launcher（后台 5s 定时），零额外 exe/管道/Mutex。职责：①实例台账（注册/注销/快照，落盘 `%LocalAppData%\DeepSeek\launcher\watchdog-state.json`）；②周期探测——**识别 dshmarket 自重启换 PID 的幽灵实例**（登记 PID 死 + 端口活 → 反查新 PID → 身份校验（DSH_HOME 命令行或 dsh web 特征）→ 转正）；③**接管重启**：用户决策——非启动器重启的实例不收养，Launcher 追加一次标准重启（杀无主实例连同 powershell/cmd 包装链→终端窗口消失，CreateNoWindow 重拉，完全受管）；④停止判定 15s 宽限（覆盖市场重启窗口）+ 5 分钟重生观察窗；⑤残留清理（桌宠 electron/市场 helper 按 DSH_HOME 命令行 + 端口）；⑥正常退出全量收尾清账；崩溃兜底=下次启动台账恢复 + 孤儿检测提示 |

其余文件与上游逐字节一致。构建 0 警告 0 错误；功能全部实测通过（见 work-log 12-15、17、28）。

### P0 借鉴落地（2026-09-09，基于 work-log 27 三仓对比，提交见 git log）

| # | 文件 | 变更 |
|---|---|---|
| 21 | `Services/ErrorCodes.cs`（新） | 错误码目录（E1xxx 运行环境 / E2xxx 插件 / E3xxx 网络代理 / E4xxx 体验 / E9xxx 内部），用户可见错误与结构化日志共用同一套码（借鉴 Ruler4396，MIT） |
| 22 | `Services/LauncherLog.cs`（新） | 轻量 JSON 行结构化日志 `%LocalAppData%\DeepSeek\launcher\launcher.log`（1MB 轮转），写入失败静默降级 |
| 23 | `Services/WindowStateStore.cs`（新）+ `MainWindow.xaml.cs` | 窗口位置/大小/最大化记忆：关闭写 `window-state.json`，启动多屏可见性校验后恢复（越界回退居中）；已恢复时跳过首次居中重算 |
| 24 | `Services/WebCacheVersionLedger.cs`（新）+ `MainWindow.xaml.cs` | WebView2 缓存版本账本：dsh 版本变化时清一次磁盘缓存（保留 Cookie/登录态）；无基线只写基线、空版本不清不写；仅在无其它 Chat 窗口时执行 |
| 25 | `Services/DiagnoseExportService.cs`（新）+ `App.xaml.cs` + 设置页 | `--diagnose [--out path]` 与设置页按钮：导出脱敏 zip（env/日志/状态/错误码汇总/实例设置），用户目录→%USER%、密钥值打码，**不含 .credentials.yaml/会话** |
| 26 | `Services/ProxySettings.cs`（新）+ `Models/VersionSettingsModels.cs` + `DshInstanceRunner.cs` + 设置页 | Launcher 级代理：`HttpClient.DefaultProxy` 全局生效 + 启动实例注入 `HTTP(S)_PROXY/NO_PROXY`；地址归一化/校验，非法只告警不阻断 |
| 27 | `Services/BalanceService.cs`（新）+ `MainWindow.xaml(.cs)` + 设置页 | DeepSeek 余额卡片（默认关闭，设置页显式启用）：内存读取所选实例 DSH_HOME 的凭据并查询余额，不落盘/不写日志/不进诊断包 |
| 28 | `Services/BrowserGuard.cs`（新）+ `MainWindow.xaml.cs` | 浏览器守卫：仅当 dsh 不支持 `--no-open` 且本次不由 Launcher 开浏览器时，启动后 30s 内结束命令行带该端口的浏览器进程 |
| 29 | `Services/ExtensionService.Diagnostics.cs`（新）+ `ExtensionWindow.xaml(.cs)` | 插件 doctor（核心包混入/代际错配/bundle 缺失，核心 bundle 由 CLI 嵌套树提供不再误报）+ 更新检查（node_modules 真实版本 vs registry latest，npmmirror→npmjs）+ 批量更新（失败不中断其余）+ 市场搜索/筛选/滚动持久化 |
| 30 | `Services/UiStateStore.cs`（新）+ `Services/TrayIconService.cs` + `MainWindow.xaml.cs` | UI 状态持久化（`ui-state.json`）；托盘“运行中的实例”二级菜单（每实例 打开/停止） |

## 行为变化（相对上游）

1. **插件依赖自愈**：导入/扫描已有实例启动前自动 `pnpm install` 恢复插件（修复"健康检查前退出"）
2. **任务栏托盘**：关闭主窗口 → 按设置（默认隐藏到托盘，实例继续）；菜单「退出」停 Managed 实例后退出
3. **启动方式三选项**（版本设置 → 个性化 → 绑定打开方式）：Web 启动（dsh 原生浏览器）/ Desktop 启动（启动器 Chat 窗口，`--no-open` 防双开）/ 自定义；运行中重开按模式分流；主按钮文案运行中→「打开实例」
4. **关闭主窗口时**（设置 / 诊断）：最小化到托盘 / 关闭启动器与实例（两选项）
5. **窗口打磨**：标题栏三按钮统一（最大化 50×50 圆角方形）；最大化占满工作区 + 直角，还原恢复圆角；市场卡片撑满整行
6. 旧设置值自动迁移（Launcher/Desktop→Desktop；Exit→ExitAndStopInstances）
7. **实例守护（进程内）**：启动器同一个进程内跑 5s 监控循环；插件市场（dshmarket）「重启以生效」换掉 dsh 进程后，监控识别幽灵实例 → **Launcher 自动接管重启**（杀掉无主实例与包装链，用无窗口方式重拉，全程受管、无终端窗口）；停止/退出时清理关联残留（桌宠 electron 等）。监控随 Launcher 同生命同进退（无额外 exe、无常驻进程）
8. **窗口记忆**：关闭（含隐藏到托盘）时记录位置/尺寸/最大化，下次启动恢复（多屏越界自动回居中）
9. **诊断包**：`DSH Launcher.exe --diagnose` 或 设置/诊断 → 导出诊断包（脱敏 zip 到下载目录）
10. **代理**：设置/诊断 → 代理（默认关），启用后 Launcher 联网与 dsh 实例同时走代理
11. **插件更新**：扩展页新增「检查更新 / 全部更新 / 依赖自检」；市场搜索、分类、来源、排序、滚动位置跨窗口记住
12. **托盘**：新增「运行中的实例」二级菜单（每个实例可打开/停止）；余额卡片（设置中显式启用后显示）

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

## 实例守护（进程内）说明

```
dist/DSH Launcher.exe   # 单文件：启动器 + 实例守护（同一个进程）
```

- **架构**：监控循环 = Launcher 内 5s 定时任务（后台 Task），无子进程、无管道、无额外 exe
- **探测**：识别市场重启（登记 PID 死 + 端口活 + 身份校验）→ 触发接管重启（杀无主实例含包装链 + CreateNoWindow 重拉）
- **停止判定**：15s 宽限（覆盖 market 重启窗口）+ 5 分钟重生观察窗
- **崩溃兜底**：Launcher 异常退出后的残留实例由下次启动的台账恢复 + 孤儿检测提示
- **日志**：`%LocalAppData%\DeepSeek\launcher\watchdog.log`（1MB 轮转）；台账 `watchdog-state.json`

## 上游同步

- 上游基线仓库：`..\repo-review-2`（121103qwq/DSH-Launcher 完整 git，origin=官方，mirror=ghproxy.net 备用）
- 上游发布新版本时：对照 `..\repo-review-2` diff，把本仓库变更集逐项移植（上面表格即移植清单）
