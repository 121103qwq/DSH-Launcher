# DSH Launcher

面向 Windows x64 的 DeepSeek Harness 启动器与生态管理器。

## 当前版本

当前为 Launcher Core 0.1.7，完成了独立 WPF 主窗口、PCL2 风格的信息架构、Node.js/DSh 环境检测、已安装 DSh 实例注册和 Source 项目检查，并已接入 installed DSh 与 Source DSh 的启动、停止、重启、空闲端口与 HTTP 可访问性检查。启动、实例、扩展、模型、Agent、对话和设置均在主窗口右侧切换，管理页不再使用独立弹窗；Chat 仍按运行状态打开独立 WebView2 窗口。Source 启动前会按项目声明的包管理器和 `engines.node` 执行依赖安装、兼容性检查与构建，并在失败时保留诊断；运行态区分 Launcher 管理的 Managed 和只连接外部服务的 Attached。Node 检测会优先选择可用的最高版本，并提供 Missing、Compatible、Incompatible、Unknown 状态及 Node.js 官方/国内镜像入口；DSh 安装提供 npm 官方源和 npmmirror 入口。Node 检测在后台异步执行，单个候选总超时后会清理残留进程；Launcher 自身不依赖 Node.js、npm、pnpm 或已经安装的 DeepSeek Harness。

当前版本还提供：按实例隔离的 Plugin 管理、Skill 导入/删除、MCP stdio/streamable-http 配置、用户 Agent Preset 导入/删除、模型 Provider 配置，以及 DSh `session.jsonl` / `session.jsonl.zstd` 对话列表、导入、导出、备份和删除。模型页面只保存 API Key 环境变量名，不保存密钥明文；扩展和会话文件的修改要求实例已停止。压缩会话按首个 Zstandard frame 读取 session header，合法文件可查看、打开和导入并保留 `.zstd` 格式；损坏或无法解析 header 的文件会保留在列表中但禁止打开。

## 构建

需要安装 .NET 8 SDK。在仓库根目录执行：

```powershell
dotnet build .\src\DshLauncher\DshLauncher.csproj -c Release -r win-x64
```

生成 Windows x64 自包含发布文件：

```powershell
dotnet publish .\src\DshLauncher\DshLauncher.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o ".\DSH Launcher"
```

发布后可直接运行 `DSH Launcher\DSH Launcher.exe`，无需开发环境和命令行。
