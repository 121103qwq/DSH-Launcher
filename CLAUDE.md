# DSH Launcher — CLAUDE.md

## 重要能力限制

**Claude 不支持视觉功能。** Claude 无法查看屏幕截图、图像、窗口渲染结果或进行 Computer Use 点测，也无法通过截图判断界面外观、颜色、布局或像素级细节。所有界面外观的验收只能依赖代码审查、构建通过和自测结果，或由用户本人实机确认。

因此：

- 不要要求 Claude 通过截图或屏幕图像“确认界面效果”。
- UI 相关改动应通过 XAML/C# 代码检查、Release 构建（0 warnings / 0 errors）和对应服务自测来验证。
- 仓库规则中“只有用户明确要求时才能使用 Computer Use”的条款，在 Claude 不支持视觉的前提下无法由 Claude 执行；如需实机点测，请由用户或支持视觉的工具完成。

## 指向核心文档

@AGENTS.md
@CURRENT_DESIGN.md
@DEV_STATE.md

## 协作约定

- 文档语言为中文；回复用户时使用与用户一致的语言。
- 修改任何已有功能前，先阅读相关代码，遵循 @AGENTS.md 的最小修改原则。
- 完成测试版或发布版构建后，按 @AGENTS.md 的要求将产物复制到 `C:\Users\121103qwq\Desktop\DSH Launcher` 并确认存在。
