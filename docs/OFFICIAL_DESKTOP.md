# 官方 DeepSeek Harness 桌面端适配

已确认（deepseek-ai/deepseek-harness `apps/desktop/README.md`、`scripts/electron-builder-config.mjs`）：
- productName `DeepSeek Harness`，NSIS，per-user（perMachine:false），卸载项记录 InstallLocation。
- dsh 运行时打包在 `resources/app.asar` 内，Launcher 不能把它当 Node 运行时使用。
- Electron 进程级单实例锁，独占 `$DSH_HOME/profiles/desktop`；数据目录 `DSH_HOME` 或 `%USERPROFILE%\.dsh`。
- 官方不建议 CLI dsh 与桌面端同时使用同一 DSH_HOME。

假设（未能从官方源码直接确认）：
- 默认安装目录 `%LOCALAPPDATA%\Programs\DeepSeek Harness`，主程序 `DeepSeek Harness.exe`（electron-builder 默认命名）。
- 二次启动会唤醒已有窗口；进程已运行时传入的 DSH_HOME 不会生效。

实现：检测官方安装（不作为运行时候选）；`"DSH Launcher.exe" open-official-desktop [--instance-id <id>]` 以实例 DSH_HOME 启动（不指定实例时使用默认 HOME）；
官方桌面端运行时阻止 Launcher 在同一 DSH_HOME 上启动实例。

启动页的“在官方桌面端打开”与 CLI 共用打开流程；未检测到安装或同 HOME 的 Launcher 实例运行时禁用并显示原因。安装检测在当前主窗口中缓存，刷新运行环境时重新检测。
版本控制列表以“官方桌面端 / 只读”显示安装，不能用作托管 Runtime，也不参与实例复制、删除、修复和快照。版本优先读取 EXE FileVersion/ProductVersion，缺失时回退卸载项 DisplayVersion；可选读取 `resources/app-update.yml` 的顶层 `channel:` / `url:`，仅在详情展示存在的值，不修改配置或请求更新地址。
