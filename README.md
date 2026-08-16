# DSH Launcher

面向 Windows x64 的 DeepSeek Harness（DSh）启动器与生态管理器，基于 .NET 8 WPF，发布为自包含单文件可执行程序。

## 当前版本

当前为 Launcher Core v0.1.11，已实现：

- Windows x64 自包含单文件 WPF 启动器。
- 实例 / 版本管理：每个版本使用独立 `DSH_HOME` 与 `DSH_AGENTS_HOME`，支持已安装 DSh 与 Source 项目的注册与隔离。
- Node.js / DSh 运行环境检测：Missing / Compatible / Incompatible / Unknown 状态，并提供 Node.js 官方页与国内镜像入口。
- DSh 启动、停止、重启、空闲端口分配与 HTTP 健康检查；运行态区分 Launcher 管理的 Managed 与只连接外部服务的 Attached。
- 独立 Chat WebView2 窗口：每个运行实例拥有独立任务栏窗口，关闭聊天窗口不会停止实例。
- Plugin / Skill / MCP / Agent Preset 管理，插件安装、更新、卸载通过官方 DSh CLI 完成。
- 插件市场：多来源（社区目录 / GitHub dsh-plugin 标签 / 自定义目录）、缓存优先、本地即时搜索、分类与排序、安装前 package.json / DSh bundle 校验。
- Provider 管理：启用/禁用、只读 `/models` 诊断、模型列表与思考档位显示；只保存 API Key 环境变量名，不保存密钥明文。
- 对话管理：`session.jsonl` / `session.jsonl.zstd` 列出、导入、导出、备份、删除与同步策略。
- 版本设置与 `.dshpack` 整合包导入 / 导出（导出会清理 API Key、Token、会话等隐私内容）。
- Theme / dsh-market 桥接：通过 dsh-market 应用主题，不直接修改 DSh Web UI 源码。

运行环境说明：Launcher 本身**不内置也不依赖** Node.js、npm、pnpm 或已经安装的 DeepSeek Harness；真正运行 DSh 时仍需要对应的外部运行环境。

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
