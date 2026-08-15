# DSH Launcher 开发状态

## 当前目标

按构建提示词继续维护 Windows x64 的独立 DSH Launcher；当前源码和发布版本为 0.1.6，启动、实例和生态管理采用 PCL2 风格的右侧工作区切换；本次会话边界修复和 Windows Release 已完成。

## 已完成内容

- 已有 .NET 8 WPF Launcher 主窗口、左侧工作区导航和启动页；发布版为自包含单文件，不依赖已安装的 Node.js、npm、pnpm 或 DSh。
- 已将启动页与实例页分成不同的右侧布局：启动页聚焦当前实例和运行环境，实例页显示实例列表、注册入口和当前实例操作。
- 已将扩展、模型、Agent、对话管理改为主窗口右侧 `ContentControl` 的内嵌页面；原管理窗口保留文件名和服务调用，但 XAML 根已改为 `UserControl`。Agent 页面只显示 Skill、Agent Preset、Workflow，并隐藏不适用的 Plugin 操作；Chat WebView2 仍是独立窗口。
- 已实现 Node.js 检测：检查 PATH 和 Windows 常见安装目录，并通过 `node.exe --version` 验证可运行性。
- Node.js 检测已异步执行；单个候选的进程与输出总超时为 2 秒，超时会清理进程树；窗口关闭时会取消检测；刷新操作在检测期间会被限制。
- 已实现 DSh 运行时检测：识别 PATH 中的 `dsh.cmd`/`dsh.exe`，验证 `--version`，解析 DSh 包根目录和版本；Windows `.cmd` 使用 `cmd.exe` 调用。
- 已实现 `ManagerInstance` 注册：支持 installed/source 类型、重复目录校验、JSON 原子保存和每实例独立 `DSH_HOME`；当前实际 UI 测试已注册 1 个 installed DSh 实例。
- 已实现 Source 项目检查：读取 `package.json`、包管理器和锁文件、构建脚本、依赖目录、CLI 入口，并把已找到的构建入口纳入状态显示。
- 已实现 Source 准备服务：按项目声明的 npm/pnpm/yarn/bun 选择命令，缺少 `node_modules` 时执行 install，再执行 `run build`；超时、取消和非零退出会清理包管理器进程树并返回输出，构建后检查 `apps/cli/lib/bin.js` 等实际入口。
- 已实现 Source 启动：使用满足 `^22.19.0 || >=24.0.0` 的 Node.js 运行构建入口，复用 installed DSh 的 DSH_HOME 隔离、loopback 端口、健康检查、停止/重启和跨 Launcher 互斥锁。
- 已修正 Node 候选选择：多个可用 `node.exe` 时选择最高可解析版本，避免 PATH 中旧 Node 覆盖满足 Source 要求的版本。
- 已修正停止状态误报：停止未被当前 Runner 管理的进程失败时不再直接保存为“已停止”，而是保留错误状态和诊断。
- 已实现 installed DSh 生命周期：按实例设置 `DSH_HOME`，分配 loopback 空闲端口，启动 `dsh web`，等待 HTTP 可访问，支持停止和重启；运行进程退出或 Launcher 重启时不会保留虚假的 Running 状态。
- 已实现同一 `DSH_HOME` 的跨 Runner 本地独占锁文件，避免两个 Launcher 同时写入同一实例数据；锁文件位于用户本地 Launcher 锁目录，不会被 DSh 的 DSH_HOME watcher 监听；Runner 还会清理整个子进程树。
- 已实现 `ExtensionService` 与内嵌扩展/Agent 页面：按 DSh 实际 profile 结构列出 Plugin，支持 Plugin 安装/更新/删除/启停；按 DSh 实际 Skill 根导入/删除 Skill；管理 MCP stdio/streamable-http 配置；导入/删除用户 Agent Preset；Workflow 仅显示随附 standard preset 能力，不伪造 DSh 不认识的目录。扩展写入前会拒绝实例运行状态、重解析点、越界路径、危险包名和命令行控制字符。
- 已实现 `ModelService` 与内嵌模型页面：读写 `settings.yaml` 的 `llm-deepseek`、`llm-pi-ai.providers`，保留无关顶层段落，原子写入无 BOM，只保存 API Key 环境变量名；模型配置修改要求实例停止。
- 已实现 `ConversationService` 与内嵌对话页面：按 DSh JSONL 会话目录列出有效和压缩日志，使用 `ZstdSharp.Port 0.8.8` 读取压缩会话首个 Zstandard frame 的 session header；支持 `.jsonl` / `.jsonl.zstd` 导入、导出、备份和删除并保留原始格式，校验 sessions 根、文件名和重解析点；打开会话通过 Chat 的 `localStorage` 预选 session ID，实例未运行或会话头部无效时拒绝打开。
- 已修复会话兼容性与导出边界：header 校验与当前 DSh `version=0`、`createdAt`、`delegationDepth` 及可选字段规则一致，字段类型异常的文件只会被标记为无效；导出保存名去除已有格式后缀，服务层同时归一化重复后缀，避免生成 `*.jsonl.zstd.jsonl.zstd`。
- 已补充用户操作回归保护：空实例入口、实例运行中修改、Skill/Preset 自包含目录复制、MCP serverName 注入、模型配置无关段落保留、API Key 不落盘、会话路径穿越、有效/损坏 Zstandard 会话和重复导入均有自测覆盖。
- 已添加 `DshLauncher.SelfTest` 控制台测试项目，覆盖注册往返、重复目录拒绝、隔离 HOME、Source 检查、当前机器 DSh 检测、安装缺失环境保护、Source 直接启动保护、启动/健康检查/重复启动/跨 Runner 拒绝/停止/重启/接管，以及生态/模型/会话边界；测试项目与 Launcher 共用 `ZstdSharp.Port 0.8.8`。
- 当前功能分支为 `agent/harden-node-detection`，GitHub PR #1 当前为 OPEN/DRAFT，目标分支为 `main`；源码提交为 `40d3175`，发布产物提交为 `4ec7057`，标签为 `v0.1.5`。
- 0.1.5 已生成并核对 `publish\\release-0.1.5\\DSH Launcher.exe`；文件版本为 `0.1.5.0`，SHA-256 为 `52E673B8CFF57BC8CAAA43EEB2351129AF4E1D14D792D07047E6F65888A221C8`；同一文件已复制到 `DSH Launcher\\DSH Launcher.exe` 顶层位置，两个文件哈希一致。
- GitHub Release `v0.1.5` 已正式发布并核对为 1 个 Windows EXE；GitHub 资产名为 `DSH.Launcher.exe`，远端 digest 为 `sha256:52e673b8cff57bc8caaa43eeb2351129af4e1d14d792d07047e6f65888a221c8`；Release 地址为 `https://github.com/121103qwq/DSH-Launcher/releases/tag/v0.1.5`。
- 0.1.6 源码提交为 `64fcf64`，Windows 产物提交为 `9972091`；`publish\\release-0.1.6\\DSH Launcher.exe` 和顶层 `DSH Launcher\\DSH Launcher.exe` 文件版本均为 `0.1.6.0`，SHA-256 均为 `0A31896F353FAEF2572A7E281CDF528B511E2CADFCA6AE3464E54CEE9E0BA6FF`。
- GitHub Release `v0.1.6` 已正式发布并核对为 1 个 Windows EXE；资产名为 `DSH.Launcher.exe`，远端 digest 与本地 SHA-256 一致；Release 地址为 `https://github.com/121103qwq/DSH-Launcher/releases/tag/v0.1.6`。

## 当前主要相关文件

- `src/DshLauncher/MainWindow.xaml`、`src/DshLauncher/MainWindow.xaml.cs`：主窗口界面、PCL2 风格导航、启动/实例布局和右侧内嵌页面切换。
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
- `src/DshLauncher/Services/ModelService.cs`、`ModelWindow.xaml(.cs)`：内嵌 Provider/model settings 页面、读写和凭据引用保护。
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
- 本次修复后的同一自测再次通过：`dotnet build` 源码和测试项目均为 0 warnings、0 errors，`dotnet run ... --no-build` 为 16/16；额外覆盖重复压缩后缀归一化和字段类型异常 header 容错。
- 已用自包含临时发布版进行 Computer Use 实测：有效 `.jsonl.zstd` 在对话右侧页面显示 `ui-zstd-test` 和 `C:\\work\\ui-zstd`，打开时因测试实例保持停止而显示预期“实例没有运行”提示；损坏压缩文件被标记为无效，打开时显示 Zstandard header 错误提示。临时窗口已关闭，测试夹具已移出实例目录。
- `dotnet publish .\\src\\DshLauncher\\DshLauncher.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\\publish\\release-0.1.5`：通过；文件版本为 `0.1.5.0`，SHA-256 已核对。
- 0.1.5 最终发布版已用 Computer Use 实测：启动页显示 Core 0.1.5，扩展、模型、Agent、对话均在主窗口右侧切换；对话页显示 `session.jsonl / .zstd` 文案且未出现管理弹窗，测试进程随后已关闭。
- 本次修复使用当前源码 Release 编译输出做 Computer Use 回归：真实 DSh 创建的有效 `.jsonl.zstd` 会话在 Launcher 对话页显示为有效并可打开；打开后 Chat 的 `deepseek` 工作区和 `新会话` 条目处于选中状态，Launcher 显示 `已打开对话`；停止实例后导出生成单一 `.jsonl.zstd`，未生成重复后缀文件。回归用 Chat、实例和 Launcher 已关闭，导出文件与测试会话已移出仓库和实例目录至 `%TEMP%` 保留。
- v0.1.6 发布构建已完成：Windows x64 自包含单文件版本为 `0.1.6.0`，本地发布目录与顶层 EXE 哈希一致；GitHub 标签解引用指向 `9972091`，Release 资产上传状态为 `uploaded`，远端 digest 与本地 SHA-256 一致。
- 分支推送 dry-run 和实际推送均通过；远端 `v0.1.5` tag 指向发布产物提交 `4ec7057`，Release 资产数量为 1，资产哈希与本地发布文件一致。
- 临时 UI 回归后确认用户原有 `main\\DSH Launcher\\DSH Launcher.exe` 与 DeepSeek Desktop DSh 进程仍在，未启动或停止用户既有 DSh 实例。

## 已知问题

- 当前自动化测试尚未覆盖 Node.js 检测的超时/取消、DSh 检测超时、Source 异常 `package.json`、DSh 安装命令的真实联网执行和所有 UI 错误提示边界。
- 本次未对真实网络 Plugin 执行安装/更新/删除，避免修改用户实例；服务和临时 UI 页面已验证。Chat 仍按现有设计使用独立 WebView2 窗口；打开合法会话仍要求实例运行并有可用 Chat 地址，这是当前生命周期约束。
- 当前分支仍是 Draft PR，尚未合并到 `main`；这不影响已发布的 `v0.1.6` Release。

## 尚未完成内容

- 本次 0.1.6 构建、推送、标签和 GitHub Release 已完成；后续未覆盖的测试边界见“已知问题”。

## 已尝试但已放弃的方案

- 曾将扩展、模型、对话作为独立管理窗口打开；该方案已放弃，当前统一改为主窗口右侧内嵌页面，只有 Chat WebView2 保持独立窗口。
- 曾用旧版 `version=1` 且缺少 `delegationDepth` 的临时 header 验证打开流程；该格式不被当前 DSh 识别，已放弃，回归改用真实 DSh UI 创建的合法会话。

## 下一步最直接的任务

- 继续按真实 UI 回归覆盖“已知问题”中的边界；当前 v0.1.6 发布交付已完成。
