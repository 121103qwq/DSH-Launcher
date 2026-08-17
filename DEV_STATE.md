# DSH Launcher 开发状态

## 当前目标

当前工作区位于 `main`，源码版本为 `v0.2.4`。本轮已修复“终端手动 `dsh` 可用，但 Launcher 因父进程 PATH 过期而无法识别或启动”的问题；真实手动 DSh 已完成检测、临时注册、独立 `DSH_HOME` 启动和停止验证。

## 已完成内容

- .NET 8 WPF Launcher，目标 Windows x64；发布版为自包含单文件，不内置 Node.js、npm、pnpm 或 DSh。
- 主窗口采用 PCL2 参考的信息层级：启动、扩展、Agent、对话和设置在主窗口内切换；Chat WebView2 为无 Owner 的独立窗口，使用黑色 DeepSeek 图标。
- 支持 installed/source DSh 实例、独立端口、健康检查、停止/重启、跨 Runner 互斥锁，以及 `Managed` / `Attached` 运行态。Attached 实例可打开 Web UI，但不会被 Stop/Restart 或退出清理误杀。
- 每个版本使用独立 `DSH_HOME` 和 `DSH_AGENTS_HOME`。实例级 Plugin、Skill、MCP、Provider 状态、Agent Preset、Settings 和 Conversation 文件均以该目录为边界；对话和模型 Provider 支持 Independent、Workspace、All/全配置同步策略。运行中的版本不会被同步写入。
- 设置/诊断页可选择任意版本并编辑与“版本设置 → 配置”相同的同步选项；工作区管理支持显示成员、添加、重命名和删除。删除工作区只解除成员版本的同步关系并切回 Independent，不删除版本或对话文件。
- Plugin 通过官方 DSh CLI 安装、更新、删除和启停；市场支持缓存优先、本地即时搜索、分类、来源、发布时间/Star 排序、多来源 identity 合并、GitHub monorepo 校验、安装前 package.json/bundle 检查、独立安装弹窗和完成后刷新。市场标题可打开 GitHub；根目录缺少有效 package.json 时会按默认分支并回退 main/master 扫描常见 monorepo 子目录。主题预览按需读取 README 图片，无图或不支持时明确提示。
- Agent 页提供缓存优先的 Skill 市场：缓存文件在后台解析，输入搜索使用 180ms 防抖，结果列表启用 Recycling 虚拟化和逻辑滚动。刷新时并行扫描 GitHub 仓库树中的嵌套 `SKILL.md`，再并行校验闭合 frontmatter 及非空 `name`、`description`；扫描阶段复用未变化的快照，校验阶段只在实际报告批次生成快照，UI 按结果变化和最小时间间隔更新列表。通过校验的文件按单个 Skill 展示并归入开发、设计、文档、效率、Agent、其他分类，无效文件不显示。同仓库同名副本优先标准 Skill 目录，仓库分支和更新时间未变化时复用缓存，暂时性网络失败保留为可重试状态。安装只复制所选 Skill 目录及其配套文件到当前停止实例的 `skills`；扩展和 Agent 页在存在多个版本时可直接切换当前实例。
- 全局 ComboBox 的点击层使用不绘制悬停背景的专用模板，鼠标移入时只改变外框颜色，不再覆盖选中文字和箭头；可编辑 ComboBox 的内部文本框显式清除重复 Padding，工作区名称不再被垂直裁切。
- 版本控制支持复制版本、新建干净版本、删除版本、双击进入版本设置和 `.dshpack` 导入/导出。整合包可包含脱敏后的 Provider 结构和模型目录，但会排除 `.credentials.yaml`、`.env`/`.env.*`、sessions、API Key、Token、密码和 URL 凭据；导入再次拒绝凭据路径并清理可分享文本，始终创建新版本。版本设置个性化页提供版本名称输入框和保存按钮，修改后同步更新实例注册记录、当前选择和页面标题。
- Provider 启动页支持启用/禁用、`/models` 只读诊断、模型列表/思考档位显示和问题说明；模型设置只保存环境变量名。DSh Web UI 当前生成的花括号映射、行尾逗号和行内模型数组可以被正确读取，不再把 `{` 识别为 Provider 名称。
- Provider 自动同步会在符合策略的停止版本之间同步模型配置、启用状态和 DSh 官方 `.credentials.yaml`；只用 `settings.yaml` 时间戳选择配置来源，并把设置、状态和同源凭据作为一个事务提交，失败会回滚。凭据文件不由 Launcher 解析、显示或写入自身配置，并拒绝重解析点和超过 1 MiB 的文件。
- Provider 刷新已改为纯读取与只读诊断，不再顺带执行版本同步。相同实例刷新时旧卡片保留到新结果就绪；取消或过期的并发刷新不能再把旧结果写回，因此不会出现先显示、刷新后消失。
- `v0.1.9` 已构建、推送并发布；PR #1 已合并到 `main`（merge commit `1eb5f65`）。
- 对话页支持 JSONL/Zstandard 会话列出、导入、导出、备份、删除、双击打开和停止实例自动启动；当前使用 Chat `localStorage` 预选 session ID。对话列表优先显示会话名称：读取 DSh `storages/session_projcache.json` 的标题，无标题时回退为“未命名 · 项目 · 时间”，不再把原始 session ID 作为首列。导入时可选择目标版本和 sessions 工作区落位，并保留原始会话内容。
- 对话文件和备份列表的归属列显示对应实例名称，不再显示会话 header 中的工作目录路径；底层仍保留该字段用于会话解析和恢复。
- 对话页新增“备份与恢复”列表，可选择当前实例的有效备份恢复；已有相同会话 ID 时拒绝覆盖，恢复后按当前会话策略同步。
- 插件市场的分类保留在主浏览行，“排序”和“来源”改为带独立文字标签的次级筛选，默认项显示为“综合排序 / 全部来源”。
- 扩展页卡顿修复：已安装插件扫描和插件市场安装状态/主题扫描移到后台线程；已安装列表与市场列表启用虚拟化（Recycling）。打开“扩展”页只读取本地市场缓存，不再每次联网刷新目录；仅在首次无缓存或用户点击“刷新目录”时才联网。缓存载入只按 identity 索引合并一次，搜索、分类和排序不再重复合并；虚拟化列表滚轮改为逐条滚动。
- 一键运行环境准备：设置/诊断页显示 Node/DSh 状态，缺失时提供“准备运行环境（官方源 / 国内镜像）”按钮。Node 缺失时通过 NodeInstallService 下载 Windows x64 Node.js 官方 MSI 并显示真实字节/百分比进度，经系统授权（msiexec /qn）安装后重新运行 NodeRuntimeDetector，无需重启 Launcher；Node 就绪但 DSh 缺失时复用 DshInstallService 通过 npm 安装 `@deepseek-ai/dsh`（不确定进度），安装后重新运行 DshRuntimeDetector。Node 版本不兼容时只提示安装兼容版本，不自动卸载。启动实例时若运行环境缺失会弹出缺失项并询问是否准备，准备成功后继续原启动流程。Launcher 启动时不静默下载或安装任何内容。Node 检测覆盖 `<ProgramFiles>\nodejs`（官方 MSI 默认位置）。
- DeepSeek Desktop 自动检测：扫描 `%LOCALAPPDATA%\Programs\DeepSeek Desktop`、Program Files 等标准目录，并读取 Windows 卸载注册表中的自定义安装位置；读取 Desktop 版本、内置 `runtime\node.exe`、官方 DSh package 版本和 `app\node_modules\.bin\dsh` 启动文件，命令版本与 package metadata 一致后才标记可用。首次无版本时会显示 Desktop 与 DSh 两个版本，并可直接创建独立版本，不重复安装环境。手动选择 Desktop 根目录也能解析 DSh package 和启动文件。检测、实例启动及 Plugin CLI 都会给子进程临时加入内置 Node 路径。
- DSh 可用性检测要求命令返回可解析的语义版本、附近存在官方 `@deepseek-ai/dsh` package root，且命令版本与 `package.json` 一致；残留 shim、损坏包和版本错配不会再被报告为可用，设置页会显示修复原因。
- DSh 检测不再只保留第一个结果：Launcher 进程 PATH、当前用户/系统 PATH、设置中的安装位置、npm 默认位置和 DeepSeek Desktop 范围内扫描到的每个不同有效 package root 都会自动注册为 Installed 版本；已有 root 会去重，每个新增版本创建独立 `DSH_HOME`，相同版本号使用不重复名称。DSh 校验和 Installed 实例启动都会临时注入已检测 Node 路径，避免父进程 PATH 过期时 `dsh.cmd` 找不到 `node`。
- 插件市场地址解析已区分 GitHub `owner/repository` 与 scoped npm `@scope/package`；后者不再生成 `https://github.com/@scope/package`，安装前校验继续走 npm registry。
- Plugin 安装模式由 Launcher 全局设置控制，默认为快速安装；兼容模式给 pnpm 使用 copy/force 参数。快速模式失败会先展示原始根因，再询问是否用兼容模式重试。实机通过快速模式安装 `dsh-at-file` 成功；安装后的 GitHub 条目可按 package name 立即识别为已安装。
- 首次运行引导：实例列表成功读取且为空时，在 Node/DSh 检测结束后自动弹出引导，但不会未经确认下载。引导允许选择官方源或 npmmirror、设置 DSh 安装位置；准备成功后创建带独立 `DSH_HOME` 的首个干净版本并继续启动。取消后主启动按钮显示“准备首个版本”，可再次打开引导；实例注册读取失败时不会误当成首次运行。
- DSh 默认 Plugin 保护：`@deepseek-ai/dsh-base` 与 `@deepseek-ai/dsh-web-app` 保留在已安装列表中，但扩展页操作按钮禁用；`ExtensionService` 同时拒绝对它们执行安装、启用、禁用、更新和删除，包含带 npm 版本后缀的 spec。
- 版本控制新增“检查版本 / 修复可处理项”：检查独立 DSH_HOME、Installed/Source DSh Runtime、Node engine 兼容性、版本设置、Provider、MCP、web profile 和旧运行记录；自动修复范围限定为创建缺失 DSH_HOME、清除不存活的运行记录，以及把失效 Installed 版本重新绑定到已验证的 DSh。
- Provider 编辑会先把 DSh Web UI 的花括号映射规范为块级 YAML；跨版本同步改为整体替换 `llm-pi-ai.providers` 子区块，保留其它顶层配置，不再产生两种 YAML 写法混排。
- “检查版本”现在通过当前 DSh Runtime 自带的 `yaml` 解析器校验 `settings.yaml`，能报告 DSh 实际会拒绝的语法错误与行列位置，校验输出不包含配置值。
- 启动、重启和对话自动启动失败时，主页面只显示简短原因；完整错误通过通知卡片的“查看详情”打开。YAML 错误会直接提示进入“版本控制 → 检查版本”。
- 启动冲突补强：同版本复用、跨 Runner DSH_HOME 锁和旧 PID/端口收编继续保留；端口分配后被抢占并出现 `EADDRINUSE` 时保持实例锁并最多换端口重试 3 次，锁占用和锁目录权限失败使用不同提示。
- 版本控制新增本地加密快照与手动回滚。快照覆盖版本设置、Provider 配置、官方 `.credentials.yaml`、MCP、launcher patch 和 Plugin profile，使用 Windows DPAPI CurrentUser 加密，不包含会话或 Runtime 依赖；回滚前自动保存当前状态。版本配置保存、Plugin 启停和官方 Plugin CLI 安装/更新/删除前自动创建快照。
- runtime bootstrap 边界修复：Node 下载阶段可取消并清理 `.part` 临时文件，关闭进度窗口等价于取消下载；MSI 安装开始后禁用取消按钮、阻止通过窗口 X 关闭进度窗口并阻止主窗口关闭（流程结束恢复后自动解除）、不强制终止 Windows Installer，安装结束后删除下载的 MSI，用户取消与真实 10 分钟超时用独立结果状态区分。DSh 重新安装并检测成功后，绑定失效的 Installed 实例经 `InstanceRuntimeRebinder` 重绑定到重新检测到的 package root / executable / version，保留实例 Id 与 DSH_HOME、不创建新实例、不修改 Source 实例；运行中或 Attached 实例不参与重绑定。Node 兼容判断以 metadata 为准：Installed 实例优先读取自身 package root 的 `engines.node`，有效但未声明时保持未声明，仅当其 runtime 失效且重装/重绑定时才使用重新检测到的 DSh metadata；Source 实例只读取自身项目 metadata，未声明时保持未声明，不继承全局 installed DSh 的版本要求；未选择实例的诊断场景使用全局 DSh engine。手动安装提示按实际 `engines.node` 要求给出；对话触发的自动启动同样先经过 runtime 准备；准备期间目标实例被删除则中止启动；会话标题缓存路径包含重解析点组件或 ACL 拒绝读取属性时拒绝/放弃读取，缓存结构损坏（意外值类型）时按无标题处理、不中断会话列表。MSI 提权安装前验证 Authenticode 签名链与 Node.js 官方发布者（OpenJS/Node.js Foundation/Joyent），验证失败不执行安装；msiexec 超时后仍可能在后台运行，MSI 清理推迟到进程真正退出，且残留安装进程结束前拒绝再次启动 Node 安装；版本索引返回形状错误的合法 JSON 时走固定版本兜底。实例 package 运行目录已删除但入口 shim 仍在时同样视为缺失并进入一键修复，准备完成时自动重绑定自愈；DSh 安装/更新后按最新 engines.node 复查 Node 兼容性，不兼容时报失败且不自动卸载，设置页就绪判定包含该兼容性。Restart 在停止完成后与 Start 使用相同语义：先 runtime readiness（可能触发一键准备）、再按最初目标实例 ID 重解析。Start/Stop/Restart/对话自动启动在 handler 入口占用 `LifecycleBusyGuard` 串行化 guard 并持有到状态更新结束，只有占用者释放；runtime 准备进行中 Stop/Restart 按钮不可用且入口拒绝。MSI 安装后把新检测到的 Node 目录补入当前进程 PATH，DSh 检测/启动可解析 node；设置/诊断页准备按钮面向全局运行环境（不传实例目标），Node 检测进行中禁止启动与一键准备，后台检测结束后自动恢复准备按钮；版本索引不可用且固定兜底版本不满足目标 engine 时停止安装并提示，不装出与 engine 不兼容的 Node；每次安装调用使用唯一 MSI 文件名避免并发干扰。

## 当前主要相关文件

- `src/DshLauncher/MainWindow.xaml(.cs)`、`ChatWindow.xaml(.cs)`、`WindowSizeHelper.cs`：主窗口导航、启动页、Provider、实例生命周期、运行环境准备、低分辨率初始尺寸约束和独立 Chat 窗口。
- `src/DshLauncher/Services/NodeInstallService.cs`、`RuntimeProgressWindow.cs`：Node 下载（真实进度）、系统安装与准备进度窗口。
- `src/DshLauncher/Services/DshInstallService.cs`、`DshRuntimeDetector.cs`、`DetectedRuntimeRegistrationService.cs`、`NodeRuntimeDetector.cs`、`DeepSeekDesktopDetector.cs`：DSh/Node/DeepSeek Desktop 检测、扫描结果自动注册与 npm 安装。
- `src/DshLauncher/Services/RuntimeSearchPaths.cs`：合并进程、当前用户和系统 PATH，供 Node/DSh 校验及子进程启动使用。
- `src/DshLauncher/Services/ExtensionService.cs`、`ExtensionWindow.xaml(.cs)`、`PluginProgressWindow.cs`：Plugin、Skill、MCP、Agent Preset、市场入口和插件安装进度弹窗。
- `src/DshLauncher/Services/MarketplaceService.cs`、`Models/MarketplaceModels.cs`、`ThemePreviewWindow.cs`：市场缓存、来源合并、搜索、排序、GitHub/monorepo 校验、安装状态和 README 图片预览。
- `src/DshLauncher/Services/SkillMarketService.cs`、`Models/SkillMarketModels.cs`：Skill 市场缓存、GitHub 发现、SKILL.md 校验和实例导入。
- `src/DshLauncher/Services/VersionSettingsService.cs`、`VersionPackageService.cs`、`VersionControlWindow.xaml(.cs)`、`VersionSettingsWindow.xaml(.cs)`：版本同步策略、工作区管理、版本复制/删除、设置和 `.dshpack`。
- `src/DshLauncher/Services/VersionHealthService.cs`、`DshSettingsYamlValidator.cs`、`VersionSnapshotService.cs`、`Models/VersionHealthModels.cs`：版本体检、DSh YAML 语义校验、安全自动修复和当前 Windows 用户加密的配置回滚点。
- `src/DshLauncher/Services/ModelService.cs`、`ModelProviderSyncService.cs`、`ProviderStateService.cs`：Provider 配置、同步和启用状态。
- `src/DshLauncher/Services/ConversationService.cs`、`ConversationSyncService.cs`、`ConversationWindow.xaml(.cs)`：会话文件管理、打开入口和同步策略。
- `tests/DshLauncher.SelfTest/Program.cs`：当前最小自测入口。
- `CURRENT_DESIGN.md`：当前有效设计约束。

## 已执行测试及结果

- `v0.2.4` 发布前完整自测：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release` 52/52 通过。Windows x64 自包含单文件 `DSH Launcher.exe` 为 72,408,566 字节，文件版本 `0.2.4.0`，SHA-256 `F6D5C67488164DB21DDA28AE16FEAC4802A5B803943D6579684832DAA6C592D1`；完整产物位于 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v0.2.4-20260817-134346`，桌面同名文件夹顶层发布文件哈希一致。
- 手动 DSh 发现与启动修复：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release` 52/52 通过。新增端到端测试直接检测 `D:\DevTools\Scoop\apps\nodejs-lts\current\bin\dsh.cmd`，在临时 Launcher 根目录自动注册对应 Installed 版本并创建独立 `DSH_HOME`，启动至 Web 健康检查通过后成功停止；现有用户实例注册文件未修改。Release 构建同时为 0 warnings、0 errors；Debug 测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-manual-dsh-detection-20260817-133127`，确认 `DSH Launcher.exe` 存在，共 13 个文件。
- `v0.2.3` 发布前完整自测：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release` 51/51 通过。Computer Use 在停止的独立实例 `DSh 0.1.0-rc.6 (2)` 中通过快速模式成功安装 `dsh-at-file`，独立进度弹窗正常显示，完成后实例列表变为 3 个 Plugin；修复 identity 后发布版搜索卡片显示为“已安装 / 更新状态未知”，无效的 `github.com/@scope/package` 已不再生成。Windows x64 自包含单文件 `DSH Launcher.exe` 为 72,408,444 字节，文件版本 `0.2.3.0`，SHA-256 `DB992B693E513F9E9446FD9504ABD84AFE60086FBA0A3F8E892973DAD8C34B0D`；完整产物位于 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v0.2.3-20260817-1300`，桌面同名文件夹顶层发布文件哈希一致。
- DSh 多安装自动注册与 GitHub 地址修复：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj --no-restore` 51/51 通过；WPF Debug 构建 0 warnings、0 errors。新增覆盖两个有效 DSh 同时被扫描、不同 package root 自动注册、独立 `DSH_HOME`、重复扫描去重，以及 scoped npm 包不生成 GitHub 地址且继续通过 npm registry 校验。最终测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-dsh-auto-register-github-url-final-20260817-122024`，确认 `DSH Launcher.exe` 存在，共 13 个文件。
- `v0.2.2` 发布前完整自测：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release --no-restore` 50/50 通过；WPF Release 构建 0 warnings、0 errors。Windows x64 自包含单文件 `DSH Launcher.exe` 为 72,401,273 字节，文件版本 `0.2.2.0`，SHA-256 `AEE6C509709C71A5D1809350C868C858F5067856ED555F27D842144E49BAAD20`。完整产物已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v0.2.2-20260817-115854`，桌面同名文件夹顶层发布文件也已更新并核对哈希。
- DeepSeek Desktop 内置运行时检测：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Debug --no-restore` 50/50 通过；WPF Debug 构建 0 warnings、0 errors。新增覆盖 Desktop/DSh 双版本读取、内置 Node 和 `.bin` 启动文件定位、Desktop 根目录反向解析、版本命令 PATH 注入、首次版本名称，以及 Plugin CLI 继承内置 Node 路径。最终完整测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-deepseek-desktop-detection-20260817-final`，确认 `DSH Launcher.exe` 存在，共 13 个文件。
- `v0.2.1` 发布前完整自测：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release --no-restore` 49/49 通过；Release 单文件自包含预构建 0 warnings、0 errors，文件版本 `0.2.1.0`。新增覆盖 Node 子进程 PATH、Plugin CLI 非动态输出与 allow-build、monorepo 自动发现、GitHub 标题链接、README 图片预览、Provider 事务回滚、快照失败回滚与自动保留，以及 `.dshpack` 普通 key 不被误删。
- Provider YAML/版本检查/启动错误摘要修复：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Debug` 49/49 通过；WPF Debug 构建 0 warnings、0 errors。新增覆盖 DSh Web UI 花括号 Provider 的编辑与跨版本同步、当前 DSh YAML 解析器验收、无效 YAML 行列报告以及启动堆栈摘要。测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-provider-yaml-health-20260817-1035`，确认 `DSH Launcher.exe` 存在，共 13 个文件。
- `v0.2.0` 发布前完整自测：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Debug --no-restore` 49/49 通过；测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-v0.2.0-20260817-010249`，确认 `DSH Launcher.exe` 存在，共 13 个文件。
- `v0.2.0` Release 单文件自包含 publish：0 errors；`DSH Launcher.exe` 72,380,251 字节，文件版本 `0.2.0.0`，SHA-256 `E5714AA7CB60F2BBB3FEB9C4B0D35F97ACAEC2D438AB2BA70CA1EB7404248065`。完整产物已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v0.2.0-20260817-010309`，并确认桌面顶层 `C:\Users\121103qwq\Desktop\DSH Launcher\DSH Launcher.exe` 存在。
- 版本检查、启动冲突和回滚自测：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Debug --no-restore` 49/49 通过。新增覆盖端口首次被占用后换端口成功、跨 Runner 锁冲突提示、缺失 DSH_HOME/失效 Runtime/旧运行记录修复、快照原始字节不含 API Key、凭据与 Plugin 配置回滚以及会话不被快照覆盖。WPF Debug 构建 0 warnings、0 errors。
- 最新 Debug 测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-health-conflict-rollback-final2-20260817`，确认 `DSH Launcher.exe` 存在，共 13 个文件。
- Provider 凭据、`.dshpack` 防泄漏、官方 YAML 与 DSh 可用性检测核验：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Debug` 46/46 通过。覆盖 `.credentials.yaml` 停止版本同步、凭据/dotenv 排除、内联密钥与 URL 凭据脱敏、恶意导入拒绝、花括号 Provider 配置、损坏命令和版本错配入口拒绝。
- Provider 刷新并发与只读语义修复后再次运行同一自测：46/46 通过；测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-provider-refresh-20260817-000328`，确认 `DSH Launcher.exe` 存在，共 13 个文件。
- `dotnet build src\DshLauncher\DshLauncher.csproj -c Debug`：0 warnings、0 errors；测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-provider-security-dsh-detection-20260816-235543`，确认 `DSH Launcher.exe` 存在，共 13 个文件。
- 当前 Git 分支为 `main`；本地 `artifacts/` 与 `src/DshLauncher/artifacts/` 是未跟踪诊断/构建目录，不纳入源码提交或 Release。
- `dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Debug`：46/46 通过；WPF XAML 编译通过，对话与备份条目新增实例名称断言；既有首次运行、默认 Plugin 保护、Skill 市场、工作区和对话恢复测试继续通过。
- Agent/Skill 市场性能修复后再次运行同一自测：46/46 通过；实际缓存包含 234 个 Skill（约 130 KB），冷读取约 31 ms，现已移到后台线程。
- 最新 Debug 测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-agent-performance-final-20260816-2300`，确认 `DSH Launcher.exe` 存在，共 13 个文件。
- `v0.1.11` 版本号更新后再次运行自测：46/46 通过；Debug 测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-v0.1.11-20260816-2305`，确认 13 个文件。
- `v0.1.11` Release 单文件自包含 publish：0 errors；`DSH Launcher.exe` 72,362,930 字节，文件版本 `0.1.11.0`，SHA-256 `A451CE524904BFCCB4E0F68F99E255165BBDED244584429310FA3D7020D828D2`。已复制到桌面顶层 `DSH Launcher.exe` 和 `release-v0.1.11-20260816-2305`；因桌面已有用户启动的测试版进程，本轮未强行关闭它执行独立冒烟。
- `dotnet build src\DshLauncher\DshLauncher.csproj -c Debug`：0 warnings、0 errors。
- 最新 Debug 测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-first-run-plugin-20260816`，确认 `DSH Launcher.exe` 存在，共 13 个文件。
- 对话实例名称显示测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-conversation-instance-20260816`，确认 `DSH Launcher.exe` 存在。
- 最新 Debug 测试版已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-combobox-hover-20260816-220500`，确认 `DSH Launcher.exe` 存在，共 12 个文件。
- `dotnet build src\DshLauncher\DshLauncher.csproj -c Debug`：0 warnings、0 errors。
- 真实 1035 项本地市场缓存只读复测：`ReadCached` 从修复前约 5.9 秒降至 85 ms；包含兼容性合并入口的 UI 分类筛选为 8 ms（176 项）。
- Release 单文件自包含 publish（README 规范参数）：0 errors；`DSH Launcher.exe` 72,362,321 字节，SHA-256 `1C2D2AECBA6CBD3DD1E33C553FF4E1F9BB3405F934BA2186A96EF52C7022F56A`。已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\DSH Launcher.exe` 和 `release-v0.1.10-20260816` 并确认存在；因桌面已有用户运行的 Launcher 进程，本轮未强行关闭它执行独立冒烟。
- 该发布文件在 Windows 中约为 69 MiB；体积来自压缩后的 .NET 8 自包含运行时、WPF、WebView2 和 Zstandard 依赖，不是内置 Node.js 或 DSh。
- 崩溃取证：`%LOCALAPPDATA%\CrashDumps\DSH Launcher.exe.35640.dmp`（17:01:29）经 dotnet-dump 分析为 `RefreshProvidersAsync` 入口 ObjectDisposedException → `Window_OnLoaded` async void 未捕获。
- `git diff --check`：通过。

## 已知问题

- Source 项目的 `.dsh/skills` 和 `.agents/skills` 是 DSh 项目级共享只读资源；实例 `DSH_HOME` 内的 Skill 才是版本私有。
- Provider 自动同步按停止版本中 `settings.yaml` 的最新写入时间选择单一来源，没有三方合并；运行中的版本会等停止后参与同步。
- 对话同步是生命周期边界同步，不是多个运行中 DSh 的实时共享写入；外部程序直接删除会话文件不会触发 Launcher 的删除传播。
- 当前只使用已验证的 Chat `localStorage` 预选会话，尚未实现官方 `?session=<id>` deep link。
- MCP 当前是实例级 stdio/streamable-http 配置和 patch 管理，尚未接入完整 MCP Manager 的 connected/needs-auth/authorizing/error、OAuth 和 Tool discovery 状态。
- 主题市场可按需显示仓库 README 中的首选图片，并保留 dsh-market 应用桥接；Wallpaper 仍未建立独立资源格式，也未在用户真实实例上做主题视觉验收。
- GitHub Topic 发现仍未做分页加载；真实第三方 Plugin CLI 对 node_modules 的副作用不能由配置快照完全回滚，失败时仍以官方 CLI 输出和 web profile 自动恢复为准。
- `.dshsnapshot` 使用 Windows DPAPI CurrentUser，只能由创建它的同一 Windows 用户在本机解密；它是本地回滚点，不是可分享格式，分享继续使用脱敏 `.dshpack`。
- 官方已安装 DSh 的 package metadata 当前未声明 `engines.node` 时，Node 兼容性只能显示“未声明/Unknown”，不会凭空套用固定版本限制。
- 一键运行环境准备的端到端链路（真实无 Node 机器上：下载、UAC 授权、msiexec 安装、自动重检测）仍需实机人工验证。
- DeepSeek Desktop 自定义安装位置依赖 Windows 卸载注册表；若用户手工移动安装目录或删除注册表记录，仍需在设置中手动选择该 Desktop/DSh 目录。
- DSh 自动注册只覆盖当前检测候选范围（PATH、设置中的安装位置、npm 默认位置和已识别 DeepSeek Desktop），不会遍历整块磁盘寻找任意未知目录。
- 空实例首次运行引导已通过代码测试，但官方源/国内镜像选择、DSh 自定义位置、自动创建并启动首个版本仍需人工走完整安装流程。
- Skill 市场当前只取 GitHub 搜索前 30 个名称含 `skill` 的仓库；没有聚合社区 catalog，也没有分页。
- 根目录 `artifacts/` 和 `src/DshLauncher/artifacts/` 是未跟踪的本地诊断/构建目录，未纳入源码提交和 Release。

## 尚未完成内容

- 本轮新增设置、工作区、备份恢复、市场筛选及首次运行引导的实机 UI 验收。
- 版本检查、修复按钮、快照选择与回滚确认的实机 UI 验收。
- 一键运行环境准备的实机端到端验证与后续打磨。
- 官方 Session deep link。
- 完整 MCP Manager 状态/认证/工具发现整合。
- Wallpaper 资源格式和用户真实实例主题视觉验收。
- GitHub Topic 分页和更多真实 Plugin CLI 失败边界。
- 多个运行中 DSh 的实时会话共享写入。

## 已尝试但已放弃的方案

- 不再把扩展、Agent 和对话作为管理弹窗；它们统一在主窗口右侧切换，只有 Chat WebView2 保持独立窗口。
- 不再把 GitHub topic 候选直接视为可安装 Plugin；现在必须先读取 package.json、检查 DSh bundle 和入口，再调用官方 CLI。
- 不再硬编码 Node.js 版本兼容规则；现在优先读取 DSh 或 Source 的 package metadata，缺失时保留 Unknown/未声明状态。
- 一键运行环境准备不建立 RuntimeManager/InstallerProvider 等大型抽象框架；仅新增 NodeInstallService 并复用 DshInstallService。

## 下一步最直接的任务

在一台真正没有 Node/DSh 的 Windows 电脑上完成“一键准备运行环境 → 自动创建首个版本 → 启动”的完整人工验收；当前手动安装 DSh 的发现、自动添加和启动链路已经验证。
