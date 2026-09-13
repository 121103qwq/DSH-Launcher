using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DshLauncher.Controls;

/// <summary>
/// 图标（变更集 147）：几何取自 <c>App.xaml</c> 的 <c>Icon.*</c> 注册表
/// （Tabler Icons v3.46.0，MIT；出处与许可全文见 <c>docs/THIRD-PARTY-NOTICES.md</c>）。
/// 用法：<c>&lt;controls:UiIcon Kind="Play" Size="18" /&gt;</c>。
/// 颜色跟随**继承下来的**前景色（<c>TextElement.Foreground</c> —— 它与 <c>Control.Foreground</c> 是同一属性，
/// 所以按钮/文本块设过的 Foreground 会传进来）；需要单独着色时给 <c>Glyph.Stroke</c> 设本地值即可。
/// 尺寸请用字号阶梯里的值（16 / 18 / 20 / 22 / 30），门禁会校验。
/// </summary>
public partial class UiIcon : System.Windows.Controls.UserControl
{
    /// <summary>Tabler 图标是 24×24 坐标系、描边宽度 2 —— 因此笔画粗细按 Size/12 折算。</summary>
    private const double StrokeUnitsPerSize = 12.0;

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(string), typeof(UiIcon),
            new PropertyMetadata(string.Empty, OnVisualChanged));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(UiIcon),
            new PropertyMetadata(16.0, OnVisualChanged));

    public UiIcon()
    {
        InitializeComponent();

        // 颜色继承：Path 自身没有 Foreground，读继承下来的 TextElement.Foreground
        Glyph.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding("(TextElement.Foreground)") { RelativeSource = RelativeSource.Self });
        Apply();
    }

    /// <summary>图标名（对应 <c>Icon.&lt;Kind&gt;</c> 资源）。</summary>
    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>图标边长（逻辑像素，走字号阶梯）。</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((UiIcon)d).Apply();

    private void Apply()
    {
        var size = Size > 0 ? Size : 16.0;
        Width = size;
        Height = size;
        Glyph.StrokeThickness = size / StrokeUnitsPerSize;
        Glyph.Data = string.IsNullOrWhiteSpace(Kind)
            ? null
            : TryFindResource("Icon." + Kind) as Geometry;
    }
}
