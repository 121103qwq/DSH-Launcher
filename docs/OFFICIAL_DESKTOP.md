# 官方 DeepSeek Harness 桌面端适配

已确认（deepseek-ai/deepseek-harness `apps/desktop/README.md`、`scripts/electron-builder-config.mjs`）：
- productName `DeepSeek Harness`，NSIS，per-user（perMachine:false），卸载项记录 InstallLocation。
- dsh 运行时打包在 `resources/app.asar` 内，Launcher 不能把它当 Node 运行时使用。
- Electron 进程级单实例锁，独占 `$DSH_HOME/profiles/desktop`；数据目录 `DSH_HOME` 或 `%USERPROFILE%\.dsh`。
- 官方不建议 CLI dsh 与桌面端同时使用同一 DSH_HOME。

假设（未能从官方源码直接确认）：
- 默认安装目录 `%LOCALAPPDATA%\Programs\DeepSeek Harness`，主程序 `DeepSeek Harness.exe`（electron-builder 默认命名）。
- 二次启动会唤醒已有窗口；进程已运行时传入的 DSH_HOME 不会生效。

实现：检测官方安装（不作为运行时候选）；`dsh-launcher open-official-desktop [实例]` 以实例 DSH_HOME 启动；
官方桌面端运行时阻止 Launcher 在同一 DSH_HOME 上启动实例。
