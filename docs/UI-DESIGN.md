# DSH Launcher · UI 设计规范

> 本文件是 UI 的**单一事实来源**。任何新增/修改界面都应先对照本规范；
> 与规范冲突的旧写法在改动时应顺手收敛（值等价优先）。
> 规范来源：2026-08-23 起的历次 UI 工作（work-log 13/15/18/23/36/37/38）与
> 2026-09-09 全量 UI 审查（work-log/30）。
> 所有令牌定义在 `src/DshLauncher/App.xaml`。

## 一、原则

1. **信息密度优先**：左栏/侧栏不放多行原始文本（路径、日志、长描述），改为
   单行省略 + 悬浮卡片 + 点击复制（见「链接」）。
2. **矢量优先**：文字永远不要放进带 `Effect`（阴影/模糊）的元素里——WPF 会把
   整棵子树先栅格化成位图，小字会发糊。阴影必须**单独一层**。
3. **值等价收敛**：同一语义（卡片圆角、语义色、卡片内边距）只允许一个令牌值。
4. **不引入新控件外观**：Button/TextBox/ComboBox/CheckBox/ProgressBar/ScrollBar/
   TabItem 的默认外观已被全局样式覆盖，新界面直接用默认样式，不要再写内联模板。
5. **可验证**：规范中的硬性条款在 `_verify-p0` harness 里有契约断言（`p1/ui:` 前缀）。

## 二、设计令牌（App.xaml）

### 颜色

| 令牌 | 值 | 用途 |
|---|---|---|
| `BlueBrush` / `BlueDarkBrush` | #1370F3 / #0B5BCB | 主色、悬停 |
| `PageBrush` / `CardBrush` / `PanelBackgroundBrush` / `InfoBackgroundBrush` / `HoverSurfaceBrush` | #EAF2FE / #FFFFFF / #F8FBFE / #F5F9FD / #F7FAFD | 页面/卡片/次级面板/信息底/悬停底 |
| `TextBrush` / `MutedBrush` | #343D4A / #8C8C8C | 正文/次要 |
| `LineBrush` | #D5E6FD | 描边、分隔线 |
| `SuccessTextBrush` / `DangerTextBrush` / `DangerBrush` | #25875A / #A25A54 / #B42318 | 成功文字/危险文字/危险操作 |
| `WarningBrush` / `WarningBackgroundBrush` | #F1C26B / #FFF9EA | 警告描边/警告底 |
| `GreenBrush` | #2EA66B | 状态点（成功） |
| `StatusIdleBrush` | #96A3B5 | 状态点（空闲/未知） |
| `DangerSurfaceBrush` / `SuccessSurfaceBrush` / `SuccessBorderBrush` | #FEF3F2 / #E7F6EC / #2E7D32 | 危险底/成功底/成功描边 |
| `InfoSurfaceBrush` / `HighlightSurfaceBrush` / `NeutralSurfaceBrush` | #F4F8FC / #E3F0FD / #F4F5F7 | 信息底/高亮底/中性底 |
| `TitleBarChromeBrush` / `TitleBarChromeBorderBrush` / `TitleBarChromeHoverBrush` / `TitleBarChromePressedBrush` / `TitleBarCloseHoverBrush` | #55FFFFFF / #80FFFFFF / #80FFFFFF / #B3FFFFFF / #CE2111 | 标题栏按钮常态/描边/悬停/按下/关闭悬停 |
| `EmptyStateGradientStartColor` / `EmptyStateGradientEndColor`（Color） + `EmptyStateAccentBrush` / `EmptyStateAccentSoftBrush` | #4890F5 / #96C0F9 / #7FB2F9 / #C6DCFC | 空态插画（GradientStop 需 Color 型） |
| `MenuHoverBrush` | #E0EAFD | 菜单项悬停底（变更集 131） |
| `BlueColor` / `BlueDarkColor`（Color） | #1370F3 / #0B5BCB | 标题栏渐变停靠点（GradientStop 需 Color 型） |
| `ShadowColor`（Color） | #3E5C7A | 卡片阴影（`CardShadow` / `CardShadowSoft`） |
| `OnBrandTextBrush` | #FFFFFF | 品牌蓝底上的文字 / 描边 |
| `ControlBorderBrush` | #C5D5E6 | 输入类控件描边 |
| `SubtleSurfaceBrush` | #E7F1FC | 次级浅蓝面（禁用底 / 选中底 / 进度条底） |
| `SelectionBrush` | #B8DCF8 | 文本选择高亮 |
| `ScrollThumbBrush` / `ScrollThumbHoverBrush` / `ScrollThumbDragBrush` | #C8D8EA / #96C0F9 / #4890F5 | 滚动条滑块常态 / 悬停 / 拖动 |
| `StatusOkSoftBrush` / `StatusErrorSoftBrush` / `StatusIdleSoftBrush` | #342EA66B / #34D94A4A / #2C96A3B5 | 状态胶囊底（≈15% 透明，8 位 ARGB） |
| `StatusOkTextBrush` / `StatusIdleTextBrush` | #1F7A50 / #606B7A | 状态胶囊文字（深色调） |
| `AccentPurpleBrush` | #6A3D9A | `Tui` / home 来源标签 |

> **颜色字面量的唯一合法归宿＝本表的令牌定义行**（`App.xaml` 里含 `x:Key=` 的行）。除此之外：
> - **XAML**（含 `App.xaml` 的样式/模板内部）不得出现 `#RRGGBB` / `#AARRGGBB`；
> - **C#** 不得出现 `Color.FromRgb/FromArgb`（**全数字参数**）与 `Colors.*`；取令牌用 `Services.UiBrush.Get("<令牌名>")`。
>
> **例外（合法）**：从**已有颜色派生**的写法，如 `Color.FromArgb(36, solid.Color.R, solid.Color.G, solid.Color.B)` —— 门禁按「全数字参数」放行。
> harness 断言「颜色已全部令牌化（XAML 含 App.xaml 非令牌行 + C# 均无颜色字面量）」自 **变更集 132** 起为全量门禁
> （变更集 127 的首版规则是「XAML 里、App.xaml 之外」，实际放过了 52 处）。

### 字体与字号

字体：`UiFont` = Segoe UI, Microsoft YaHei UI。

| 字号 | 用途 |
|---|---|
| 24 / 28 / 30 | 品牌/插画/空态图标（例外，不用于正文；**图标字形另见「图标体系」**） |
| 20 | 页面标题、页内大区块标题（如「插件市场」「Skill 市场」） |
| 18 | 卡片标题（如「当前实例」「检查版本」） |
| 16 | 子标题（实例名、版本名等） |
| 15 | 区块标题（“版本检查”“整合包格式”） |
| 14 | 按钮、正文 |
| 13 | 输入框、下拉、复选框 |
| 12 | 胶囊/徽标、列表描述、角标 |
| 11 | 辅助文字（说明、次要信息、时间戳、提示行）——**全仓用量最大（103 处）**，2026-09-13 正式补入阶梯 |

> **阶梯口径（2026-09-13 决议，变更集 128）**：本表此前只列到 12，与代码实况（11px 用于辅助文字 103 处）以及
> `UI-UNIFICATION-TODO.md` §1.3 的「11（辅助）」都不一致。本轮按"以规范为准 + 规范未覆盖即补充"的规则**把 11 正式补入阶梯**，
> 而不是把 103 处 11px 改成 12px（那会造成大范围视觉变化）。越界判定与门禁：`FontSize` ∈ {11,12,13,14,15,16,18,20,24,28,30}，
> **图标字形**（`Text`/`Content` 为几何符号，如 `— □ × ⌄`）不参与文字阶梯，由图标体系规范（变更集 C）。

> **小字字重（2026-09-13 决议，变更集 129）**：**只有「胶囊 / 徽标 / 状态标签」类**（小圆角 `CornerRadius="3"`、浅色底、短标签文字的 `Border`）
> 用 **12px + `SemiBold`**；其余 ≤12px 文字（说明行、次要信息、时间戳）保持**常规字重**。
> 原因：`UI-UNIFICATION-TODO.md` §1.3 早先写的「≤12px 一律 `SemiBold`」经实测（129 个 ≤12px 文本元素里 112 个未加粗）
> 若照字面执行会让辅助文字整体变重，故**按角色收窄**为只覆盖胶囊类。harness 断言「胶囊/徽标/状态标签为 12px + SemiBold」逐个小圆角胶囊扫描。
| 11 | 元信息（版本号、路径、来源、时间）——**最小字号** |

规则：11px 只用于元信息；可交互/需要强调的小字用 12px + `SemiBold`。

### 图标（字形集）

本项目使用**一套固定的字形**（符号字体字形，单色渲染，随 `UiFont`），不引入位图/矢量图标资源；
**禁止 emoji**（U+1F300 及以上）与未登记的新字形——新增图标必须先加进本表（harness 2026-09-13 起有断言）。

| 语义 | 字形 | 码位 | 用在哪 | 字号 |
|---|---|---|---|---|
| 启动 / 运行 | ▶ | U+25B6 | 导航「启动」、主启动按钮 | 18（导航继承） |
| 扩展 | ◇ | U+25C7 | 导航「扩展」 | 同上 |
| Agent | ✦ | U+2726 | 导航「Agent」 | 同上 |
| 对话 | ☷ | U+2637 | 导航「对话」 | 同上 |
| 任务 | ⧗ | U+29D7 | 导航「任务」 | 同上 |
| 设置 / 诊断、配置 | ⚙ | U+2699 | 导航「设置 / 诊断」、实例设置「配置」 | 同上 |
| 个性化 | ▣ | U+25A3 | 实例设置分类 | 同上 |
| 插件管理 | ♧ | U+2667 | 实例设置分类 | 同上 |
| 运行状况 | ◎ | U+25CE | 实例设置分类 | 同上 |
| 导出 | ⇧ | U+21E7 | 实例设置分类 | 同上 |
| 返回 | ← | U+2190 | 返回按钮 | 18 |
| 展开 / 下拉 | ▾ | U+25BE | 下拉、折叠、实例选择器（**统一用 ▾，不再用 ⌄**） | 18 |
| 最小化 / 最大化 / 关闭 | — □ × | U+2014 / U+25A1 / U+00D7 | 标题栏三按钮（**统一 18px**，2026-09-13 由 22/18/28 收敛） | 18 |
| 外部链接 | ↗ | U+2197 | 「中文官网 ↗」等外链按钮 | 继承 |
| 新建 | ＋ | U+FF0B | 空态「尚未创建版本」加号（全角，58×58 圆角框内视觉居中） | 30（品牌例外） |

- **间距**：图标与文字一律用令牌 **`IconTextGap`**（`0,0,8,0`）；字形自身的微调（如最大化字形 `Margin="0,0,2,0"`）不属于图标-文字间距，留在元素上。
- **尺寸**：导航 / 按钮 / 标题栏字形统一 **18px**；空态装饰字形 30px（品牌例外）；列表内字形继承宿主字号。
- **不做的**：矢量 `Path`/`Geometry` 图标集（托盘/任务栏仍用 `.ico` 资源）——保留为独立主题，需要时再评估。

### 圆角 / 间距

| 令牌 | 值 | 用途 |
|---|---|---|
| `CardCornerRadius` | 12 | 主卡片、页面级面板 |
| `PanelCornerRadius` | 10 | 卡片内次级面板、列表容器 |
| `ItemCornerRadius` | 8 | 列表项、按钮、输入框、小卡片 |
| `CardPadding` | 18 | 主卡片内边距 |
| `PanelPadding` | 16 | 次级面板内边距 |
| `ItemMargin` | 0,0,0,8 | 列表项之间的间距 |

页面外边距统一 18–22；卡片标题到内容 14–16；按钮组内间距 8。

### 阴影

- `CardShadow`（Blur 16 / Depth 3 / 14%）：主卡片。
- `CardShadowSoft`（Blur 10 / Depth 1 / 8%）：侧栏卡片。
- **阴影层必须是独立的空 `Border`**，与内容层同尺寸同圆角，叠在内容层下面：

```xml
<Grid Grid.Column="2">
    <Border Background="{StaticResource CardBrush}"
            CornerRadius="{StaticResource CardCornerRadius}"
            BorderBrush="{StaticResource LineBrush}" BorderThickness="1"
            Effect="{StaticResource CardShadow}" />
    <Border Background="{StaticResource CardBrush}"
            CornerRadius="{StaticResource CardCornerRadius}"
            Padding="{StaticResource CardPadding}"
            BorderBrush="{StaticResource LineBrush}" BorderThickness="1">
        <!-- 内容 -->
    </Border>
</Grid>
```

- 内容层的 Border 若有 `Visibility` 切换，阴影层要绑定它：
  `Visibility="{Binding Visibility, ElementName=<内容层名>}"`。

### 窗口尺寸计算
- `SystemParameters.WorkArea` **已经是 DIP**（125% 缩放下 1920×1020 物理 → 1536×816）；
  **禁止再除以 `DpiScale`**——否则 `Width` 小于 `MinWidth`，原生窗口按偏小值创建、
  布局按 `MinWidth` 排，右侧/底部内容会被裁掉。
- 尺寸适配与居中统一走 `WindowSizeHelper.FitInitialSize`（构造函数），
  `Window_OnLoaded` 不再重复计算。
- 启动时写一行“窗口尺寸诊断”日志（dpi/workArea/width/height/actual/min），便于排查。

## 三、组件规范

### 卡片（Card）

**2026-09-13 实测补充（变更集 135）**

- **圆角只用令牌**：主卡片 `CardCornerRadius`(12)、次级面板 `PanelCornerRadius`(10)、列表项/按钮/输入框 `ItemCornerRadius`(8)。
  **白名单例外**（允许裸值）：`3`＝胶囊/徽标（见「状态胶囊」）、`17`/`18`＝透明窗口外框与 58×58 空态插画。harness 断言"非白名单裸圆角 = 0"。
- **阴影**：一律 `CardShadow`/`CardShadowSoft`，且**必须独立分层**（`Effect` 不挂含内容的元素）——harness 已断言。
- **卡片外边距**：页面内容与卡片之间用 `0,0,0,12`（现口径，实测 6–12）；卡片内部间距用 `CardPadding`/`PanelPadding` 令牌。
白色底 + `LineBrush` 1px 描边 + `CardCornerRadius` + `CardPadding` + 独立阴影层。
标题 18px SemiBold，副标题 11px Muted。

### 次级面板（Panel）
`InfoBackgroundBrush` 或 `CardBrush` 底 + `PanelCornerRadius` + `PanelPadding`，无阴影。

### 列表（ListBox / ListView）
- 项卡片：`CardBrush` 底 + `LineBrush` 描边 + `ItemCornerRadius` + 内边距 10–14。
- `ItemContainerStyle` 必须 `HorizontalContentAlignment=Stretch`（否则卡片会缩到内容宽度）。
- 长文本：单行 `TextTrimming="CharacterEllipsis"` + `ToolTip` 全文；描述最多 2 行
  （`MaxHeight≈34`）。
- 大列表开启虚拟化：`VirtualizingStackPanel.IsVirtualizing=True`、
  `VirtualizationMode=Recycling`、`ScrollViewer.CanContentScroll=True`。
- 列表容器设 `ClipToBounds=True`，避免滚动内容压出圆角。

### 数据表（ListView + GridView）

**2026-09-13 实测补充（变更集 134，见 work-log/125）**

- **列宽自适应**：`GridViewColumn.Width` 是**像素 `double`**——**没有 `MinWidth`，也不支持星号比例**。做法：XAML 写设计初值，窗口 `SizeChanged`/`Loaded` 时由代码把"最小宽之外的剩余宽度"按权重分配（`ConversationWindow.DistributeColumns`）。
- **长列表虚拟化**：在 `ListBox`/`ListView` **元素上显式**声明 `VirtualizingStackPanel.IsVirtualizing="True"` + `VirtualizingStackPanel.VirtualizationMode="Recycling"` + `ScrollViewer.CanContentScroll="True"`。
- ⚠️ **禁止**用隐式样式 + `BasedOn="{StaticResource {x:Type ListView}}"` 批量设置：本应用里该键**解析不到主题样式**，会在**创建列表时**抛 `XamlParseException`（"无法找到名为 System.Windows.Controls.ListView 的资源"），表现为"点页面没反应、内容停在上一页"。harness 已断言禁止该写法。
- **列头与行高统一：待做**（要统一 `GridViewColumnHeader`/`ListViewItem` 外观同样需要主题样式 `BasedOn`，得先找到安全做法：定义在窗口 Resources 里并实测，或写完整模板）。
允许横向滚动（`HorizontalScrollBarVisibility=Auto`）。列宽总和应 ≤ 默认窗口内容宽度，
避免默认尺寸下就出现横向滚动条；窄窗口才出现属正常。

### 按钮

**三级动作按钮（2026-09-13 统一，变更集 135）**

| 级别 | 样式 | 外观 |
|---|---|---|
| 主操作 | `PrimaryButton` | 蓝实心（`BlueBrush` 底 + `OnBrandTextBrush` 字），悬停 `BlueDarkBrush` |
| 次级操作 | 默认按钮（不挂样式） | 白底（`CardBrush`）+ `ControlBorderBrush` 描边 |
| 危险操作 | `DangerButton` | `DangerTextBrush` 字 + `DangerBrush` 描边 + 浅底，悬停转 `DangerBrush` 实心 |

- **统尺寸**：`PrimaryButton`/`DangerButton` 都带 `MinHeight="38"` + `Padding="16,0"` → 同屏的主/危险按钮**高度与左右内边距一致**。
- **紧凑按钮分层**：工具条/卡片内的小按钮（`CompactToolbarButton` 等）允许 `Padding="10,4"`~`"12,7"`，**不与三级动作按钮混排在同一行**。
- 链接式动作用 `PathLinkButton`（无高度、无边框）。
- **危险操作必须用 `DangerButton`**（`Content` 含 删除/卸载/回滚/清理/移除 的按钮）：harness 有断言。
- 默认样式：白底 + `#C5D5E6` 描边 + 8px 圆角 + 内边距 16,9；悬停变主色。
- 主操作：`PrimaryButton`（蓝底白字 SemiBold）。
- 紧凑按钮：`Padding="10,6"`；工具条按钮 `Padding="12,7"`。
- 信息卡内的多按钮工具条（如「当前实例」卡）：`Padding="9,4"` + `FontSize="12"` + `Margin="0,0,6,6"`（本页定义 `CompactToolbarButton` / `CompactToolbarPrimaryButton`），避免在窄列里换行过多。
- 危险操作：`Foreground="{StaticResource DangerBrush}"`。
- 不要自建按钮模板；导航按钮用 `NavButton` / `TopNavButton`。

### 输入 / 下拉 / 复选 / 进度条
- TextBox / ComboBox 由全局样式提供（8px 圆角、聚焦蓝边）。
- CheckBox 由全局样式提供（18px 圆角方框 + 蓝色对钩），不要用系统默认外观。
- ProgressBar 由全局样式提供（蓝色圆角；不确定态脉冲），高度默认 6。

### 状态胶囊（Chip）

> 规格：**12px + `SemiBold`** + 小圆角（3）+ 浅色底 + 同族描边（如成功底 `SuccessSurfaceBrush` / 描边 `SuccessBorderBrush`、警告底 `WarningBackgroundBrush` / 描边 `WarningBrush`、中性底 `NeutralSurfaceBrush` / 描边 `LineBrush`）；内边距 6–9 × 1–3。
12px SemiBold 文字 + 1px 同色描边 + 半透明同色底（不透明度 ≥ 44）+ 8px 圆点；
`SnapsToDevicePixels` + `UseLayoutRounding`。颜色按语义取 `SuccessTextBrush` /
`DangerTextBrush` / `MutedBrush` 系。

### 空态 / 加载态 / 错误态（2026-09-13 统一，变更集 136）

| 态 | 文案 | 颜色 | 动作入口 |
|---|---|---|---|
| 空态 | 一句话说明"为什么是空的" | `MutedBrush` | 有可执行动作则给出按钮或明确指引（如"点击「刷新目录」"） |
| 加载态 | "正在…，请稍候"（≤1s 的操作可省） | `MutedBrush` | 长任务另配 `ProgressBar IsIndeterminate` |
| 警告态（部分失败） | "已更新，但有 N 个来源暂时不可用" | `WarningTextBrush` | 说明影响范围 |
| 错误态 | "…失败：原因（点「X」重试）" | `DangerTextBrush` | **必须**写明下一步点哪里 |

**纪律**：错误**不允许只写进日志**而没有界面反馈；失败文案**必须自带动作指引**。
参考实现：`ExtensionWindow.SetStatusText(block, text, isError:, isWarning:)`（市场 / Skill 状态行 11 处调用，失败文案统一带"（点「刷新目录」重试）"）。

### 菜单（ContextMenu / MenuItem）

| 部位 | 规格 |
|---|---|
| 弹出层 `ContextMenu` | `CardBrush` 底 + `LineBrush` 描边（1px）+ 圆角 10 + 内边距 6 + `HasDropShadow` + `MaxHeight=410`（超出内部滚动）；样式 `ContextInstanceMenuStyle` |
| 菜单项 `MenuItem` | 内边距 `10,8` + 圆角 7 + 前景 `TextBrush`；悬停底 `MenuHoverBrush`；选中（`IsChecked`）底 `PageBrush` + 前景 `BlueBrush`；行间距 `Margin 0,1`；样式 `ContextInstanceMenuItemStyle`（**隐式样式，所有菜单项默认命中**） |
| 禁用态（`IsEnabled=False`） | 前景 `MutedBrush` + **无悬停底** + 光标箭头 |
| 危险项 | `MenuItemDangerStyle`：前景 `DangerTextBrush`，悬停底仍用中性 `MenuHoverBrush` |
| 分隔线 | `MenuSeparatorStyle`：`LineBrush` 1px + `Margin 10,6`（隐式样式已生效） |
| 图标列 / 快捷键列 | **暂未启用**（未使用 `MenuItem.Icon` / `InputGestureText`）。若要启用：图标必须先在「图标（字形集）」登记，快捷键列用于展示 `InputGestureText` |

> **双行菜单项（变更集 133）**：需要解释的动作（如「导入实例」的 5 个入口）用「**标题 + 11px `MutedBrush` 说明**」两行承载。
> 说明写在 `MenuItem` 的 **Header 内容**里（`StackPanel` + 两个 `TextBlock`，说明行 `Margin="0,3,0,0"`），
> **不覆写共享模板**——覆写就要把悬停/选中/禁用三个触发器复制一份，日后改样式必然漏一处。
> 悬停/选中时**说明保持 `MutedBrush`**，只有标题随前景变化。

> ⚠️ **在 `App.xaml` 新增样式时的顺序要求**：`BasedOn` / `StaticResource` **不支持前向引用**——被引用的样式必须**先定义**。
> 2026-09-13（变更集 131）曾因把隐式 `MenuItem` 样式插到 `ContextInstanceMenuItemStyle` 之前，导致启动即抛
> `XamlParseException`（`StaticResourceHolder`）；harness 的「冒烟执行无异常」断言当场抓住。

### 链接（PathLinkButton）
用于长路径/标识：显示**尾部**（`TailPath`，上限 42 字符）+ `CharacterEllipsis`；
蓝色、悬停下划线、点击复制完整值；悬停出**独立悬浮卡片**（圆角边框 + 阴影 ToolTip），
卡片里给完整路径与必要上下文。不要为复制单独放按钮。

### 悬浮卡片（ToolTip）
结构化信息用卡片式 ToolTip（`PathCardToolTip` 样式）：标题 12px SemiBold +
正文 11px + “点击即可复制”提示，`MaxWidth=420`。简单文本用默认 ToolTip。

### 空态（Empty State）
居中插画/图标 + 15px SemiBold 标题 + 12px Muted 说明 + 主操作按钮。

### 对话框与异步操作
- 需要联网获取数据的对话框（如「新建版本」）必须**先弹窗**（用本机已有数据渲染），
  远程列表/元数据**后台异步补全**；禁止让按钮等网络（npmjs 在部分网络可长达几十秒）。
- 打开对话框/发起写操作的按钮必须有**忙碌态**：`IsEnabled` 绑定 `!_isBusy` + 代码重入保护
  （否则连点会弹出多个对话框或创建重复实例）。
- 远程数据失败/超时（建议 ≤ 8s）时静默回退到本地数据，不打断用户操作。

### 滚动条
全局 10px 细滚动条（无箭头、圆角 thumb、透明轨道）。禁止出现：
- 同方向嵌套滚动；
- 内容能放下却出现滚动条（通常是固定宽/高导致，改 `Auto`/`*`）；
- 横向滚动条挡住内容（容器 `Padding` + `ClipToBounds`）。

### 窗口
`WindowStyle=None` + `AllowsTransparency=True`（圆角窗口）。注意：此模式下
ClearType 会被系统降级为灰度抗锯齿，**11px 以下小字会明显发虚**，因此最小字号
11px，且所有 Window/UserControl 继承全局 `UseLayoutRounding` +
`TextFormattingMode=Display`。

**ClearType 降级范围（变更集 56 实测）**：只有 `MainWindow` 与 ComboBox 弹层是透明窗口（灰度抗锯齿）；`ChatWindow` / `ExtensionWindow` / `ConversationWindow` / 内嵌的版本设置页都是标准窗口，ClearType 正常。因此小字发虚只在主窗口出现——这也是主窗口最小字号 11px 且小字一律 `SemiBold` 的原因。**结论：保留透明圆角**（视觉核心），不用换取清晰度。

默认启动尺寸与**最小可调尺寸一致**（`MainWindow` 1180×720，小屏上自动收缩）；用户放大后由窗口记忆持久化，下次启动恢复用户尺寸。

## 四、审查清单（提交前逐项）

- [ ] 没有把 `Effect` 挂在含内容的元素上（阴影独立层）。
- [ ] 没有半透明（<40%）底 + 11px 小字。
- [ ] 没有硬编码语义色（用令牌）。
- [ ] 圆角/内边距使用令牌值，没有 13/14/9 等“近邻值”。
- [ ] 长文本有省略/换行策略，且不会撑破容器。
- [ ] 列表项 `HorizontalContentAlignment=Stretch`、长列表开虚拟化。
- [ ] 默认窗口尺寸下不出现横向滚动条；窄窗口才出现属正常。
- [ ] 空态有引导；错误提示用 `DangerBrush`。
- [ ] 需要联网的对话框先弹窗、后异步补数据；打开/创建类按钮有忙碌态与防重入。
- [ ] 运行 `_verify-p0` harness（`p1/ui:` 契约断言）通过。

> **跨页面一致性统一**不在功能迭代中顺手做：见 [`UI-UNIFICATION-TODO.md`](UI-UNIFICATION-TODO.md)（功能完成后作为独立变更集执行）。

## 五、历史演进（为什么有这些规则）

| 变更集 | 教训 |
|---|---|
| 13 | 托盘 + 启动方式三选项：操作入口必须成组、状态要可见 |
| 15 | 标题栏按钮统一、最大化占满：圆角/直角在最大化时要正确切换 |
| 18 | 响应式宿主 + 最大化工作区：不要用无限测量 ScrollViewer；边距统一 18 |
| 23 | 全局细滚动条 + `ClipToBounds`：滚动内容不能压出圆角 |
| 36 | 长路径改为链接 + 悬浮卡片：侧栏不再被原始文本挤占 |
| 37 | 实例名/版本合一行并下对齐：减少无信息空白 |
| 38 | 状态胶囊发糊：根因是 `DropShadowEffect` 把卡片内文字一起栅格化 + 小字半透明底 |
| 30（审查） | 全量检查：4 处阴影挂内容、语义色散落、CheckBox/ProgressBar 未定制、备份表默认就横向滚动 |
| 41 | 设置页分类栏改圆角卡片；「新建干净版本」先弹窗后联网（原要等 npmjs 数秒，像按钮坏了）+ 忙碌态防连点；默认窗口尺寸改为最小可调尺寸 |
| 56 | WPF `TextBlock` 没有 `MaxLines`（那是 WinUI）；两行截断要用 `MaxHeight` + `TextTrimming=CharacterEllipsis`。固定列宽的筛选行在窄窗口会挤爆，用 `*` + `MinWidth` |
