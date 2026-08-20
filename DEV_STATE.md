# DSH Launcher 开发状态

## 当前目标

当前源码版本仍为 `v1.0.7`。DeepSea Glass 第一阶段的代码、自动验证和测试产物已完成，并已提交到 Draft PR [#10](https://github.com/121103qwq/DSH-Launcher/pull/10)：统一设计资源、主窗口与启动页、Windows 11 DWM backdrop 回退、可取消的 last-request-wins 页面转场、reduced motion 和 P0 可访问性收口；不改变业务语义、不升版本、不发布 Release、不合并 PR。按任务约束不使用 Computer Use，真实视觉、DWM 材质和 DPI 实机表现保持未证明状态。

## 已完成内容

- DeepSea Glass 第一阶段：主窗口和启动页已切换到统一暗色深海材质资源、固定顶部导航与移动选中层，并按 P0 补齐三层材质宪法、Primary/Secondary/Tertiary 层级、停止非危险态、High Contrast 系统色/去高光阴影、2 DIP 键盘焦点与根布局像素对齐。系统材质通过普通 HWND 和 Windows 11 DWM attribute 38 应用，能力不足、透明效果关闭、高对比度、强制关闭或 DWM 失败时使用完整不透明 fallback。页面切换集中为可取消的 last-request-wins Opacity/TranslateTransform 转场，使用 180ms EaseOut 入场和 120ms EaseIn 退场，打断时继承当前视觉值，并将实例 ID 纳入页面 identity。Release 自检 `73/73` 通过，Windows x64 自包含单文件 publish 成功且版本仍为 `1.0.7.0`；最终测试产物已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\deepsea-glass-ui-final-20260821-021109`，`DSH Launcher.exe` 为 72,498,257 字节，SHA-256 `3319CFB4DA7D108DAED6B3C01179EFF5A6B9EF0FAA2BB26FE885D81DF99E1B74`。按任务约束未使用 Computer Use，真实视觉、DWM、DPI/缩放、输入法、系统动画关闭及旧嵌入页面内部异步取消仍未证明。
- 本轮修复：DeepSeek Chat 继续保持无 Owner 窗口，并在 HWND 创建后写入独立的窗口级 AppUserModelID；因此它不再与 Launcher 共用任务栏分组，同时保留黑色 DeepSeek 图标以及现有实例生命周期清理。
- 本轮新增：任意版本可在“版本设置 → 个性化 → 绑定打开方式”中手动选择 EXE、COM、BAT、CMD、PowerShell 脚本、LNK 快捷方式或其它 Windows 可打开文件。启动页主按钮改为“打开窗口”，同时保留 Launcher 启动；可直接启动的目标继承当前版本 `DSH_HOME` 和 `DSH_AGENTS_HOME`。本机绑定路径不会进入 `.dshpack`。
- 本轮新增：Managed 实例运行时，未安装的市场 Plugin 显示“热加载”；点击后先检查该实例的 dsh-market 状态和候选目录 URL，满足条件时调用官方 loopback `/install`，已安装项更新调用 `/update`。dsh-market 不可用、实例关闭热加载或候选不在其目录时，不再直接修改运行中 profile，而是提示停止实例后普通安装。停止实例仍使用现有 DSh Plugin CLI。
- Plugin CLI 继承当前环境或 Git 全局代理，修复从资源管理器启动 Launcher 后 GitHub codeload 下载没有走既有代理而超时的问题；Plugin 进度改为 pnpm 实际包计数，Skill ZIP 下载显示真实字节进度。README 已加入当前界面截图和本次安装行为说明。
- 本轮修复：Plugin 安装和更新继续只调用官方 DSh CLI，不再从仓库 README 提取或改写安装指令；调用前会把 web profile 中遗留的 pnpm `allowBuilds` 未决占位明确设为 `false`，当前已验证插件仍单独授权，且 `pnpm-workspace.yaml` 已纳入失败回档。Skill 市场会把当前实例内同名的可管理 Skill 标记为“已安装”并禁用重复安装；Agent 左栏增大独立高度并为已安装列表保留最小可见区域，`grill-me` 这类已安装条目不会再被当前选择区和长路径挤没。
- 发布前补强：指定 DSh 版本先安装到同级临时目录，校验通过后再替换正式版本目录；离开版本控制会取消下载。`.dshpack` 脱敏已覆盖 YAML 多行 API Key/私钥正文。

- 本轮新增：插件市场提供“精选”分类，按 dsh-market 同源社区目录筛选；GitHub Plugin 卡片显示仓库开发者头像，图片加载失败时保留字母占位。每个实例可独立开启 dsh-market 热加载，应用主题前创建 Plugin 范围自动快照；每个实例最多保留 10 个自动快照，手动快照不参与清理。
- 本轮新增：版本控制和启动页显示实例实际 DSh 版本；“新建干净版本”可从官方 npm metadata 选择 DSh 版本，本机缺少时精确安装到 Launcher 设置的 DSh 目录下 `versions\\<version>`，不修改系统 PATH、不触发 UAC。
- 本轮新增：Plugin 安装、更新、卸载和手动安装失败会先使用现有 web profile 回档，再生成未脱敏的本地诊断 ZIP；报告保留完整错误、`.credentials.yaml` 和选定配置，排除会话文件、`node_modules` 与运行依赖。若当前 DSh 可用，Launcher 会自动打开或复用 Chat，发送报告路径和继续排查安装的指令。

- 本轮代码已实现：Plugin 与 Skill 分类切换分别保存并恢复列表滚动位置；`.dshpack` 导入只从当前 Launcher 设置的 DSh 安装位置解析运行时，不再沿用整合包或模板的 `RootPath`；检测到 DSH Desktop 入口的版本可在版本设置中绑定“打开窗口”，启动页保留独立的“Launcher 启动”按钮；已安装 Plugin 列表对名称和描述单行省略，并明确显示“已启用/已禁用”。
- .NET 8 WPF Launcher，目标 Windows x64；发布版为自包含单文件，不内置 Node.js、npm、pnpm 或 DSh。
- 主窗口采用 PCL2 参考的信息层级：启动、扩展、Agent、对话和设置在主窗口内切换，并移除各模块重复的大号页标题。启动页标题栏左侧显示 `DSH Launcher`；扩展和 Agent 页在同一位置改为当前实例选择器，可下拉快速切换并用绿/灰/红圆点显示运行、停止和错误状态，点击实例名称直接进入该版本的插件管理。Chat WebView2 为无 Owner 窗口，使用独立 AppUserModelID 和黑色 DeepSeek 图标，在任务栏中与 Launcher 分组分离。
- 标题栏实例下拉已改为紧凑圆角菜单；从扩展或 Agent 进入版本设置后，左上角显示返回原页面的按钮。启动页全部实例支持双击：停止实例复用主启动流程，已运行或 Attached 实例直接打开现有 Web UI。
- 启动页先从本地注册文件立即显示全部实例；界面恢复响应后再核对遗留运行状态并扫描 DSh/Node。左侧为固定窄度的当前实例状态、重命名、实例设置和启动/停止/重启操作，右侧为更宽的全部实例列表与版本控制入口；左右容器高度解耦，实例列表在右栏内部滚动。最近使用时间仍用于启动时默认选择，不再限制启动页显示数量。
- Launcher 保持单实例；第二次启动通过命名管道要求已有进程显示、恢复并聚焦主窗口，旧版兼容回退才按窗口句柄查找。已有进程处于退出阶段时可等待锁释放后接管。主窗口关闭先取消当前操作并异步清理实例，清理完成后的最终 `Close` 通过 Dispatcher 排到原 `Closing` 事件结束后，避免同步完成时重入关闭而留下无窗口后台进程。
- 支持 installed/source DSh 实例、独立端口、健康检查、停止/重启、跨 Runner 互斥锁，以及 `Managed` / `Attached` 运行态。Attached 实例可打开 Web UI，但不会被 Stop/Restart 或退出清理误杀。
- 每个版本使用独立 `DSH_HOME` 和 `DSH_AGENTS_HOME`。实例级 Plugin、Skill、MCP、Provider 状态、Agent Preset、Settings 和 Conversation 文件均以该目录为边界；对话和模型 Provider 支持 Independent、Workspace、All/全配置同步策略。运行中的版本不会被同步写入。
- 设置/诊断页可选择任意版本并编辑与“版本设置 → 配置”相同的同步选项；工作区管理支持显示成员、添加、重命名和删除。删除工作区只解除成员版本的同步关系并切回 Independent，不删除版本或对话文件。
- Plugin 通过官方 DSh CLI 安装、更新、删除和启停；市场支持缓存优先、本地即时搜索、分类、来源、发布时间/Star 排序、多来源 identity 合并、GitHub monorepo 校验、安装前 package.json/bundle 检查、独立安装弹窗和完成后刷新。安装弹窗按检查、备份、CLI 操作和刷新显示阶段百分比；操作前可见但完成时被最小化的 Owner 会恢复原状态。市场标题可打开 GitHub；根目录缺少有效 package.json 时会按默认分支并回退 main/master 扫描常见 monorepo 子目录。主题预览按需读取 README 图片，无图或不支持时明确提示。
- Managed 运行实例的 Plugin 安装和更新只调用 dsh-market 热加载接口；dsh-market 不可用或候选不在其目录时提示停止实例后重试。停止实例继续使用快速/兼容模式的官方 DSh Plugin CLI；卸载仍要求停止，Attached 始终只读。市场安装目标只使用目录条目以及 npm/GitHub `package.json` 校验结果，不读取 README 安装指令。
- Agent 页提供缓存优先的 Skill 市场：缓存文件在后台解析，输入搜索使用 180ms 防抖，结果列表启用 Recycling 虚拟化和逻辑滚动。刷新时并行扫描 GitHub 仓库树中的嵌套 `SKILL.md`，再并行校验闭合 frontmatter及非空 `name`、`description`；扫描阶段复用未变化的快照，校验阶段只在实际报告批次生成快照，UI 按结果变化和最小时间间隔更新列表。通过校验的文件按单个 Skill 展示并归入开发、设计、文档、效率、Agent、其他分类，无效文件不显示。同仓库同名副本优先标准 Skill 目录，仓库分支和更新时间未变化时复用缓存，暂时性网络失败保留为可重试状态。安装只复制所选 Skill 目录及其配套文件到当前停止实例的 `skills`；同名可管理 Skill 已存在时显示“已安装”并禁用按钮。扩展和 Agent 页左栏显示当前实例详情、已安装条目及启用/禁用/更新/删除操作，右侧保留市场和横向分类；实例切换只保留标题栏入口。
- 全局 ComboBox 的点击层使用不绘制悬停背景的专用模板，鼠标移入时只改变外框颜色，不再覆盖选中文字和箭头；可编辑 ComboBox 的内部文本框显式清除重复 Padding，工作区名称不再被垂直裁切。
- 版本控制支持导入实例、复制版本、新建干净版本、删除版本、双击进入版本设置和 `.dshpack` 导入/导出。“导入实例”可扫描文件夹或解析 Windows `.lnk` 快捷方式的目标/工作目录，再复用现有 DSh Runtime 校验；普通 DSh 使用当前有效的 `$DSH_HOME`（默认 `~/.dsh`），DeepSeek Desktop 从安装包 `.modules.yaml` 反查实际 `%LOCALAPPDATA%\DeepSeek Harness Data`，把工作区、对话、设置、Plugin/Skill 和本机 Provider 凭据复制到独立 HOME，不与原桌面程序共用目录。同一 package root 再次手动导入时更新已有实例，不再建立 `(2)/(3)`；自动检测到同一 root 的真实来源 HOME 改变时也覆盖原停止实例并更新来源记录。覆盖会保留 `.dsh-launcher` 版本设置，运行中的实例拒绝覆盖。导入跳过 `webview2` 浏览器缓存、重解析点和整棵 `node_modules`，新注册失败会撤销。整合包可包含脱敏后的 Provider 结构和模型目录，但会排除 `.credentials.yaml`、`.env`/`.env.*`、sessions、API Key、Token、密码和 URL 凭据；导入再次拒绝凭据路径并清理可分享文本，始终创建新版本。版本设置个性化页提供版本名称输入框和保存按钮，修改后同步更新实例注册记录、当前选择和页面标题。
- 实例注册文件加载时会合并历史遗留的“相同 DSh package root + 相同导入 DSH_HOME”重复项，保留运行状态更有效或最近使用的一项并持久化结果；没有导入来源的复制版本、干净版本和各自 DSH_HOME 不参与合并，也不会删除旧目录。
- 导入 DSH_HOME 会跳过整棵 `node_modules`，只恢复 profile dependencies、bundles 和 `cordis.patch.yml` 直接引用的第三方 Plugin；已导入版本在启动前会补齐缺包，修复自定义 Plugin 引起的 `ERR_MODULE_NOT_FOUND`，并避免每个版本复制约 166 MB 的派生依赖。
- 启动页已移除 Provider 卡片、启停按钮和网络诊断，不再在打开或切换实例时发起 Provider 检测。设置中的供应商自动同步仍保留：在符合策略的停止版本之间同步模型配置、本地状态和 DSh 官方 `.credentials.yaml`，以事务方式提交并在失败时回滚；凭据不由 Launcher 解析或显示。
- `v0.1.9` 已构建、推送并发布；PR #1 已合并到 `main`（merge commit `1eb5f65`）。
- 对话页支持 JSONL/Zstandard 会话列出、导入、导出、备份、删除、双击打开和停止实例自动启动；顶部版本选择器决定当前实例，列表只读取该版本独立 `DSH_HOME`，并可筛选全部、独立对话或工作区对话。当前使用 Chat `localStorage` 预选 session ID。对话列表优先显示会话名称：读取 DSh `storages/session_projcache.json` 的标题，无标题时回退为“未命名 · 项目 · 时间”，不再把原始 session ID 作为首列。导入时可选择目标版本和 sessions 工作区落位，并保留原始会话内容。
- 对话文件和备份列表的归属列显示对应实例名称，不再显示会话 header 中的工作目录路径；底层仍保留该字段用于会话解析和恢复。
- 对话页新增“备份与恢复”列表，可选择当前实例的有效备份恢复；已有相同会话 ID 时拒绝覆盖，恢复后按当前会话策略同步。
- 插件市场的分类保留在主浏览行，“排序”和“来源”改为带独立文字标签的次级筛选，默认项显示为“综合排序 / 全部来源”。
- 扩展页卡顿修复：已安装插件扫描和插件市场安装状态/主题扫描移到后台线程；已安装列表与市场列表启用虚拟化（Recycling）。打开“扩展”页只读取本地市场缓存，不再每次联网刷新目录；仅在首次无缓存或用户点击“刷新目录”时才联网。缓存载入只按 identity 索引合并一次，搜索、分类和排序不再重复合并；虚拟化列表滚轮改为逐条滚动。
- 一键运行环境准备：设置/诊断页显示 Node/DSh 状态，缺失时提供“准备运行环境（官方源 / 国内镜像）”按钮。Node 缺失时通过 NodeInstallService 下载 Windows x64 Node.js 官方 MSI 并显示真实字节/百分比进度，经系统授权（msiexec /qn）安装后重新运行 NodeRuntimeDetector，无需重启 Launcher；Node 就绪但 DSh 缺失时复用 DshInstallService 通过 npm 安装 `@deepseek-ai/dsh`，默认目标为 Manager 根目录下的 `runtime\dsh`，设置页和首次引导可覆盖该位置，安装后重新运行 DshRuntimeDetector。Node 版本不兼容时只提示安装兼容版本，不自动卸载。启动实例时若运行环境缺失会弹出缺失项并询问是否准备，准备成功后继续原启动流程。Launcher 启动时不静默下载或安装任何内容。Node 检测覆盖 `<ProgramFiles>\nodejs`（官方 MSI 默认位置）。
- DeepSeek Desktop 自动检测：扫描 `%LOCALAPPDATA%\Programs\DeepSeek Desktop`、Program Files 等标准目录，并读取 Windows 卸载注册表中的自定义安装位置；读取 Desktop 版本、内置 `runtime\node.exe`、官方 DSh package 版本和 `app\node_modules\.bin\dsh` 启动文件，命令版本与 package metadata 一致后才标记可用。检测会从 `.modules.yaml` 定位 Desktop 实际使用的 DSH_HOME；当前机器已确认从 `C:\Users\121103qwq\.dsh` 纠正为 `C:\Users\121103qwq\AppData\Local\DeepSeek Harness Data`。首次无版本时会显示 Desktop 与 DSh 两个版本，并可直接创建独立版本，不重复安装环境。手动选择 Desktop 根目录也能解析 DSh package、启动文件和数据来源。检测、实例启动及 Plugin CLI 都会给子进程临时加入内置 Node 路径。
- DSH Desktop v2 兼容：识别 `DSH Desktop.exe` 与 `resources\app.asar.unpacked` 中的官方 DSh、`lib\desktop-cli.js` 和 pnpm；使用 Electron Run-as-Node 启动、检测版本及调用 Plugin CLI，不要求系统 Node.js。运行时启动描述随版本注册信息保存，旧版本仍从原 `DshExecutablePath` 自动迁移。Launcher 另提供仅对该封装版本显示的“桌面窗口”入口，并拒绝与同一版本的 Managed 进程并发写同一个 `DSH_HOME`。
- 运行时发现性能：自动检测只访问 PATH、Node 版本管理器/Scoop、npm prefix、标准安装目录和卸载注册表，候选最多 4 路并发；有效结果按宿主/入口/package 文件指纹缓存 24 小时。自定义目录扫描限制为 6 层、20,000 个目录、跳过重解析点且可取消，不扫描整块磁盘；源码首次安装依赖或构建前必须由用户确认。
- DSh 可用性检测要求命令返回可解析的语义版本、附近存在官方 `@deepseek-ai/dsh` package root，且命令版本与 `package.json` 一致；残留 shim、损坏包和版本错配不会再被报告为可用，设置页会显示修复原因。
- DSh 检测不再只保留第一个结果：Launcher 进程 PATH、当前用户/系统 PATH、设置中的安装位置、npm 默认位置和 DeepSeek Desktop 范围内扫描到的每个不同有效 package root 都会自动导入为 Installed 版本；已有 root 会去重，每个新增版本创建独立 `DSH_HOME`，并在旧 HOME 存在时复制原有数据，相同版本号使用不重复名称。DSh 校验和 Installed 实例启动都会临时注入已检测 Node 路径，避免父进程 PATH 过期时 `dsh.cmd` 找不到 `node`。
- Node.js 候选检查最多并发运行 4 个版本命令，坏候选各自保持 2 秒超时并清理进程树，不再把多个超时串行累加；版本输出必须可解析。存在多个有效 Node 时优先选择满足当前实例 `engines.node` 的最高版本，没有兼容项时返回最高有效版本并显示 `Incompatible`，方便用户修复。
- 插件市场地址解析已区分 GitHub `owner/repository` 与 scoped npm `@scope/package`；后者不再生成 `https://github.com/@scope/package`，安装前校验继续走 npm registry。
- Plugin 安装模式由 Launcher 全局设置控制，默认为快速安装；兼容模式给 pnpm 使用 copy/force 参数。快速模式失败会先展示原始根因，再询问是否用兼容模式重试。实机通过快速模式安装 `dsh-at-file` 成功；安装后的 GitHub 条目可按 package name 立即识别为已安装。
- 设置/诊断页的 Plugin 安装模式状态文字使用现有 `BlueBrush`，不再因引用不存在的资源导致整个设置页无法打开；自测会核对 `MainWindow` 代码引用的资源键均存在于 `App.xaml`。
- 首次运行引导：实例列表成功读取且为空时，在 Node/DSh 检测结束后自动弹出引导，但不会未经确认下载。引导允许选择官方源或 npmmirror、设置 DSh 安装位置；准备成功后创建带独立 `DSH_HOME` 的首个干净版本并继续启动。取消后主启动按钮显示“准备首个版本”，可再次打开引导；实例注册读取失败时不会误当成首次运行。
- DSh 默认 Plugin 保护：`@deepseek-ai/dsh-base` 与 `@deepseek-ai/dsh-web-app` 保留在已安装列表中，但扩展页操作按钮禁用；`ExtensionService` 同时拒绝对它们执行安装、启用、禁用、更新和删除，包含带 npm 版本后缀的 spec。
- 版本控制新增“检查版本 / 修复可处理项”：检查独立 DSH_HOME、Installed/Source DSh Runtime、Node engine 兼容性、版本设置、Provider、MCP、web profile 和旧运行记录；自动修复范围限定为创建缺失 DSH_HOME、清除不存活的运行记录，以及把失效 Installed 版本重新绑定到已验证的 DSh。
- Provider 编辑会先把 DSh Web UI 的花括号映射规范为块级 YAML；跨版本同步改为整体替换 `llm-pi-ai.providers` 子区块，保留其它顶层配置，不再产生两种 YAML 写法混排。
- “检查版本”现在通过当前 DSh Runtime 自带的 `yaml` 解析器校验 `settings.yaml`，能报告 DSh 实际会拒绝的语法错误与行列位置，校验输出不包含配置值。
- 启动、重启和对话自动启动失败时，主页面只显示简短原因；完整错误通过通知卡片的“查看详情”打开。YAML 错误会直接提示进入“版本控制 → 检查版本”。
- 启动冲突补强：同版本复用、跨 Runner DSH_HOME 锁和旧 PID/端口收编继续保留；端口分配后被抢占并出现 `EADDRINUSE` 时保持实例锁并最多换端口重试 3 次，锁占用和锁目录权限失败使用不同提示。
- 版本控制新增本地加密快照与手动回滚。快照覆盖版本设置、Provider 配置、官方 `.credentials.yaml`、MCP、launcher patch 和 Plugin profile，使用 Windows DPAPI CurrentUser 加密，不包含会话或 Runtime 依赖；回滚前自动保存当前状态。版本配置保存、Plugin 启停和官方 Plugin CLI 安装/更新/删除前自动创建快照。
- 版本设置新增“快照回滚”页，直接复用上述快照服务；可创建手动快照、选择已有快照回滚，并在完成后重新读取同步设置、窗口/Node 设置和 Plugin 状态。
- runtime bootstrap 边界修复：Node 下载阶段可取消并清理 `.part` 临时文件，关闭进度窗口等价于取消下载；MSI 安装开始后禁用取消按钮、阻止通过窗口 X 关闭进度窗口并阻止主窗口关闭（流程结束恢复后自动解除）、不强制终止 Windows Installer，安装结束后删除下载的 MSI，用户取消与真实 10 分钟超时用独立结果状态区分。DSh 重新安装并检测成功后，绑定失效的 Installed 实例经 `InstanceRuntimeRebinder` 重绑定到重新检测到的 package root / executable / version，保留实例 Id 与 DSH_HOME、不创建新实例、不修改 Source 实例；运行中或 Attached 实例不参与重绑定。Node 兼容判断以 metadata 为准：Installed 实例优先读取自身 package root 的 `engines.node`，有效但未声明时保持未声明，仅当其 runtime 失效且重装/重绑定时才使用重新检测到的 DSh metadata；Source 实例只读取自身项目 metadata，未声明时保持未声明，不继承全局 installed DSh 的版本要求；未选择实例的诊断场景使用全局 DSh engine。手动安装提示按实际 `engines.node` 要求给出；对话触发的自动启动同样先经过 runtime 准备；准备期间目标实例被删除则中止启动；会话标题缓存路径包含重解析点组件或 ACL 拒绝读取属性时拒绝/放弃读取，缓存结构损坏（意外值类型）时按无标题处理、不中断会话列表。MSI 提权安装前验证 Authenticode 签名链与 Node.js 官方发布者（OpenJS/Node.js Foundation/Joyent），验证失败不执行安装；msiexec 超时后仍可能在后台运行，MSI 清理推迟到进程真正退出，且残留安装进程结束前拒绝再次启动 Node 安装；版本索引返回形状错误的合法 JSON 时走固定版本兜底。实例 package 运行目录已删除但入口 shim 仍在时同样视为缺失并进入一键修复，准备完成时自动重绑定自愈；DSh 安装/更新后按最新 engines.node 复查 Node 兼容性，不兼容时报失败且不自动卸载，设置页就绪判定包含该兼容性。Restart 在停止完成后与 Start 使用相同语义：先 runtime readiness（可能触发一键准备）、再按最初目标实例 ID 重解析。Start/Stop/Restart/对话自动启动在 handler 入口占用 `LifecycleBusyGuard` 串行化 guard 并持有到状态更新结束，只有占用者释放；runtime 准备进行中 Stop/Restart 按钮不可用且入口拒绝。MSI 安装后把新检测到的 Node 目录补入当前进程 PATH，DSh 检测/启动可解析 node；设置/诊断页准备按钮面向全局运行环境（不传实例目标），Node 检测进行中禁止启动与一键准备，后台检测结束后自动恢复准备按钮；版本索引不可用且固定兜底版本不满足目标 engine 时停止安装并提示，不装出与 engine 不兼容的 Node；每次安装调用使用唯一 MSI 文件名避免并发干扰。

## 当前主要相关文件

- `src/DshLauncher/App.xaml.cs`、`SingleInstanceActivationChannel.cs`：Launcher 单实例互斥、进程间窗口唤醒、退出阶段锁接管与异常后台残留恢复。
- `src/DshLauncher/MainWindow.xaml(.cs)`、`ChatWindow.xaml(.cs)`、`TaskbarWindowIdentity.cs`、`WindowSizeHelper.cs`：主窗口导航、本地优先启动页、实例生命周期、异步关闭清理、运行环境准备、低分辨率初始尺寸约束和独立任务栏分组的 Chat 窗口。
- `src/DshLauncher/Services/NodeInstallService.cs`、`RuntimeProgressWindow.cs`：Node 下载（真实进度）、系统安装与准备进度窗口。
- `src/DshLauncher/Models/DshRuntimeLaunchSpec.cs`、`Services/DshRuntimeCommandFactory.cs`、`DshRuntimeDetector.cs`、`DeepSeekDesktopDetector.cs`、`DetectedRuntimeRegistrationService.cs`、`DshHomeImportService.cs`、`NodeRuntimeDetector.cs`、`DshInstallService.cs`：普通 npm、源码、旧 Desktop 与 DSH Desktop v2 的检测、启动描述、命令创建、实例数据导入和 npm 安装。
- `src/DshLauncher/Services/RuntimeSearchPaths.cs`：合并进程、当前用户和系统 PATH，供 Node/DSh 校验及子进程启动使用。
- `src/DshLauncher/Services/ExtensionService.cs`、`ExtensionWindow.xaml(.cs)`、`PluginProgressWindow.cs`：Plugin、Skill、MCP、Agent Preset、市场入口、精选筛选、开发者头像、dsh-market 热加载和插件安装进度弹窗。
- `src/DshLauncher/Services/MarketplaceService.cs`、`Models/MarketplaceModels.cs`、`ThemePreviewWindow.cs`：市场缓存、来源合并、搜索、排序、GitHub/monorepo 校验、安装状态、开发者头像地址和 README 图片预览。
- `src/DshLauncher/Services/SkillMarketService.cs`、`Models/SkillMarketModels.cs`：Skill 市场缓存、GitHub 发现、SKILL.md 校验和实例导入。
- `src/DshLauncher/Services/VersionSettingsService.cs`、`VersionOpenTargetService.cs`、`ShortcutTargetResolver.cs`、`VersionPackageService.cs`、`VersionSnapshotService.cs`、`DshVersionCatalogService.cs`、`VersionControlWindow.xaml(.cs)`、`VersionSettingsWindow.xaml(.cs)`、`NewVersionWindow.xaml(.cs)`：版本同步策略、手动打开方式、快捷方式解析、工作区管理、DSh 版本选择与精确安装、版本复制/删除、加密快照回滚、设置和 `.dshpack`。
- `src/DshLauncher/Services/VersionHealthService.cs`、`DshSettingsYamlValidator.cs`、`VersionSnapshotService.cs`、`Models/VersionHealthModels.cs`：版本体检、DSh YAML 语义校验、安全自动修复和当前 Windows 用户加密的配置回滚点。
- `src/DshLauncher/Services/ModelService.cs`、`ModelProviderSyncService.cs`、`ProviderStateService.cs`：Provider 配置、同步和启用状态。
- `src/DshLauncher/Services/ConversationService.cs`、`ConversationSyncService.cs`、`ConversationWindow.xaml(.cs)`：会话文件管理、打开入口和同步策略。
- `tests/DshLauncher.SelfTest/Program.cs`：当前最小自测入口。
- `CURRENT_DESIGN.md`：当前有效设计约束。

## 已执行测试及结果

- `v1.0.7` DeepSeek 任务栏独立发布候选：Release build/publish 为 0 warnings、0 errors；完整自测 65/65 通过，新增验证 Chat 无 MainWindow Owner、独立 AppUserModelID、任务栏可见性和 DeepSeek 图标。Windows x64 自包含单文件 `DSH.Launcher.exe` 为 72,480,217 字节，文件版本 `1.0.7.0`，SHA-256 `4AE02F2C4A728D7048FC38756485C09C3B1A928E29C47194C244F94DB27037B6`；完整产物位于 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v1.0.7-independent-taskbar-20260819`，桌面顶层 `DSH Launcher.exe` 已同步。按当前任务未使用 Computer Use。
- 本轮手动打开方式代码级自测：Release build 为 0 warnings、0 errors；完整自测 64/64 通过。覆盖 CMD/BAT 真实执行并读取版本 `DSH_HOME`、LNK 目标解析、版本设置持久化，以及 `.dshpack` 不携带本机绑定路径。按用户要求未使用 Computer Use。Windows x64 自包含单文件 `DSH Launcher.exe` 为 72,479,128 字节，文件版本 `1.0.6.0`，SHA-256 `8F794DE5F7532CCDFFB95FB65E9F8B94782380F2BA825C269E3A98985C6598FD`；完整产物位于 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v1.0.6-custom-open-target-20260819`，桌面顶层发布文件哈希一致。
- 本轮 dsh-market Plugin 热加载自测：Release 完整自测 63/63 通过；覆盖运行中按钮文案、社区目录原始 URL 保留、同源请求头、`/install` 与 `/update` 请求体及返回状态解析。Windows x64 自包含压缩单文件已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\dsh-market-hot-load-20260819-144730`；`DSH Launcher.exe` 为 72,475,323 字节，SHA-256 `AD9191623F094F3C4F3AF6594F6AA25D16C0FDEA47144DE61952150072D15C28`。
- 本轮 dsh-market 实机验证：在隔离的临时 `DSH_HOME` 中通过官方 DSh CLI 安装 `dshmarket 1.12.1`，启动真实 DSh 后调用其 loopback `/install` 热加载 `dsh-session-hotkeys 1.5.1`，接口返回 HTTP 200、`ok=true`、`hot=true`，安装后状态为 live；测试版 Launcher 能正常打开扩展页并读取 1331 个缓存候选。测试窗口已关闭，临时 DSh 进程已停止且端口已释放。
- `v1.0.5` 发布候选：Release 完整自测 63/63 通过；Windows x64 自包含压缩单文件版本为 `1.0.5.0`，大小 72,475,314 字节，SHA-256 `622A9B5320315AF676B6D9E36FA63730F39131B958F555C5CF04F609DBEA2369`。最终 EXE 已复制到桌面 `DSH Launcher` 文件夹顶层及 `release-v1.0.5-dsh-market-hot-load-20260819-150902`；通过 Computer Use 启动最终 EXE，主界面和 4 个实例正常显示，随后已关闭测试窗口。
- `v1.0.4` 发布候选：Release build 为 0 warnings、0 errors，完整自测 63/63 通过；Windows x64 自包含单文件版本为 `1.0.4.0`，产物已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v1.0.4-plugin-install` 和桌面同名文件夹顶层。最终文件大小与 SHA-256 以 GitHub Release 记录为准。
- Agent 已安装 Skill 左栏修复：Release 完整自测 63/63 通过，新增检查左侧已安装列表保留最小高度且 Agent 左右栏继续使用独立高度；WPF Release 构建 0 warnings、0 errors。测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\skill-installed-list-20260818-161929`，并确认顶层 `DSH Launcher.exe` 存在。
- 本轮 Plugin/Skill 修复：Release 完整自测 63/63 通过，覆盖 pnpm `allowBuilds` 未决项安全落为 `false`、已明确允许项保持 `true`、README 不改写 Plugin 安装目标，以及已导入 Skill 的市场安装状态；WPF Release 构建 0 warnings、0 errors。测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\plugin-skill-official-cli-20260818-161125`，并确认 `win-x64\DSH Launcher.exe` 存在。
- `v1.0.3` 最终发布前修复：指定 DSh 版本下载改为临时目录校验后替换，`.dshpack` 补齐 YAML 多行敏感值清理；Release build 为 0 warnings、0 errors，完整自测 63/63 通过，`git diff --check` 通过。最终产物信息以 GitHub Release 记录为准。

- 本轮发布前候选：Release 完整自测 63/63 通过，覆盖精选来源与 GitHub 开发者头像、DSh 官方版本目录排序、指定版本安装、dsh-market 热加载开关持久化、运行中 Managed 实例的 Plugin 范围自动快照，以及 10 个自动快照上限和手动快照保留；Release build/publish 为 0 warnings、0 errors，`git diff --check` 通过。Windows x64 自包含单文件已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\pre-release-version-market-avatar-20260818`；`DSH Launcher.exe` 为 72,468,814 字节，SHA-256 `52248320E20F28550A4633B17A602D6EF2D570451D856DC411EDE3E0DA8029DD`。
- Computer Use 实机验证：启动页显示实例 DSh 版本；扩展页显示“精选”分类、按实例保存的 dsh-market 开关和 GitHub 开发者头像，头像网络失败时仍有占位；新建干净版本弹窗在 125% 缩放下完整显示按钮，官方版本下拉包含 `0.1.0-rc.7`、`0.1.0-rc.6`。测试用 `Research` 实例已正常停止，本次打开的 Launcher 窗口已关闭，未留下相关 DSh 进程。
- `v1.0.2` 发布候选：Release 构建 0 warnings、0 errors；完整自测 62/62 通过，包含未脱敏 Plugin 失败诊断报告生成测试。Windows x64 自包含单文件为 72,463,029 字节，文件版本 `1.0.2.0`，SHA-256 `4580A74CAAA42E2591AE2592A2B0D60044183C23171D06490791800B5D3DF9AD`；产物已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v1.0.2-plugin-failure-handoff-20260818`。
- Computer Use 实机验证：启动上述最新构建成功，启动页、扩展页和 Agent/Skill 页均可打开；扩展页读取 1091 个缓存候选，Agent 页显示 234 个 Skill，当前实例名称和实例隔离路径正常显示。验证后已关闭本次启动的测试窗口。
- 本轮验证（含 Plugin 列表显示修改）：`dotnet build .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release --no-restore` 为 0 warnings、0 errors；`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release --no-build --no-restore` 为 61/61 通过；`git diff --check` 通过。更新后的 Windows x64 自包含压缩单文件已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-plugin-status-display-20260818-113529`；`DSH Launcher.exe` 为 72,452,760 字节，SHA-256 `65FA11CDCCBDFA092614ECAE114B3E32BD55C3D9E8477CF35266A1DED1646F51`，并已确认文件存在。
- `v1.0.1` 发布候选：Release 构建 0 warnings、0 errors，完整自测 61/61 通过。Windows x64 自包含压缩单文件为 72,450,328 字节，SHA-256 `5838D7A7C13EA51209D0608BF3EB44194CBAE87E5CABE4C071F49137054AF7F3`；发布候选目录为 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v1.0.1-restore-extension-agent-20260818-105725`。桌面顶层旧版 `DSH Launcher.exe` 当时仍被 PID 66680 使用，因此未强制终止用户进程覆盖，Release 使用候选目录中的同一文件。
- 扩展/Agent 左侧管理区恢复：`ExtensionWindow.xaml` XML 解析通过；Release 构建 0 warnings、0 errors；完整自测 61/61 通过，并新增静态断言确认标题栏保留唯一实例选择入口、左侧实例详情/已安装列表/操作按钮可见且页面内不再存在重复实例选择器。Windows x64 自包含压缩单文件测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\restore-left-management-20260818-011724`；`DSH Launcher.exe` 为 72,450,325 字节，SHA-256 `E12212CDEF7871CEB249D73A6E930BD09D074E682BF844F6583E2485E8BF94A4`。
- `v1.0.0`“UI 重构”最终发布候选：Release 完整自测 61/61 通过；新增覆盖运行实例热安装、运行中拒绝卸载、README npm spec 优先、身份不一致和 shell 拼接命令拒绝，以及 `--profile web` / `--profile=web` 两种写法。Windows x64 自包含压缩单文件构建成功，文件版本 `1.0.0.0`，`DSH Launcher.exe` 为 72,450,291 字节，SHA-256 `729ED12638E6D6A81297681F754CCB872D71D2CD6D7747A1ACC1E565980B94D0`；完整产物已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v1.0.0-ui-refactor-final-20260818`，桌面同名文件夹顶层 EXE 哈希一致。
- 标题栏返回/下拉、实例去重、版本设置快照入口和双击启动/打开：`dotnet build src/DshLauncher/DshLauncher.csproj -c Release --no-restore` 为 0 warnings、0 errors；Release 完整自测 61/61 通过。自测实际创建 DPAPI 加密快照、修改配置并回滚，确认凭据与 Plugin 配置恢复、会话不受影响；同时覆盖重复导入注册合并和相关 XAML/代码入口。最终 Windows x64 自包含单文件测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\instance-dedupe-snapshot-20260818-0002`；`DSH Launcher.exe` 为 72,445,430 字节，SHA-256 `7943DCDD15E125F28084DC50BE52C74267AD229AA9210AA3B2FA56A5A44C29BC`。

- 启动页标题栏品牌文字：MainWindow XAML 解析通过，Release 构建 0 warnings、0 errors，完整自检 60/60 通过。测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\startup-brand-20260817-234340`，SHA-256 `8BC15960D740716FEC69623F347119F60C22DB48906219598A2D49A1E0A4B510`。
- 启动页全部实例与左右隔离布局：标题栏实例选择器由 250 缩短为 190；启动页列宽改为 320 / 自适应，右侧列表绑定全部 `Instances` 并在固定区域独立滚动。MainWindow XAML 解析通过，Release 构建 0 warnings、0 errors，完整自检 60/60 通过。测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\startup-all-instances-ui-20260817-233811`，SHA-256 `9D17DBA98239518B81415C47A4AE461F84378AD7856FFE76FFA50F58D7B28005`。
- Launcher 关闭重入修复：最新 `crash.log` 确认原因为清理同步完成时在 `Closing` 调用栈内再次 `Close()`；现仅把最终关闭排到 Dispatcher 下一轮，并在既有 MainWindow 源码约束测试中加入回归断言。Release 构建 0 warnings、0 errors，完整自检 60/60 通过；新构建连续执行 3 次“启动→关闭”，进程均正常退出且最终残留数为 0。测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\shutdown-reentry-fix-20260817-232542`，SHA-256 `EC12D066EEA9C554FF99CACEEC76AF17FCBF7D930571317527F9B11FD5B8640A`。
- 主窗口信息层级调整：`dotnet build .\src\DshLauncher\DshLauncher.csproj -c Debug --no-restore` 为 0 warnings、0 errors；Release 完整自测 60/60 通过。已静态校验本轮修改的 4 个 XAML 文件均为有效 XML，`git diff --check` 通过。Windows x64 自包含压缩单文件发布构建成功，完整产物位于 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v0.2.6-20260817-231651`，并已把同一 `DSH Launcher.exe` 复制到桌面同名文件夹顶层；文件为 68,551,629 字节，SHA-256 `175D0272DF3EB104EAD789F45FC11F4ECB05CB3ADA53B4057153F9D07720E30A`。
- 同地址导入覆盖修复：Release 完整自测 60/60 通过；覆盖手动重复导入不新增实例、来源内容覆盖到原 DSH_HOME、`.dsh-launcher` 版本设置保留、运行中实例拒绝覆盖，以及自动检测到错误来源时原位纠正。Windows x64 自包含单文件发布构建成功，已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-import-overwrite-20260817-205734`；`DSH Launcher.exe` 为 72,440,653 字节，SHA-256 `2C252A49058D5C366DB187D64E4C933DF8502B70F089B316CC17079692A4EE07`。
- 启动页本地优先、Provider 卡片移除和导入缺包启动修复：Release 完整自测 60/60 通过；新增覆盖 profile dependencies/bundles/cordis.patch 直接 Plugin 恢复、整棵 node_modules 不复制、旧实例缺包补齐，以及启动页不再引用 Provider 诊断和本地实例先于运行探测显示。Windows x64 自包含单文件发布构建成功，已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-startup-cache-provider-sync-20260817-203515`；`DSH Launcher.exe` 为 72,439,681 字节，SHA-256 `D9CF6248EBDDEF37387AA9DF7E7C97D2029D1A0822E421758E964C26949CE544`。
- 最近实例与旧数据补入修复：Release 完整自测 59/59 通过，新增最近 3 个实例排序覆盖，并扩展自动导入测试验证旧空实例恢复会话/工作区、目标凭据保留、源中缺少凭据追加及来源标记；Windows x64 自包含单文件 publish 为 0 warnings、0 errors。完整测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\recent-instances-sync-final-20260817-192347`，`DSH Launcher.exe` 为 72,439,829 字节，SHA-256 `33965FC98CB59CD3C3B33BB0E1AB32C06BBE931CB2E5DF5E2269AA5D93DB1077`。
- Launcher 后台残留修复：Release 完整自测 58/58 通过，新增命名管道唤醒、不可泵送 WPF 同步上下文和无监听端快速失败覆盖；WPF Release 构建 0 warnings、0 errors。进程级复测在既有 `v0.2.6` 主窗口运行时启动修复版次进程，约 2.3 秒完成旧版兼容唤醒并退出，确认没有留下同路径后台进程且既有主进程继续运行。测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\single-instance-shutdown-fix-20260817-190157`，确认 `DSH Launcher.exe` 存在。
- 默认 DSh 安装目录与 Plugin 安装进度修复：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release --no-restore` 56/56 通过；`dotnet build .\src\DshLauncher\DshLauncher.csproj -c Release --no-restore` 为 0 warnings、0 errors。完整 Release 测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\Test Builds\default-path-plugin-progress-20260817-160310`，确认 `DSH Launcher.exe` 存在。
- `v0.2.6` 发布前完整自测：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release --no-restore` 54/54 通过；Windows x64 自包含单文件 publish 为 0 warnings、0 errors。`DSH Launcher.exe` 为 72,425,534 字节，文件版本 `0.2.6.0`，SHA-256 `A955E5251A1CCD0460343D12048F4268F2007BAE6939D1F99AB8CC18271DC2BD`；完整产物位于 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v0.2.6-20260817-153750`，桌面同名文件夹顶层发布文件哈希一致。
- DSH Desktop v2 Runtime 兼容：官方仓库 API 于 2026-08-17 返回 10,406 Star，最新 Windows Release 为 v2.0.0。下载其 147,818,995 字节安装包到临时目录并用 7-Zip 静态检查，确认真实文件位于 `resources\app.asar.unpacked\lib\desktop-cli.js`、官方 DSh package/CLI 和内置 pnpm；未安装、未修改系统 PATH。解包后用 `DSH Desktop.exe` + `ELECTRON_RUN_AS_NODE=1` 实际执行 bootstrap `--version`，返回 `0.1.0-rc.6`。Launcher 对真实目录在约 438 ms 内只识别出 1 个 `ElectronBootstrap` 运行时，并使用临时 `DSH_HOME` 启动 Web 到健康检查通过后正常停止，无残留进程。最终完整自测 54/54 通过，0 warnings、0 errors；Release 测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-runtime-compatibility-dsh-desktop-v2-20260817`，确认 `DSH Launcher.exe` 存在。
- `v0.2.5` 发布前完整自测：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release` 53/53 通过；Windows x64 自包含单文件 publish 为 0 warnings、0 errors。`DSH Launcher.exe` 为 72,410,054 字节，文件版本 `0.2.5.0`，SHA-256 `C200559201F8B7E1247EA5395C14378AA159D4BD485C6348BA2AD392DD0B0196`；完整产物位于 `C:\Users\121103qwq\Desktop\DSH Launcher\release-v0.2.5-20260817-141605`，桌面同名文件夹顶层发布文件哈希一致。
- Node.js 检查优化：`dotnet build .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release --no-restore` 为 0 warnings、0 errors；随后 `dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release --no-build --no-restore` 53/53 通过。新增覆盖 Node 20/24 并存时按 `engines.node` 选择兼容版本、没有兼容版本时保留最高版本用于诊断。测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-node-check-optimization-20260817-140149`，确认 `DSH Launcher.exe` 存在。
- 设置页资源修复：`dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release --no-restore` 53/53 通过，新增 `Main window code resource references` 回归检查。测试构建已复制到 `C:\Users\121103qwq\Desktop\DSH Launcher\test-settings-open-fix-20260817-135419`，确认 `DSH Launcher.exe` 存在。
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
- 本地 `artifacts/`、`src/DshLauncher/artifacts/` 和根目录既有 WebView2 XML 是未跟踪诊断/构建文件，不纳入源码修改。
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

- DeepSeek 独立任务栏分组已通过代码和回归测试，仍需在 Windows Shell 中实际打开一次 Chat 窗口确认视觉分组；本轮按默认规则未使用 Computer Use。
- Source 项目的 `.dsh/skills` 和 `.agents/skills` 是 DSh 项目级共享只读资源；实例 `DSH_HOME` 内的 Skill 才是版本私有。
- Provider 自动同步按停止版本中 `settings.yaml` 的最新写入时间选择单一来源，没有三方合并；运行中的版本会等停止后参与同步。
- 对话同步是生命周期边界同步，不是多个运行中 DSh 的实时共享写入；外部程序直接删除会话文件不会触发 Launcher 的删除传播。
- 当前只使用已验证的 Chat `localStorage` 预选会话，尚未实现官方 `?session=<id>` deep link。
- MCP 当前是实例级 stdio/streamable-http 配置和 patch 管理，尚未接入完整 MCP Manager 的 connected/needs-auth/authorizing/error、OAuth 和 Tool discovery 状态。
- 主题市场可按需显示仓库 README 中的首选图片，并保留 dsh-market 应用桥接；Wallpaper 仍未建立独立资源格式，也未在用户真实实例上做主题视觉验收。
- dsh-market Plugin 热加载已经在隔离实例上实机通过；主题应用和自动快照回滚仍需在测试实例中完成实机验证。
- GitHub Topic 发现仍未做分页加载；真实第三方 Plugin CLI 对 node_modules 的副作用不能由配置快照完全回滚，失败时仍以官方 CLI 输出和 web profile 自动恢复为准。
- Plugin CLI 没有稳定的总下载字节数，因此界面显示 pnpm 实际解析、复用、下载和添加数量，其他无法量化的阶段保持不确定进度；只有可取得 `Content-Length` 的 Skill ZIP 下载显示字节百分比。
- `.dshsnapshot` 使用 Windows DPAPI CurrentUser，只能由创建它的同一 Windows 用户在本机解密；它是本地回滚点，不是可分享格式，分享继续使用脱敏 `.dshpack`。
- 官方已安装 DSh 的 package metadata 当前未声明 `engines.node` 时，Node 兼容性只能显示“未声明/Unknown”，不会凭空套用固定版本限制。
- 一键运行环境准备的端到端链路（真实无 Node 机器上：下载、UAC 授权、msiexec 安装、自动重检测）仍需实机人工验证。
- DeepSeek Desktop / DSH Desktop 自定义安装位置优先依赖卸载注册表；用户手工移动目录或删除注册表记录后，需要在设置中使用“扫描自定义目录”。上游 DSH Desktop 的 `desktop-cli` 属于私有打包入口，因此 Launcher 只在官方宿主、bootstrap、DSh package/CLI 和 pnpm 全部存在且版本命令通过时启用；上游改变封装结构后会安全降级为未识别并要求重新扫描。
- DSh 自动检测/导入只覆盖定向候选范围，不遍历整块磁盘寻找任意未知目录；用户可显式选择一个上级目录进行有限深度扫描。同地址实例运行时不会自动覆盖，必须先停止后重新导入。
- 空实例首次运行引导已通过代码测试，但官方源/国内镜像选择、DSh 自定义位置、自动创建并启动首个版本仍需人工走完整安装流程。
- Skill 市场当前只取 GitHub 搜索前 30 个名称含 `skill` 的仓库；没有聚合社区 catalog，也没有分页。
- 根目录 `artifacts/` 和 `src/DshLauncher/artifacts/` 是未跟踪的本地诊断/构建目录，未纳入源码提交和 Release。

## 尚未完成内容

- 实机打开一个 Launcher Chat，确认任务栏同时出现相互独立的 Launcher 与黑色 DeepSeek 图标。
- 通过 UI 实际下载并创建一个本机尚未安装的 DSh 版本；本轮只验证了官方版本列表、精确安装代码路径和自测，没有触发外部软件安装。
- 在安装 dsh-market 的测试实例上应用一个主题，并确认主题状态刷新和自动快照可回滚。
- 本轮新增设置、工作区、备份恢复、市场筛选及首次运行引导的实机 UI 验收。
- 版本检查、修复按钮，以及版本控制/版本设置两处快照选择与回滚确认的实机 UI 验收。
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

实机打开一个实例的 DeepSeek Chat，确认它与 Launcher 在 Windows 任务栏中独立显示且关闭 Chat 不停止实例。
