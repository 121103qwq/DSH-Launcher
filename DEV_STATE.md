# DSH Launcher 开发状态

## 当前目标

收口长期分支 `agent/harden-node-detection`：把 `origin/main`（早期 README 提交）合并进当前分支，更新 README 与维护文档，验证完整 Launcher，并将 PR #1 合并到 `main`。本轮不开发新功能。

## 已完成内容

- .NET 8 WPF Launcher，目标 Windows x64；发布版为自包含单文件，不内置 Node.js、npm、pnpm 或 DSh。
- 主窗口采用 PCL2 参考的信息层级：启动、扩展、Agent、对话和设置在主窗口内切换；Chat WebView2 为无 Owner 的独立窗口，使用黑色 DeepSeek 图标。
- 支持 installed/source DSh 实例、独立端口、健康检查、停止/重启、跨 Runner 互斥锁，以及 `Managed` / `Attached` 运行态。Attached 实例可打开 Web UI，但不会被 Stop/Restart 或退出清理误杀。
- 每个版本使用独立 `DSH_HOME` 和 `DSH_AGENTS_HOME`。实例级 Plugin、Skill、MCP、Provider 状态、Agent Preset、Settings 和 Conversation 文件均以该目录为边界；对话和模型 Provider 支持 Independent、Workspace、All/全配置同步策略。运行中的版本不会被同步写入。
- Plugin 通过官方 DSh CLI 安装、更新、删除和启停；市场支持缓存优先、本地即时搜索、分类、来源、发布时间/Star 排序、多来源 identity 合并、GitHub monorepo 校验、安装前 package.json/bundle 检查、进度反馈和完成后刷新。
- 版本控制支持复制版本、新建干净版本、删除版本、双击进入版本设置和 `.dshpack` 导入/导出。整合包会清理 API Key、Token、密码、环境变量值和 sessions；导入始终创建新版本。版本设置页不能修改版本名称，名称只由版本控制维护。
- Provider 启动页支持启用/禁用、`/models` 只读诊断、模型列表/思考档位显示和问题说明；模型设置只保存环境变量名，不保存 API Key 明文。
- 对话页支持 JSONL/Zstandard 会话列出、导入、导出、备份、删除、双击打开和停止实例自动启动；当前使用 Chat `localStorage` 预选 session ID。
- `v0.1.9` 已构建、推送并发布；Release 资产和本地发布文件的 SHA-256 已核对一致。
- 本轮把 `origin/main`（`d5cfa78`，早期 README 更新）合并进 `agent/harden-node-detection`：仅 README.md 一处冲突，已按 v0.1.9 真实状态重写并解决；其余文件自动合并。

## 已暂存（未并入本轮 PR，保留在 stash@{0}）

- 对话列表显示会话名称（读取 DSh 投影缓存标题，不再显示原始 session ID）。
- 扩展/插件市场页卡顿修复（后台扫描 + 列表虚拟化 + 打开页面不再每次联网）。
- 运行环境一键准备（NodeInstallService 下载/安装 Node、一键准备按钮、启动前环境拦截、检测器补 Program Files 路径）。

## 当前主要相关文件

- `src/DshLauncher/MainWindow.xaml(.cs)`：主窗口导航、启动页、Provider、实例生命周期和内嵌页面切换。
- `src/DshLauncher/Services/DshInstanceRunner.cs`、`NodeRuntimeDetector.cs`、`DshRuntimeDetector.cs`：DSh/Node 检测和 Managed/Attached 生命周期。
- `src/DshLauncher/Services/ExtensionService.cs`、`ExtensionWindow.xaml(.cs)`：Plugin、Skill、MCP、Agent Preset 和市场入口。
- `src/DshLauncher/Services/MarketplaceService.cs`、`Models/MarketplaceModels.cs`：市场缓存、来源合并、搜索、排序、校验和安装状态。
- `src/DshLauncher/Services/VersionPackageService.cs`、`VersionControlWindow.xaml(.cs)`、`VersionSettingsWindow.xaml(.cs)`：版本复制、删除、设置和 `.dshpack`。
- `src/DshLauncher/Services/ModelService.cs`、`ModelProviderSyncService.cs`、`ProviderStateService.cs`：Provider 配置、同步和启用状态。
- `src/DshLauncher/Services/ConversationService.cs`、`ConversationSyncService.cs`、`ConversationWindow.xaml(.cs)`：会话文件管理、打开入口和同步策略。
- `src/DshLauncher/Services/DshMarketThemeService.cs`：dsh-market loopback 主题状态和应用桥接；不修改 DSh Web UI。
- `tests/DshLauncher.SelfTest/Program.cs`：当前最小自测入口。
- `CURRENT_DESIGN.md`：当前有效设计约束。

## 已执行测试及结果

- 当前 Git：分支 `agent/harden-node-detection`，已合并 `origin/main`；合并提交尚未创建。
- 合并后 `dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release`：26/26 通过。
- 合并后 Release 单文件自包含发布：0 errors、0 warnings；`DSH Launcher.exe` 的 SHA-256 为 `7220D23C8DA300AFBC22AC201CF13AA34DE2FBC2B0B5F8782F33A3BA047E645B`。
- `git diff --check`：通过。
- GitHub：PR #1 待更新标题/描述并从 Draft 转为 Ready；随后合并到 `main`。

## 已知问题

- Source 项目的 `.dsh/skills` 和 `.agents/skills` 是 DSh 项目级共享只读资源；实例 `DSH_HOME` 内的 Skill 才是版本私有。
- Provider 自动同步按停止版本中 `settings.yaml` 的最后写入时间选择来源，没有三方合并；运行中的版本会等停止后参与同步。
- 对话同步是生命周期边界同步，不是多个运行中 DSh 的实时共享写入；外部程序直接删除会话文件不会触发 Launcher 的删除传播。
- 当前只使用已验证的 Chat `localStorage` 预选会话，尚未实现官方 `?session=<id>` deep link。
- MCP 当前是实例级 stdio/streamable-http 配置和 patch 管理，尚未接入完整 MCP Manager 的 connected/needs-auth/authorizing/error、OAuth 和 Tool discovery 状态。
- 主题当前是市场资源、文字信息预览和 dsh-market 应用桥接，尚未建立视觉预览图或 Wallpaper 资源格式，也未在用户真实实例上做主题视觉验收。
- GitHub Topic 发现仍未做分页加载；真实 Plugin CLI 失败回滚和启动冲突检查仍缺少更多异常边界覆盖。
- 官方已安装 DSh 的 package metadata 当前未声明 `engines.node` 时，Node 兼容性只能显示“未声明/Unknown”，不会凭空套用固定版本限制。
- `src/DshLauncher/artifacts/` 是未跟踪的本地构建产物目录，未纳入源码提交和 Release。

## 尚未完成内容

- 官方 Session deep link。
- 完整 MCP Manager 状态/认证/工具发现整合。
- 主题视觉预览、Wallpaper 资源格式和用户真实实例视觉验收。
- GitHub Topic 分页、更多 Plugin 失败回滚异常和启动冲突检查。
- 多个运行中 DSh 的实时会话共享写入。
- 对话名称显示、扩展页卡顿修复与运行环境一键准备（已 stash 保留，留待后续分支）。

## 已尝试但已放弃的方案

- 不再把扩展、Agent 和对话作为管理弹窗；它们统一在主窗口右侧切换，只有 Chat WebView2 保持独立窗口。
- 不再把 GitHub topic 候选直接视为可安装 Plugin；现在必须先读取 package.json、检查 DSh bundle 和入口，再调用官方 CLI。
- 不再硬编码 Node.js 版本兼容规则；现在优先读取 DSh 或 Source 的 package metadata，缺失时保留 Unknown/未声明状态。

## 下一步最直接的任务

合并 PR #1 到 `main` 并验证 `main`；随后从最新 `main` 新建 `feature/runtime-bootstrap`，恢复 `stash@{0}` 继续运行环境一键准备。