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
| `BlueBrush` / `BlueDarkBrush` / `BlueBrightBrush` | #1370F3 / #0B5BCB / #4890F5 | 主色、悬停、强调 |
| `PaleBlueBrush` / `HoverBlueBrush` / `SelectedBlueBrush` | #96C0F9 / #E0EAFD / #EAF2FE | 浅色交互态 |
| `PageBrush` / `CardBrush` / `PanelBackgroundBrush` / `InfoBackgroundBrush` / `HoverSurfaceBrush` | #EAF2FE / #FFFFFF / #F8FBFE / #F5F9FD / #F7FAFD | 页面/卡片/次级面板/信息底/悬停底 |
| `TextBrush` / `MutedBrush` / `FaintBrush` | #343D4A / #8C8C8C / #A6A6A6 | 正文/次要/更弱 |
| `LineBrush` | #D5E6FD | 描边、分隔线 |
| `SuccessTextBrush` / `DangerTextBrush` / `DangerBrush` | #25875A / #A25A54 / #B42318 | 成功文字/危险文字/危险操作 |
| `WarningBrush` / `WarningBackgroundBrush` | #F1C26B / #FFF9EA | 警告描边/警告底 |
| `GreenBrush` / `RedBrush` | #2EA66B / #CE2111 | 状态点、强提示 |

> **禁止**在窗口 XAML 里硬编码上述语义色（harness 有断言）。

### 字体与字号

字体：`UiFont` = Segoe UI, Microsoft YaHei UI。

| 字号 | 用途 |
|---|---|
| 24 / 30 / 28 | 品牌/插画/空态图标（例外，不用于正文） |
| 20 | 页面标题 |
| 18 | 卡片标题 |
| 16 | 子标题（实例名等） |
| 15 | 区块标题（“版本检查”“整合包格式”） |
| 14 | 按钮、正文 |
| 13 | 输入框、下拉、复选框 |
| 12 | 胶囊/徽标、列表描述 |
| 11 | 元信息（版本号、路径、来源、时间）——**最小字号** |

规则：11px 只用于元信息；可交互/需要强调的小字用 12px + `SemiBold`。

### 圆角 / 间距

| 令牌 | 值 | 用途 |
|---|---|---|
| `CardCornerRadius` | 12 | 主卡片、页面级面板 |
| `PanelCornerRadius` | 10 | 卡片内次级面板、列表容器 |
| `ItemCornerRadius` | 8 | 列表项、按钮、输入框、小卡片 |
| `ChipCornerRadius` | 10 | 状态胶囊 |
| `CardPadding` | 18 | 主卡片内边距 |
| `PanelPadding` | 16 | 次级面板内边距 |
| `SectionMargin` | 0,16,0,0 | 区块之间的垂直间距 |
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

## 三、组件规范

### 卡片（Card）
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
允许横向滚动（`HorizontalScrollBarVisibility=Auto`）。列宽总和应 ≤ 默认窗口内容宽度，
避免默认尺寸下就出现横向滚动条；窄窗口才出现属正常。

### 按钮
- 默认样式：白底 + `#C5D5E6` 描边 + 8px 圆角 + 内边距 16,9；悬停变主色。
- 主操作：`PrimaryButton`（蓝底白字 SemiBold）。
- 紧凑按钮：`Padding="10,6"`；工具条按钮 `Padding="12,7"`。
- 危险操作：`Foreground="{StaticResource DangerBrush}"`。
- 不要自建按钮模板；导航按钮用 `NavButton` / `TopNavButton`。

### 输入 / 下拉 / 复选 / 进度条
- TextBox / ComboBox 由全局样式提供（8px 圆角、聚焦蓝边）。
- CheckBox 由全局样式提供（18px 圆角方框 + 蓝色对钩），不要用系统默认外观。
- ProgressBar 由全局样式提供（蓝色圆角；不确定态脉冲），高度默认 6。

### 状态胶囊（Chip）
12px SemiBold 文字 + 1px 同色描边 + 半透明同色底（不透明度 ≥ 44）+ 8px 圆点；
`SnapsToDevicePixels` + `UseLayoutRounding`。颜色按语义取 `SuccessTextBrush` /
`DangerTextBrush` / `MutedBrush` 系。

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

默认启动尺寸 = **最小可调尺寸**（`MainWindow` 880×520）；用户放大后由窗口记忆持久化，
下次启动恢复用户尺寸。

## 四、审查清单（提交前逐项）

- [ ] 没有把 `Effect` 挂在含内容的元素上（阴影独立层）。
- [ ] 没有半透明（<40%）底 + 11px 小字。
- [ ] 没有硬编码语义色（用令牌）。
- [ ] 圆角/内边距使用令牌值，没有 13/14/9 等“近邻值”。
- [ ] 长文本有省略/换行策略，且不会撑破容器。
- [ ] 列表项 `HorizontalContentAlignment=Stretch`、长列表开虚拟化。
- [ ] 默认窗口尺寸下不出现横向滚动条；窄窗口才出现属正常。
- [ ] 空态有引导；错误提示用 `DangerBrush`/`RedBrush`。
- [ ] 需要联网的对话框先弹窗、后异步补数据；打开/创建类按钮有忙碌态与防重入。
- [ ] 运行 `_verify-p0` harness（`p1/ui:` 契约断言）通过。

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
