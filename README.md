# DSH Launcher

面向 Windows x64 的 DeepSeek Harness 启动器与生态管理器。

## 当前版本

当前为 Launcher Core 0.1.2，完成了独立 WPF 主窗口、PCL2 风格的信息架构骨架、Node.js/DSh 环境检测、已安装 DSh 实例注册和 Source 项目检查，并已接入 installed DSh 与 Source DSh 的启动、停止、重启、空闲端口与 HTTP 可访问性检查。Source 启动前会按项目声明的包管理器执行依赖安装和构建，并在失败时保留诊断；Node 检测会优先选择可用的最高版本。Node 检测在后台异步执行，单个候选总超时后会清理残留进程；Launcher 自身不依赖 Node.js、npm、pnpm 或已经安装的 DeepSeek Harness。

当前版本已提供 DSh 安装/更新执行入口和 Source 构建/运行入口，但尚未覆盖 Plugin、Skill、MCP、模型和对话管理；这些入口会明确显示为后续阶段，避免把未完成能力当成已完成能力。

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
