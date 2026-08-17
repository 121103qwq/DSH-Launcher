# DSH Launcher

面向 Windows x64 的 [DeepSeek Harness（DSh）](https://github.com/deepseek-ai/deepseek-harness) 图形化启动器与生态管理器。

DSH Launcher 使用 .NET 8 WPF 开发，负责管理多个 DSh 版本、运行实例、Plugin、Skill、Provider 和对话文件。Launcher 本身可以在没有 Node.js 或 DSh 的环境中打开，并提供运行环境检测与安装引导。

## 主要功能

### 多版本与多实例

- 每个版本使用独立的 `DSH_HOME` 与 `DSH_AGENTS_HOME`。
- 支持复制现有版本、新建干净版本、删除版本和修改版本名称。
- 支持导入、导出 `.dshpack` 整合包；密钥、dotenv 和会话不会进入分享包，导入时创建新版本且不覆盖已有版本。
- 同一个 DSh 运行目录可供多个隔离版本使用，并可同时启动多个实例。
- 区分 Launcher 自己启动的 **Managed** 实例与连接外部服务的 **Attached** 实例；Attached 实例不会被停止或重启操作误杀。

### 运行环境

- 检测 Node.js 的 Missing / Compatible / Incompatible / Unknown 状态。
- DSh 只有在命令可执行、官方安装包可解析且两边版本一致时才视为可用；残留命令或损坏安装会进入修复引导。
- 根据当前 DSh 的 `package.json` / `engines.node` 判断 Node.js 兼容性，不长期硬编码版本要求。
- 没有版本时提供首次运行引导，可选择官方源或国内镜像，并设置 DSh 安装位置。
- 支持 installed DSh 与 Source 项目。
- Launcher 不内置 Node.js、npm、pnpm 或 DeepSeek Harness；发布包约 69 MiB 主要来自自包含 .NET、WPF、WebView2 和压缩依赖。

### Plugin 与 Skill 市场

- Plugin 市场聚合社区目录、GitHub `dsh-plugin` 标签和用户自定义目录。
- 缓存优先打开，搜索、分类、来源和排序在本地完成；只有刷新目录时才联网。
- 安装前检查 `package.json`、`dsh.bundle.patch` 和实际安装来源，最后调用官方 DSh Plugin CLI。
- 默认使用快速安装；快速模式失败时可直接用兼容模式重试，也可在设置中把兼容模式设为默认。
- 安装、更新、卸载显示进度，完成后自动刷新当前实例状态。
- 默认 Plugin `@deepseek-ai/dsh-base` 与 `@deepseek-ai/dsh-web-app` 只读保护，不能禁用或删除。
- Skill 市场发现并校验仓库中的单个 `SKILL.md`，支持分类、搜索和按 Skill 目录安装。
- 支持导入本地 Skill 与 Agent Preset，并按当前版本隔离保存。

### Provider、对话与同步

- Provider 启用状态、连接诊断、`/models` 模型列表和思考档位检查。
- Launcher 配置只保存 API Key 的环境变量名称；真实密钥继续由 DSh 官方 `.credentials.yaml` 管理。
- Provider 自动同步只在双方都开启同步且实例已停止时生效，会原子复制官方凭据文件，但不会解析、显示、记录或打包其中的密钥；没有 llm Provider 配置时不会覆盖文件。
- 管理 `session.jsonl` / `session.jsonl.zstd`：查看、打开、导入、导出、备份、恢复和删除。
- 对话列表显示会话名称和所属实例，不直接暴露内部路径 ID。
- 对话可选择版本独立、按工作区同步或全量同步；运行中的版本不会被同步写入。

### 独立聊天窗口

- 每个运行实例使用独立 WebView2 Chat 窗口和任务栏图标。
- 关闭 Chat 窗口不会停止 DSh 实例。
- 再次点击启动或双击运行中的实例，可以重新呼出对应 Chat 窗口。

## 快速开始

1. 从 [Releases](https://github.com/121103qwq/DSH-Launcher/releases/latest) 下载最新的 `DSH Launcher.exe`。
2. 直接运行程序，不需要安装 .NET SDK。
3. 如果本机没有版本，按首次运行引导选择下载源和 DSh 安装位置。
4. 选择或创建版本，点击“启动实例”。

Launcher 启动时不会静默下载或安装内容。Node.js 或 DSh 缺失时，只有在用户确认后才进入准备流程。

## 数据隔离

Launcher 注册信息与各个 DSh 版本的数据分开保存。默认结构如下：

```text
Documents\DeepSeek\launcher\
├─ instances.json
├─ launcher-settings.json
├─ instances\
│  ├─ <version-id>\dsh-home\
│  └─ <version-id>\dsh-home\
└─ backups\
   └─ <version-id>\
```

Plugin、Skill、MCP、Provider、Agent、Settings 和 Conversation 策略都以版本自己的 `DSH_HOME` 为边界。复制版本会复制整套版本数据；新建干净版本只复用 DSh 运行程序，不复用原版本数据。

## 从源码构建

需要安装 .NET 8 SDK。在仓库根目录执行：

```powershell
dotnet build .\src\DshLauncher\DshLauncher.csproj -c Release -r win-x64
```

生成 Windows x64 自包含单文件：

```powershell
dotnet publish .\src\DshLauncher\DshLauncher.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o ".\DSH Launcher"
```

运行当前自测：

```powershell
dotnet run --project .\tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Debug
```

发布文件位于 `DSH Launcher\DSH Launcher.exe`。

## 当前版本

当前源码版本为 **v1.0.0**。下载、变更说明和 SHA-256 信息请查看 [GitHub Releases](https://github.com/121103qwq/DSH-Launcher/releases)。
