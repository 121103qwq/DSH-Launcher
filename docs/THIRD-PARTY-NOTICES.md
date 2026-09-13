# 第三方资源与许可声明（THIRD-PARTY NOTICES）

本仓库包含来自第三方的图标几何数据。以下逐条列出出处、版本与许可条款。

---

## 1. Tabler Icons（图标几何数据）

- **用途**：`src/DshLauncher/App.xaml` 中的 `Icon.*` 几何资源（按钮/导航/标题栏图标），经 `src/DshLauncher/Controls/UiIcon.cs` 渲染。
- **来源**：<https://github.com/tabler/tabler-icons>
- **发行包**：npm `@tabler/icons`（本仓库取用版本 **3.46.0**；每个图标的 `path` 数据按原样内联，未做形状修改）
- **取值地址**：`https://cdn.jsdelivr.net/npm/@tabler/icons@3.46.0/icons/outline/<name>.svg`
- **许可**：MIT License

```
MIT License

Copyright (c) 2020-2026 Paweł Kuna

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

> 说明：MIT 许可要求"在软件的所有副本或实质性部分中保留上述版权声明与本许可声明"。因此本文件包含许可全文，
> 并在 `App.xaml` 的图标注册表处标注了来源与版本。若要升级图标版本，请同步更新本节与 `App.xaml` 中的版本号。

---

## 2. 未被采用但评估过的候选（备查）

| 图标集 | 许可 | 未采用原因 |
|---|---|---|
| Feather Icons | MIT | 覆盖度略低（缺 sparkles / package-export 等语义） |
| Heroicons | MIT | 风格偏填充与粗线，与本应用线形图标不统一 |
| Material Symbols | Apache-2.0 | 许可非 MIT（用户指定 MIT） |
