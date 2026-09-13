# 与上游的差异盘点（供评审）

- 日期：2026-09-13
- 对象：**PR #15**（分支 `launcher-extended`）——基于上游 **v1.0.7**（`846c44c1`），**212 个提交 / 163 文件 / +38,598 / −7,065**，**不含**上游 v1.0.8 ~ v1.1.2 的提交。
- 文中"本分支"指上述分支，"上游"指 `121103qwq/DSH-Launcher` 的 `main`（v1.1.2，`b49dc53`）。所有数字为 git 实测，复现命令见文末。
- 说明：本报告自身也是本分支新增的文件（+130 行）；下表**不含**本报告自身，因此 PR 页面上的合计会比下表多 1 个文件与对应行数。

## 0. 先看三件事

1. **两条线已分叉**：本分支相对 v1.0.7 是 162 文件 / +38,468 / −7,065（含 2026-09-13 追加的 UI 统一整轮、两轮验收修复与行尾统一，见 §1b / §1c）；上游自己从 v1.0.7 到 v1.1.2 走了 99 文件 / +18,833 / −722。**41 个文件双方都改过** —— 这是冲突面的来源，也是 GitHub 显示 *can't automatically merge* 的原因。
2. **硬前提**：本分支基于 v1.0.7，**没有** v1.0.8~v1.1.2 对既有文件的改动。在本机做过一次三方合并演练：冲突 **35 文件 / 108 块**，最重的是 `MainWindow.xaml.cs`、`DshInstanceRunner.cs`、`MarketplaceService.cs`。
3. **111 个文件是上游没有的新增文件**（相对 v1.0.7），删除只有 1 个：上游提交进仓库的构建产物 `DSH Launcher/DSH Launcher.exe`（约 5.7 MB，建议改为 Release 附件）。

## 1. 体量对比（相对 v1.0.7）

| 领域 | 本分支 | 上游（v1.0.7 → v1.1.2） |
|---|---|---|
| `src/DshLauncher/Services/`（业务逻辑） | 89 文件，+19,313 / −431 | 36 文件，+8,513 / −345 |
| `src/DshLauncher/` 其余（窗口 XAML / 代码后置 / 入口等） | 45 文件，+14,698 / −1,889 | 25 文件，+6,286 / −298 |
| `Models/` | 14 文件，+819 / −17 | 17 文件，+917 / −7 |
| `tests/` | 2 文件，+2,354 / −4,616 | 13 文件，+2,641 / −43 |
| `docs/` | 7 文件，+900 / −0（不含本报告自身） | 0 |
| 其它（README / .gitignore / CI 等） | 5 文件，+384 / −112 | 8 文件，+476 / −29 |
| **合计** | **162 文件（新增 111 / 删除 1），+38,468 / −7,065**（不含本报告自身；PR 页面为 163 文件 / +38,598 / −7,065） | **99 文件，+18,833 / −722** |

> `tests/` 的删除行数偏大，是因为本分支把上游 v1.0.7 的 `tests/DshLauncher.SelfTest/Program.cs`（4,694 行）整体替换成了自己的自测文件（2,421 行 / 175 项断言）。如果希望保留上游测试，我可以改成在上游文件基础上追加。

## 1b. 2026-09-13 追加：UI 统一整轮（变更集 127–143）

本分支在此报告之后又追加了 **19 个提交**，把 fork 里的 UI 统一整轮与三轮真机验收修正一并带来评审：

- **127–139（11 个主题，一个主题一个变更集）**：颜色令牌化（38 处硬编码色 → `App.xaml` 令牌）、字号阶梯（11px 正式入阶梯 + 归并 6 处越界）、小字字重规则收窄到胶囊/徽标、图标字形集（17 种，标题栏三字形按**墨迹实测**定尺寸）、菜单统一、表格与侧栏自适应（`GridViewColumn` 无 MinWidth/星号，列宽改代码分配 + 16 个长列表显式虚拟化）、卡片/圆角/按钮三级、空/加载/错误三态分色、窗口与页面框架（内嵌页标题 20px、窗口最小尺寸）、键盘可达性（15 个无文字按钮补可访问名）、高 DPI 像素对齐（13 个根元素）
- **140–143（三轮验收修复）**：菜单 `Header` 语义（子元素 ≠ `Header`）、危险按钮悬停红底白字（并修掉"内联 `Foreground` 优先级高于样式触发器"）、标题栏字形按墨迹等大、市场源删除按钮（**代码动态创建**，XAML 门禁盲区）、Skill 市场卡片与插件市场统一、Skill 市场列表横向溢出（`CanContentScroll=True` 下条目被无限宽度测量 → 安装按钮跑出窗口）

**顺带修掉一个 PR 副作用（并已扩展到全仓）**：旧尖端里 `src/DshLauncher/ExtensionWindow.xaml.cs` **整份是 CRLF**（v1.0.7 基线是 LF），在 PR diff 中表现为 `+2,385 / −1,766` 的"整文件重写"假象；本轮该文件回归 LF。随后又扫描了全部共同文件的行尾风格，发现另有两处同类问题（来自 fork 早期导入，master 上同样存在）：`src/DshLauncher/Services/MarketplaceService.cs`（2,367 行）与 `src/DshLauncher/DshLauncher.csproj`（40 行），已在 PR 分支一并统一为 LF（commit `e451d6d`）。**共消除 2,407 行行尾噪音**：本分支相对 v1.0.7 的插入/删除行数因此从 `+39,819 / −8,982` 降到 `+37,952 / −7,115`。

> 说明：本次只在 PR 分支统一行尾，master 侧未动（避免为纯行尾改动重做发布）。忽略行尾差异后，本轮的实质改动是 **24 文件 / +743 / −290**。

## 1c. 2026-09-13 追加：两轮验收修复（变更集 144–153，16 个提交）

§1b 之后又按真机验收反馈追加了这一批（含 fork 侧同步完成的图标矢量轮与存储清理修复）：

- **144–146（提示条与状态呈现）**：提示条**按文字长度自适应消失**（`4s + 字数/30`，上限 12s；失败/异常类与带 detail 的常驻；悬停续期，右侧 `×` 可手动关）、扩展列表 `Delete` 快捷键、三态分色收口（新增共享 `Services/StatusTextStyler.cs`，四页接入）、滚动位置记忆扩展到内嵌页（`Services/ScrollMemory.cs`，600 ms 防抖 + 防自触发）。
- **147–148（图标改矢量）**：字形图标整体替换为**矢量注册表** —— 新增 `Controls/UiIcon.xaml(.cs)` 与 19 个 `Icon.*` 几何资源，图标取自 **Tabler Icons v3.46.0（MIT）**，许可与出处见新增的 `docs/THIRD-PARTY-NOTICES.md`；颜色跟随继承的 `TextElement.Foreground`、线宽 = `Size/12`、像素对齐。替换覆盖导航/标题栏/启动/返回/空态/提示条/下拉/版本设置导航/日志返回/扩展外链共 17 处。
- **149–151（日志中心与按钮布局）**：日志页头部改「文字 `*` + 按钮 `Auto`」两列 Grid（修掉按钮压住说明文字）；**清理与日志解析移出 UI 线程**（实测 `LauncherStorageService.Scan()` 13.2 s 全在 UI 线程）；四个按钮排两行后改为 **2×2 Grid 共享 `Auto` 列**（同列同宽、左右边对齐）；日志中心新增「清理日志」（复用存储清理分类，只清 `crash`/`old-logs`，**当前 `launcher.log` 保留**）。
- **152–153（存储清理：卡顿 + 一个静默失效缺陷）**：① 设置 →「诊断与日志」的存储清理按钮原本**同步**跑 `Scan()`（13.2 s）→ 改 `async` + `Task.Run`，期间按钮置忙并给进度文案，服务侧新增 `CleanAllCleanable()` 去掉确认后的重复扫描；A/B 实测「操作期间 UIA 调用延迟」由 **11,469 ms → 40 ms**（数字与文案未变）。② 顺带查实 `BuildFileCategory` 的 `primary` 只取路径列表**第一条**（在判存在之前），而删除只认这一条 → 「轮转旧日志」(`launcher.log.old` + `watchdog.log.1`) 与「市场目录缓存」(`marketplace-cache.json` + `skill-market-cache.json`) 的删除会**静默变成空操作**且不报错；已改为保留完整路径列表、逐条处理（真机验证 `已清理 1 个文件/0.0 KB` → `3 个文件/1168.6 KB`，与清点合计一致）。

> 本轮同样**不改变**任何已定稿的口径：13 秒的测量口径按用户选择保留（只解决"界面假死"）；i18n 与主题化按用户决定**不做**。

## 2. 冲突面明细（41 个双方都改过的文件）

按"本分支改动量"排序的前 22 个：

| 文件 | 本分支 | 上游 v1.1.2 |
|---|---|---|
| `tests/DshLauncher.SelfTest/Program.cs` | +2,341 / −4,614 | +1,295 / −43 |
| `src/DshLauncher/MainWindow.xaml.cs` | +4,920 / −990 | +2,269 / −186 |
| `src/DshLauncher/Services/MarketplaceService.cs` | +2,367 / −1,949 | +420 / −38 |
| `src/DshLauncher/ExtensionWindow.xaml.cs` | +2,385 / −1,766 | +607 / −44 |
| `src/DshLauncher/VersionSettingsWindow.xaml.cs` | +971 / −222 | +177 / −6 |
| `src/DshLauncher/Services/DshInstanceRunner.cs` | +656 / −54 | +332 / −57 |
| `src/DshLauncher/MainWindow.xaml` | +422 / −119 | +32 / −0 |
| `src/DshLauncher/VersionControlWindow.xaml.cs` | +469 / −54 | +161 / −15 |
| `src/DshLauncher/VersionSettingsWindow.xaml` | +416 / −99 | +81 / −9 |
| `src/DshLauncher/Services/LauncherTaskService.cs` | +395 / −0 | +726 / −0 |
| `src/DshLauncher/Services/ConversationService.cs` | +375 / −16 | +353 / −5 |
| `README.md` | +286 / −105 | +56 / −7 |
| `src/DshLauncher/Services/SkillMarketService.cs` | +261 / −78 | +280 / −67 |
| `src/DshLauncher/Services/DshCredentialStoreNormalizer.cs` | +263 / −0 | +90 / −0 |
| `src/DshLauncher/Services/ExtensionService.cs` | +211 / −31 | +137 / −28 |
| `src/DshLauncher/Services/DshProfileService.cs` | +209 / −0 | +126 / −0 |
| `src/DshLauncher/App.xaml.cs` | +194 / −10 | +85 / −11 |
| `src/DshLauncher/ExtensionWindow.xaml` | +191 / −91 | +44 / −0 |
| `src/DshLauncher/Services/VersionSettingsService.cs` | +163 / −6 | +36 / −1 |
| `src/DshLauncher/ConversationWindow.xaml.cs` | +131 / −9 | +346 / −4 |
| `src/DshLauncher/VersionControlWindow.xaml` | +128 / −38 | +15 / −1 |
| `src/DshLauncher/Services/ConversationSyncService.cs` | +93 / −36 | +7 / −0 |

**读法**：`MarketplaceService.cs`（本分支近乎重写）、`ExtensionWindow.xaml.cs` 这类双方都大改同一文件，逐块合并成本最高；`LauncherTaskService.cs`（双方都是纯新增）这类反而是可拼的。其余 19 个文件的完整清单可用文末命令重放。

## 3. 功能对照（抽样：上游有没有对应的东西）

**上游完全没有的（13 项，本分支独有）**：
`SessionFileNames.cs`（会话格式代际）、`CrashCauseClassifier.cs`（崩溃归因）、`InstanceUiPluginScanner.cs`（UI 插件检测）、`PresentationSurfaceService.cs`（呈现面）、`LaunchModePolicy.cs`（启动方式策略）、`TerminalLaunchService.cs`（终端启动 / 命令）、`SafeProfileService.cs`（隔离 profile）、`SkillMarketQuery.cs`（技能市场筛选排序）、`LogCenterService.cs`（日志中心）、`MarketSourceSettingsService.cs`（市场来源配置）、`AppDialog.cs`（自绘对话框）、`LogCenterWindow.xaml`、`EnvironmentScanWindow.xaml`（环境扫描导入）

**上游有同名文件、但实现与程度不同（6 项，抽样）**：
`ExtensionService.cs`、`MarketplaceService.cs`、`SkillMarketService.cs`、`ConversationSyncService.cs`、`DshInstanceRunner.cs`、`VersionControlWindow.xaml`

## 4. 上游自己这条线（v1.0.7 → v1.1.2）

| 版本 / 提交 | 内容 |
|---|---|
| v1.0.8 | global provider and model management |
| v1.0.9 | profile switching and download center |
| v1.0.10 | runtime state hardening + Desktop installer |
| v1.0.11 | docs（发布记录） |
| `2d6c31a` | launcher management suite |
| v1.1.1 | snapshots / GitHub requests / theme state hardening |
| v1.1.2 | restore self-contained runtime packs in Windows CI |

方向上有重叠（实例与运行时管理、profile 切换、provider / 模型管理、下载中心），只是各写各的 —— 这解释了冲突为何集中在 `MainWindow.xaml.cs` / `DshInstanceRunner.cs` / `LauncherTaskService.cs` / `DshProfileService.cs` 这些文件上。

## 5. 我们能配合的三种方式（对应 PR 描述）

| 方式 | 我们要做的 | 说明 |
|---|---|---|
| ① 你指定一块，我移植到 `main` | 在 v1.1.2 基线上重做该块，并用**你的**自测验证 | 最省事的是会话格式代际（补丁已在本机对 v1.1.2 验过：构建 0/0、上游自测 77/77） |
| ② 我整体 rebase 到 `main` | 解 §2 的冲突（实测 108 块），解完重跑双方自测 | 需要你给取舍标准（哪些文件以哪边为准） |
| ③ 只当参考实现 | 不做改动 | 需要哪块告诉我，我再单独整理 |

没有"必须接受"的意思；都不合适也可以直接说。

## 6. 诚实边界

- 本报告只做**体量、结构与文件级**盘点；每块代码的移植成本需要具体尝试才能确定。例如 `CrashCauseClassifier` 依赖本分支自己的 `StartupEvidence` / `InstanceLogLine` 模型，移植要连模型一起搬。
- "上游没有"仅对**同名文件**成立：本次抽样 19 个文件，未逐文件核查上游是否存在"不同名但同功能"的实现。
- 未核查上游的 `AGENTS.md` / `CURRENT_DESIGN.md` / `REVIEW_ROADMAP.md` 等文档里是否已规划同类能力；若已规划，贡献方式应改为对接其路线。
- 本分支把上游 v1.0.7 的自测文件整体替换为自研自测（见 §1 注）；这不是无意删除，但确实改变了该文件的形态。

## 附录：复现命令

在 fork 仓库内即可重放（`846c44c1` = v1.0.7，`b49dc53` = v1.1.2）：

```bash
# 本分支相对 v1.0.7
git diff --shortstat 846c44c1 launcher-extended
git diff --shortstat 846c44c1 launcher-extended -- src/DshLauncher/Services
git diff --diff-filter=A --name-only 846c44c1 launcher-extended | wc -l
git diff --diff-filter=D --name-only 846c44c1 launcher-extended | wc -l

# 上游自己的这条线
git diff --shortstat 846c44c1 b49dc53

# 双方都改过的文件（冲突面）
comm -12 <(git diff --name-only 846c44c1 b49dc53 | sort) \
         <(git diff --name-only 846c44c1 launcher-extended | sort)

# 某个文件两边各改了多少
git diff --numstat 846c44c1 launcher-extended -- src/DshLauncher/MainWindow.xaml.cs
git diff --numstat 846c44c1 b49dc53 -- src/DshLauncher/MainWindow.xaml.cs
```
