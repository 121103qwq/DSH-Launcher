# UI 统一待办（功能完成后执行）

> **状态：挂起。** 本文件只记录"跨页面的视觉/交互一致性"事项，**不随功能迭代顺手改**，避免每次功能变更都动全局样式。
>
> **触发条件**：所有功能性需求（#11 安全模式、#14 扫描导入、#16 中文插件源、#17 插件×实例矩阵、#18 已完成、对话全文检索、资源监控、任务中心等）确认结束后，作为**一个独立变更集**统一处理。
>
> **规范依据**：`docs/UI-DESIGN.md`（令牌、卡片、字号、阴影分层、PathLink 等）。本文件与规范冲突时以规范为准，规范未覆盖的项在本轮补充。

---

## 0. 执行前置

- [ ] 功能冻结：确认不再有正在开发的功能分支/未合并变更集
- [ ] 基线截图：每个窗口/内嵌页在 1180×720 与最大化两种尺寸下截图存档（125% DPI）
- [ ] 全量扫描脚本：把下面第 2 节的扫描命令固化为 `_verify-p0` 的 UI 契约断言，统一前后各跑一次
- [ ] 一次只改一个主题（先颜色令牌 → 再图标 → 再表格），每步跑 harness + 截图对比

## 1. 统一清单（按主题）

### 1.1 图标体系
- [x] 现状复核（**2026-09-13，变更集 130**）：完整清单 = 短字形 **14 处** + 实例设置分类字形 **5 处** = **19 处 / 17 种**（先前的扫描漏了 `⧗`「任务」与 `＋` 空态加号、`↗` 外链箭头）
- [x] 决策（用户 2026-09-13）：**统一字符集 + 规范文档**，字形表写进 `docs/UI-DESIGN.md`「图标（字形集）」；矢量 `Path`/`Geometry` 图标集**不做**，留作独立主题；托盘/任务栏/窗口图标继续用 `.ico` 资源（已是同源）
- [x] 导航/按钮/分类字形收敛进规范表，**禁止 emoji**（实测：U+1F300 及以上**零命中**；`✦ ☷ ⚙ ♧` 属符号字形，已登记）
- [x] 图标与文字间距**令牌化**（**2026-09-13**）：新增 `IconTextGap`（`0,0,8,0`），导航 6 处、返回 `←`、启动按钮 `▶` 共 **8 处**改用令牌（原 6/7/9px 混用）
- [x] 附带：标题栏三字形 **22/18/28 → 统一 18px**；折叠箭头 `⌄` → `▾` 统一（**2026-09-13**，本属 1.3 遗留项）

### 1.2 颜色令牌
- [x] 清除散落硬编码（**2026-09-13 完成，变更集 127 / work-log/118**）：实测 **38 处 / 7 个文件**（不是 2026-09-09 记的 8 处），全部收敛进 `App.xaml`
- [x] 新增缺失令牌（**2026-09-13**，共 16 个）：`TitleBarChromeBrush`/`TitleBarChromeBorderBrush`/`TitleBarChromeHoverBrush`/`TitleBarChromePressedBrush`/`TitleBarCloseHoverBrush`、`DangerSurfaceBrush`/`SuccessSurfaceBrush`/`SuccessBorderBrush`/`InfoSurfaceBrush`/`HighlightSurfaceBrush`/`NeutralSurfaceBrush`、`StatusIdleBrush`、`EmptyStateGradientStartColor`/`EmptyStateGradientEndColor`（Color 型）+ `EmptyStateAccentBrush`/`EmptyStateAccentSoftBrush`；`WarningTextBrush` 等既有令牌改为被真实引用
- [x] 复核 App.xaml 令牌表（**2026-09-13**）：命名统一（画刷 `*Brush`、颜色 `*Color`）、语义色成对；`docs/UI-DESIGN.md` 颜色表同步
- [x] 硬化 harness 断言（**2026-09-13**）：旧的「8 色白名单」断言升级为**全量门禁**——遍历 `src/DshLauncher/**/*.xaml`，断言 `App.xaml` 之外无颜色字面量

### 1.3 字体与层级
- [x] `FontSize="10"` 两处（`ExtensionWindow.xaml:504`、`MainWindow.xaml:241`）→ **提到 12**（**2026-09-13 完成**，变更集 128）
- [x] 统一字号阶梯（**2026-09-13 完成，变更集 128**）：**以 `docs/UI-DESIGN.md` 为准**，并把 **11 正式补入阶梯**（事实用量 103 处，规范此前只列到 12）→ 最终阶梯 **11/12/13/14/15/16/18/20 + 24/28/30（品牌/插画例外）**；本轮实际归并 **6 处**：`10→12` ×2、`17→18`（「当前实例」）、`17→16`（版本名）、`21→20` ×2（「插件市场」「Skill 市场」）。**不再**把 14/16/13 当成"待归并"（它们是合法阶梯）；`UV` 记录：先前"26 处归并"的估计基于与规范冲突的粗阶梯，已作废
- [x] 越界门禁（**2026-09-13**）：harness 断言「字号全部落在规范阶梯」——遍历所有 XAML，**图标字形**（`Text`/`Content` 为几何符号）不参与文字阶梯，交给 1.1 图标体系
- [x] 小字字重规则（**2026-09-13 定案，变更集 129 / work-log/120**）：实测 **129 个 ≤12px 文本里 112 个未加粗**，「一律 `SemiBold`」会让辅助文字整体变重 → 收窄为**仅胶囊/徽标/状态标签（12px + `SemiBold`）**，其余小字保持常规字重；按「小圆角 `CornerRadius="3"` + 浅色底 + 短标签」识别出 **6 个真胶囊**并修正；harness 新增**规则型**断言「胶囊/徽标/状态标签为 12px + SemiBold」（非白名单）

### 1.4 卡片 / 圆角 / 阴影
- [ ] 新页面（环境变量、任务中心、日志/空间管理若加入）按 `CardBrush + CardCornerRadius + CardPadding` 复核
- [ ] 阴影一律独立分层（`Effect` 不挂内容元素）——新增页面纳入 harness 断言
- [ ] 内嵌页与独立窗口的卡片外边距统一（当前 14/16/20/22 混用）

### 1.5 按钮与操作层级
- [ ] 主/次/危险三级样式全覆盖：删除、卸载、回滚等危险操作统一 `DangerButton`
- [ ] 按钮高度（36/38/40）与内边距（12,7 / 14,0 / 15,0）统一
- [ ] 同屏按钮数量 >3 时的排布规则（WrapPanel vs 主次分离）

### 1.6 表格与列表
- [ ] `ConversationWindow` 表格列宽（240/180/160/260/170/260）改为按比例 + `MinWidth`，窄窗口不横向滚动
- [ ] `ExtensionWindow` 侧栏 340 固定宽、按钮 148/168 固定宽 → 自适应
- [ ] 长列表统一虚拟化设置与 `HorizontalContentAlignment=Stretch`
- [ ] 列头排序/空态/行高统一

### 1.7 空态 / 加载态 / 错误态
- [ ] 每个列表/页面都有：空态引导、加载中骨架或进度、错误态（`DangerBrush` + 重试入口）
- [ ] 统一文案风格（一句话说明 + 一个动作按钮）

### 1.8 窗口与页面框架
- [ ] 内嵌页（版本控制、版本设置）与独立窗口（Chat、对话、扩展、新建版本）的返回/关闭交互统一
- [ ] 页面标题/副标题（`PageTitle`/`PageSubtitle`）层级与间距统一
- [ ] 各窗口最小尺寸、最大化行为、窗口记忆策略复核
- [ ] 各页滚动位置记忆（设置页已做，其它页待补）

### 1.9 交互反馈与键盘可达性
- [ ] 忙碌态防重入、操作结果提示（`ShowNotice`）位置与时长统一
- [ ] 键盘：Tab 顺序、Enter 提交、Esc 关闭、列表方向键 + 打开/删除快捷键（上游 U-01）
- [ ] 焦点可见（`FocusVisualStyle`）在所有按钮/输入框上一致
- [ ] `AutomationProperties.Name` 补齐（托盘、图标按钮、状态胶囊）

### 1.10 术语与文案
- [ ] 术语表：实例 / 版本 / 运行时 / DSH_HOME / Plugin / Skill / MCP / Profile / Provider 的中英与大小写统一
- [ ] 按钮文案动宾一致（"移动已有安装" vs "选择文件夹" 之类混用）
- [ ] 错误码文案与 `ErrorCodes` 表一致

### 1.11 高 DPI 与渲染
- [ ] 125% / 150% / 200% 下逐窗口检查 1px 描边、像素对齐（`SnapsToDevicePixels`/`UseLayoutRounding`）
- [ ] 明确 ClearType 降级范围（当前只有 MainWindow 透明窗口）——决定是否保留透明圆角
- [ ] 图标/位图在缩放下的模糊检查

### 1.12 主题与视觉定制（P2，若做）
- [ ] 主题色 5 选
- [ ] 深色模式（当前仅浅色）
- [ ] FAB 悬浮按钮、壁纸模式

### 1.13 下拉菜单与右键菜单（含「导入实例」）
- [ ] 现状：`VersionControlWindow` 的「导入实例」`ContextMenu` 用系统默认外观——无 `CardBrush`/圆角/内边距/阴影令牌，
      与同屏卡片、按钮风格不连续；展开后也无图标与说明，5 个动作（文件夹/快捷方式/源码/扫描/整合包）只看文字不易区分
- [ ] 全应用菜单统一规范：`MenuItem` 高度与内边距、图标列宽、快捷键列、分隔线、分组标题、禁用态、危险项着色
- [ ] 评估「导入实例」改为**菜单按钮/下拉面板**（图标 + 一行说明），并重新梳理它与「新建干净版本」「导入整合包」的层级关系
- [ ] 菜单展开/收起与按钮禁用态（`CanAddInstance`）的视觉反馈统一

## 2. 当前已知不一致（2026-09-09 扫描结果）

```bash
# 小于 11px 字号
grep -rn 'FontSize="[0-9]"' --include=*.xaml src/DshLauncher
# 非令牌硬编码颜色（排除 App.xaml 令牌定义）
grep -rn '="#[0-9A-Fa-f]*"' --include=*.xaml src/DshLauncher | grep -v App.xaml
# 图标字符
grep -rn 'Text="[←→▶◀⌄◇✦☷⚙—□×▣✕✓]"' --include=*.xaml src/DshLauncher
# 固定列宽（>=200，排除 MinWidth/MaxWidth）
grep -rn 'Width="[2-9][0-9][0-9]"' --include=*.xaml src/DshLauncher
```

> **复核（2026-09-13，master `15eb116` / 变更集 126 之后）——以下为实测数字，上方 2026-09-09 的旧数字作废。**

- **字号**：全仓 XAML `FontSize` 共 **181 处**，偏离规范阶梯 **26 处**——`10px` **2 处**（`ExtensionWindow.xaml:504`、`MainWindow.xaml:241`，低于 11px 下限）、`16px` 15 处、`17px` 2 处、`21px` 2 处、`13/14/22/28/30px` 各 1 处
- **硬编码颜色**：**38 处 / 7 个文件**（已排除 App.xaml 的令牌定义）——`MainWindow.xaml` 22、`PluginMatrixWindow.xaml` 6、`EnvironmentScanWindow.xaml` 3、`ExtensionWindow.xaml` 3、`VersionControlWindow.xaml` 2、`ChatWindow.xaml` 1、`VersionSettingsWindow.xaml` 1
- **图标字符**：**11 种 / 12 处**（`▶`×2、`←`、`⌄`、`▾`、`◇`、`✦`、`☷`、`⚙`、`—`、`□`、`×`）
- **固定宽度 ≥200**：**22 处**（对话表 6 列、扩展侧栏、日志中心、各内嵌页左栏 320、模态窗 430/660/680 等）
- **窗口框架**：**独立窗口 5 个**（`MainWindow`/`ChatWindow`/`NewVersionWindow`/`PackImportWindow`/`VersionSwitchWindow`）+ **内嵌页 8 个**（`Conversation`/`EnvironmentScan`/`Extension`/`LauncherTask`/`LogCenter`/`PluginMatrix`/`VersionControl`/`VersionSettings`），返回/关闭交互不统一
- **资源引用**：`StaticResource` 549 处、`Binding` 184 处、`TemplateBinding` 46 处、**`DynamicResource` 0 处** —— 1.12 的「主题色 5 选 / 深色模式」必须先决定：把需要换肤的令牌改成 `DynamicResource`，还是整字典替换
- **令牌表**：`App.xaml` 现 34 个键；1.2 里点名的 `WarningTextBrush`/`DangerTextBrush`/`InfoBackgroundBrush`/`WarningBackgroundBrush`/`HoverSurfaceBrush` **都已存在**，真正缺的是 `TitleBarChromeBrush`/`TitleBarChromeHoverBrush`/`InfoSurfaceBrush`/`HighlightSurfaceBrush`
- **高 DPI**：仓库内**没有 `app.manifest`**，csproj 也无 DPI 相关属性 → 走 WPF 默认；125%/150%/200% 的实际表现**待截图实测**（1.11）
- **截图/UI 断言工具链（可复用，不必新建）**：`_verify-p0/func-check/uia.ps1` 提供 UIA 查找 + 截图；harness 已有 `p1/ui: 设计令牌齐备 + CheckBox/ProgressBar 自定义样式` 与字号契约断言（`_verify-p0/Program.cs` 约 2624/2632 行），本轮在其上扩展

## 3. 验收方式

- [ ] `_verify-p0` 新增/更新 UI 契约断言（令牌、字号下限、图标来源、表格自适应、阴影分层、空态存在）
- [ ] 每个窗口 1180×720 + 最大化 + 125%/150% DPI 截图对比
- [ ] 键盘走查：只用 Tab/Enter/Esc 完成主要流程
- [ ] 更新 `docs/UI-DESIGN.md` 与本文件的"变更记录"

## 4. 变更记录

| 日期 | 内容 |
|---|---|
| 2026-09-09 | 建立本待办（变更集 56 后挂起）：扫描出 10px 字号 1 处、硬编码颜色 8 处、图标字符 11 种、固定列宽 8 处 |
| 2026-09-11 | 新增 1.13 下拉/右键菜单外观与层级（触发点：「导入实例」下拉加入「导入整合包」后需与整体风格统一） |
| 2026-09-13 | **变更集 127（UI 统一 A：颜色令牌化）**：38 处硬编码颜色 → 16 个新令牌；harness 门禁升级为全量；前后截图逐像素对比（25 张）无布局回归 |
| 2026-09-13 | **变更集 128（UI 统一 B：字号阶梯）**：口径冲突解决（11 补入规范阶梯，不做 103 处 11px→12px）；归并 6 处越界；新增字号门禁；`≤12px 是否一律 SemiBold` 待决策 |
| 2026-09-13 | **变更集 129（B 收尾：小字字重）**：字重规则按角色收窄为「仅胶囊/徽标/状态标签 12px + SemiBold」；6 个真胶囊修正；新增规则型胶囊门禁 |
| 2026-09-13 | **变更集 130（UI 统一 C：图标体系）**：17 种字形进规范表（含补扫出的 ⧗ ＋ ↗）；标题栏三字形统一 18px；`⌄`→`▾`；新增 `IconTextGap` 令牌并替换 8 处间距；新增「字形在表内 + 无 emoji」门禁 |
| 2026-09-13 | 动手前复核：旧扫描数字作废（颜色 8→**38** 处、字号 1→**26** 处偏离阶梯、固定宽 →**22** 处、窗口 4+2→**5 窗口 + 8 内嵌页**）；补测 `DynamicResource` **0 处**、令牌表 34 键与真实缺项——已写入上方第 2 节 |
