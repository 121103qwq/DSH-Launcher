# 与官方 DeepSeek Harness 桌面端共存（Harness Compat）

状态：v1 已实现（Launcher 1.3.0）。适用于官方桌面端 0.2.x（实测 0.2.1-rc.5，Windows 按用户 NSIS 安装）。

## 1. 背景与目标

官方桌面端（Electron）自带 DSh 运行时，单实例运行，数据放在 `DSH_HOME`（默认 `%USERPROFILE%\.dsh`）。
Launcher 管理多个 npm 版 DSh，每个版本使用独立的 `DSH_HOME`。目标是：**两者可以同时安装、同时使用，
不会相互覆盖数据，并且用户可以在两者之间显式迁移配置。**

## 2. DSH_HOME 策略

| 模式 | 说明 | 默认 |
| --- | --- | --- |
| 隔离（Isolated） | 每个 Launcher 版本有自己的 `instances\<id>\dsh-home`；官方桌面端继续使用自己的默认 HOME。 | ✅ |
| 共享（关联已有 DSH_HOME） | 用户通过“关联已有 DSH_HOME”把官方 HOME 注册成 Launcher 版本；Launcher 只记录路径，不复制、不初始化、删除时只解除关联。 | 可选 |

默认 HOME 的解析顺序与官方一致：进程 `DSH_HOME` → 用户级 `DSH_HOME` 环境变量 → `%USERPROFILE%\.dsh`。

## 3. 检测

- 安装位置：卸载注册表（HKCU/HKLM，含自定义 `/D=` 目录）+ 常见目录；只认带官方签名的 `DeepSeek Harness.exe`。
- 版本：安装目录下 `resources\app.asar` 内 package.json / 文件版本。
- 更新通道：`resources\app-update.yml`（`provider`、`url`、`channel`，例如 `nightly`，feed 为
  `https://download.deepseek.com/dsh-desk/feeds/win-x64/`）。v1 在导入/导出对话框中显示通道。
- 运行状态：按可执行文件路径匹配进程；进程使用的 HOME 通过读取进程环境变量 `DSH_HOME` 得到（含子进程/僵尸子进程树）。

## 4. 启动与交接（handoff）

- “在官方桌面端打开”：以版本的 `DSH_HOME` 启动官方程序。
- 官方是单实例：若已在运行，第二次启动只会唤醒原窗口并 **忽略** 新的 `DSH_HOME`。因此：
  - 官方正在使用别的 HOME → Launcher 拒绝并提示先退出官方桌面端（1.3.0 修复，之前会悄悄打开错误的数据）。
  - 官方正在使用同一个 HOME → 只唤醒窗口。
- 同一 HOME 只能有一个进程：Launcher 正在运行该版本时，必须先停止才能交给官方，反之亦然。

## 5. 数据与配置互通

| 数据 | 位置 | v1 处理 |
| --- | --- | --- |
| Provider 设置 | `settings.yaml`（官方 0.2.1 默认 HOME 中可能不存在） | 可导入/导出 |
| 凭据 | `.credentials.yaml`（含 browser-session 等） | 可导入/导出（只复制文件，不解析、不打印） |
| 会话 | `sessions\` | 不自动复制；用 Launcher 的对话同步/导入 |
| Profile | `profiles\desktop`（cordis.yml、package.json bundles） | 官方专用，Launcher 不修改 |
| 存储 | `storages\`、`.anonymous-user-id` | 不复制 |

## 6. 冲突与锁规则

1. 复制只在 **源和目标都未被使用** 时进行（官方进程、Launcher 进程、外部 dsh 进程都算）。
2. 目标原文件先备份为 `<文件>.bak-yyyyMMdd-HHmmss`，再通过临时文件原子替换。
3. 单文件超过 1 MB 拒绝复制；源与目标相同拒绝复制。
4. Launcher 永远不删除官方 HOME；“关联”的版本删除时只解除注册。

## 7. 双向迁移

- 官方 → Launcher：版本控制 → “从官方桌面端导入 Provider”；需要完整数据时用“关联已有 DSH_HOME”或复制版本。
- Launcher → 官方：版本控制 → “导出 Provider 到官方桌面端”；或直接“在官方桌面端打开”该版本的 HOME。

## 8. Launcher 应停止 / 保留

停止：
- 把官方内置运行时当成可管理的运行时（官方 asar 内的 dsh 不能被 Launcher 启动/升级）。
- 在官方运行时尝试切换它的 HOME。
- 修改 `profiles\desktop` 或官方更新器缓存。

保留：
- 多版本、独立 HOME、快照、整合包、插件市场。
- 对 dsh web 启动令牌（`?token=`）的支持：dsh ≥ 0.2.1 未带令牌访问会返回 401。

## 9. 路线图

- v1（1.3.0）：检测 + 更新通道显示、HOME 切换冲突保护、Provider 双向复制（含备份）、令牌认证。
- v1.1：设置页增加“兼容模式”开关（新建版本时自动从官方导入 Provider）；显示官方是否有可用更新（读取 feed 的 latest.yml，只读）。
- v2：会话双向导入（按 session id 去重）；官方 profile 插件列表与 Launcher 插件对照。
- v3：在官方提供稳定 CLI/IPC 后改为通过官方接口交接，而不是环境变量。
