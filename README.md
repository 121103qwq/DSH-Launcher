# DSH Launcher

面向 Windows x64 的 DeepSeek Harness 启动器与生态管理器。

## 当前版本

当前为 Launcher Core 0.1.0，完成了独立 WPF 主窗口、PCL2 风格的信息架构骨架和 Node.js 环境检测。Launcher 自身不依赖 Node.js、npm、pnpm 或已经安装的 DeepSeek Harness。

当前版本暂不实现完整的 DSh 安装、实例注册、Source 构建、Plugin、Skill、MCP、模型和对话管理；这些入口会明确显示为后续阶段，避免把未完成能力当成已完成能力。

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
