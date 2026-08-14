# DSH Launcher 开发状态

## 当前目标

按构建提示词继续实现 Windows x64 的独立 DSH Launcher；当前已完成 DSh 运行时识别、已安装实例注册与隔离、Source 项目识别与依赖/构建/运行链路，以及 installed DSh 的基础运行生命周期。

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
- 已添加无外部 NuGet 依赖的 `DshLauncher.SelfTest` 控制台测试项目，覆盖注册往返、重复目录拒绝、Source 检查、当前机器 DSh 检测、安装缺失环境保护、Source 直接启动保护、启动/健康检查/重复启动/跨 Runner 拒绝/停止/重启/接管。
- 当前功能分支为 `agent/harden-node-detection`，远端基线为已推送的实例注册提交；GitHub PR #1 当前为 OPEN/DRAFT，目标分支为 `main`。
- 顶层发布文件 `DSH Launcher\\DSH Launcher.exe` 仍是上一版构建产物；本次 0.1.2 代码已通过 Release 编译，但最终顶层单文件需要在提交前重新发布并重新核对 SHA-256。

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
- `tests/DshLauncher.SelfTest/Program.cs`：当前最小自测入口。
- `src/DshLauncher/DshLauncher.csproj`：.NET 8、win-x64、自包含单文件发布配置，并引用 `Microsoft.Web.WebView2 1.0.4078.44`。
- `CURRENT_DESIGN.md`：当前有效设计约束。

## 已执行测试及结果

- `dotnet build .\\src\\DshLauncher\\DshLauncher.csproj -c Release -r win-x64`：通过，0 warnings、0 errors。
- `git diff --check`：通过。
- `dotnet run --project .\\tests\\DshLauncher.SelfTest\\DshLauncher.SelfTest.csproj -c Release --no-restore`：通过，12/12；实际识别到 DSh `0.1.0-rc.6`，Source 安装/构建成功与包管理器失败均被覆盖，Source 本地 HTTP 生命周期、DSh 异常提前退出清理、installed 生命周期/健康检查/停止/重启均通过，跨 Runner 启动被拒绝，测试清理未留下 junction 错误。
- 已通过当前发布版临时 UI 模拟：实际点击启动并打开 Chat，Chat 内出现 DeepSeek Harness 页面；只关闭 Chat 后 Launcher 窗口仍在、`http://127.0.0.1:5500/` 仍返回 HTTP 200；再从 Launcher 点击停止后端口不可访问、DSh 进程退出；右侧当前实例保持选中并显示运行地址，状态可在“运行中/已停止”间切换；Source 选择器可打开并取消。
- 已检查发布文件存在、大小和 SHA-256；检查结果如上。

## 已知问题

- 当前自动化测试尚未覆盖 Node.js 检测的超时/取消、DSh 检测超时、Source 异常 `package.json`、DSh 安装命令的真实联网执行和完整 UI 错误提示边界。
- Source 已实现依赖安装、构建、启动和 Web 健康检查；installed / Source 成功启动后会打开独立 Chat WebView2 窗口，扩展中心、模型和对话管理仍未实现。
- 当前分支仍是 Draft PR，尚未合并到 `main`；下一版 Release 尚未创建。

## 尚未完成内容

- DSh 实例安装/更新后的注册联动、Source 项目依赖安装与构建、Source/installed 的端口生命周期和基础 Chat WebView2 窗口已完成；扩展与对话能力仍未接入。
- Source DSh 项目管理、对话管理，以及 Plugin、Skill、MCP、Workflow、Preset 等扩展能力。

## 已尝试但已放弃的方案

- 当前仓库、Git 提交和现有文档中没有可确认的已放弃实现或方案记录。

## 下一步最直接的任务

- 补充真实发布版 Source 路径的 UI 点击回归，并实现扩展中心的最小可用 Plugin/Skill/MCP 安装与启停管理。
