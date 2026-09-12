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
| 31 | `Services/PnpmFailureClassifier.cs`（新）+ `Services/PluginCommandFailedException.cs`（新）+ `Services/ExtensionService.cs` | pnpm 失败分类（20 种模式→可操作中文提示）+ 瞬时/GitHub 网络失败自动重试一次 + 三段式错误提示（原因/重试/残留清理） |
| 32 | `Services/PluginProfileResidue.cs`（新）+ `Services/GitMirrorEnvironment.cs`（新） | 失败安装残留回滚（只清本次新增的 package.json 依赖/bundles、node_modules、cordis.patch.yml）；GitHub 直装失败依次用 `GIT_CONFIG_*` 进程级镜像重写（gh-proxy/gitclone，不改用户 git 配置） |
| 33 | `Services/PortableNodeService.cs`（新）+ `NodeRuntimeDetector.cs` + `MainWindow.xaml.cs` | 便携版 Node（免管理员）：npmmirror/官方源下载 zip → 校验 node.exe 可运行 → 原子替换到 Launcher 数据目录 `node\`，检测时优先于系统 Node；低于社区红线 22.19 自动抬版；失败时再询问是否改用官方 MSI |
| 34 | `MarketplaceService.VerifyManualInstallAsync`（新）+ `ExtensionWindow.xaml.cs` | 手动安装「装前校验」：npm 包查 registry 清单（npmmirror→npmjs）、GitHub 查 raw package.json、本地路径直接读，要求 `dsh.bundle.patch` + 可加载入口（main/module/exports/dsh.client）；普通 npm 包默认拦截（可显式确认后继续）；无法联网/无法识别时放行但提示 |
| 35 | `ExtensionWindow.xaml(.cs)` + `App.xaml.cs` | 扩展页左栏实例路径改为“尾部省略 + Tooltip + 复制按钮”（不再换行挤占列表）；`App.OnStartup` 增加仅验证用的 `DSH_LAUNCHER_VERIFY=1` 开关（不执行单实例/代理/激活管道） |
| 36 | `ExtensionWindow.xaml(.cs)` | 路径交互升级为“超链接 + 悬浮卡片”：实例目录/DSH_HOME 与插件详情来源均显示尾部、点击即复制、悬停出独立浮层卡片（含完整路径/类型/状态/描述）；去掉单独的复制按钮；按钮区与选择面板重新排版（热加载开关独立一行） |
| 37 | `ExtensionWindow.xaml(.cs)` | 实例头部压缩为一行：实例名 + 版本·类型紧跟（间距 8px、**下对齐**，名字超长省略，最大 190px），去掉原来版本/installed 各占一行的空白 |
| 38 | `MainWindow.xaml(.cs)` | 启动页「实例状态」胶囊清晰度：小字 11→12px 加粗、加 1px 同色描边、背景不透明度 38→52（默认 30→44）、`SnapsToDevicePixels`/`UseLayoutRounding`；卡片阴影改为**独立分层**（不再把卡片内文字一起栅格化） |
| 39 | 全部窗口 XAML + `App.xaml` + `docs/UI-DESIGN.md`（新） | 全量 UI 审查与规范化：4 处阴影挂内容→独立分层；语义色/圆角/间距令牌化（8 种硬编码语义色清零）；补齐 CheckBox/ProgressBar 自定义样式；市场列表去 `MaxHeight`、备份表列宽收敛、主窗口间距收敛；新增长期设计规范文档 |
| 40 | `MainWindow.xaml.cs`（`CreateSettingsPage`） | 「设置 / 诊断」改为**左侧分类栏 + 右侧内容**（运行环境 / 常规 / 网络与账户 / 诊断与日志），与版本设置页同一套 `NavButton` 导航外观；分类切换保持滚动位置在右侧面板 |
| 41 | `MainWindow.xaml(.cs)` + `VersionControlWindow.xaml(.cs)` + `NewVersionWindow.xaml.cs` | 设置页分类栏改为**圆角卡片**；「新建干净版本」**先弹窗再异步补全官方版本列表**（原来要等 npmjs 响应数秒，像按钮坏了）+ 忙碌态禁用/重入保护 |
| 42 | `VersionControlWindow.xaml.cs` + `MainWindow.xaml` + `VersionPackageService.cs` | 修复创建版本失败（后台线程读对话框 WPF 控件 → 先在 UI 线程取值）；窗口尺寸语义纠正为“**最小尺寸 = 启动尺寸**”（1180×720）；删除版本后清理 `instances/<id>` 空壳目录 |
| 43 | `MainWindow.xaml.cs` | 修复非最大化下右侧/底部内容被裁切：`Window_OnLoaded` 对 `SystemParameters.WorkArea`（本就是 DIP）**二次除以 DpiScale** → Width 小于 MinWidth，原生窗口偏小、布局偏大；删除该冗余块（`WindowSizeHelper.FitInitialSize` 已正确处理），并新增启动尺寸诊断日志 |
| 44 | `Services/DshInstallMoveService.cs`（新）+ `MainWindow.xaml.cs` | 「移动已有安装」：把已安装的 DSh 运行目录（含 `versions/`）整体搬到新位置——同盘原子重命名、跨盘复制+校验+删源；同步重写实例的 RootPath/Exe/LaunchSpec 与 `DshInstallDirectory`；目标非空/实例运行中/嵌套目录均拒绝，失败尽力回滚（E1008） |

| 45 | `Services/FileSystemCleanup.cs`（新）+ 11 处删除点 + `DshInstallMoveService` + `VersionSnapshotService` | 修复“删除版本 Access denied”：dsh 的 attachments 对象存储带 ReadOnly，递归删除前先清属性（11 处统一收口）；「移动已有安装」改为以**当前选中实例的运行时**为源（回退配置目录）；新增快照单文件恢复 `TryExtractSnapshotFile` |

| 46 | `Services/FileSystemCleanup.cs` + `VersionControlWindow.xaml.cs` | 删除版本确认框预扫描并提示“含 N 个只读文件，删除时会自动清除只读属性”（`CountReadOnlyFiles`） |
| 47 | `MainWindow.xaml.cs` | 「移动已有安装」新增**「要移动的运行时」下拉框**（配置的安装位置 + 各实例运行时，去重），不再依赖启动页的选中实例；移动成功后自动重建列表 |
| 48 | `MainWindow.xaml.cs` | 「要移动的运行时」改为**圆角卡片 + 复选框行**（每行显示名称与完整当前位置，单选）；「重新检测 / 扫描自定义目录」上移到卡片之上（按反馈去掉了卡片内重复的扫描按钮） |
| 49 | `App.xaml` + `MainWindow.xaml(.cs)` + `Services/PathDisplay.cs`（新）+ `ExtensionWindow.xaml(.cs)` | 路径链接样式提升为全局；启动页「实例状态」卡片的 目录/DSH_HOME 改为链接（点击复制、悬停悬浮卡片、未选实例隐藏）；`Tail` 尾部显示逻辑收口到 `PathDisplay` |
| 50 | `MainWindow.xaml.cs` + `Services/DshInstallMoveService.cs` | 运行时复选框改为**独立多选**（不再互相取消）；新增 `MoveManyAsync`：勾选多个时每个运行时分别搬入目标目录下的同名子文件夹，安装位置指向目标根 |
| 51 | `MainWindow.xaml.cs` | 「要移动的运行时」卡片**只列实例**（每行：实例名 + 其运行时路径）；配置的安装位置无实例关联时不再显示（无实例时卡片给提示） |
| 52 | `MainWindow.xaml.cs` | 卡片**列出全部已登记实例**（不再限于 Launcher 数据根；仅排除系统 npm/nodejs 目录）；`<root>\versions\<ver>` 正确归一到 `<root>`；共用同一运行时的多个实例合并为一行（标签用、分隔） |
| 53 | `Services/LauncherPaths.cs` + `Services/VersionSettingsService.cs` + `Services/ErrorCodes.cs` | **默认安装位置改为 `<exe 同目录>\run_time`**（便携优先）；exe 同目录不可写时自动回退旧默认 `<数据根>\runtime\dsh` 并记 E1009；设置页文案同步 |
| 54 | `DshLauncher.csproj` + `Services/WebView2DataFolder.cs`（新）+ `ChatWindow.xaml.cs` + `Services/WebCacheVersionLedger.cs` + `Services/LauncherPaths.cs` | **独立运行/便携化**：① 自包含单文件发布参数写进 csproj（裸 publish 即得 0 dll 单文件）；② WebView2 数据目录 exe 旁不可写 → 回退 `%LocalAppData%`（E1010，缓存账本同步双布局）；③ 便携数据根：exe 旁 `launcher-data` 或 `DSH_LAUNCHER_DATA_ROOT`（不可写回退 E1011）；诊断包新增 `webview2_data=` |
| 55 | `Services/DshCredentialStoreNormalizer.cs`（新）+ `Services/DshHomeImportService.cs` + `Services/ModelProviderSyncService.cs` + `Services/VersionSnapshotService.cs` + `MainWindow.xaml.cs` | **上游对照后的三个同源缺陷修复**：① 旧格式凭据（version/records/refs）保守归一化（E1012），接入导入/覆盖/合并/Provider 同步/快照恢复五处；② 快照补 `profiles/web/pnpm-workspace.yaml`（pnpm 构建许可）；③ 列表 `SelectedItem` 瞬时清空防护（实例仍在列表中则忽略并回写绑定） |
| 56 | `Models/DshEnvironmentVariables.cs`（新）+ `Services/VersionSettingsService.cs` + `Services/DshRuntimeCommandFactory.cs` + `Services/DshInstanceRunner.cs` + `VersionSettingsWindow.xaml(.cs)` + `App.xaml.cs` + `ExtensionWindow.xaml` + `ConversationWindow.xaml` | **短件三项**：① 实例级环境变量（保留项 DSH_HOME/DSH_AGENTS_HOME/PATH；敏感值 DPAPI 加密落盘、读取还原；启动时注入；版本设置页可增删保存；E1013）；② 启动阶段异常不再无窗口驻留（无主窗口则提示 + 退出）；③ UI 尾巴：市场/技能描述限高 + 省略号 + ToolTip、对话页筛选行自适应、ClearType 范围澄清 |
| 57 | `Watchdog/InstanceResourceSampler.cs`（新）+ `Watchdog/WatchdogCore.cs` + `Watchdog/WatchdogRuntime.cs` + `MainWindow.xaml(.cs)` + `Services/ConversationService.cs` + `Models/EcosystemModels.cs` + `ConversationWindow.xaml(.cs)` | **会话全文检索 + 实例资源监控**：① 资源采样挂在守护进程既有探测轮（CPU/内存/运行时长/进程数，无独立定时器），启动页卡片展示；② 修孤儿检测误报（根 PID → 认整个进程树）；③ 对话页全文检索（JSONL+zstd、32MiB/文件上限、损坏跳过、命中排序、片段高亮位置、跨实例双击打开） |
| 58 | `VersionSettingsWindow.xaml(.cs)` + `Controls/MetricChart.cs`（新）+ `Services/InstanceLogBuffer.cs`（新）+ `Watchdog/InstanceResourceSampler.cs` + `Watchdog/WatchdogCore.cs` + `Watchdog/WatchdogRuntime.cs` + `Watchdog/ProcessQuery.cs` + `App.xaml` + `MainWindow.xaml.cs` + `Models/InstanceHealthModels.cs`（新） | **实例设置「运行状况」大栏**：运行概况 + CPU/内存折线图（20 分钟环形历史）+ 进程树 + 运行日志窗（dsh 输出/生命周期事件，2000 行环形不落盘）+ 清理残留进程（keepPid 保留本体 + 二次确认）；顺带修两个真实缺陷：`FindProcessesForDshHome` 因 `Name=="cmd"` 永不匹配导致按 DSH_HOME 清理静默失效、运行状况页头标题漏分支 |
| 59 | `Services/SafeProfileService.cs`（新）+ `Services/StartupHealthEvidence.cs`（新）+ `Services/DshInstanceRunner.cs` + `Models/DshInstanceRunResult.cs` + `MainWindow.xaml.cs` + `ChatWindow.xaml.cs` + `Services/ErrorCodes.cs` | **#11 安全模式 + 四层启动健康证据（第一阶段）**：① `.dsh-safe` 隔离 profile（Tier1 剥离第三方保留核心 / Tier2 最小、用户文件只读、SHA-256 零污染证据、正常启动自动清理）；② 启动健康四层证据：进程（exitCode）、日志（新增行签名）、HTTP（探针异常只记不判）、页面（WebView2 失败上报）；③ 启动失败且存在第三方插件时**会话内只问一次**、Tier1→Tier2 自动降级；提示区分安全模式/普通启动（E1014/E1015） |
| 60 | `ChatWindow.xaml.cs` + `Services/StartupEvidenceStore.cs`（新）+ `Services/HttpHealthMonitor.cs`（新）+ `Watchdog/WatchdogRuntime.cs` + `MainWindow.xaml(.cs)` + `VersionSettingsWindow.xaml(.cs)` + `Models/InstanceHealthModels.cs` | **#11 安全模式第二阶段**：① 页面层升级为判死输入（`ProbePageAsync` DOM 探针 + 失败时安全模式重启提议）；② 「运行状况」新增“启动证据”区块（进程/日志/HTTP/页面，含接管记录）；③ 就绪后 HTTP 连续 3 次失败报降级（探针异常按 miss 计）；④ 实例卡片“安全模式”徽标 |
| 61 | `Services/InstanceIdleTracker.cs`（新）+ `Services/DshInstanceRunner.cs` + `Watchdog/ProcessQuery.cs` + `MainWindow.xaml.cs` + `VersionSettingsWindow.xaml(.cs)` + `Models/VersionSettingsModels.cs` | **空闲自动停止（可开关）**：每实例开关 + 5–240 分钟阈值；后台任务不算空闲（进程树外部连接/CPU≥1%/会话写入/dsh 输出/Launcher 忙）；停止复用既有链路并写证据；修两个真缺陷（活动信号漏接 CPU 与写入；运行状况页每秒刷新覆盖用户正在编辑的控件） |
| 62 | `Services/StartupEvidenceStore.cs` + `MainWindow.xaml.cs` + `VersionSettingsWindow.xaml(.cs)` + `Services/DiagnoseExportService.cs` | **启动证据落盘**：内存环形 + `<DSH_HOME>/.dsh-launcher/startup-evidence.jsonl`（最近 200 条、每 25 次追加裁剪、损坏行容忍）；运行状况页「清空启动证据」；诊断包附带证据文件 |
| 63 | `Models/CrashRecoveryModels.cs`（新）+ `Services/CrashRecoveryService.cs`（新）+ `Services/DshInstanceRunner.cs` + `MainWindow.xaml(.cs)` + `VersionSettingsWindow.xaml(.cs)` + `Models/VersionSettingsModels.cs` + `Services/ErrorCodes.cs` | **崩溃恢复策略**：每实例「仅通知 / 自动重启（退避 5s→15s→1m→5m，默认上限 5）/ 直接冷却关闭 / 重启后冷却」；主动停止不误判（手动/空闲/安全模式/更新/退出）；崩溃现场落盘（退出码/运行时长/日志尾部/证据/资源，最近 10 条）；冷却跨 Launcher 重启保留 + 「崩溃冷却」徽标 + 「清冷却并重启」；稳定运行 10 分钟重置计数；E1016 |
| 64 | `Services/PluginBisectService.cs`（新）+ `Services/SafeProfileService.cs` + `Services/DshInstanceRunner.cs` + `MainWindow.xaml.cs` + `VersionSettingsWindow.xaml(.cs)` + `Services/ErrorCodes.cs` | **逐插件定位**：隔离 profile（复制清单 + 改写 bundles + node_modules 目录联接）二分禁用，2–4 轮锁定肇事插件；结果一键「禁用并正常启动」（自动存回滚点）；清理不跟随联接；E1017 |
| 65 | `Models/CrashCauseModels.cs`（新）+ `Services/CrashCauseClassifier.cs`（新）+ `Models/CrashRecoveryModels.cs` + `Services/CrashRecoveryService.cs` + `MainWindow.xaml.cs` + `VersionSettingsWindow.xaml(.cs)` | **崩溃原因归类**：有序规则表（Node 不可用/端口占用/模块解析/插件异常/内存/权限/磁盘/正常退出）+ 端口与 Node 探针；置信度高/中/低（低置信只标未知）；原因落盘（记录 + 状态 + 证据 + 建议）；端口冲突自动先清残留再重启 |
| 66 | `Models/PluginMatrixModels.cs`（新）+ `Services/PluginMatrixService.cs`（新）+ `PluginMatrixWindow.xaml(.cs)`（新）+ `ExtensionWindow.xaml(.cs)` + `MainWindow.xaml.cs` | **插件 × 实例矩阵**：行=至少在一个实例启用的插件（排除核心/运行时依赖假行），列=实例，单元格三态（已启用/已装未启用/未安装）；动态列 + 复制为 TSV；只读视图 |
| 67 | `ExtensionWindow.xaml` + `docs/UI-DESIGN.md` | **扩展页工具条按钮紧凑化**：当前实例卡内 9 个按钮改 `CompactToolbarButton`（12px / Padding 9,4 / Margin 0,0,6,6），主操作同理；实测 44→33 设备像素、3 行→2 行 |
| 68 | `Services/DshInstanceRunner.cs` + `MainWindow.xaml.cs` | **外部连接（Attached）失效收敛**：修 `_attached` 只加不删（没开实例却提示“请先停止实例”）；按 PID/端口活性校验并自动摘除；外部实例也登记 watchdog；5s 周期收敛 + 停止守卫区分死活 |
| 69 | `Services/StartupHealthEvidence.cs` + `Services/DshInstanceRunner.cs` + `Services/DialogText.cs`（新）+ `MainWindow.xaml.cs` | **启动健康检查日志基线 + 对话框文本收敛**（work-log/47）：① 健康检查只扫本次进程启动后的新增日志行（`logBaselineAt` + `StartupLogClassifier.Since`），修复“旧失败签名行让本会话内后续启动/安全模式/自动重启瞬间假失败”（清空日志才恢复）；② 新增 `DialogText.ForMessageBox`（每行 ≤120 字符硬折 + 总长 ≤1200 + 截断提示），安全模式询问改用 `FormatStartFailure` 摘要 + 证据 + 插件清单 + 指向运行日志（原来原样塞 ~5.3KB 输出，弹窗实测 1584×1182、是/否按钮出屏），「启动详情」弹窗同步收敛 |
| 70 | `Services/PluginCompatibility.cs`（新）+ `Models/DshCoreBundles.cs`（新）+ `Services/MarketplaceService.cs` + `Models/MarketplaceModels.cs` + `ExtensionWindow.xaml.cs` + `Services/DshInstanceRunner.cs` + `Models/ManagerInstance.cs` + `Services/ExtensionService.cs` + `Services/PluginMatrixService.cs` + `Services/SafeProfileService.cs` + `MainWindow.xaml.cs` | **46 号遗留 4 项（work-log/48 验证、49 实施）**：① **装前/更新前兼容性预检**——新增 `PluginCompatibility`（npm 语义化范围匹配含预发布规则 + 实例内实际版本解析），手动安装/市场安装/单条更新/批量更新均检查 `@deepseek-ai/*` peerDependencies，不兼容返回 `Incompatible` 并警告（computer-user 这类“装完就崩”会被拦住；批量更新自动跳过）；② **生命周期日志**——启动/停止写 `launcher.log`（实例名/端口/PID/normal|safe）；③ **核心包口径统一**——新增 `DshCoreBundles`（计数/列表/矩阵/安全模式共用），实例卡片不再把核心算成插件、插件管理/扩展页不再列核心；④ **harness 隔离**——`ExtensionWindow.UiStateStore` 可注入，UI 冒烟用临时目录，不再污染真实 `ui-state.json` |
| 71 | `Services/DshEnvironmentScanner.cs`（新）+ `Services/ScannedHomeImportService.cs`（新）+ `EnvironmentScanWindow.xaml(.cs)`（新）+ `VersionControlWindow.xaml(.cs)` + `MainWindow.xaml.cs` | **#14 本机 DSH 环境扫描导入（work-log/50）**：扫描 `%USERPROFILE%\.dsh*` + `DSH_HOME`，按 `dsh.profile.bundles` 分类 web/tui/other；勾选后一 home 一实例（只收 `profiles/web`，重复源跳过、失败回滚）；「导入实例」菜单新增「扫描本机 DSH 环境」与接上死代码的「从源码目录导入」 |
| 72 | `Services/DshEnvironmentScanner.cs` + `Services/ScannedHomeImportService.cs` + `Services/DshHomeImportService.cs` + `EnvironmentScanWindow.xaml(.cs)` | **清理：移除 WSL 扫描冗余**（work-log/50 第五节）：本机 WSL 服务被禁用（`Wsl/0x80070422`），WSL 分支无法验证且无使用场景，删除发行版枚举/`sh` 清单协议/UNC 转换/WSL 源跳过插件包复制/UI 标签与文案；harness 255 PASS |
| 73 | `Services/SessionFileNames.cs`（新）+ `Services/ConversationService.cs` + `Services/ConversationSyncService.cs` + `EnvironmentScanWindow.xaml` | **dsh 0.1.5-rc 会话格式兼容（work-log/51）**：① 会话头接受任意版本（0..N，含 `isSeeded` 可选）；② 文件名识别 dsh canonical 规则（`session.jsonl[.zstd]` = v0、`session.vN.jsonl[.zstd]`），导入按**头部版本**命名、编码跟随目标实例（不一致时流式转码）；③ 旧 dsh 实例拒绝 vN 导入并明确报错；④ 跨实例同步按会话目录归并、取最高代际、跨编码/不支持版本时跳过；⑤ 修掉变更集 71 扫描页 `Run Text` TwoWay 绑定导致的 57 条 E9001 XamlParseException |
| 114 | `ExtensionWindow.xaml(.cs)` + `VersionSettingsWindow.xaml.cs` | **扩展页去掉「当前 Profile」；命令卡片自动指向能起 TUI 的 profile（work-log/97）**：删掉 `ProfilePanel`（下拉/提示）与三个方法两个字段零残留；活动 profile 切回 `web`（否则主按钮被非 Web 面拒绝且界面已无切换入口）；「终端启动（TUI 插件）」命令改为**优先指向承载 TUI 的终端面 profile**（活动 profile=web 时自动给 `--profile dsh-tui`）；harness 源码契约同步改为“断言扩展页已无 ProfileComboBox/ProfileHintText”；SelfTest 155 / harness 348 / UIA 9 PASS；发布 MD5 `b2e201ab…` |
| 113 | `ExtensionWindow.xaml(.cs)` + `VersionSettingsWindow.xaml(.cs)` | **终端命令卡片搬进实例设置（work-log/96）**：扩展/Agent 页删掉 112 新加的命令卡片（Agent 页本来就靠 `agentOnly ? Collapsed : Visible` 自动屏蔽 Profile 面板，卡片当时漏了门控）；实例设置→个性化新增卡片「终端启动（TUI 插件）」（位置：版本数据位置之后、实例环境变量之前），含只读命令 + 复制按钮，扫到 TUI 插件才显示；Profile 下拉保留（共用 + Agent 页自动隐藏）；SelfTest 155 / harness 348 / UIA 10 PASS；发布 MD5 `f0acc258…` |
| 112 | `Models/VersionSettingsModels.cs` + `Services/{LaunchModePolicy,TerminalLaunchService,VersionSettingsService,VersionPackageService,PresentationSurfaceService}.cs` + `MainWindow.xaml(.cs)` + `VersionSettingsWindow.xaml(.cs)` + `ExtensionWindow.xaml(.cs)` + `tests/…/Program.cs` | **启动方式收敛：只留 Web / Desktop（+隔离），TUI 改为「复制终端启动命令」（work-log/95）**：删「终端启动」「手动绑定（自定义）」两种方式、整张「启动方式」设置卡片、`LaunchModeVisibility`/`TerminalWorkingDirectory`/`TerminalAskWorkspaceEachTime`/`CustomOpenTargetPath` 四个字段与 `VersionOpenTargetService`；**保留**隔离启动（崩溃恢复/逐插件定位在用 `SafeProfileService`）与「打开窗口」；扩展页新增「终端启动（TUI 插件）」卡片：命令 `$env:DSH_HOME='…'; & '<版本>\dsh.cmd' --profile <活动 profile>` + 复制按钮；顺带修掉**设置窗口保存路径漏 `ActiveProfile`**（会把活动 profile 抹成 null→web）；SelfTest 155 / harness 348 / UIA 13 PASS；发布 MD5 `7dfe3a40…` |
| 111 | `VersionSettingsWindow.xaml(.cs)` + `Services/LaunchModePolicy.cs` + `MainWindow.xaml.cs` + `tests/…/Program.cs` | **默认启动方式下拉：与「启动方式显示」联动 + 「未设置」置空（work-log/94）**：下拉按显示开关过滤被关闭的方式（隔离/终端），当前默认被关闭时回退 Web 并在状态文字里说明（**仅在显示/生效层，不改写用户设置**）；补「终端启动」项（按呈现面置灰）与「未设置（按运行时自动）」项（清掉 OpenMode→null，按运行时自动）；`LaunchModePolicy` 新增 `isolatedModeVisible`（隔离被隐藏→Web）；SelfTest 158 / harness 348 / UIA 真机 16 PASS；发布 MD5 `6fdbad11…` |
| 110 | `VersionSettingsWindow.xaml(.cs)` | **实例设置：启动方式内容集中到一张「启动方式」卡片（work-log/93）**：原「更多打开方式」升级为「启动方式」卡片，内含小节「默认启动方式」（原下拉/自定义目标/状态/保存）+「启动方式显示」（原代码追加卡片）+「终端启动的工作区」（原代码追加卡片）；两段代码追加改为追回到 `LaunchModeExtraSections`（标题 14→13、间距 18）；无设置字段变更；SelfTest 157 / harness 348 / UIA 布局 9 PASS（Y 坐标验证同卡片与顺序）；发布 MD5 `8c8b5233…` |
| 109 | `Models/VersionSettingsModels.cs` + `Services/LaunchModePolicy.cs`（新）+ `Services/TerminalLaunchService.cs`（新）+ `MainWindow.xaml(.cs)` + `VersionSettingsWindow.xaml.cs` + `Services/VersionSettingsService.cs` + `Services/VersionPackageService.cs` + `tests/…/Program.cs` | **终端启动＝一种启动方式 + 终端工作区（work-log/92）**：新增 `VersionOpenMode.Terminal`——▼ 菜单「终端启动（终端面）」只切换（与 Web/Desktop/隔离一致），主按钮变「终端启动」并在 Windows Terminal 里跑活动 profile；非终端面/入口隐藏时回退 Web（`LaunchModePolicy`）；实例设置新增「终端启动的工作区」（目录+浏览+清除）与「每次终端启动都先让我选目录」开关，`TerminalLaunchService` 解析 cwd（dsh-tui 用进程 cwd 当工作区，插件无 --cwd 参数）；修 `VersionSettingsService.Clone` 漏字段（新字段一存就被抹）；SelfTest 157 / harness 348 / 真实数据 E2E 13 PASS；发布 MD5 `96d73bf9…` |
| 108 | `Services/PresentationSurfaceService.cs` + `MainWindow.xaml.cs` + `tests/DshLauncher.SelfTest/Program.cs` | **「在终端打开」漏注入 DSH_HOME（work-log/91）**：wt.exe 原用 `UseShellExecute=true` 且不传 `DSH_HOME` → 终端里的 dsh 退到默认 home `~/.dsh`，自定义 profile（dsh-tui）直接报 `profile does not exist`（内置模板 web/acp/headless/sdk 会自建，所以一直未暴露）；新增可测工厂 `CreateWindowsTerminalStartInfo`（注入 DSH_HOME/DSH_AGENTS_HOME/PATH）+ 「profile 不存在」启动前校验；SelfTest 154 / harness 348 / 真实数据 E2E 9 PASS；发布 MD5 `ffea652e…` |
| 107 | `Services/InstanceUiPluginScanner.cs` + `tests/DshLauncher.SelfTest/Program.cs` | **TUI 插件检测修复：旧版 profile 本地 node_modules（work-log/90）**：扫描器改为两处布局都扫（`profiles/<名>/node_modules` 优先 + `profiles/node_modules` 共享，同名去重）——0.1.2 等旧版把插件装在 profile 本地，原实现只扫共享目录导致检测不到（用户报告的“装了 TUI 插件但不显示在终端打开”根因）；SelfTest 夹具覆盖两种布局；SelfTest 153 / harness 348 / 真实数据 UIA 7 PASS；发布 MD5 `525815ea…` |
| 106 | `Services/InstanceUiPluginScanner.cs`（新）+ `Models/VersionSettingsModels.cs` + `Services/VersionSettingsService.cs` + `VersionSettingsWindow.xaml.cs` + `MainWindow.xaml.cs` + `tests/...` | **UI 插件检测 + 「启动方式显示」开关（work-log/89）**：扫描实例 profile 的插件（bundles ∪ dependencies，且必须声明 dsh 插件）识别 TUI/GUI（spec 声明＞keywords＞包名；官方核心包不参与，`dsh-desktop*` 例外）；实例设置「个性化」新增开关子列表（默认全开）控制卡片 ▼ 菜单显示（在终端打开/打开窗口/隔离启动；Desktop/Web 常显）；未启用 TUI 插件在「在终端打开」时提示并可一键启用；4 处保存路径补字段防抹（ReadConfigurationSettings/CopySettings/SavePluginSettings/MainWindow）；修租传递依赖库 keywords 误报；SelfTest 153、harness 348、UIA 7 PASS；发布 MD5 `386278a8…` |
| 105 | `App.xaml` + `MainWindow.xaml.cs` + `Services/DshInstanceRunner.cs` + `Services/ExtensionService.cs` + `Services/MarketplaceService.cs` + `Services/ModelService.cs` + `docs/UI-DESIGN.md` | **死代码清理（work-log/88）**：全仓扫描（0 引用私有方法 / XAML 资源）→ 逐步删除 **16 个零引用方法**（5 个未接线处理函数 + 9 个扫描命中 + 2 个级联）、**11 个未引用设计令牌**，并清掉级联的“安装态残留”（1 个恒 false 字段 + 2 个无消费者属性 + 8 处通知点）；+1/−222 行（代码）；复扫双清零；SelfTest 150 / harness 348 / MainWindow 冒烟 8 PASS；发布 MD5 `4dbe42df…` |
| 104 | `VersionSettingsWindow.xaml(.cs)` | **O4 验收发现的两个缺陷修复（work-log/87）**：① 「检查并复位 profile 残留」按钮**漏了 Click 接线**（work-log/71 的 A3 声称已完成，实际点了没反应）→ 补接线；② 卫生检查/复位的结果文本被运行状况页 1s 周期刷新覆盖（<1s 消失，与 work-log/39 同类）→ 加 20 秒保留期，周期刷新在保留期内不覆盖状态文本；O4 UIA 7 PASS（缓存清理/隔离启动确认框/卫生复位） |
| 103 | `Services/LogCenterService.cs`（新）+ `LogCenterWindow.xaml(.cs)`（新）+ `MainWindow.xaml.cs` + `tests/DshLauncher.SelfTest/Program.cs` | **日志中心（work-log/86）**：设置 →「诊断与日志」→ 内嵌页，只读浏览 `launcher.log`（含 `.old` 轮转）：按天分组（本地时区）+ 级别/关键字/实例过滤 + 尾部长上限（读 5000 / 渲染 500），坏行计数、`FileShare.ReadWrite` 不阻塞写入；SelfTest 150 PASS（+7）、harness 348 PASS、UIA 8 PASS（真实 756 条：分组/过滤/返回） |
| 102 | `MainWindow.xaml(.cs)` | **启动页只读显示当前 profile（work-log/85）**：实例卡形态行下方显示 `profile：<活动 profile>`（复用 `DshProfileService.ResolveActiveName`；切实例/守护探测轮/设置保存三处刷新），tooltip 说明 profile 的作用与切换入口；UIA 5 PASS（快照+还原） |
| 101 | `Services/PresentationSurfaceService.cs` + `MainWindow.xaml(.cs)` + `tests/DshLauncher.SelfTest/Program.cs` | **实例卡片呈现面徽标（work-log/84）**：按当前 profile 的 `dsh.profile.bundles`（读不到按 profile 名兜底）判定呈现面，非 Web 面（终端/无界面/未识别）才显示徽标 + 说明 tooltip；与 ▼ 菜单「在终端打开」同一套判据；SelfTest +1（143 PASS）、harness 348 PASS、UIA 4 PASS（快照+还原） |
| 100 | `Services/DangerousConfigAuditService.cs` + `MainWindow.xaml.cs` + `tests/DshLauncher.SelfTest/Program.cs` | **启动前危险配置提示（work-log/83）**：用户主动启动实例前，当前 profile 命中权限类危险项（沙箱 `danger-full-access` / `DSH_PERMISSION_MODE` / `approval: never`）即非阻断提示一次（每个 Launcher 会话、每个实例+profile；**只提示不改**，完整清单仍在安全体检）；`FindStartupPermissionDangers` 只挑 4 个权限类 Danger id（遥测/HMR/来源不打扰启动）；自动重启/接管重启/隔离启动不弹；SelfTest +1（危险样本命中、安全样本零提示） |
| 99 | `MainWindow.xaml(.cs)` + `Models/VersionSettingsModels.cs` + `VersionSettingsWindow.xaml(.cs)` + `Services/PresentationSurfaceService.cs` + `tests/DshLauncher.SelfTest/Program.cs` | **启动切分按钮 · 菜单行为落地（work-log/82）**：▼ 菜单 Web/Desktop/**隔离启动**改为**只切换不启动**（点击写回 `version-settings.json` 的 `OpenMode`；`VersionOpenMode` 新增 `Isolated`，设置页「更多打开方式」同步第 4 项；主按钮按最近选择执行，隔离启动走安全 profile 链路）；菜单当前方式打勾（`IsCheckable`）、「打开窗口」按 `CanOpenDesktopShell`、「在终端打开」按呈现面置灰（禁用 tooltip）；dsh 自带 desktop surface 判据（官方 scope `@deepseek-ai/dsh-desktop*`）命中则隐藏 Desktop/打开窗口并回落 Web；**修掉真实缺陷**：`VersionOpenModeConverter` 旧值映射（`Launcher`→Desktop）在真实读路径上是死代码（options 里的 `JsonStringEnumConverter` 抢先）→ 属性级 `[JsonConverter]` 修复；SelfTest 136→142、harness 348 PASS、UIA 18 PASS（快照+还原真实设置） |
| 98 | `MainWindow.xaml(.cs)` + `VersionSettingsWindow.xaml` | **实例卡片启动切分按钮 + 「更多打开方式」（work-log/81）**：卡片按钮重排——启动切分按钮（左＝启动横占 + 右＝▼）/ 停止·重启均分 / 重命名·实例设置均分；原独立按钮「隔离启动」「打开窗口」「在终端打开」折叠进 ▼ 菜单；版本设置「绑定打开方式」改名「更多打开方式」（手动绑定 Custom 保留）；本轮只做入口与呈现，菜单写回语义见变更集 99 |
| 97 | `Services/PresentationSurfaceService.cs`（新）+ `MainWindow.xaml(.cs)` + `tests/DshLauncher.SelfTest/Program.cs` | **呈现面识别 + 「在终端打开」（TUI MVP，work-log/80）**：按 profile 名与 `dsh.profile.bundles` 判 Web/终端/无界面/未识别（bundles 优先、名字兜底、未知不硬套）；终端面实例可「在终端打开」（Windows Terminal：`-d <目录> <dsh 入口> --profile <名>`；缺 `wt.exe`/参数显式提示）；不做 PTY/终端仿真；SelfTest 133→136 |
| 96 | `ExtensionWindow.xaml(.cs)` | **市场滚动追加修复（work-log/79 I2）**：初始展开量 150→5000（修「拉到底不刷新」，实测 150/3168）；滚动到底继续显示已抓取快照（不联网）、批大小 50；中间一次「外层滚动」推断经复核回退；滚动诊断日志默认关（`DSH_LAUNCHER_MARKET_SCROLL_DIAG=1` 开启）；市场追加议题按用户决定搁置；harness 348 / SelfTest 133 |
| 95 | `PackVersionMismatchWindow.cs`（新）+ `Services/PackTemplateResolution.cs`（新）+ `VersionControlWindow.xaml.cs` + `tests/DshLauncher.SelfTest/Program.cs` | **整合包版本不一致：先提示、再选择（work-log/78）**：规范 `.dspack` 原先硬拒绝、v1 `.dshpack` 原先不检查（会造出「标 X 版本、实际跑 Y 运行时」的实例）；现统一为提示后让用户选「用模板实例的版本 / 下载所需版本 / 取消」，含候选模板定位（匹配优先、标注）与版本比较（精确匹配、忽略大小写/空白/前导 v、空值不匹配）；SelfTest 130→133 |
| 94 | `docs/ARCHITECTURE_DECISIONS.md` + `docs/BEHAVIOR_MATRIX.md`（均新，借鉴清单 #23 收尾） | **工程治理收尾**：10 条 ADR（测试不碰真实数据 / 凭据只看位置 / 安全反证+双向自校准 / 体检不调 dsh CLI / 导入永远新建实例+回滚 / 契约哨兵防静默失效 / 来源管理语义 / **运行时行为不许用推断代替数据** / 只借鉴不复制 / 发布 SOP）与「行为×验证方式矩阵」（实例、整合包、安全、界面、工程五类，标注 ST/HV/人工/无覆盖） |
| 93 | `src/DshLauncher/Services/AuditProbeTimelineService.cs`（新）+ 安全体检页第三块「行为时间轴」 | **#20 增量 3（work-log/75）**：读取社区探针 `@marcog-h/dsh-audit` 的 `audit/*.jsonl`（两种命名都认）做行为时间轴；**只读白名单字段，`raw` 原文永不读取**，`key` 片段仅在显式开启预览时读取；容错解析（非 JSON 跳行、类型不对不丢行）+ 上限（8 MB/5000 条）；未装探针时空态不引导安装；反证 F/G（raw 零泄漏、纯读取） |
| 92 | `src/DshLauncher/Services/DangerousConfigAuditService.cs`（新）+ 设置页安全体检分区扩展（+7 断言） | **#20 增量 5：危险配置检查（work-log/74）**：按用户勾选范围检查权限档位/`DSH_PERMISSION_MODE`/`approval.policy`/**沙箱范围 workspaceRoot**/遥测模式与导出口/依赖来源/凭据文件保护/HMR；**只直接读文件，不调 `dsh --dump-config`**（调研发现它会写盘）；读不出的 `!!js` 表达式记 Notes 不猜；安全样本零发现 + 危险样本全命中（双向自校准）+ 纯读取反证 |
| 91 | `src/DshLauncher/Services/CredentialAuditService.cs`（新）+ `tests/.../Program.cs`（+8 断言） | **#20 安全审计 · 增量 1（work-log/73）**：Launcher 侧被动凭据体检——按硬边界扫描 DSH_HOME 配置/凭据文件，只报告**命中位置**（文件/行/列/模式名），默认不回显任何值；显式开启才给脱敏预览（前 3 + 后 4）；配套**安全反证**：报告序列化不含凭据值及中段特征、体检纯读取（临时 HOME 零变化）、日志零泄露、node_modules 不扫 |
| 90 | `Services/DshPackFormat.cs` / `DshPackArchive.cs` / `DshPackImportService.cs` / `DshPackFileDownloader.cs` / `DshPackWriter.cs` / `PackExportService.cs`（均新）+ 实例设置导出按钮 + `docs/DSH_CONTRACT_INVENTORY.md` | **#24 整合包规范对齐（work-log/70、72）**：容器识别（`.dspack` v2/v3、旧 `.tgz`）+ manifest **v2–v5** 解析与校验；导入到新建实例（权威重建 `package.json`、`overrides/`、`home/`，失败整体回滚）；`files[]` 下载（默认拒绝、显式同意、sha256+size 双校验、镜像回退）；导出 **manifest v4 + `.dspack` v2**（自家 v1 保留为旧格式）；SelfTest +40 条（含往返）。（#24 收尾随 work-log/77：导入确认面板自绘 `PackImportWindow`/`LegacyPackImportWindow`、v1 导入链路修复（优先用模板实例运行时）与配色/下拉统一，不单独占号。） |
| 89 | `MainWindow.xaml.cs` + `_verify-p0/Program.cs` | **设置页导航顺序调整（work-log/69）**：把「插件与技能来源」从末位挪到**「常规」与「网络与账户」之间**（顺序：运行环境 → 常规 → 插件与技能来源 → 网络与账户 → 诊断与日志）；harness 的导航顺序断言同步为逐项校验 |
| 88 | `Services/MarketSourceSettingsService.cs` + `Services/MarketplaceService.cs` + `Models/VersionSettingsModels.cs` + `MainWindow.xaml.cs` + `tests/DshLauncher.SelfTest/Program.cs` | **来源管理归一：唯一固定来源＝GitHub（work-log/68）**：两个中文源不再作为独立开关，改为**预置在自定义列表里的条目**（`adapter:zh1024` / `adapter:dshfind`，默认停用、可开关可删除、可测试连通）；`MarketplaceService` 改为按列表里启用项驱动适配器；移除 2 个启动器设置字段与 2 个复选框；SelfTest 期望同步（首次使用预置 2 条停用条目） |
| 87 | `Services/DshfindCatalogService.cs`（新）+ `Services/MarketplaceService.cs` + `Models/VersionSettingsModels.cs` + `MainWindow.xaml.cs` + `tests/DshLauncher.SelfTest/Program.cs` | **中文插件源 #16 第二个源：dshfind.com（work-log/67）**：一次拉全量（在线实测 14,043 条，排除 74 条 archived）+ **TTL 30 分钟缓存 + 单飞**（8 MB 载荷并发刷新只发一次）+ 失败回退旧缓存；映射用 `fullName`、`i18nDescriptions` 的 zh 优先、无分类取首个 tag、官方/精选/★/标签进来源名、缺 install 时以 `github:owner/repo` 兜底；默认关闭、独立开关；SelfTest +5 条 |
| 86 | `Services/Deepseek1024CatalogService.cs`（新）+ `Services/MarketplaceService.cs` + `Models/VersionSettingsModels.cs` + `MainWindow.xaml.cs` + `tests/DshLauncher.SelfTest/Program.cs` | **中文插件源 #16（work-log/66）**：接入 `deepseek1024.com`（13,656 条，带 `install` 标识与安装/失败统计）——分页并发拉取 + TTL 30 分钟缓存（写原始载荷、映射现算）+ 拉取失败回退旧缓存 + 单页失败不放弃整次；映射处理该源的真实形状（`id=owner/repo/子路径`、`repository` 只有仓库名、`description.en/zh`），信息不足的条目直接丢弃；市场侧按 `MarketplaceSourceKind.ZhCatalog` 合并，**默认关闭**，由设置 →「插件与技能来源」里的开关打开；SelfTest +5 条 |
| 85 | `Services/MarketSourceSettingsService.cs` + `Services/MarketplaceService.cs` + `Services/SkillMarketService.cs` + `MainWindow.xaml.cs` + `tests/DshLauncher.SelfTest/Program.cs` | **来源列表带开关与删除、固定高度内部滚动（work-log/65）**：来源条目从纯字符串升级为 `{value, enabled}`（读侧兼容旧格式）；插件与 Skill 两块列表都改成固定高度（158px）内部滚动的管理条，每条带「启用」开关、「测试」、「删除」；停用不删除、只影响市场消费；插件侧解析同步支持本地 `.json` 与停用项；SelfTest +2 条（开关落盘/旧格式兼容） |
| 84 | `Services/MarketSourceSettingsService.cs`（新）+ `Services/SkillMarketService.cs` + `MainWindow.xaml.cs` + `tests/DshLauncher.SelfTest/Program.cs` | **设置里新增「插件与技能来源」大项（work-log/64）**：插件与 Skill 两块结构平行——列出/添加/移除/连通测试自定义来源（插件＝本地目录 JSON 或网址，走既有 `marketplace-sources.json`；Skill＝GitHub `owner/repo`（扫描其中 SKILL.md，已真正接入市场候选）或本地目录 JSON / 网址（已保存，扫描接入留待下一步））；去重/上限 50/损坏按空/校验非法输入；探测走统一 UA（实测有些目录站会 403 拒默认 UA）。SelfTest +4 条，harness 更新"设置页 5 类"断言 |
| 83 | `tests/DshLauncher.SelfTest/`（新）+ `.github/workflows/ci.yml`（新）+ `docs/VERIFICATION.md`（新） | **工程治理（work-log/62）**：仓库自带纯逻辑自测（29 条断言：会话文件命名与代际、语义化版本比较、dsh profile 解析、任务台账、存储清理边界含**安全反证**、会话代际归并、长文本收敛、核心 bundle 常量），`dotnet run --project tests/DshLauncher.SelfTest` 即可跑，全程用临时目录、不联网、不开窗口；新增 CI 工作流（构建 → 自测 → 发布，windows-latest + .NET 8）；新增 `docs/VERIFICATION.md`（三层验证 + 发布 SOP + 测试边界约定）
| 82 | `App.xaml.cs` + `Services/LauncherStorageService.cs`（新）+ `Services/MarketplaceService.cs` + `MainWindow.xaml.cs` | **日志治理与存储清理（work-log/61）**：① 崩溃日志三重保险——单文件 2 MB 轮转（`crash.log.old`）、同类异常 60 秒内折叠（签名＝类型＋消息前 120 字符＋栈首行）、每进程上限 200 条（超限只留一行说明）。起因是实测发现 `crash.log` **1.2 GB**（扫描页模板 TwoWay 绑定到只读属性 → 每 3 毫秒抛一次、两天 92,629 条）；② 新增 `LauncherStorageService`（只统计/清理 **Launcher 自有文件**：崩溃日志/轮转旧日志/市场缓存/运行环境缓存/插件回滚点/会话备份副本/换版本导出，删除走**回收站**；实例数据、自动快照、手动快照、凭据、会话一律不碰）；③ 插件回滚点加保留上限（最近 10 份）；④ 设置页「诊断与日志」加「存储与清理」入口（两步确认，预览占用后执行）。harness 344 PASS；安全反证（会话/凭据/手动快照必须存活）当场抓出"清理会对实例目录下手"的缺陷 |
| 81 | `Services/DshProfileService.cs`（新）+ `ExtensionWindow.xaml(.cs)` + `Services/ExtensionService.cs` + `Services/ExtensionService.ProfileRepair.cs` + `Services/PluginProfileResidue.cs` + `Services/DshInstanceRunner.cs` + `Models/VersionSettingsModels.cs` + `Services/VersionSettingsService.cs` | **Profile 切换（work-log/60）**：dsh profile＝`$DSH_HOME/profiles/<name>`（`dsh.profile.bundles` + `cordis.patch.yml`，共享 `profiles/node_modules`，shipped 模板按名首次使用时由 dsh 初始化）。新增只读的 `DshProfileService`（枚举/读 bundles/判定是否带 Web App/解析当前 profile，**不创建不修改**任何 profile 目录）；插件链路里写死的 `web`（CLI `--profile`、workspace、profile manifest、残留检测、修复文案）全部改为经解析器取当前 profile；实例设置新增 `ActiveProfile`（空值＝沿用 `web` 别名，行为不变）；启动前校验：非 Web App bundle 的 profile **拒绝启动并说明原因**（headless/acp/sdk 交给命令行）；插件页新增「当前 Profile」选择器（只列已存在 profile + bundles/来源/patchReload 提示，Agent 页隐藏）。harness 新增 11 条 `p1/profile:` 与 2 条 `contract/profile:`（含上游 `PROFILE_TEMPLATES` 哨兵）；UI 实测插件页渲染选择器 |
| 80 | `Services/LauncherTaskService.cs`（新）+ `Models/LauncherTaskModels.cs`（新）+ `LauncherTaskWindow.xaml(.cs)`（新）+ `MainWindow.xaml(.cs)` + `PluginProgressWindow.cs` + `ExtensionWindow.xaml.cs` + `VersionSwitchWindow.xaml.cs` + `VersionControlWindow.xaml.cs` | **任务中心（work-log/59）**：长任务统一台账——`LauncherTaskService`（无 WPF 依赖、`Changed` 事件、`launcher-tasks.json` 上限 50 条、详情截断、损坏当空、崩溃遗留的"进行中"归一化为中断）+ 内嵌页「任务」（运行中与历史一张表：时间/类别/对象/标题/状态/耗时/摘要 + 取消；清空历史自绘两段确认）+ 顶栏入口与运行中角标；台账与进度窗取消令牌**双向串联**（两边点取消等效）；四类长任务接线：运行环境准备/修复、插件与技能装删改、实例导入（扫描进度逐条上报）、更换运行版本。harness 322 PASS；UIA 实测任务页渲染与中断归一化 |
| 79 | `VersionControlWindow.xaml(.cs)` + `VersionSettingsWindow.xaml(.cs)` + `MainWindow.xaml.cs` + `Services/VersionSwitchHistoryService.cs` | **版本相关操作集中到「版本控制」页（work-log/57）**：运行版本（更换 + 更新提示开关/提示行）、切换历史（含一键回退）、配置快照与回滚全部搬到「版本控制」的选中版本详情；实例设置里的「版本与快照」整页移除（快照与版本控制页原本完全重复）；切换成功后左列表与详情同步换实例对象；保存前自动快照保留（窗口自建快照服务）。harness 302 PASS；UI 端到端：版本控制页完成整轮切换并写台账 |
| 78 | `Services/ConversationService.cs` + `Models/EcosystemModels.cs` + `ConversationWindow.xaml(.cs)` | **对话页按会话目录归并（work-log/56，work-log/53 发现 F1）**：dsh 的模型是"一个会话目录 = 一个会话、文件是格式代际、读最高代际"，对话页原来每文件一行 → 从旧版升上来的实例同一会话显示两行（旧内容是升级前状态，导出/删除容易选错）。现在按目录归并只列最高代际，新增「代际」列与状态行提示；删除确认说明"会退回更早一代"；「降级前一键导出」改为逐代际全量导出（列表与备份语义显式分开） |
| 77 | `Services/VersionSwitchHistoryService.cs`（新）+ `Services/DshUpdateNoticeService.cs`（新）+ `Services/InstanceVersionSwitchService.cs` + `Services/DshVersionCatalogService.cs` + `VersionSwitchWindow.xaml.cs` + `VersionSettingsWindow.xaml(.cs)` + `MainWindow.xaml(.cs)` | **切换历史 + 一键回退 + 卡片更新徽标（work-log/55）**：① 每次换版本留痕（`version-switch-history.json`，含方向/快照/会话备份，保留 50 条），「版本与快照」页新增历史卡片与「回退到上一版本」（只预选进向导，仍需检查+确认）；② 实例卡片新增「有新版本」徽标，与实例设置共用 `DshUpdateNoticeService`（6 小时缓存、失败不缓存、只对开启开关的实例联网）；③ **修掉官方版本排序缺陷**：旧比较器丢掉预发布标签（alpha.2 与 rc.2 同序）→ 「官方最新」被算成 0.1.5-alpha.2，改为复用 semver 比较器 |
| 76 | `Services/LauncherLog.cs` + `App.xaml.cs` + `VersionSwitchWindow.xaml(.cs)` + `_verify-p0/Program.cs` | **日志隔离 + 确认面板自绘（work-log/54）**：① `LauncherLog` 支持 `DSH_LAUNCHER_LOG_ROOT` 覆盖，harness 与崩溃兜底日志一律写临时目录，不再污染用户真实 `launcher.log`（`diagnose` 用例在 Launcher 运行中也稳定）；② 更换运行版本的确认步骤改为窗口内自绘面板（`ConfirmPanel`：逐条说明 + 实际生效的勾选项 + 「返回」/「确认切换」），彻底移除 `MessageBox`，换版本/改勾选会作废旧确认 |
| 75 | `docs/DSH_CONTRACT_INVENTORY.md`（新）+ `Services/SessionFileNames.cs` + `Services/ConversationService.cs` + `Services/DshInstanceRunner.cs` + `_verify-p0/Program.cs` | **dsh 契约哨兵 + 降级实测（work-log/53）**：① 13 条契约清单（C1–C13：会话格式/文件名/头部字段/目录编码/根布局/编码/CLI/就绪输出行/Node 引擎/profile bundles/插件 peerDeps/`--no-open`/DSH_HOME 固定文件）；② harness 新增 13 条 `contract:` 哨兵——直接与**上游源码**和**已安装运行时**比对（文件名正则、目录编码向量取自上游测试、CLI 旗标、`dsh web:` 输出行、Node 下限），并用 252 个上游会话样本 + 9 会话语料回归启动器真实读写链路（含导出逐字节保真）；上游缺失时整段 SKIP；③ 降级单向性用已装两个运行时的实物证据固定（0.1.2 无 format catalog/迁移链、文件名恒为 `session.jsonl`）；④ 产出发现 F1：多代际会话目录在对话页列多行（已记录待决定） |
| 74 | `Services/InstanceVersionSwitchService.cs`（新）+ `Services/InstanceRuntimeRebinder.cs` + `Services/PluginCompatibility.cs` + `Services/ConversationService.cs` + `Services/SessionFileNames.cs` + `Models/VersionSettingsModels.cs` + `Services/VersionSettingsService.cs` + `VersionSwitchWindow.xaml(.cs)`（新）+ `VersionSettingsWindow.xaml(.cs)` + `MainWindow.xaml.cs` | **更换实例运行版本（升级/降级，work-log/52）**：入口并入「版本设置 → 版本与快照」页（与原「快照回滚」同页，页面因此改名）；解析目标（候选目录：`versions/<v>` → 主运行根 → 实例当前根，缺则从官方 npm 下载）→ 预检（Node 引擎 / 插件核心 peerDeps / 会话代际 / 方向）→ 可选快照与「一键导出会话」→ 强制重绑定（只改 RootPath/入口/启动描述/版本号，DSH_HOME 不变）→ 失败回滚原绑定；另加实例级「DSh 更新提示」开关（默认关） |

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
13. **插件安装韧性**：pnpm 失败会翻译成可操作原因（幽灵依赖/store 不一致/需要允许构建/安全等待期等），瞬时网络与 GitHub 直装失败自动重试一次并依次走国内镜像；失败安装的残留（package.json 依赖/bundles、node_modules、cordis.patch.yml）自动清理，避免下次启动 include-loader 直接崩
14. **便携版 Node**：「准备运行环境」默认下载免管理员的便携版 Node 到 Launcher 数据目录（不写系统 PATH、不影响已有 Node），低于 22.19 自动抬版；失败时才询问是否改用官方 MSI
15. **装前校验**：手动安装框先确认目标是 DSH 插件（`dsh.bundle.patch` + 可加载入口）——像 `abc`/`nothing` 这类普通 npm 包默认拦下（可选择继续），避免装进 profile 变成“已安装（默认禁用）”的幽灵依赖；联网不可达时放行但提示
16. **扩展页左栏**：实例路径改为只显示尾部（鼠标悬停看完整路径，右侧「复制」按钮一键复制实例目录 + DSH_HOME），不再因长路径换行挤占插件列表
17. **路径链接化**：实例目录/DSH_HOME 与插件详情的来源路径都变成链接样式——**点击即复制**，**悬停出独立悬浮卡片**（卡片里给完整路径、类型、状态、描述，不占原界面）；插件详情的类型/状态/描述改为紧凑单行，热加载开关独立成行，左栏不再拥挤
18. **实例头部单行**：实例名（左）+ 版本·类型（右）合为一行，去掉原来三行占用的空白
19. **启动页状态胶囊**：文字放大加粗、加描边与像素对齐，背景更实；卡片阴影独立成层，卡片内文字不再因阴影效果发糊
20. **UI 规范**：卡片阴影统一独立分层（文字不再被效果栅格化）、语义色/圆角/间距全部走 `App.xaml` 令牌、复选框与进度条改为统一外观；长期规范见 `docs/UI-DESIGN.md`
21. **设置页分类导航**：「设置 / 诊断」改为左侧分类栏（运行环境 / 常规 / 网络与账户 / 诊断与日志）+ 右侧内容，不再是一长条堆叠
22. **分类栏圆角化 + 新建版本修复**：设置页分类栏改为圆角卡片；「新建干净版本」改为先弹窗（0.2 秒内出现）、官方版本列表后台补全，并加忙碌态防连点
23. **窗口尺寸**：最小可调尺寸提升到与启动尺寸一致（1180×720，小屏自动收缩）；创建版本不再报“调用线程无法访问此对象”（对话框属性改在 UI 线程读取）；删除版本顺手清理空目录
24. **修复非最大化窗口裁切**：删除了对工作区尺寸的二次 DPI 除法（WPF 的 `SystemParameters.WorkArea` 本身就是逻辑单位），右侧/底部内容不再被裁；启动时日志新增一行窗口尺寸诊断
25. **移动已有安装**：设置 → 运行环境新增「移动已有安装」，把已装好的 DSh 运行目录整体搬到新位置并自动更新引用它的实例（同盘秒级、跨盘复制校验；失败回滚）
26. **删除版本修复**：attachments 对象存储的只读文件不再导致“Access denied”（递归删除前自动清除只读属性，11 处统一处理）；删除确认框先统计并提示将清除的只读文件数；「移动已有安装」以当前选中实例的运行时为源；快照支持单文件精确恢复
27. **移动源可选择**：设置 → 运行环境新增「要移动的运行时」下拉框（配置位置 + 各实例运行时），无需回启动页选中实例
28. **运行时卡片**：「要移动的运行时」改为圆角卡片 + 复选框（每行带完整当前位置）；「重新检测 / 扫描自定义目录」上移到卡片上方
29. **启动页路径链接化**：实例状态卡片的 目录 / DSH_HOME 改为链接（点击复制、悬停出悬浮卡片），与扩展页一致
30. **运行时多选移动**：卡片复选框可同时勾选多个；勾多个时分别搬入目标目录下的同名子文件夹（避免互相覆盖），安装位置指向目标根
31. **运行时卡片只列实例**：每行是「实例名 + 该实例的运行时路径」；没有实例关联的安装位置不再出现（无实例时卡片显示提示）
32. **卡片列出全部实例**：不再限于 Launcher 数据根（仅排除系统 npm/nodejs 目录）；`versions/<版本>` 自动归一到安装根；多个实例共用同一运行时合并为一行
33. **默认安装位置**：exe 同目录的 `run_time`（便携优先）；exe 同目录不可写时回退 `<数据根>\runtime\dsh`；已显式配置的路径优先，不受影响
34. **发布形态**：csproj 内置 `SelfContained/PublishSingleFile/IncludeNativeLibrariesForSelfExtract/EnableCompressionInSingleFile`，裸 `dotnet publish -c Release` 即得可独立运行的单文件（无需装 .NET）
35. **WebView2 数据目录**：默认 exe 旁 `<exe 名>.WebView2`；exe 同目录不可写时回退 `%LocalAppData%\DeepSeek\launcher\WebView2`（Desktop 窗口仍可用）
36. **便携数据根**：exe 旁存在 `launcher-data` 目录，或设置了 `DSH_LAUNCHER_DATA_ROOT` 环境变量时，数据根改到该处（实例/设置/缓存/便携 Node 全部随 exe 走）；不可写则回退 `%Documents%\DeepSeek\launcher`
37. **凭据格式**：导入/覆盖刷新/回填合并/Provider 同步/快照恢复遇到旧包装（`version: 1` + `records:` + `refs:`）的 `.credentials.yaml` 会保守转换为顶层键值映射；无法确认的格式保持原样（E1012）
38. **快照内容**：版本快照与插件快照均包含 `profiles/web/pnpm-workspace.yaml`（pnpm `allowBuilds` 构建许可）；旧快照不含该文件时恢复不会删除它
39. **实例选中**：列表刷新/页面切换导致的 `SelectedItem` 瞬时 `null` 会被忽略并回写绑定；真正删除实例后仍可正常清空
40. **实例环境变量**：版本设置 → 个性化可增删键值对；启动该实例时注入进程环境；`DSH_HOME`/`DSH_AGENTS_HOME`/`PATH` 不可覆盖；含 KEY/TOKEN/SECRET/PASSWORD 等的值用 DPAPI 加密落盘，诊断包不含变量值
41. **启动失败**：启动后没有主窗口或启动阶段出现未处理异常时，记日志 + 提示 + 退出，不再无窗口驻留
42. **市场/技能描述**：限高两行 + 省略号 + ToolTip（悬停看全文）
43. **对话页筛选行**：版本/显示两个下拉按比例分配宽度（MinWidth 240/150），窄窗口不再挤爆
44. **实例资源**：运行中实例的卡片显示 `CPU x% · 内存 xxx MB · 运行 x 分 x 秒 · n 个进程`，采样跟随守护进程探测间隔（默认 5s），停止即清空
45. **会话全文检索**：对话页搜索框可跨实例检索会话正文（含压缩的 `session.jsonl.zstd`），单文件最多读 32 MiB，损坏文件跳过；结果按命中次数排序，双击直接打开（自动切换到所属实例）
46. **孤儿检测**：监听实例端口的是该实例进程树内的子进程（cmd → node）时不再误报"未受管进程占用端口"
47. **实例设置 → 运行状况**：运行概况（状态/时长/进程数/端点）+ CPU 与内存折线图（最近 20 分钟，每 5 秒一点）+ 进程树表 + 运行日志窗（dsh 输出与启动/停止/健康检查事件，带时间戳与来源，保留 2000 行不落盘）
48. **清理残留进程**：运行中传 keepPid 只清残留（桌宠/市场 helper/上次异常退出遗留），未运行时清理全部遗留；执行前二次确认、结束后反馈数量
49. **日志捕获范围**：只有当前 Launcher 进程启动的实例才有 stdout 日志；被接管的实例日志窗为空（曲线与进程树仍有数据）
50. **安全模式**：启动失败且 web profile 有第三方插件时，会话内询问一次；同意后在 `<DSH_HOME>\profiles\.dsh-safe` 生成隔离 profile（Tier1 剥离第三方保留核心，失败自动降级 Tier2 最小核心），用 `dsh --profile .dsh-safe` 启动；**用户配置文件零改动**（前后 SHA-256 快照比对，改动会警告）
51. **安全模式清理**：正常启动成功后自动删除 `.dsh-safe`；停止/删除实例不影响用户 profile
52. **启动健康证据**：进程提前退出（附 exitCode）、dsh 日志命中失败签名（新增行，只认 dsh/stderr）、HTTP 探测（探针异常只记证据）、WebView2 页面加载失败（上报提示）四层；失败原因与证据会出现在弹窗与运行日志中
53. **启动提示**：安全模式成功时提示"第三方插件未加载，你的配置未被修改"，普通启动保持原提示
54. **页面层判死**：Desktop 启动后对 Chat 页面做 DOM 探针（根节点 + 错误签名）；失败且存在第三方插件时会话内询问一次是否安全模式重启；探针异常只记证据不判死
55. **启动证据**：实例设置 → 运行状况新增“启动证据”区块，记录每次启动尝试的结论与四层证据（含接管已运行实例），最多 60 条、进程内不落盘
56. **就绪后 HTTP 健康**：任何 HTTP 响应（含 401/403）算服务活着；连续 3 次无响应报一次降级提示并记证据，成功即清零
57. **安全模式徽标**：实例卡片状态胶囊旁显示“安全模式”徽标（存在 `.dsh-safe` 时），正常启动清理后消失
58. **空闲自动停止**：实例设置 → 运行状况可开启（默认关），阈值 5/15/30/60/120/240 分钟；只对 Launcher 托管的运行中实例生效，停止后写启动证据并提示
59. **后台任务不算空闲**：进程树有到非回环地址的已建立连接（等模型响应/工具联网）、CPU ≥ 1%、`sessions`/`storages` 最近 90 秒有写入、dsh 有新输出、Launcher 正在安装/准备 —— 任一命中都不停
60. **运行状况页刷新**：只初始化一次控件，之后仅刷新状态文案，不再覆盖用户正在编辑的复选框/下拉
61. **启动证据落盘**：每次启动判定/四层证据追加到 `<实例 dsh-home>/.dsh-launcher/startup-evidence.jsonl`（保留最近 200 条），Launcher 重启后仍可在「运行状况」回看；诊断包也带一份
62. **崩溃恢复**：实例设置 → 运行状况可设「仅通知（默认）/ 自动重启（退避 5s→15s→1m→5m，上限 1–10 次）/ 直接冷却关闭 / 自动重启后冷却」；稳定运行 10 分钟后再崩溃计数清零
63. **主动停止不算崩溃**：手动停止、空闲自动停止、安全模式切换、插件重试、更新重启、退出 Launcher 都不触发自动重启
64. **崩溃现场与冷却**：每次崩溃记录退出码/运行时长/日志尾部/启动证据/资源采样（最近 10 条，落盘）；达上限转「冷却中」，实例卡片显示“崩溃冷却”徽标（跨 Launcher 重启保留），运行状况页可「清冷却并重启」
65. **逐插件定位**：运行状况 →「插件排查」→「定位肇事插件」；实例需先停止；隔离 profile 二分禁用（不动用户文件），2–4 轮给出肇事插件，可一键「禁用 X 并正常启动」（自动存回滚点）
66. **定位的实现要点**：试验 profile 复制用户清单并改写 bundles，`node_modules` 用目录联接指向用户 web profile（否则第三方插件无法解析）；结束时先摘联接再删隔离目录，绝不跟随到用户依赖
67. **崩溃原因归类**：每次崩溃在本地跑规则表（只读 dsh/stderr 日志），给出原因 + 置信度 + 依据 + 建议；低置信只显示“未知原因”；原因随现场落盘并在「崩溃恢复」表格/状态中展示；端口冲突时会先清理残留进程再自动重启
68. **插件 × 实例矩阵**：「扩展 / Agent」→「插件矩阵」内嵌页；行=至少在一个实例启用的插件（排除 `@deepseek-ai` 核心与 schemastery/cosmokit/cordis 等运行时依赖，避免假插件行），列=实例，单元格三态；可「复制为文本」（TSV）；只读，启停仍在插件管理
69. **扩展页工具条按钮**：「当前实例」卡内的工具条按钮改紧凑档（12px / `Padding="9,4"`，样式 `CompactToolbarButton`），125% DPI 下高度 44→33 设备像素、换行 3→2 行
70. **外部服务失效收敛**：接管的外部 dsh 进程退出后，Launcher 最多 5 秒把实例收敛为“已停止”（不再残留“运行中”），插件/技能/MCP 修改恢复可用；外部服务仍存活时仍拒绝停止
71. **启动日志基线**：健康检查只认本次进程启动后新增的日志行；上一次尝试留下的失败签名不再让后续启动（含安全模式与自动重启）瞬间假失败（运行状况页的历史日志仍保留）
72. **长文本弹窗**：安全模式询问与「启动详情」不再直接显示原始日志/堆栈（每行 ≤120 字符、总长 ≤1200 字符，超出提示“完整内容见运行状况 → 运行日志”），避免按钮被撑出屏幕
73. **插件兼容性预检**：手动安装 / 市场安装 / 更新前读取插件清单的 `@deepseek-ai/*` peerDependencies 与实例实际版本比对（含预发布规则）；不兼容时弹窗说明并默认不继续，批量更新自动跳过不兼容项；非插件仍按原逻辑拦截
74. **实例生命周期日志**：启动与停止各写一条 `launcher.log`（含实例名、端口、PID、normal/safe 模式），不必再去 watchdog.log 查
75. **核心包口径统一**：`@deepseek-ai/dsh-base` / `dsh-web-app` 不计入实例卡片「N Plugins」，也不出现在插件管理页与扩展页已装列表（与矩阵/doctor/安全模式同一份定义）
76. **harness 隔离**：UI 冒烟注入临时 `UiStateStore`，不再写入真实 `ui-state.json`
77. **扫描本机 DSH 环境**：「版本控制 → 导入实例 → 扫描本机 DSH 环境」列出 `%USERPROFILE%\.dsh*` 与 `DSH_HOME` 指向的 home（带 web/tui/other 分类与「已登记」标记），勾选后按 home 建实例；已登记源自动跳过，重复导入不产生副本
78. **从源码目录导入**：「导入实例」菜单补上原本悬空的源码导入入口（之前只有提示文字指向不存在的按钮）
79. **不含 `profiles/web` 的 home 不可导入**：列表里标红说明（TUI profile 需 #19 支持后才能启动）
80. **会话格式跟随 dsh 版本**：对话页/检索/导出/备份/删除/同步都能识别 `session.v<N>.jsonl[.zstd]`（dsh 0.1.5-rc 起的命名），会话头接受 v0..当前 与新增的 `isSeeded`；导入/恢复按头部版本命名并跟随目标实例的压缩编码（只换容器、不改内容），交给 dsh 自己的格式迁移链处理旧版本
81. **旧版实例不接收新格式会话**：目标实例的 DSh 读不了 vN（无 format catalog）时，导入会明确报错而不是写出坏文件
82. **更换实例运行版本（升级/降级）**：「版本控制」页选中某个版本后的「运行版本」卡片 →「更换运行版本」；只改运行时绑定（DSH_HOME / 配置 / 插件 / 会话全部保留），支持自动下载未安装版本，失败自动回滚原绑定
83. **降级保护**：目标版本读不了现有 `session.vN` 会话时明确警告，并提供「导出现有会话备份」；不做会话格式转换（迁移交给 dsh）
84. **DSh 更新提示（可选）**：实例级开关（默认关）；开启后在运行版本区块联网显示“官方最新 x.y.z”与升降级建议
86. **切换历史与一键回退**：「版本控制」页记录每次更换（时间/前→后版本/升级或降级/是否建快照或导出会话）；「回退到上一版本」把上一条的起点版本预选进向导
87. **实例卡片「有新版本」徽标**：实例设置里开启「更新提示」后，卡片上直接显示是否有官方新版本（6 小时缓存，关闭开关的实例不联网）
89. **同一会话只列一行**：同一会话目录里的历史代际不再各占一行；「代际」列显示当前代际与历史代际数，状态行给出合计
91. **版本相关操作集中在一处**：「运行版本」「切换历史」「配置快照与回滚」都在「版本控制」页的选中版本详情里；实例设置不再有「版本与快照」页，快照不再重复两份
90. **删除确认说清后果**：删掉最高代际时明确提示 dsh 会退回到更早的那一代（升级前的旧内容）
88. **官方版本排序修正**：预发布标签参与排序（alpha < beta < rc < 正式版），「官方最新」不再被算成更旧的 alpha 版本
85. **切换确认在同窗内完成**：确认面板会列出实际生效的勾选项（会话备份/配置快照），改选版本或勾选项会自动作废上一次确认；切换过程不再弹模态框
92. **长任务有台账**：运行环境准备、插件/技能操作、实例导入、更换运行版本都在「任务」页留痕（时间/类别/对象/状态/耗时/最近进度），运行中可以取消，最近 50 条会跨启动保留；进程被杀时遗留在"进行中"的行重新打开后自动标成"启动器已重启，任务中断"
93. **实例可换 Profile**：插件页顶部选择该实例的 dsh profile（`$DSH_HOME/profiles/<name>`），插件/技能管理与实例启动都跟着走；只有带 Web App bundle 的 profile 能由 Launcher 启动，headless/acp/sdk 这类会在启动前被拦下并说明原因
94. **异常循环写不爆磁盘**：崩溃日志超过 2 MB 自动轮转、同类异常 60 秒内折叠、每进程最多 200 条（实测那次 1.2 GB 的崩溃循环不会再发生）
95. **存储可清点可回收**：设置页「存储与清理」列出 Launcher 自有文件的占用并一键清理（走回收站可恢复）；实例数据、会话、凭据、快照不在清理范围内
96. **仓库自带可跑的自测**：`tests/DshLauncher.SelfTest` 覆盖核心纯逻辑并含安全反证，clone 后一条命令即可验证（另有 CI 工作流与 `docs/VERIFICATION.md` 说明三层验证与发布 SOP）
97. **自定义插件与技能来源**：设置里「插件与技能来源」可管理自定义来源（插件目录文件/网址；Skill 的 GitHub 仓库即时生效），带连通测试、去重与非法输入拦截
98. **来源可开关**：自定义来源列表固定高度内部滚动，每条可「启用/停用」「测试连通」「删除」；停用不删文件，市场下次刷新即不再使用
99. **中文插件源（可选）**：设置里一键启用 deepseek1024.com（约 1.36 万条，含安装/失败统计），市场按「中文源」标记合并；默认关闭、失败自动回退旧缓存
100. **第二个中文插件源（可选）**：设置里可单独启用 dshfind.com（在线约 1.4 万条，自动排除已归档），来源名里带官方/精选/星标/标签
101. **来源管理只剩一处**：唯一固定来源是 GitHub（awesome 目录）；两个中文源作为预置条目躺在自定义列表里，开关/删除/测试都在同一处（默认停用）

## 待办（功能完成后统一处理）

- **[UI 统一待办](docs/UI-UNIFICATION-TODO.md)**：挂起中——图标体系、颜色令牌、字号下限、表格自适应、窗口框架、键盘可达性、高 DPI 复检等，等所有功能性需求结束后作为一个独立变更集统一处理。
- 功能侧剩余（按性价比）：对话全文检索 / 实例资源监控（1–2 天）→ #17 插件×实例矩阵 / #16 中文插件源 → **#11 安全模式 + 四层启动健康证据**（3–5 天）；#14 本机 DSH 扫描导入已完成（变更集 71，WSL 已在 72 移除，见 work-log/50）。

## 构建与发布（SOP）

```powershell
# 需 .NET 8 SDK（本机：..\dotnet-sdk）
$env:DOTNET_ROOT = 'D:\Program Files (x86)\dsh_from_github\dotnet-sdk'
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
cd src\DshLauncher

# 开发构建
dotnet build -c Release

# 发布（单文件自包含，输出到仓库 dist\；参数已内置 csproj，无需再手敲）
# ⚠️ 前置：先停止运行中的 DSH Launcher（dist exe 被锁定 → MSB4018）
dotnet publish DshLauncher.csproj -c Release -o ..\..\dist
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
