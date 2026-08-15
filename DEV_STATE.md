# DSH Launcher 开发状态

## 当前目标

完成当前 UI 调整并继续维护 Windows x64 的独立 DSH Launcher；当前发布目标为 0.1.8，内容包括 PCL2 风格版本设置、版本控制、插件市场缓存/筛选、独立 Chat 窗口、多实例启动和安全整合包改动。

## 已完成内容

- 已有 .NET 8 WPF Launcher 主窗口、左侧工作区导航和启动页；发布版为自包含单文件，不依赖已安装的 Node.js、npm、pnpm 或 DSh。
- 已加入 `Assets\DSHLauncher.ico` 应用图标；主窗口、Chat 窗口和 Windows 自包含 EXE 均使用该图标。
- 启动页当前聚焦 Provider、实例选择/启动和正在运行的实例；运行环境检测入口移到“设置 / 诊断”页，不再在启动页展示运行环境卡片。
- 已将扩展、Agent、对话管理改为主窗口右侧 `ContentControl` 的内嵌页面；模型配置页已移除，Provider 状态和诊断保留在启动页。Agent 页面只显示 Skill、Agent Preset、Workflow，并隐藏不适用的 Plugin 操作；Chat WebView2 仍是独立窗口。
- 已实现 Node.js 检测：检查 PATH 和 Windows 常见安装目录，并通过 `node.exe --version` 验证可运行性。
- Node.js 检测已异步执行；单个候选的进程与输出总超时为 2 秒，超时会清理进程树；窗口关闭时会取消检测；刷新操作在检测期间会被限制。
- 已实现 DSh 运行时检测：识别 PATH 中的 `dsh.cmd`/`dsh.exe`，验证 `--version`，解析 DSh 包根目录和版本；Windows `.cmd` 使用 `cmd.exe` 调用。
- 已实现 `ManagerInstance` 注册：支持 installed/source 类型、共享 DSh 根目录下的多版本记录、JSON 原子保存和每实例独立 `DSH_HOME`；当前实际 UI 测试已注册 1 个 installed DSh 实例。
- 已实现 Source 项目检查：读取 `package.json`、包管理器和锁文件、构建脚本、依赖目录、CLI 入口，并把已找到的构建入口纳入状态显示。
- 已实现 Source 准备服务：按项目声明的 npm/pnpm/yarn/bun 选择命令，缺少 `node_modules` 时执行 install，再执行 `run build`；超时、取消和非零退出会清理包管理器进程树并返回输出，构建后检查 `apps/cli/lib/bin.js` 等实际入口。
- 已实现 Source 启动：按 Source `package.json`/`apps/cli/package.json` 的 `engines.node` 选择兼容 Node.js，复用 installed DSh 的 DSH_HOME 隔离、loopback 端口、健康检查、停止/重启和跨 Launcher 互斥锁。
- 已修正 Node 候选选择：多个可用 `node.exe` 时选择最高可解析版本，避免 PATH 中旧 Node 覆盖满足 Source 要求的版本。
- 已修正停止状态误报：停止未被当前 Runner 管理的进程失败时不再直接保存为“已停止”，而是保留错误状态和诊断。
- 已实现 installed DSh 生命周期：按实例设置 `DSH_HOME`，分配 loopback 空闲端口，启动 `dsh web`，等待 HTTP 可访问，支持停止和重启；运行进程退出或 Launcher 重启时不会保留虚假的 Running 状态。
- 已实现同一 `DSH_HOME` 的跨 Runner 本地独占锁文件，避免两个 Launcher 同时写入同一实例数据；锁文件位于用户本地 Launcher 锁目录，不会被 DSh 的 DSH_HOME watcher 监听；Runner 还会清理整个子进程树。
- 已实现 `ExtensionService` 与内嵌扩展/Agent 页面：按 DSh 实际 profile 结构列出 Plugin，支持 Plugin 安装/更新/删除/启停；按 DSh 实际 Skill 根导入/删除 Skill；管理 MCP stdio/streamable-http 配置；导入/删除用户 Agent Preset；Workflow 仅显示随附 standard preset 能力，不伪造 DSh 不认识的目录。扩展写入前会拒绝实例运行状态、重解析点、越界路径、危险包名和命令行控制字符。
- 已保留 `ModelService` 的 `settings.yaml` 读写和凭据引用保护；模型配置页面已移除，当前 UI 不再提供单独模型页。
- 已调整启动页 UI：导航并入标题栏并居中，新增中间“启动”入口，同时保留左上角 DSH 品牌按钮返回启动页。Provider 区域继续位于启动页左栏的账号/离线配置位置并默认展开，实例切换和启动操作位于 Provider 下方，运行中实例保留在右侧；启动页移除运行环境卡片、无操作页脚和无效副标题。
- 已统一内嵌页面 UI：扩展/Agent 不再显示重复的“扩展中心”标题，对话不再重复显示页面标题和实例灰字；主窗口、扩展、对话内容均提供垂直滚动，TextBox、TabItem、Button、卡片和页面边框采用统一圆角视觉。
- 已拆分 Chat 窗口关系：Chat 不再设置 Launcher 为 Owner，每个运行实例维护独立 Chat 窗口和任务栏入口，使用黑色 DeepSeek 图标；关闭某个 Chat 只关闭窗口，不停止对应 DSh 实例。
- 已修复内嵌页面滚轮路由：主窗口预览滚轮事件会优先滚动鼠标所在且仍有空间的内层列表/页面，内层到边界后继续滚动主页面；扩展页改为左侧当前实例与已安装 Plugin/MCP、右侧插件市场，并显式设置深色文字。
- 已调整页面布局：窗口和页面外边距收紧到比上一版更小，窗口边缘 `WindowChrome` 调整命中区扩大到 12px；Agent 模式下已安装内容面板跨满原双栏区域，不再只占左半边。
- 已加入运行实例交互：正在运行的实例列表支持双击打开或重新呼出对应 Chat 窗口；版本设置主标题和左侧设置栏使用当前实例名称。
- 已加入版本控制交互：版本列表双击会选中对应版本并直接进入该版本的版本设置。
- 已加入 Provider 启动页状态与诊断 UI：`ProviderStateService` 将启用状态写入实例 `DSH_HOME\\.dsh-launcher\\providers.json`；`ProviderDiagnosticService` 只读检测 `/models`，卡片显示绿/红状态、模型列表/思考能力和问号诊断弹窗。
- 已加入插件市场第一版：扩展页只显示“插件市场”，支持关键词搜索、刷新目录、来源选择、按发布时间/Star 数量排序、安装、更新和卸载；候选来自社区目录、GitHub `dsh-plugin` 标签、当前实例可读到的官方 bundle 和可选自定义 JSON 目录。市场结果写入 Launcher 根目录缓存，打开页面先显示缓存再后台更新。安装前会再次读取 npm/GitHub 的 `package.json`，检查 `dsh.bundle.patch` 与入口；操作前备份 web profile 配置，并拒绝运行中或 Attached 实例。
- 已修复 Plugin CLI 的 pnpm 环境：官方 DSh 会在 profile 中直接调用 `pnpm`，Launcher 现在优先传递已有 pnpm；若仅有 Corepack，则创建临时 pnpm shim 注入 Plugin 子进程，不修改系统 PATH；两者都缺少时提示国内/国外安装命令。
- 已加入版本控制页面：启动页的“版本控制”按 PCL2 的左侧版本列表/右侧详情结构提供复制版本、新建干净版本和整合包导入；手动选中已停止版本后“复制版本”启用并复制完整 `DSH_HOME`，按钮提示会说明运行中的版本需先停止；多个版本可共享 DSh 根目录但使用独立 `DSH_HOME`。默认 `.dshpack` 为 ZIP 包，要求 `manifest.json` 和 `dsh-home/`，版本设置页可修改扩展名，导入会建立新版本并拒绝越界条目。
- 已加入 PCL2 风格版本设置页：左侧为“个性化、配置、插件管理、导出”，配置保存到当前版本 `DSH_HOME\\.dsh-launcher\\version-settings.json`，支持全版本同步、独立、工作区同步和全量兜底，并提供独立的“所有版本自动同步模型”开关；插件页复用官方 Plugin CLI 的启停/删除能力，并保存自定义窗口标题与当前版本 Node.js 路径。
- 已加入版本设置个性化改名：可编辑当前版本名称并保存到实例注册文件，版本列表、启动页和版本设置标题会同步显示新名称；空名称和超过 80 个字符会被拒绝。
- 已扩展 `.dshpack`：导出为 ZIP 压缩包，保留可分享的版本设置、Provider 配置和精简 Plugin profile；API Key/Token/密码/环境变量值、sessions 和本机 Node.js 路径不会导出，导入时再次清理敏感内容。
- 已实现 `ConversationService` 与内嵌对话页面：按 DSh JSONL 会话目录列出有效和压缩日志，使用 `ZstdSharp.Port 0.8.8` 读取压缩会话首个 Zstandard frame 的 session header；支持 `.jsonl` / `.jsonl.zstd` 导入、导出、备份和删除并保留原始格式，校验 sessions 根、文件名和重解析点；打开会话通过 Chat 的 `localStorage` 预选 session ID，实例未运行或会话头部无效时拒绝打开。
- 已修复会话兼容性与导出边界：header 校验与当前 DSh `version=0`、`createdAt`、`delegationDepth` 及可选字段规则一致，字段类型异常的文件只会被标记为无效；导出保存名去除已有格式后缀，服务层同时归一化重复后缀，避免生成 `*.jsonl.zstd.jsonl.zstd`。
- 已补充用户操作回归保护：空实例入口、实例运行中修改、Skill/Preset 自包含目录复制、MCP serverName 注入、模型配置无关段落保留、API Key 不落盘、会话路径穿越、有效/损坏 Zstandard 会话和重复导入均有自测覆盖。
- 已添加 `DshLauncher.SelfTest` 控制台测试项目，覆盖注册往返、共享根目录版本、隔离 HOME、Source 检查、当前机器 DSh 检测、安装缺失环境保护、Source 直接启动保护、启动/健康检查/重复启动/跨 Runner 拒绝/停止/重启/接管，以及生态/模型/会话边界；测试项目与 Launcher 共用 `ZstdSharp.Port 0.8.8`。
- 当前功能分支为 `agent/harden-node-detection`，GitHub PR #1 当前为 OPEN/DRAFT，目标分支为 `main`；最近发布标签为 `v0.1.8`，源码与 Windows 产物提交均为 `71be839`。
- 0.1.5 已生成并核对 `publish\\release-0.1.5\\DSH Launcher.exe`；文件版本为 `0.1.5.0`，SHA-256 为 `52E673B8CFF57BC8CAAA43EEB2351129AF4E1D14D792D07047E6F65888A221C8`；同一文件已复制到 `DSH Launcher\\DSH Launcher.exe` 顶层位置，两个文件哈希一致。
- GitHub Release `v0.1.5` 已正式发布并核对为 1 个 Windows EXE；GitHub 资产名为 `DSH.Launcher.exe`，远端 digest 为 `sha256:52e673b8cff57bc8caaa43eeb2351129af4e1d14d792d07047e6f65888a221c8`；Release 地址为 `https://github.com/121103qwq/DSH-Launcher/releases/tag/v0.1.5`。
- 0.1.6 源码提交为 `64fcf64`，Windows 产物提交为 `9972091`；`publish\\release-0.1.6\\DSH Launcher.exe` 和顶层 `DSH Launcher\\DSH Launcher.exe` 文件版本均为 `0.1.6.0`，SHA-256 均为 `0A31896F353FAEF2572A7E281CDF528B511E2CADFCA6AE3464E54CEE9E0BA6FF`。
- GitHub Release `v0.1.6` 已正式发布并核对为 1 个 Windows EXE；资产名为 `DSH.Launcher.exe`，远端 digest 与本地 SHA-256 一致；Release 地址为 `https://github.com/121103qwq/DSH-Launcher/releases/tag/v0.1.6`。
- v0.1.7 源码提交为 `11b9bd3`，Windows 产物提交为 `0ce1282`；`publish\\release-0.1.7\\DSH Launcher.exe` 和顶层 `DSH Launcher\\DSH Launcher.exe` 文件版本均为 `0.1.7.0`，SHA-256 均为 `39907792C94CB9C2F61394213E3834930DF789DFC2961407BA7008653EDF7125`。
- GitHub Release `v0.1.7` 已正式发布并核对为 1 个 Windows EXE；资产名为 `DSH.Launcher.exe`，远端 digest 与本地 SHA-256 一致；Release 地址为 `https://github.com/121103qwq/DSH-Launcher/releases/tag/v0.1.7`。
- v0.1.8 源码与 Windows 产物提交均为 `71be839`；`publish\\release-0.1.8-20260815\\DSH Launcher.exe`、仓库顶层 `DSH Launcher\\DSH Launcher.exe` 和桌面复制文件版本均为 `0.1.8.0`，SHA-256 均为 `163F537C81212E292BE1B25D96A7B7EB9C0FB736F9E2B6754DAB42878E3F0329`。
- GitHub Release `v0.1.8` 已正式发布并核对为 1 个 Windows EXE；资产名为 `DSH.Launcher.exe`，远端 digest 为 `sha256:163f537c81212e292be1b25d96a7b7eb9c0fb736f9e2b6754dab42878e3f0329`；Release 地址为 `https://github.com/121103qwq/DSH-Launcher/releases/tag/v0.1.8`。
- Node 运行时现在从检测到的 DSh 或 Source `package.json` 读取 `engines.node`，以 `Missing`、`Compatible`、`Incompatible`、`Unknown` 表示状态；官方当前安装包未声明 `engines.node` 时不再使用固定的 22/24 版本规则。Source 构建、启动和 Plugin CLI 管理均复用同一 metadata 判断。
- 实例运行态现在区分 `Managed` 和 `Attached`：启动时记录 Launcher 管理态；Launcher 重启时仅对已有 loopback `WebUrl` 做健康探测，成功后恢复为 Attached；Attached 仍可打开 Chat，但 Runner 和 UI 都禁止 Stop/Restart/重复启动外部进程。
- 运行环境页现在提供 Node.js 官方下载页、Node.js npmmirror、DSh npm 官方源和 DSh npmmirror 四个入口；DSh 安装服务只接受这两个明确 registry，Launcher 本身仍不内置 Node.js 或 DSh。

## 当前主要相关文件

- `src/DshLauncher/App.xaml`：全局按钮、文本框和标签页的圆角视觉样式。
- `src/DshLauncher/MainWindow.xaml`、`src/DshLauncher/MainWindow.xaml.cs`：主窗口界面、PCL2 风格标题栏导航、启动布局、设置检测入口和右侧内嵌页面切换。
- `src/DshLauncher/ExtensionWindow.xaml(.cs)`、`ConversationWindow.xaml(.cs)`：扩展/Agent 和对话内嵌页面的标题去重与滚轮布局。
- `src/DshLauncher/ChatWindow.xaml`、`src/DshLauncher/ChatWindow.xaml.cs`：健康检查后的 WebView2 Chat 窗口；关闭 Chat 不停止 Launcher 或实例。
- `src/DshLauncher/Services/NodeRuntimeDetector.cs`：Node.js 运行环境检测与进程生命周期处理。
- `src/DshLauncher/Models/NodeRuntimeInfo.cs`：检测结果模型。
- `src/DshLauncher/Models/ManagerInstance.cs`、`DshRuntimeInfo.cs`、`SourceProjectInfo.cs`：实例、DSh 运行时和 Source 检查结果模型。
- `src/DshLauncher/Services/InstanceRegistry.cs`、`LauncherPaths.cs`：实例注册持久化和隔离目录。
- `src/DshLauncher/Services/DshRuntimeDetector.cs`、`SourceProjectInspector.cs`：DSh 检测和 Source 项目检查。
- `src/DshLauncher/Services/DshInstanceRunner.cs`：installed DSh 进程启动、端口分配、健康检查、停止/重启和实例互斥锁。
- `src/DshLauncher/Services/SourceBuildService.cs`、`src/DshLauncher/Models/SourceBuildResult.cs`：Source 依赖准备、构建命令执行、超时/取消清理和构建入口验证。
- `src/DshLauncher/Services/DshInstallService.cs`：使用检测到的 Node.js/npm 执行 DSh 全局安装/更新。
- `src/DshLauncher/Models/DshInstanceRunResult.cs`、`DshInstallResult.cs`：运行和安装结果模型。
- `src/DshLauncher/Services/ExtensionService.cs`、`ExtensionWindow.xaml(.cs)`：Plugin、Skill、MCP、Agent Preset、Workflow 列表和实例级变更；同一 UserControl 支持扩展与 Agent 两种筛选页。
- `src/DshLauncher/Models/MarketplaceModels.cs`、`src/DshLauncher/Services/MarketplaceService.cs`：插件市场候选、来源合并、搜索、npm/GitHub 元数据检查、自定义目录和安装前 profile 备份。
- `src/DshLauncher/VersionControlWindow.xaml(.cs)`、`VersionSettingsWindow.xaml(.cs)`、`src/DshLauncher/Services/VersionPackageService.cs`：PCL2 风格版本列表、复制/干净版本、整合包格式设置和安全导入。
- `src/DshLauncher/Models/VersionSettingsModels.cs`、`src/DshLauncher/Services/VersionSettingsService.cs`：版本级同步规则、窗口标题和 Node.js 路径存储与判断。
- `src/DshLauncher/Services/ModelService.cs`：DSh model settings 读写和凭据引用保护；模型设置页已从主窗口移除。
- `src/DshLauncher/Models/ProviderModels.cs`、`src/DshLauncher/Services/ProviderStateService.cs`、`src/DshLauncher/Services/ProviderDiagnosticService.cs`：启动页 Provider 卡片、实例级启用状态和只读诊断。
- `src/DshLauncher/Services/ConversationService.cs`、`ConversationWindow.xaml(.cs)`：内嵌 DSh session 文件页面、文件操作和 Chat 会话预选。
- `src/DshLauncher/Models/EcosystemModels.cs`：扩展、MCP、Provider 和会话记录模型。
- `tests/DshLauncher.SelfTest/Program.cs`：当前最小自测入口。
- `src/DshLauncher/DshLauncher.csproj`：.NET 8、win-x64、自包含单文件发布配置，并引用 `Microsoft.Web.WebView2 1.0.4078.44` 和 `ZstdSharp.Port 0.8.8`。
- `CURRENT_DESIGN.md`：当前有效设计约束。

## 已执行测试及结果

- `git diff --check`：通过。
- `dotnet build .\\src\\DshLauncher\\DshLauncher.csproj -c Release -r win-x64 --no-restore`：通过，0 warnings、0 errors。
- `dotnet build .\\tests\\DshLauncher.SelfTest\\DshLauncher.SelfTest.csproj -c Release --no-restore`：通过，0 warnings、0 errors。
- `dotnet run --project .\\tests\\DshLauncher.SelfTest\\DshLauncher.SelfTest.csproj -c Release --no-build`：通过，16/16；除既有生命周期覆盖外，新增扩展隔离、模型 settings 回环、会话文件管理、空/越界/运行中/压缩日志等用户操作边界均通过。
- `dotnet restore .\\src\\DshLauncher\\DshLauncher.csproj`、`dotnet restore .\\tests\\DshLauncher.SelfTest\\DshLauncher.SelfTest.csproj`：通过；已恢复 `ZstdSharp.Port 0.8.8`。
- `dotnet build .\\src\\DshLauncher\\DshLauncher.csproj -c Release -r win-x64 --no-restore`：通过，0 warnings、0 errors；本次变更后再次通过。
- `dotnet build .\\tests\\DshLauncher.SelfTest\\DshLauncher.SelfTest.csproj -c Release --no-restore`：通过，0 warnings、0 errors；本次变更后再次通过。
- `dotnet run --project .\\tests\\DshLauncher.SelfTest\\DshLauncher.SelfTest.csproj -c Release --no-build`：通过，16/16；包含两帧拼接 Zstandard 会话读取、损坏压缩文件识别、压缩导入/导出/备份格式保留。
- 本次版本与市场改动后：`dotnet build src/DshLauncher/DshLauncher.csproj --configuration Release --no-restore` 通过，0 warnings、0 errors；`dotnet run --project tests/DshLauncher.SelfTest/DshLauncher.SelfTest.csproj --configuration Release --no-restore` 通过，21/21，覆盖市场缓存/发布时间/Star 排序、共享根目录版本、复制/干净版本和 `.dshpack` 导入。
- 本次版本设置改动后：Launcher Release 构建和 SelfTest Release 构建均通过，0 warnings、0 errors；SelfTest 为 21/21，新增覆盖版本同步规则、Provider/API Key 清理、sessions 排除、精简 Plugin 配置导出和导入回环。
- 本次滚轮与扩展布局改动后：Launcher Release 构建和 SelfTest Release 构建均通过，0 warnings、0 errors；SelfTest 为 21/21。自包含发布目录 `publish\\ui-scroll-extension-20260815` 已复制到桌面同名目录，EXE 版本为 `0.1.7.0`，源文件与桌面副本 SHA-256 均为 `1499544918CB8C8648B2CD179869AF8CE67D2A935C2F549DC6FFB9DAE859ACC2`。
- 本次移除模型页并调整全版本模型同步后：Launcher Release 构建和 SelfTest Release 构建均通过，0 warnings、0 errors；SelfTest 为 21/21。自包含发布目录 `publish\\model-page-removed-20260815` 已复制到桌面同名目录，EXE 版本为 `0.1.7.0`，源文件与桌面副本 SHA-256 均为 `18F38095459F34A1B302257D43808BD6B9B8580F03399538AFBD8BE79CADB1A6`。
- 本次页面放宽改动后：Launcher Release 构建和 SelfTest Release 构建均通过，0 warnings、0 errors；SelfTest 为 21/21。自包含发布目录 `publish\\spacious-ui-20260815` 已复制到桌面同名目录，EXE 版本为 `0.1.7.0`，源文件与桌面副本 SHA-256 均为 `C611D0EF2C2CB2D3ED9A6F9B76A8F8E7EDC3BC1FFE950B79F4958249CFD3A44F`。
- 本次收紧窗口边距、扩大边缘拖动命中区并修复 Agent 全宽布局后：Launcher Release 构建和 SelfTest Release 构建均通过，0 warnings、0 errors；SelfTest 为 21/21。自包含发布目录 `publish\\compact-agent-edge-20260815` 已复制到桌面同名目录，EXE 版本为 `0.1.7.0`，源文件与桌面副本 SHA-256 均为 `82A6593D10D8793E076FB8431CD39A12EE25D4C760BDBC7C5657F0B2FADC9226`。
- 本次运行实例双击打开和实例名设置标题改动后：Launcher Release 构建和 SelfTest Release 构建均通过，0 warnings、0 errors；SelfTest 为 21/21。自包含发布目录 `publish\\running-instance-open-20260815` 已复制到桌面同名目录，EXE 版本为 `0.1.7.0`，源文件与桌面副本 SHA-256 均为 `6AF46DB4E3BFF08179C89A14BF6A83A63B2D51BDBC1BE9364584C9EE210DFE46`。
- 本次版本控制双击进入设置改动后：Launcher Release 构建和 SelfTest Release 构建均通过，0 warnings、0 errors；SelfTest 为 21/21。自包含发布目录 `publish\\version-control-double-click-20260815` 已复制到桌面同名目录，EXE 版本为 `0.1.7.0`，源文件与桌面副本 SHA-256 均为 `64038C12DEB9424D8D32BC042B28CA5192112438EA82A4CA360DD2E49EE89E1F`。
- 本次应用图标改动后：Launcher Release 构建和 SelfTest Release 构建均通过，0 warnings、0 errors；SelfTest 为 21/21；已从发布 EXE 提取并确认图标。自包含发布目录 `publish\\app-icon-20260815` 已复制到桌面同名目录，EXE 版本为 `0.1.7.0`，源文件与桌面副本 SHA-256 均为 `B8AA321C57DE86C15A4EBFED9C92EC6729A7D179747FBC6CFE3C0D5B59F3E974`。
- 本次版本设置个性化改名后：Launcher Release 构建和 SelfTest Release 构建均通过，0 warnings、0 errors；SelfTest 为 22/22，新增实例改名持久化覆盖。自包含发布目录 `publish\\version-name-edit-20260815` 已复制到桌面同名目录，EXE 版本为 `0.1.7.0`，源文件与桌面副本 SHA-256 均为 `C9A9BD6499AFE025D21B68FE1BA1DDB5F2071AEDA2E699E7187835560E7C54F2`。
- 本次 Plugin pnpm 环境修复后：Launcher Release 构建和 SelfTest Release 构建均通过，0 warnings、0 errors；SelfTest 为 23/23，新增覆盖 PATH 缺少 pnpm 但 DSh 同目录存在 Corepack 的 Plugin CLI 场景。自包含发布目录 `publish\\plugin-pnpm-runtime-20260815` 已复制到桌面同名目录，EXE 版本为 `0.1.7.0`，源文件与桌面副本 SHA-256 均为 `24F6696653292CB4522691D19A182B12DBFC31A0180D2A6C8B2D655FCEF3ABF7`。
- 本次 Chat 独立窗口、多实例和版本复制交互改动后：Launcher Release 构建和 SelfTest Release 构建均通过，0 warnings、0 errors；SelfTest 为 23/23，包含两个不同 DSH_HOME 同时运行、不同端口和独立停止覆盖。自包含发布目录 `publish\\multi-instance-chat-20260815` 已复制到桌面同名目录，EXE 版本为 `0.1.7.0`，源文件与桌面副本 SHA-256 均为 `D90F465EC8AFCDE30408453DB9A5BD28A8EDC8BB2409EA57D20CDA1816B4FF58`。
- 本次修复后的同一自测再次通过：`dotnet build` 源码和测试项目均为 0 warnings、0 errors，`dotnet run ... --no-build` 为 16/16；额外覆盖重复压缩后缀归一化和字段类型异常 header 容错。
- 已用自包含临时发布版进行 Computer Use 实测：有效 `.jsonl.zstd` 在对话右侧页面显示 `ui-zstd-test` 和 `C:\\work\\ui-zstd`，打开时因测试实例保持停止而显示预期“实例没有运行”提示；损坏压缩文件被标记为无效，打开时显示 Zstandard header 错误提示。临时窗口已关闭，测试夹具已移出实例目录。
- `dotnet publish .\\src\\DshLauncher\\DshLauncher.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\\publish\\release-0.1.5`：通过；文件版本为 `0.1.5.0`，SHA-256 已核对。
- 0.1.5 最终发布版已用 Computer Use 实测：启动页显示 Core 0.1.5，扩展、Agent、对话均在主窗口右侧切换；对话页显示 `session.jsonl / .zstd` 文案且未出现管理弹窗，测试进程随后已关闭。
- 本次修复使用当前源码 Release 编译输出做 Computer Use 回归：真实 DSh 创建的有效 `.jsonl.zstd` 会话在 Launcher 对话页显示为有效并可打开；打开后 Chat 的 `deepseek` 工作区和 `新会话` 条目处于选中状态，Launcher 显示 `已打开对话`；停止实例后导出生成单一 `.jsonl.zstd`，未生成重复后缀文件。回归用 Chat、实例和 Launcher 已关闭，导出文件与测试会话已移出仓库和实例目录至 `%TEMP%` 保留。
- v0.1.6 发布构建已完成：Windows x64 自包含单文件版本为 `0.1.6.0`，本地发布目录与顶层 EXE 哈希一致；GitHub 标签解引用指向 `9972091`，Release 资产上传状态为 `uploaded`，远端 digest 与本地 SHA-256 一致。
- 本次 runtime 改进自测已完成：`DshLauncher.SelfTest` 为 `18/18`，新增 Node engine range、安装源白名单和 Attached 外部生命周期保护覆盖；Launcher Release 构建为 `0 warnings / 0 errors`。
- 本次启动页 UI 调整后：`dotnet build .\\tests\\DshLauncher.SelfTest\\DshLauncher.SelfTest.csproj -c Release --no-restore` 通过，0 warnings、0 errors；`dotnet run --project .\\tests\\DshLauncher.SelfTest\\DshLauncher.SelfTest.csproj -c Release --no-build` 通过，19/19。
- 本次插件市场实现后：`dotnet build src/DshLauncher/DshLauncher.csproj -c Debug --no-restore` 通过，0 warnings、0 errors；`dotnet run --project tests/DshLauncher.SelfTest/DshLauncher.SelfTest.csproj -c Debug --no-restore` 通过，20/20；新增测试覆盖目录合并、关键词过滤、待检查状态、有效/无效 package.json 和安装前备份。
- 本次实际 UI 回归使用独立输出版：插件市场页面成功显示，实时目录显示 623 个候选、3 个来源，搜索框可输入 `Aegis`；未对用户实例执行真实安装、更新或卸载。临时测试进程已关闭，用户原有桌面 Launcher 和 DeepSeek Desktop 未停止。
- 本次 Computer Use 回归确认顶部导航居中、Provider 默认展开、Provider 绿/红切换可恢复、问题问号弹窗和品牌按钮返回启动页；未启动或停止用户原有 DeepSeek Desktop/DSh 进程。窗口边缘缩放代码已启用 `ResizeMode=CanResize`，但工具右下角坐标被其它窗口覆盖，实际拖拽尚未完成确认。
- 本次 UI 调整后使用当前 Release 编译输出完成 Computer Use 回归：启动页显示居中的“启动”入口且不显示运行环境卡片；点击左上角 DSH 可返回启动页；扩展、Agent、对话页无重复主标题；文本框、标签页、卡片和按钮显示为圆角。测试窗口未启动或停止用户原有 DeepSeek Desktop/DSh 进程。
- 分支推送 dry-run 和实际推送均通过；远端 `v0.1.5` tag 指向发布产物提交 `4ec7057`，Release 资产数量为 1，资产哈希与本地发布文件一致。
- 临时 UI 回归后确认用户原有 `main\\DSH Launcher\\DSH Launcher.exe` 与 DeepSeek Desktop DSh 进程仍在，未启动或停止用户既有 DSh 实例。

## 已知问题

- 当前自动化测试尚未覆盖 Node.js 检测的超时/取消、DSh 检测超时、Source 异常 `package.json`、DSh 安装命令的真实联网执行和所有 UI 错误提示边界。
- 当前 UI 自测已覆盖启动入口、品牌返回、扩展/Agent/对话页面切换；不同窗口尺寸下的整体排版、窗口边缘拖动/缩放仍需单独回归。
- 本次版本设置 UI 未进行 Computer Use 点测；当前仓库规则要求只有用户明确要求时才能使用 Computer Use。本次已通过 XAML/C# Release 构建和对应服务自测，真实窗口点击仍是后续验收项。
- 本次移除模型页的导航和页面文件未进行 Computer Use 点测；已通过 XAML/C# Release 构建、模型同步规则自测和静态残留检查，后续实机验收需确认顶部导航和启动页 Provider 展示符合预期。
- 本次窗口边缘和 Agent 布局改动未进行 Computer Use 点测；已通过 XAML/C# Release 构建，后续实机验收需重点检查 1080px 最小窗口宽度下的边缘拖动、Agent 全宽布局和扩展双栏。
- 本次运行实例双击和设置标题改动未进行 Computer Use 点测；已通过 XAML/C# Release 构建，后续实机验收需确认双击后能聚焦/重开正确实例的 Chat 窗口。
- 本次版本控制双击改动未进行 Computer Use 点测；已通过 XAML/C# Release 构建，后续实机验收需确认双击版本后进入的设置标题与所选版本一致。
- 本次滚轮与扩展左右布局改动尚未进行 Computer Use 点测；已通过 XAML/C# Release 构建，后续实机验收需重点检查嵌套列表滚轮和不同窗口高度下的左右布局。
- Provider 诊断当前只调用只读模型列表接口，不发送聊天请求；无配置 Provider 会显示问号。窗口可调整大小的配置已存在，但仍需在可用屏幕坐标下补做一次右下角拖拽验证。
- 本次未对真实网络 Plugin 执行安装/更新/删除，避免修改用户实例；服务和临时 UI 页面已验证。Chat 仍按现有设计使用独立 WebView2 窗口；打开合法会话仍要求实例运行并有可用 Chat 地址，这是当前生命周期约束。
- Attached 当前只恢复注册记录中已有且通过 loopback 健康检查的 Web 端点，不扫描任意端口，也不自动认领没有已知 URL 的外部 DSh 服务；这是为了避免误接管或误识别其它本地 HTTP 服务。
- 当前官方 `@deepseek-ai/dsh` 安装包的 `package.json` 未声明 `engines.node`，因此 installed DSh 页面只能显示“未声明”而不能凭空判断 Node 20 一定不兼容；Source 项目若声明 engine 则按其自身 metadata 判断。
- 当前分支仍是 Draft PR，尚未合并到 `main`；这不影响已发布的 `v0.1.7` Release。

## 尚未完成内容

- v0.1.8 构建、推送、标签和 GitHub Release 已完成；当前未完成项仍包括真实网络 Plugin 的完整安装回滚、试启动冲突检查、Session deep link、MCP Manager 状态桥接和 Theme/Wallpaper 资源。`.dshpack` 的最小导入格式已实现，尚未实现市场资源导出或更复杂的整合包内容。

## 已尝试但已放弃的方案

- 曾将扩展、对话作为独立管理窗口打开；该方案已放弃，当前统一改为主窗口右侧内嵌页面，只有 Chat WebView2 保持独立窗口；模型配置页则已移除。
- 曾用旧版 `version=1` 且缺少 `delegationDepth` 的临时 header 验证打开流程；该格式不被当前 DSh 识别，已放弃，回归改用真实 DSh UI 创建的合法会话。
- 曾在 Node 模型中硬编码 `22.19+ 的 22.x 或 24+`；当前改为读取 DSh/Source package metadata，metadata 缺失时保留“未声明/Unknown”语义。
- 插件市场没有采用“看到 GitHub 标签就直接安装”或“CLI 返回 0 就算成功”的方案；当前先读取 package.json，并保留停止实例、备份和重启提示。

## 下一步最直接的任务

- 下一步最直接的任务是按用户允许的方式做版本设置页实际 UI 验收，重点检查四项左侧切换、全版本同步后的灰色禁用状态、Plugin 行操作和导出文件选择；真实网络 Plugin 安装/删除仍需单独验证。
