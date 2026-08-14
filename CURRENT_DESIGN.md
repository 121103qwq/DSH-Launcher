# DSH Launcher 当前设计

- 技术栈：.NET 8 WPF，目标 Windows x64；发布版为自包含单文件，不依赖 Node.js、npm、pnpm 或已经安装的 DSh。
- 当前界面：无边框 Launcher 窗口，顶部操作栏、左侧工作区导航、启动页实例列表与运行环境卡片；视觉层级参考 PCL2，但未使用其源码或素材。
- 当前能力：启动页骨架、Node.js 检测、Node.js 路径与版本显示、重新检测、Node.js 官方安装页入口。
- 当前状态：DSh 实例、实例隔离、安装/启动流程、Source 项目和扩展中心尚未实现；界面中的实例入口只显示后续阶段提示，不伪装成已完成能力。
- Node 检测：先检查 PATH 中的 `node.exe`，再检查 Windows 常见 Node.js 安装目录，并执行 `node.exe --version` 验证可运行性。
- 数据目录与实例目录：尚未确定，待实例核心实现时再落地并记录。
