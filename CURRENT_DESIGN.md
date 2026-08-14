# DSH Launcher 当前设计

- 技术栈：.NET 8 WPF，目标 Windows x64；发布版为自包含单文件，不依赖 Node.js、npm、pnpm 或已经安装的 DSh。
- 当前界面：无边框 Launcher 窗口，顶部操作栏、左侧工作区导航、启动页实例列表与运行环境卡片；视觉层级参考 PCL2，但未使用其源码或素材。
- 当前能力：启动页骨架、异步 Node.js/DSh 检测、已安装 DSh 注册、Source 项目检查与实例列表、每个实例独立的 DSH_HOME、可取消的重新检测、Node.js 官方安装页和 DSh 官方说明入口。
- 当前状态：DSh 安装执行、启动/端口/健康检查、运行窗口、Source 构建、扩展中心、模型和对话管理尚未实现；界面中的启动入口只显示后续阶段提示，不伪装成已完成能力。
- Node 检测：先检查 PATH 中的 `node.exe`，再检查 Windows 常见 Node.js 安装目录，并执行 `node.exe --version` 验证可运行性；单个候选的总检测时间不超过 2 秒，超时会杀掉整个进程树，窗口关闭时会取消检测。
- DSh 检测：检查 PATH 中的 `dsh.cmd`/`dsh.exe` 等候选，执行 `--version`，并从相邻 `package.json` 解析版本和包根目录；Windows `.cmd` 通过 `cmd.exe` 调用。
- 实例注册：Manager 数据保存在 `%USERPROFILE%\\Documents\\DeepSeek\\launcher\\instances.json`；每个实例使用 `%USERPROFILE%\\Documents\\DeepSeek\\launcher\\instances\\<id>\\dsh-home`，注册时创建目录，取消注册不删除该目录。
- Source 检查：读取项目根目录 `package.json`、包管理器字段/锁文件、构建脚本、依赖目录和 CLI 入口，当前只注册检查结果，不执行安装或构建。
