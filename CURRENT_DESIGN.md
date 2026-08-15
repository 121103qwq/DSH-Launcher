# DSH Launcher 当前设计

- 技术栈：.NET 8 WPF，目标 Windows x64；发布版采用自包含单文件，不依赖 Node.js、npm、pnpm 或已经安装的 DSh。
- 界面：无边框 Launcher 窗口；左侧采用 PCL2 风格工作区导航，右侧通过 `ContentControl` 内嵌切换启动、实例、扩展、模型、Agent、对话和设置页面。扩展、模型、Agent、对话管理不使用独立弹窗；Chat WebView2 仍可作为独立窗口打开。代码、视觉和素材独立实现。
- 实例隔离：Manager 数据位于 `%USERPROFILE%\\Documents\\DeepSeek\\launcher`；每个实例使用 `instances\\<id>\\dsh-home`，子进程同时注入 `DSH_HOME` 和实例专用 `DSH_AGENTS_HOME`。注册记录会拒绝越界 HOME、重解析点、重复 ID 和重复根目录；取消注册不删除实例数据。
- 生命周期：`installed` 使用注册的 `dsh` 入口，`source` 使用 Source `package.json`/`apps/cli/package.json` 声明的 Node.js engine；Runner 使用 loopback 空闲端口、HTTP 健康检查、实例独占锁和整棵子进程树清理。运行态区分 Launcher 管理的 `Managed` 与只连接已记录 loopback Web 端点的 `Attached`；Attached 可打开 Web UI，但 Stop、Restart 和退出清理都不触碰外部进程。启动成功后可打开独立 Chat WebView2 窗口；关闭 Chat 不停止实例。
- Source：读取项目根 `package.json`、包管理器字段/锁文件、构建脚本、依赖目录和 CLI 入口；准备阶段执行 install 和 `run build`，只接受实际找到的 CLI 构建入口，超时/取消/非零退出会保留诊断并清理进程树。
- 扩展：Plugin 依据 DSh `profiles\\web\\package.json` 的 dependencies 与 `dsh.profile.bundles` 管理；安装、更新、删除通过 DSh CLI 完成。Skill 使用 DSh 的实例 `skills`、实例 `.agents\\skills` 和项目 `.dsh\\skills`/`.agents\\skills` 根；Agent Preset 使用实例 `.agent-presets`。Workflow 当前表示 DSh 随附 standard preset 提供的能力，不虚构独立目录。
- MCP：配置符合 DSh MCP client 的 `stdio` 或 `streamable-http` 结构，元数据保存在实例 `.dsh-launcher\\mcp.json`，启用项生成实例 `launcher.patch.yml`，启动时通过 `--patch` 叠加；配置与 Plugin/Skill/Preset 修改都要求实例停止。
- 模型：编辑实例 `settings.yaml` 中的 `llm-deepseek` 和 `llm-pi-ai.providers`；保留无关顶层段落，只写环境变量引用、URL 和模型目录，不写 API Key 明文。
- 对话：按当前 DSh JSONL 持久化格式扫描实例 `sessions`，识别 `session.jsonl` 和 `session.jsonl.zstd`；压缩文件按首个 Zstandard frame 读取 session header，要求 `version=0`、安全非负整数 `createdAt`/`delegationDepth` 及当前字段类型，头部有效的会话可从 Chat 入口预选，文件可导入、导出、备份或删除并保留原始格式，修改类操作要求实例停止。无法读取 header 的文件仍列出但禁止打开；导出目标会归一化重复的 `.jsonl`/`.jsonl.zstd` 后缀。
- Node/DSh 检测：检查 PATH 和 Windows 常见目录，实际执行版本命令；多个 Node 候选优先选择最高可解析版本，并从已检测 DSh 包或 Source package metadata 读取 `engines.node`，显示 `Missing`、`Compatible`、`Incompatible` 或 `Unknown`。官方包未声明 engine 时不额外写死版本限制。单候选检测超时会清理进程树，窗口关闭会取消检测；Windows `.cmd` 入口通过 `cmd.exe` 调用。Node.js 下载提供官方页和 npmmirror，DSh 安装提供 npm 官方源和 npmmirror 选择。
