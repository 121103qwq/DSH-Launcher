# DSH Launcher 当前设计

- 技术栈：.NET 8 WPF，目标 Windows x64；发布版为自包含单文件，不依赖 Node.js、npm、pnpm 或已经安装的 DSh。
- 当前界面：无边框 Launcher 窗口，顶部操作栏、左侧工作区导航、启动页实例列表与运行环境卡片；视觉层级参考 PCL2，但未使用其源码或素材。
- 当前能力：启动页骨架、异步 Node.js/DSh 检测、已安装 DSh 注册、Source 项目检查与实例列表、每个实例独立的 DSH_HOME、可取消的重新检测、Node.js 官方安装页、DSh 安装执行器，以及已安装 DSh 的启动/停止/重启、空闲端口分配和 HTTP 可访问性检查。
- 当前状态：Source 项目仍只检查、不安装依赖或构建；扩展中心、模型和对话管理尚未实现。Source 实例不会被当作已构建运行时启动。
- Node 检测：先检查 PATH 中的 `node.exe`，再检查 Windows 常见 Node.js 安装目录，并执行 `node.exe --version` 验证可运行性；单个候选的总检测时间不超过 2 秒，超时会杀掉整个进程树，窗口关闭时会取消检测。
- DSh 检测：检查 PATH 中的 `dsh.cmd`/`dsh.exe` 等候选，执行 `--version`，并从相邻 `package.json` 解析版本和包根目录；Windows `.cmd` 通过 `cmd.exe` 调用。
- 实例注册：Manager 数据保存在 `%USERPROFILE%\\Documents\\DeepSeek\\launcher\\instances.json`；每个实例使用 `%USERPROFILE%\\Documents\\DeepSeek\\launcher\\instances\\<id>\\dsh-home`，注册时创建目录，取消注册不删除该目录。
- 实例生命周期：只有 `installed` 实例可由 Runner 启动；Runner 将 `DSH_HOME` 注入子进程，绑定 loopback 空闲端口并等待 HTTP 可访问；同一 `DSH_HOME` 由用户本地 Launcher 独占锁文件限制为一个运行实例。Launcher 重启时不会假设旧进程仍归当前 Runner 管理，会把持久化的 `Running` 状态恢复为 `Stopped`。
- Source 检查：读取项目根目录 `package.json`、包管理器字段/锁文件、构建脚本、依赖目录和 CLI 入口，当前只注册检查结果，不执行安装或构建。
