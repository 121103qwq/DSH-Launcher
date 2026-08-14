# DSH Launcher 开发状态

## 当前目标

按构建提示词继续实现 Windows x64 的独立 DSH Launcher；当前已完成第一批 DSh 运行时识别、已安装实例注册和 Source 项目识别基础。

## 已完成内容

- 已有 .NET 8 WPF Launcher 主窗口、启动页骨架和基础导航；发布版为自包含单文件，不依赖已安装的 Node.js、npm、pnpm 或 DSh。
- 已实现 Node.js 检测：检查 PATH 和 Windows 常见安装目录，并通过 `node.exe --version` 验证可运行性。
- Node.js 检测已异步执行；单个候选的进程与输出总超时为 2 秒，超时会清理进程树；窗口关闭时会取消检测；刷新操作在检测期间会被限制。
- 已实现 DSh 运行时检测：识别 PATH 中的 `dsh.cmd`/`dsh.exe`，验证 `--version`，解析 DSh 包根目录和版本；Windows `.cmd` 使用 `cmd.exe` 调用。
- 已实现 `ManagerInstance` 注册：支持 installed/source 类型、重复目录校验、JSON 原子保存和每实例独立 `DSH_HOME`；当前实际 UI 测试已注册 1 个 installed DSh 实例。
- 已实现 Source 项目检查：读取 `package.json`、包管理器和锁文件、构建脚本、依赖目录、CLI 入口；当前不执行安装或构建。
- 已添加无外部 NuGet 依赖的 `DshLauncher.SelfTest` 控制台测试项目，覆盖注册往返、重复目录拒绝、Source 检查和当前机器 DSh 检测。
- 当前功能分支为 `agent/harden-node-detection`，已确认的远端基线提交为 `ea2e6d3`；GitHub PR #1 当前为 OPEN/DRAFT，目标分支为 `main`。
- 顶层发布文件 `DSH Launcher\\DSH Launcher.exe` 已存在；当前文件大小为 71,597,369 字节，SHA-256 为 `0C81711D339427A886435EB1749FCEDE546F5FA3AF27E9DB77F52E97FEA96704`。

## 当前主要相关文件

- `src/DshLauncher/MainWindow.xaml`、`src/DshLauncher/MainWindow.xaml.cs`：主窗口界面、导航和检测状态交互。
- `src/DshLauncher/Services/NodeRuntimeDetector.cs`：Node.js 运行环境检测与进程生命周期处理。
- `src/DshLauncher/Models/NodeRuntimeInfo.cs`：检测结果模型。
- `src/DshLauncher/Models/ManagerInstance.cs`、`DshRuntimeInfo.cs`、`SourceProjectInfo.cs`：实例、DSh 运行时和 Source 检查结果模型。
- `src/DshLauncher/Services/InstanceRegistry.cs`、`LauncherPaths.cs`：实例注册持久化和隔离目录。
- `src/DshLauncher/Services/DshRuntimeDetector.cs`、`SourceProjectInspector.cs`：DSh 检测和 Source 项目检查。
- `tests/DshLauncher.SelfTest/Program.cs`：当前最小自测入口。
- `src/DshLauncher/DshLauncher.csproj`：.NET 8、win-x64、自包含单文件发布配置。
- `CURRENT_DESIGN.md`：当前有效设计约束。

## 已执行测试及结果

- `dotnet build .\\src\\DshLauncher\\DshLauncher.csproj -c Release -r win-x64`：通过，0 warnings、0 errors。
- `git diff --check`：通过。
- `dotnet run --project .\\tests\\DshLauncher.SelfTest\\DshLauncher.SelfTest.csproj -c Release`：通过，4/4；实际识别到 DSh `0.1.0-rc.6`。
- 已通过发布版临时 UI 模拟：注册已安装 DSh 后列表显示 1 个实例、状态为可用且 `DSH_HOME` 指向实例隔离目录；启动入口显示“启动、端口分配和健康检查将在下一模块接入”；Source 选择器可打开并取消。
- 已检查发布文件存在、大小和 SHA-256；检查结果如上。

## 已知问题

- 当前自动化测试尚未覆盖 Node.js 检测的超时/取消、DSh 检测超时、Source 异常 `package.json` 和 UI 错误提示边界。
- DSh 安装按钮目前只打开官方说明，不执行安装；已注册实例的启动、端口分配和健康检查尚未实现。
- 当前分支仍是 Draft PR，尚未合并到 `main`；仓库现有发布标签为 `v0.1.0`，没有已确认的 `v0.1.1` 发布记录。

## 尚未完成内容

- DSh 实例安装、启动、健康检查、端口/WebView 连接和运行生命周期；已完成注册与实例数据隔离基础。
- Source DSh 项目管理、对话管理，以及 Plugin、Skill、MCP、Workflow、Preset 等扩展能力。

## 已尝试但已放弃的方案

- 当前仓库、Git 提交和现有文档中没有可确认的已放弃实现或方案记录。

## 下一步最直接的任务

- 实现 DSh 安装流程和实例生命周期：安装/准备、分配空闲端口、启动、健康检查、停止/重启，并补充实际用户失败路径测试。
