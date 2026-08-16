# DSH Launcher 开发状态

## 当前目标

处理 PR #2 第四轮 Codex Review（1 条 P1 + 5 条 P2，全部确认为真实问题并已修复）：MSI 提权前验证 Authenticode 签名链与 Node.js 官方发布者、形状错误的版本索引走固定版本兜底、msiexec 超时后延迟清理安装包、标题缓存损坏不中断会话列表、package 目录失效也进入一键修复并自愈重绑定、DSh 装后按最新 engines.node 复查 Node 兼容性；完成后重新触发 Review。

## 已完成内容

- .NET 8 WPF Launcher，目标 Windows x64；发布版为自包含单文件，不内置 Node.js、npm、pnpm 或 DSh。
- 主窗口采用 PCL2 参考的信息层级：启动、扩展、Agent、对话和设置在主窗口内切换；Chat WebView2 为无 Owner 的独立窗口，使用黑色 DeepSeek 图标。
- 支持 installed/source DSh 实例、独立端口、健康检查、停止/重启、跨 Runner 互斥锁，以及 `Managed` / `Attached` 运行态。Attached 实例可打开 Web UI，但不会被 Stop/Restart 或退出清理误杀。
- 每个版本使用独立 `DSH_HOME` 和 `DSH_AGENTS_HOME`。实例级 Plugin、Skill、MCP、Provider 状态、Agent Preset、Settings 和 Conversation 文件均以该目录为边界；对话和模型 Provider 支持 Independent、Workspace、All/全配置同步策略。运行中的版本不会被同步写入。
- Plugin 通过官方 DSh CLI 安装、更新、删除和启停；市场支持缓存优先、本地即时搜索、分类、来源、发布时间/Star 排序、多来源 identity 合并、GitHub monorepo 校验、安装前 package.json/bundle 检查、进度反馈和完成后刷新。
- 版本控制支持复制版本、新建干净版本、删除版本、双击进入版本设置和 `.dshpack` 导入/导出。整合包会清理 API Key、Token、密码、环境变量值和 sessions；导入始终创建新版本。版本设置页不能修改版本名称，名称只由版本控制维护。
- Provider 启动页支持启用/禁用、`/models` 只读诊断、模型列表/思考档位显示和问题说明；模型设置只保存环境变量名，不保存 API Key 明文。
- `v0.1.9` 已构建、推送并发布；PR #1 已合并到 `main`（merge commit `1eb5f65`）。
- 对话页支持 JSONL/Zstandard 会话列出、导入、导出、备份、删除、双击打开和停止实例自动启动；当前使用 Chat `localStorage` 预选 session ID。对话列表优先显示会话名称：读取 DSh `storages/session_projcache.json` 的标题，无标题时回退为“未命名 · 项目 · 时间”，不再把原始 session ID 作为首列。
- 扩展页卡顿修复：已安装插件扫描和插件市场安装状态/主题扫描移到后台线程；已安装列表与市场列表启用虚拟化（Recycling）。打开“扩展”页只读取本地市场缓存，不再每次联网刷新目录；仅在首次无缓存或用户点击“刷新目录”时才联网，避免每次切换页面等待 GitHub/社区目录超时。
- 一键运行环境准备：设置/诊断页显示 Node/DSh 状态，缺失时提供“准备运行环境（官方源 / 国内镜像）”按钮。Node 缺失时通过 NodeInstallService 下载 Windows x64 Node.js 官方 MSI 并显示真实字节/百分比进度，经系统授权（msiexec /qn）安装后重新运行 NodeRuntimeDetector，无需重启 Launcher；Node 就绪但 DSh 缺失时复用 DshInstallService 通过 npm 安装 `@deepseek-ai/dsh`（不确定进度），安装后重新运行 DshRuntimeDetector。Node 版本不兼容时只提示安装兼容版本，不自动卸载。启动实例时若运行环境缺失会弹出缺失项并询问是否准备，准备成功后继续原启动流程。Launcher 启动时不静默下载或安装任何内容。Node 检测覆盖 `<ProgramFiles>\nodejs`（官方 MSI 默认位置）。
- runtime bootstrap 边界修复：Node 下载阶段可取消并清理 `.part` 临时文件，关闭进度窗口等价于取消下载；MSI 安装开始后禁用取消按钮、阻止通过窗口 X 关闭进度窗口并阻止主窗口关闭（流程结束恢复后自动解除）、不强制终止 Windows Installer，安装结束后删除下载的 MSI，用户取消与真实 10 分钟超时用独立结果状态区分。DSh 重新安装并检测成功后，绑定失效的 Installed 实例经 `InstanceRuntimeRebinder` 重绑定到重新检测到的 package root / executable / version，保留实例 Id 与 DSH_HOME、不创建新实例、不修改 Source 实例；运行中或 Attached 实例不参与重绑定。Node 兼容判断以 metadata 为准：Installed 实例优先读取自身 package root 的 `engines.node`，有效但未声明时保持未声明，仅当其 runtime 失效且重装/重绑定时才使用重新检测到的 DSh metadata；Source 实例只读取自身项目 metadata，未声明时保持未声明，不继承全局 installed DSh 的版本要求；未选择实例的诊断场景使用全局 DSh engine。手动安装提示按实际 `engines.node` 要求给出；对话触发的自动启动同样先经过 runtime 准备；准备期间目标实例被删除则中止启动；会话标题缓存路径包含重解析点组件时拒绝读取，缓存结构损坏（意外值类型）时按无标题处理、不中断会话列表。MSI 提权安装前验证 Authenticode 签名链与 Node.js 官方发布者（OpenJS/Node.js Foundation/Joyent），验证失败不执行安装；msiexec 超时后仍可能在后台运行，MSI 清理推迟到进程真正退出；版本索引返回形状错误的合法 JSON 时走固定版本兜底。实例 package 运行目录已删除但入口 shim 仍在时同样视为缺失并进入一键修复，准备完成时自动重绑定自愈；DSh 安装/更新后按最新 engines.node 复查 Node 兼容性，不兼容时报失败且不自动卸载，设置页就绪判定包含该兼容性。MSI 安装后把新检测到的 Node 目录补入当前进程 PATH，DSh 检测/启动可解析 node；设置/诊断页准备按钮面向全局运行环境（不传实例目标），Node 检测进行中禁止启动与一键准备，后台检测结束后自动恢复准备按钮；版本索引不可用且固定兜底版本不满足目标 engine 时停止安装并提示，不装出与 engine 不兼容的 Node；每次安装调用使用唯一 MSI 文件名避免并发干扰。

## 当前主要相关文件

- `src/DshLauncher/MainWindow.xaml(.cs)`：主窗口导航、启动页、Provider、实例生命周期、运行环境准备与内嵌页面切换。
- `src/DshLauncher/Services/NodeInstallService.cs`、`RuntimeProgressWindow.cs`：Node 下载（真实进度）、系统安装与准备进度窗口。
- `src/DshLauncher/Services/DshInstallService.cs`、`DshRuntimeDetector.cs`、`NodeRuntimeDetector.cs`：DSh/Node 检测与 npm 安装。
- `src/DshLauncher/Services/ExtensionService.cs`、`ExtensionWindow.xaml(.cs)`：Plugin、Skill、MCP、Agent Preset 和市场入口。
- `src/DshLauncher/Services/MarketplaceService.cs`、`Models/MarketplaceModels.cs`：市场缓存、来源合并、搜索、排序、校验和安装状态。
- `src/DshLauncher/Services/VersionPackageService.cs`、`VersionControlWindow.xaml(.cs)`、`VersionSettingsWindow.xaml(.cs)`：版本复制、删除、设置和 `.dshpack`。
- `src/DshLauncher/Services/ModelService.cs`、`ModelProviderSyncService.cs`、`ProviderStateService.cs`：Provider 配置、同步和启用状态。
- `src/DshLauncher/Services/ConversationService.cs`、`ConversationSyncService.cs`、`ConversationWindow.xaml(.cs)`：会话文件管理、打开入口和同步策略。
- `tests/DshLauncher.SelfTest/Program.cs`：当前最小自测入口。
- `CURRENT_DESIGN.md`：当前有效设计约束。

## 已执行测试及结果

- 当前 Git：分支 `feature/runtime-bootstrap`，基于 `main`（含 PR #1 合并 `1eb5f65`）；最新提交为第四轮 Review 修复提交（哈希见 git log），已推送 origin。
- `dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release`：38/38 通过（第四轮新增：形状错误索引兜底、MSI Authenticode 验证与发布者白名单、msiexec 存活时延迟清理安装包、标题缓存损坏不中断列表、package 目录失效的启动就绪判定、全局环境就绪兼容性判定）。
- `dotnet build src\DshLauncher\DshLauncher.csproj -c Debug`：0 warnings、0 errors。
- Release 单文件自包含 publish：0 errors、0 warnings；`DSH Launcher.exe` 的 SHA-256 为 `CAE616051263664AEBD7A09E479C94AE0A1DA753C6BDD8E76CF8508B082A5FD0`，已复制到桌面 `runtime-bootstrap-review-fix5-20260816`；启动冒烟验证通过（进程正常驻留，测试后终止）。
- `git diff --check`：通过。

## 已知问题

- Source 项目的 `.dsh/skills` 和 `.agents/skills` 是 DSh 项目级共享只读资源；实例 `DSH_HOME` 内的 Skill 才是版本私有。
- Provider 自动同步按停止版本中 `settings.yaml` 的最后写入时间选择来源，没有三方合并；运行中的版本会等停止后参与同步。
- 对话同步是生命周期边界同步，不是多个运行中 DSh 的实时共享写入；外部程序直接删除会话文件不会触发 Launcher 的删除传播。
- 当前只使用已验证的 Chat `localStorage` 预选会话，尚未实现官方 `?session=<id>` deep link。
- MCP 当前是实例级 stdio/streamable-http 配置和 patch 管理，尚未接入完整 MCP Manager 的 connected/needs-auth/authorizing/error、OAuth 和 Tool discovery 状态。
- 主题当前是市场资源、文字信息预览和 dsh-market 应用桥接，尚未建立视觉预览图或 Wallpaper 资源格式，也未在用户真实实例上做主题视觉验收。
- GitHub Topic 发现仍未做分页加载；真实 Plugin CLI 失败回滚和启动冲突检查仍缺少更多异常边界覆盖。
- 官方已安装 DSh 的 package metadata 当前未声明 `engines.node` 时，Node 兼容性只能显示“未声明/Unknown”，不会凭空套用固定版本限制。
- 一键运行环境准备的端到端链路（真实无 Node 机器上：下载、UAC 授权、msiexec 安装、自动重检测）仍需实机人工验证。
- `src/DshLauncher/artifacts/` 是未跟踪的本地构建产物目录，未纳入源码提交和 Release。

## 尚未完成内容

- 一键运行环境准备的实机端到端验证与后续打磨。
- 官方 Session deep link。
- 完整 MCP Manager 状态/认证/工具发现整合。
- 主题视觉预览、Wallpaper 资源格式和用户真实实例视觉验收。
- GitHub Topic 分页、更多 Plugin 失败回滚异常和启动冲突检查。
- 多个运行中 DSh 的实时会话共享写入。

## 已尝试但已放弃的方案

- 不再把扩展、Agent 和对话作为管理弹窗；它们统一在主窗口右侧切换，只有 Chat WebView2 保持独立窗口。
- 不再把 GitHub topic 候选直接视为可安装 Plugin；现在必须先读取 package.json、检查 DSh bundle 和入口，再调用官方 CLI。
- 不再硬编码 Node.js 版本兼容规则；现在优先读取 DSh 或 Source 的 package metadata，缺失时保留 Unknown/未声明状态。
- 一键运行环境准备不建立 RuntimeManager/InstallerProvider 等大型抽象框架；仅新增 NodeInstallService 并复用 DshInstallService。

## 下一步最直接的任务

接手点：第四轮 Review 的 6 条发现（1 P1 + 5 P2）已全部修复并推送，6 条线程已回复并 resolve，第五轮 `@codex review` 已重新触发。下一步：等待第五轮结果；干净后仍保留真实无 Node Windows 的 Installed/Source 实机人工验收，验收通过才合并 main。