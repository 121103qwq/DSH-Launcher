# DSH Launcher 开发状态

## 当前目标

按构建提示词继续维护 Windows x64 的独立 DSH Launcher；0.1.3 已完成第一版生态和对话管理入口，并已提交、推送和发布。

## 已完成内容

- 已有 .NET 8 WPF Launcher 主窗口、启动页骨架和基础导航；发布版为自包含单文件，不依赖已安装的 Node.js、npm、pnpm 或 DSh。
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
- 已实现 `ExtensionService` 与扩展窗口：按 DSh 实际 profile 结构列出 Plugin，支持 Plugin 安装/更新/删除/启停；按 DSh 实际 Skill 根导入/删除 Skill；管理 MCP stdio/streamable-http 配置；导入/删除用户 Agent Preset；Workflow 仅显示随附 standard preset 能力，不伪造 DSh 不认识的目录。扩展写入前会拒绝实例运行状态、重解析点、越界路径、危险包名和命令行控制字符。
- 已实现 `ModelService` 与模型窗口：读写 `settings.yaml` 的 `llm-deepseek`、`llm-pi-ai.providers`，保留无关顶层段落，原子写入无 BOM，只保存 API Key 环境变量名；模型配置修改要求实例停止。
- 已实现 `ConversationService` 与对话窗口：按 DSh JSONL 会话目录列出有效和压缩日志，支持未压缩会话导入、导出、备份和删除，并校验 sessions 根、文件名和重解析点；打开会话通过 Chat 的 `localStorage` 预选 session ID，实例未运行或会话头部无效时拒绝打开。
- 已补充用户操作回归保护：空实例入口、实例运行中修改、Skill/Preset 自包含目录复制、MCP serverName 注入、模型配置无关段落保留、API Key 不落盘、会话路径穿越、压缩会话和重复导入均有自测覆盖。
- 已添加无外部 NuGet 依赖的 `DshLauncher.SelfTest` 控制台测试项目，覆盖注册往返、重复目录拒绝、隔离 HOME、Source 检查、当前机器 DSh 检测、安装缺失环境保护、Source 直接启动保护、启动/健康检查/重复启动/跨 Runner 拒绝/停止/重启/接管，以及生态/模型/会话边界。
- 当前功能分支为 `agent/harden-node-detection`，GitHub PR #1 当前为 OPEN/DRAFT，目标分支为 `main`；0.1.3 代码提交为 `fe820889bd79548169f1b38195d92678bf23cf66`，标签为 `v0.1.3`。
- 本次 0.1.3 已生成同名发布目录 `publish\\DSH Launcher\\DSH Launcher.exe`；文件版本为 `0.1.3.0`，SHA-256 为 `7A20C3789738A264B5BC2FC30D5DA42BAC5972845BAB5CE751B9BA0052307395`。仓库工作目录中的旧版 `DSH Launcher\\DSH Launcher.exe` 当前仍被用户原有 Launcher 进程占用，因此没有强制结束进程或覆盖该锁定文件。
- GitHub Release `v0.1.3` 已正式发布，资产已上传并核对为 1 个 Windows EXE；Release 地址为 `https://github.com/121103qwq/DSH-Launcher/releases/tag/v0.1.3`。

## 当前主要相关文件

- `src/DshLauncher/MainWindow.xaml`、`src/DshLauncher/MainWindow.xaml.cs`：主窗口界面、导航和检测状态交互。
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
- `src/DshLauncher/Services/ExtensionService.cs`、`ExtensionWindow.xaml(.cs)`：Plugin、Skill、MCP、Agent Preset、Workflow 列表和实例级变更。
- `src/DshLauncher/Services/ModelService.cs`、`ModelWindow.xaml(.cs)`：Provider/model settings 读写和凭据引用保护。
- `src/DshLauncher/Services/ConversationService.cs`、`ConversationWindow.xaml(.cs)`：DSh session 文件列表、文件操作和 Chat 会话预选。
- `src/DshLauncher/Models/EcosystemModels.cs`：扩展、MCP、Provider 和会话记录模型。
- `tests/DshLauncher.SelfTest/Program.cs`：当前最小自测入口。
- `src/DshLauncher/DshLauncher.csproj`：.NET 8、win-x64、自包含单文件发布配置，并引用 `Microsoft.Web.WebView2 1.0.4078.44`。
- `CURRENT_DESIGN.md`：当前有效设计约束。

## 已执行测试及结果

- `git diff --check`：通过。
- `dotnet build .\\src\\DshLauncher\\DshLauncher.csproj -c Release -r win-x64 --no-restore`：通过，0 warnings、0 errors。
- `dotnet build .\\tests\\DshLauncher.SelfTest\\DshLauncher.SelfTest.csproj -c Release --no-restore`：通过，0 warnings、0 errors。
- `dotnet run --project .\\tests\\DshLauncher.SelfTest\\DshLauncher.SelfTest.csproj -c Release --no-build`：通过，16/16；除既有生命周期覆盖外，新增扩展隔离、模型 settings 回环、会话文件管理、空/越界/运行中/压缩日志等用户操作边界均通过。
- `dotnet publish .\\src\\DshLauncher\\DshLauncher.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\\publish\\ui-next`：通过；临时自包含发布版实际打开扩展中心、模型与 Provider、对话管理窗口，空会话列表正常显示，随后已关闭临时进程。
- `dotnet publish .\\src\\DshLauncher\\DshLauncher.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\\publish\\DSH Launcher`：通过；同名发布目录中的单文件版本和 SHA-256 已核对。
- 临时 UI 回归后确认用户原有 `main\\DSH Launcher\\DSH Launcher.exe` 与 DeepSeek Desktop DSh 进程仍在，未启动或停止用户既有 DSh 实例。

## 已知问题

- 当前自动化测试尚未覆盖 Node.js 检测的超时/取消、DSh 检测超时、Source 异常 `package.json`、DSh 安装命令的真实联网执行和所有 UI 错误提示边界。
- 本次未对真实网络 Plugin 执行安装/更新/删除，避免修改用户实例；服务和临时 UI 窗口已验证。压缩会话仍不能导入或通过 Chat 预选打开。
- 当前分支仍是 Draft PR，尚未合并到 `main`；这不影响已发布的 `v0.1.3` Release。

## 尚未完成内容

- 仓库工作目录中的旧版顶层文件尚未覆盖，因为它仍被用户原有 Launcher 锁定；当前版本已在独立同名发布目录生成并核对。
- `v0.1.3` 的提交、分支推送、标签推送和 GitHub Release 已完成；工作树仍保留一个因用户进程锁定而无法覆盖的旧版顶层 EXE 差异。

## 已尝试但已放弃的方案

- 当前仓库、Git 提交和现有文档中没有可确认的已放弃实现或方案记录。

## 下一步最直接的任务

- 下一版优先补齐 Zstandard 会话的读取/打开能力，并继续覆盖真实 UI 的错误提示边界；处理顶层旧版 EXE 前仍不要强制结束用户原有 Launcher 进程。
