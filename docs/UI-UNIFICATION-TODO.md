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
- [x] **收尾（2026-09-13，变更集 132）**：上述门禁有**两块盲区**——`App.xaml` 样式/模板内 **28 处** + C# 内 **24 处**（含子目录 `Models/`、`Controls/`）；新增 **16 个令牌**全部收敛；门禁升级为「**令牌定义行是唯一合法处**」（含 App.xaml 非令牌行 + C#；`FromRgb/FromArgb` 只认全数字参数，从变量派生 alpha 合法）

### 1.3 字体与层级
- [x] `FontSize="10"` 两处（`ExtensionWindow.xaml:504`、`MainWindow.xaml:241`）→ **提到 12**（**2026-09-13 完成**，变更集 128）
- [x] 统一字号阶梯（**2026-09-13 完成，变更集 128**）：**以 `docs/UI-DESIGN.md` 为准**，并把 **11 正式补入阶梯**（事实用量 103 处，规范此前只列到 12）→ 最终阶梯 **11/12/13/14/15/16/18/20 + 24/28/30（品牌/插画例外）**；本轮实际归并 **6 处**：`10→12` ×2、`17→18`（「当前实例」）、`17→16`（版本名）、`21→20` ×2（「插件市场」「Skill 市场」）。**不再**把 14/16/13 当成"待归并"（它们是合法阶梯）；`UV` 记录：先前"26 处归并"的估计基于与规范冲突的粗阶梯，已作废
- [x] 越界门禁（**2026-09-13**）：harness 断言「字号全部落在规范阶梯」——遍历所有 XAML，**图标字形**（`Text`/`Content` 为几何符号）不参与文字阶梯，交给 1.1 图标体系
- [x] 小字字重规则（**2026-09-13 定案，变更集 129 / work-log/120**）：实测 **129 个 ≤12px 文本里 112 个未加粗**，「一律 `SemiBold`」会让辅助文字整体变重 → 收窄为**仅胶囊/徽标/状态标签（12px + `SemiBold`）**，其余小字保持常规字重；按「小圆角 `CornerRadius="3"` + 浅色底 + 短标签」识别出 **6 个真胶囊**并修正；harness 新增**规则型**断言「胶囊/徽标/状态标签为 12px + SemiBold」（非白名单）

### 1.4 卡片 / 圆角 / 阴影
- [x] 卡片令牌复核（**2026-09-13 变更集 135**）：`CardBrush` 47、`CardCornerRadius` 39、`PanelCornerRadius` 24、`CardPadding` 15 处使用；**9 处裸圆角收敛进令牌**（`9`→Panel、`8`→Item、`12`→Card），白名单例外 `3`（胶囊）/`17`·`18`（窗口外框、空态插画）写入规范
- [x] 阴影一律独立分层（**既有断言**，本轮复核通过：`Effect` 不挂含内容元素）
- [x] 卡片外边距：**实测为 `0,0,0,6~12`（12 为主），与本文旧记的"14/16/20/22"不符** → 不批量改，改为在 `UI-DESIGN.md` 记录现口径（页面内容外边距 12；卡片内部用 `CardPadding`/`PanelPadding`）

### 1.5 按钮与操作层级
- [x] 三级样式全覆盖（**2026-09-13 变更集 135**）：危险操作 6 处补 `DangerButton`（删除选中对话/删除/卸载/删除版本/回滚选中快照/插件删除），复核 **5/5 合规、0 缺失**；harness 新增断言「危险按钮必须挂 DangerButton」
- [x] 按钮高度与内边距统一（**2026-09-13**）：`PrimaryButton`/`DangerButton` 补 `MinHeight=38` + `Padding=16,0`（三级同尺寸）；**紧凑工具条按钮分层**（允许 10,4~12,7，不与三级混排）——实测原先"样式无尺寸、各页自写 8 种 Padding"的问题由此消解
- [x] 同屏 >3 按钮的排布规则（**2026-09-13 记录**）：优先 `WrapPanel` 自动换行；有主次时**主按钮单独一行/靠左、次级成组**（版本控制页即此形态）；本轮不批量重排

### 1.6 表格与列表
- [x] 对话页表格列宽自适应（**2026-09-13 变更集 134**）：`GridViewColumn` **没有 `MinWidth`、`Width` 是像素 double（不支持星号）** → 改为**代码按可用宽度分配**（权重 + 最小宽，`SizeChanged`/`Loaded` 触发；`ConversationWindow.DistributeColumns`）；17 列全部纳入，窗口变宽即铺满
- [x] 扩展页侧栏与筛选控件自适应（**2026-09-13**）：侧栏 `320` → `0.26*` + `MinWidth 260`/`MaxWidth 380`；`MarketplaceSortBox`/`MarketplaceSourceBox`/`SkillMarketSourceBox` 固定 `Width`（148/168/240）→ `MinWidth`
- [x] 长列表统一虚拟化（**2026-09-13**）：**16 个列表元素显式声明** `IsVirtualizing` + `VirtualizationMode=Recycling` + `CanContentScroll`（⚠️ 隐式样式 + `BasedOn="{StaticResource {x:Type ListView}}"` 会让列表创建即崩，**已列入门禁禁止**）
- [ ] **列头与行高统一（待做）**：需先解决"统一外观要不要 BasedOn 主题样式"（隐式做法已证明会崩）；`HorizontalContentAlignment=Stretch` 保持各处显式声明（不做隐式覆盖，避免影响个别列表）
- [ ] 列头排序（功能项，本应用尚未提供；空态见 §1.7）

### 1.7 空态 / 加载态 / 错误态
- [x] 三态分色 + 失败动作指引（**2026-09-13 变更集 136，参考实现**）：`ExtensionWindow` 市场/Skill 状态行 **11 处**改走 `SetStatusText`（加载＝`MutedBrush` / 部分失败＝`WarningTextBrush` / 失败＝`DangerTextBrush`），失败文案统一补"（点「刷新目录」重试）"；盘点发现**全仓原本 0 处「重试」入口**
- [x] 文案风格统一（**2026-09-13**）：写入 `UI-DESIGN.md`「空态/加载态/错误态」小节——空态"一句话说明 + 可执行动作"、错误态"原因 + 下一步点哪里"；纪律＝**错误不允许只进日志**
- [ ] **其余页面三态待逐页改造**（对话页/版本控制/日志中心/任务页）：现有状态行已承担空态与失败提示，但**未分色、失败无动作指引**；版本控制页**缺版本列表空态**（需补 `Visibility` 属性）

### 1.8 窗口与页面框架
- [x] 返回/关闭交互统一（**2026-09-13 变更集 137：复核符合 + 写入规范**）：内嵌页「返回 <来源页>」（返回版本控制 / 返回设置 / 主窗上下文返回）；模态窗「取消」+ `IsCancel="True"` **3/3 通过**（Esc 可关）；向导类另有「返回上一步」
- [x] 页面标题层级统一（**2026-09-13**）：4 个内嵌页标题 `18 → 20`（规范：页面标题 20 / 卡片标题 18）；harness 断言守住
- [x] 窗口最小尺寸 / 最大化行为 / 窗口记忆复核（**2026-09-13**）：5 个独立窗口**全部**有 `MinWidth`/`MinHeight`（补了 `NewVersionWindow` 430×320）；主窗启动尺寸＝最小尺寸 1180×720（既有断言）；窗口记忆走 `WindowStateStore`（多屏越界回退）
- [ ] **各页滚动位置记忆待补**（2026-09-13 复核）：目前**只有**设置页与扩展页（`UiStateStore`）；对话 / 日志中心 / 任务 / 插件矩阵切走再切回会回到顶部——补法明确（`UiStateStore` + `ScrollViewer.ScrollChanged`），留后续小变更集

### 1.9 交互反馈与键盘可达性
- [x] 焦点可见统一（**2026-09-13 变更集 138**）：新增 `AppFocusVisual`（蓝虚线 2px）并挂到 **5 个 keyed 按钮样式**；`PathLinkButton` 原先**显式关掉了焦点框**（`{x:Null}`）已修复；⚠️ 隐式样式方案会致 App.xaml 加载崩溃（与 134 的 ListView 同源），已写入规范禁止
- [x] `AutomationProperties.Name` 补齐（**2026-09-13**）：盘点发现**全仓 0 个按钮有可访问名**、其中 **15 个无文字按钮**（导航 6 / 路径链接 3 / 标题栏 3 / 实例 2 / 启动 1）全部补齐；harness 断言「无文字按钮必须有名」
- [x] 键盘约定复核（**2026-09-13**）：`Enter`＝模态窗主按钮（`IsDefault` 3/3 ✓）、`Esc`＝取消/关闭（`IsCancel` 3/3 ✓）、列表方向键＝WPF 默认 ✓、双击打开 ✓；`TabIndex` 全仓未用 → 规范写明"不手动设，靠视觉顺序"
- [ ] 提示（`ShowNotice`）时长统一：当前**常驻**（无自动消失），需定时器 + 淡出；与"长文案阅读时间"一起设计
- [ ] 列表 `Delete` 快捷键：目前删除靠按钮 + 二次确认；是否加快捷键需与危险操作确认流程一起设计

### 1.10 术语与文案
- [ ] 术语表：实例 / 版本 / 运行时 / DSH_HOME / Plugin / Skill / MCP / Profile / Provider 的中英与大小写统一
- [ ] 按钮文案动宾一致（"移动已有安装" vs "选择文件夹" 之类混用）
- [ ] 错误码文案与 `ErrorCodes` 表一致

### 1.11 高 DPI 与渲染
- [x] 像素对齐（**2026-09-13 变更集 139**）：13 个窗口/页面根元素补 `UseLayoutRounding` + `SnapsToDevicePixels`（此前**根元素 0 处**；只在 App.xaml/MainWindow 内部零星出现）；顺带踩坑记录：批量改 XAML 找根元素必须用 `<(Window|UserControl) `（带空格），否则宽正则会回退匹配到 `<Window.Resources>` 属性元素并插坏 XML（`MC3000`）
- [x] ClearType 降级范围（**2026-09-13 定案**）：**只有 `MainWindow`**（`AllowsTransparency` + `WindowStyle=None` 透明窗）走灰度抗锯齿；其余窗口不透明；**决定保留透明圆角**
- [x] 图标/位图缩放（**2026-09-13**）：应用内图标＝字体字形（矢量，缩放不糊）；位图仅 MainWindow/ExtensionWindow 少量
- [ ] **150% / 200% 逐窗口人工核对**：改系统缩放需用户操作并重启；核对清单（描边/圆角/文字/图标/最大化）已写入 `UI-DESIGN.md`

### 1.12 主题与视觉定制（P2，若做）
- [ ] 主题色 5 选
- [ ] 深色模式（当前仅浅色）
- [ ] FAB 悬浮按钮、壁纸模式

### 1.13 下拉菜单与右键菜单（含「导入实例」）
- [x] 现状复核（**2026-09-13，变更集 131**）：§1.13 原记"导入实例菜单用系统默认外观"**说反了**——真正没样式的是 `MainWindow` 的**启动方式分体菜单**（4 项都没挂样式，且当时**没有隐式 MenuItem 样式**）；导入菜单早已挂样式
- [x] 全应用菜单统一（**2026-09-13**）：新增**隐式 `MenuItem` 样式**（所有菜单项默认命中）+ **禁用态**（`MutedBrush` 前景 / 无悬停底 / 箭头光标）+ **`MenuItemDangerStyle`**（危险前景）；弹出层 `White`→`CardBrush`、`#C8DDF0`→`LineBrush`；菜单项悬停 `#E0EAFD`→**新令牌 `MenuHoverBrush`**、选中 →`PageBrush`；`MainWindow.xaml.cs` 动态菜单里 3 处 `FromRgb` 状态点色 → `FindResource` 令牌；`docs/UI-DESIGN.md` 增「菜单」小节；图标列/快捷键列**暂未启用**（写入规范待用）
- [x] **内容层（2026-09-13 变更集 133，用户决策：加说明不加字形）**：「导入实例」5 个动作改为**双行项**（标题 + 11px `MutedBrush` 说明）；`导入实例` 按钮 ToolTip 改写为**与「新建干净版本」的区别**；5 项补 `AutomationProperties.Name`（Header 非字符串时 UIA 名称为空）；
      说明写在 Header 内容里**不覆写共享模板**；新增 harness 断言「双行项 5 条 + ToolTip 说明」
- [x] 菜单展开/收起与按钮禁用态视觉反馈：（**2026-09-13**）禁用态样式已统一；按钮自身的 `IsEnabled="{Binding CanAddInstance}"` 沿用既有按钮禁用样式（置灰）
- [x] 附带发现（**2026-09-13**）：颜色令牌化（变更集 127）存在**盲区**——`App.xaml` 样式/模板内 **32 处**字面色 + C# 内 **27 处** `FromRgb/FromArgb` 未被门禁覆盖，另立候选变更集收尾

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
| 2026-09-13 | **变更集 131（UI 统一 D：菜单统一）**：隐式 MenuItem 样式 + 禁用态 + 危险项样式；菜单面颜色令牌化（新增 `MenuHoverBrush`）；C# 动态菜单状态点改令牌；**过程中因 StaticResource 前向引用把启动器改崩一次，harness 冒烟断言抓住并修复** |
| 2026-09-13 | **变更集 132（UI 统一 A2：颜色令牌化收尾）**：补齐 127 的盲区（App.xaml 样式 28 处 + C# 24 处）→ 16 个新令牌；门禁升级为「令牌定义行唯一合法」；新增菜单样式断言 |
| 2026-09-13 | **变更集 133（§1.13 内容层：导入实例菜单双行化）**：5 项加 11px 说明 + ToolTip 讲清与新建版本的区别 + `AutomationProperties.Name`；`docs/UI-DESIGN.md` 补「双行菜单项」规范；记录 `.ps1` 纯 ASCII 与 popup 截图两个工具坑 |
| 2026-09-13 | **变更集 134（UI 统一 E：表格与侧栏自适应）**：对话页 17 列改代码分配；扩展页侧栏/筛选控件自适应；16 个列表显式虚拟化；**踩坑 3 个**（GridViewColumn 无 MinWidth / 隐式 BasedOn 主题样式致页面构造崩溃 / 批量脚本两次误伤） |
| 2026-09-13 | **变更集 135（UI 统一 F：卡片/圆角/阴影/按钮三级）**：6 个危险按钮统一 `DangerButton`；9 处裸圆角令牌化（白名单 3/17/18）；主/危险按钮补统一 `MinHeight 38`+`Padding 16,0`；新增「危险按钮 + 圆角白名单 + 按钮同尺寸」门禁 |
| 2026-09-13 | **变更集 136（UI 统一 G：空/加载/错误三态）**：市场/Skill 状态行 11 处三态分色 + 失败文案自带重试指引；新增 `UI-DESIGN.md` 三态规范与门禁；其余页面列为遗留 |
| 2026-09-13 | **变更集 137（UI 统一 H：窗口与页面框架）**：4 个内嵌页标题 18→20；`NewVersionWindow` 补最小尺寸；返回/关闭交互规则写入规范；新增「标题/最小尺寸/IsCancel」门禁 |
| 2026-09-13 | **变更集 138（UI 统一 I：键盘可达性与自动化）**：统一焦点框 `AppFocusVisual` 挂 keyed 按钮样式（修 `PathLinkButton` 的 `{x:Null}`）；15 个无文字按钮补可访问名；**隐式样式 + `BasedOn {x:Type Button}` 致 App.xaml 加载崩溃**（harness 抓住）并固化禁令 |
| 2026-09-13 | **变更集 139（UI 统一 J：高 DPI 与渲染）**：13 个窗口/页面根元素补像素对齐（此前 0 处）；明确 ClearType 降级范围（仅 MainWindow 透明窗）与图标缩放策略；新增门禁；150%/200% 人工核对清单入规范 |
| 2026-09-13 | 动手前复核：旧扫描数字作废（颜色 8→**38** 处、字号 1→**26** 处偏离阶梯、固定宽 →**22** 处、窗口 4+2→**5 窗口 + 8 内嵌页**）；补测 `DynamicResource` **0 处**、令牌表 34 键与真实缺项——已写入上方第 2 节 |
