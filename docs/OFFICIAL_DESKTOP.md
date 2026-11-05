# 官方 DeepSeek Harness 桌面端适配

已在 Windows Server 2025 VM 中用官方安装包实测确认（`https://download.deepseek.com/desktop/dsh-latest-windows-x64.exe`，0.2.1-rc.5，`/S` 静默安装）：
- productName `DeepSeek Harness`，NSIS，per-user；默认安装目录 `%LOCALAPPDATA%\Programs\DeepSeek Harness`，主程序 `DeepSeek Harness.exe`，卸载程序 `Uninstall DeepSeek Harness.exe`。
- 卸载项位于 `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\<GUID>`：`DisplayName` 为 **`DeepSeek Harness <版本>`**（如 `DeepSeek Harness 0.2.1-rc.5`），`DisplayVersion`、`InstallLocation` 均有值，`Publisher` 为空。
- EXE `ProductName`/`FileDescription` = `DeepSeek Harness`，`FileVersion` = 完整版本（含预发布后缀）。
- `resources/app-update.yml`：`provider: generic`、`channel`（如 `nightly`）、`url`。
- dsh 运行时打包在 `resources/app.asar`（及 `app.asar.unpacked`、`resources/runtime`），Launcher 不把它当 Node/dsh 运行时使用。
- 数据目录：`DSH_HOME`，未设置时为 `%USERPROFILE%\.dsh`。
- 单实例：Electron `app.requestSingleInstanceLock()`（进程级，与 DSH_HOME 无关）。二次启动只会唤醒已有窗口，此时传入的 `DSH_HOME` 不生效。
- `$DSH_HOME/profiles/desktop/lock` **不是**常驻锁：桌面端仅在准备/修改 profile 期间以 `wx` 创建该文件（内容为 PID），操作结束即删除；平时不存在。因此 Launcher 以进程检测判断占用，而不是检查这个文件。
- 运行时有多个同名进程：主进程 + Chromium renderer/GPU/utility 子进程。子进程在沙箱中，读取其环境会失败，但它们继承主进程环境；Launcher 只把主进程（父进程不是同名 EXE 的那个）视为 HOME 占用者。
- 官方不建议 CLI dsh 与桌面端同时使用同一 DSH_HOME。

实现：检测官方安装（不作为运行时候选）；`"DSH Launcher.exe" open-official-desktop [--instance-id <id>]` 以实例 DSH_HOME 启动（不指定实例时使用默认 HOME）；
官方桌面端运行时阻止 Launcher 在同一 DSH_HOME 上启动或修改实例。

启动页的“在官方桌面端打开”与 CLI 共用打开流程；未检测到安装或同 HOME 的 Launcher 实例运行时禁用并显示原因。尚无版本时，启动页空状态和首次运行引导提供“打开官方桌面端”（默认 HOME）。安装检测在当前主窗口中缓存，刷新运行环境时重新检测。
版本控制列表以“官方桌面端 / 只读”显示安装，不能用作托管 Runtime，也不参与实例复制、删除、修复和快照。版本优先读取 EXE FileVersion/ProductVersion，缺失时回退卸载项 DisplayVersion；可选读取 `resources/app-update.yml` 的顶层 `channel:` / `url:`，仅在详情展示存在的值，不修改配置或请求更新地址。

## npm 版 dsh 与 Node 版本
npm 上的 `@deepseek-ai/dsh` 0.2.x 未声明 `engines.node`，但其 `lib/bin.js` 只在 `import.meta.main` 为真时运行 CLI。Node 22.18 之前（及 24.0–24.1）没有 `import.meta.main`，`dsh --version` 会无输出且退出码 0。Launcher 对这类包推断最低要求 `^22.18.0 || >=24.2.0`，在安装前检查并在失败时说明原因。
