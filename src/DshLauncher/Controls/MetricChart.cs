using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace DshLauncher.Controls;

/// <summary>
/// 极简折线图（无第三方依赖）：网格 + 面积填充 + 折线 + 当前值/峰值标注。
/// 数据点由调用方按固定间隔追加，控件只负责画最近 N 个点。
/// </summary>
public sealed class MetricChart : FrameworkElement
{
    private const double PaddingLeft = 8;
    private const double PaddingRight = 8;
    private const double PaddingTop = 20;
    private const double PaddingBottom = 6;
    private const int GridLines = 3;

    public string Caption { get; set; } = string.Empty;

    public Brush LineBrush { get; set; } = Brushes.SteelBlue;

    public Brush MutedBrush { get; set; } = Brushes.Gray;

    public Brush GridBrush { get; set; } = Brushes.Gainsboro;

    /// <summary>0 = 自适应最大值；否则固定上限（如 CPU 用 100）。</summary>
    public double FixedMaximum { get; set; }

    /// <summary>当前值的格式化（如 0.0% / 0.0 MB）。</summary>
    public Func<double, string>? FormatValue { get; set; }

    public IReadOnlyList<double> Values { get; private set; } = Array.Empty<double>();

    public void SetSeries(IReadOnlyList<double> values)
    {
        Values = values ?? Array.Empty<double>();
        InvalidateVisual();
    }

    /// <summary>把数值序列映射到画布坐标（供渲染与测试）。</summary>
    internal static IReadOnlyList<Point> BuildPoints(
        IReadOnlyList<double> values,
        double width,
        double height,
        double maximum)
    {
        if (values.Count == 0 || width <= PaddingLeft + PaddingRight || height <= PaddingTop + PaddingBottom)
        {
            return Array.Empty<Point>();
        }

        var plotWidth = width - PaddingLeft - PaddingRight;
        var plotHeight = height - PaddingTop - PaddingBottom;
        var max = maximum <= 0 ? 1 : maximum;
        var step = values.Count > 1 ? plotWidth / (values.Count - 1) : 0;
        var points = new Point[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var value = Math.Clamp(values[index], 0, max);
            points[index] = new Point(
                PaddingLeft + (index * step),
                PaddingTop + plotHeight - (value / max * plotHeight));
        }

        return points;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var plotTop = PaddingTop;
        var plotBottom = height - PaddingBottom;

        for (var line = 0; line < GridLines; line++)
        {
            var y = plotTop + (plotBottom - plotTop) * line / (GridLines - 1);
            drawingContext.DrawLine(new Pen(GridBrush, 1), new Point(PaddingLeft, y), new Point(width - PaddingRight, y));
        }

        var maximum = FixedMaximum > 0
            ? FixedMaximum
            : Values.Count == 0
                ? 1
                : Math.Max(1, Values.Max() * 1.15);
        var points = BuildPoints(Values, width, height, maximum);
        if (points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(points[0].X, plotBottom), isFilled: true, isClosed: true);
                context.LineTo(points[0], isStroked: true, isSmoothJoin: false);
                for (var index = 1; index < points.Count; index++)
                {
                    context.LineTo(points[index], isStroked: true, isSmoothJoin: true);
                }

                context.LineTo(new Point(points[^1].X, plotBottom), isStroked: true, isSmoothJoin: false);
            }

            geometry.Freeze();
            var fill = LineBrush is SolidColorBrush solid
                ? new SolidColorBrush(Color.FromArgb(36, solid.Color.R, solid.Color.G, solid.Color.B))
                : Brushes.Transparent;
            fill.Freeze();
            drawingContext.DrawGeometry(fill, null, geometry);
            drawingContext.DrawGeometry(
                null,
                new Pen(LineBrush, 1.5) { LineJoin = PenLineJoin.Round },
                geometry);
        }

        if (!string.IsNullOrWhiteSpace(Caption))
        {
            var caption = new FormattedText(
                Caption,
                CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                MutedBrush,
                dpi);
            drawingContext.DrawText(caption, new Point(PaddingLeft, 2));
        }

        if (Values.Count == 0)
        {
            var empty = new FormattedText(
                "暂无数据（实例运行后每 5 秒采样一次）",
                CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11,
                MutedBrush,
                dpi);
            drawingContext.DrawText(
                empty,
                new Point(PaddingLeft, plotTop + (plotBottom - plotTop - empty.Height) / 2));
            return;
        }

        var formatter = FormatValue ?? (value => value.ToString("0.#", CultureInfo.CurrentCulture));
        var current = new FormattedText(
            $"{formatter(Values[^1])}   峰值 {formatter(Values.Max())}",
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            MutedBrush,
            dpi);
        drawingContext.DrawText(current, new Point(Math.Max(PaddingLeft, width - PaddingRight - current.Width), 2));
    }
}
