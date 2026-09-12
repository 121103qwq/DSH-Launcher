using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaPen = System.Windows.Media.Pen;

namespace DshLauncher.Controls;

/// <summary>
/// A light-weight, resource-driven surface that keeps the normal <see cref="Border"/>
/// API (including <see cref="Border.Child"/>, padding and corner radius).
/// Decorative work is deliberately limited to the surface chrome; content is
/// never given an effect.
/// </summary>
public class VisualSurface : Border
{
    private static readonly DependencyProperty HighlightResourceTokenProperty =
        DependencyProperty.Register(
            nameof(HighlightResourceToken),
            typeof(object),
            typeof(VisualSurface),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ShadowResourceTokenProperty =
        DependencyProperty.Register(
            nameof(ShadowResourceToken),
            typeof(object),
            typeof(VisualSurface),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public VisualSurface()
    {
        // These are references, rather than copied brushes, so changing the
        // launcher window's local palette updates existing surfaces in place.
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(BorderBrushProperty, "LineBrush");
        // Resource values are mirrored through render-affecting DPs. This
        // makes material changes invalidate the edge pass deterministically,
        // instead of relying on a background brush update as a side effect.
        SetResourceReference(HighlightResourceTokenProperty, "SurfaceHighlightOpacity");
        SetResourceReference(ShadowResourceTokenProperty, "SurfaceShadowOpacity");
    }

    private object? HighlightResourceToken
    {
        get => GetValue(HighlightResourceTokenProperty);
        set => SetValue(HighlightResourceTokenProperty, value);
    }

    private object? ShadowResourceToken
    {
        get => GetValue(ShadowResourceTokenProperty);
        set => SetValue(ShadowResourceTokenProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (ActualWidth <= 0 || ActualHeight <= 0 || BorderThickness.Left <= 0)
        {
            return;
        }

        var brush = TryFindResource("SurfaceHighlightBrush") as MediaBrush;
        if (brush is null)
        {
            return;
        }

        var opacity = TryFindResource("SurfaceHighlightOpacity") switch
        {
            double value when !double.IsNaN(value) && !double.IsInfinity(value) => value,
            _ => 0d
        };
        if (opacity <= 0)
        {
            return;
        }

        var thickness = Math.Max(0.5, BorderThickness.Left);
        var inset = thickness / 2;
        var rect = new Rect(inset, inset, Math.Max(0, ActualWidth - thickness), Math.Max(0, ActualHeight - thickness));
        var radius = Math.Max(0, Math.Min(CornerRadius.TopLeft, Math.Min(rect.Width, rect.Height) / 2));

        var shadowOpacity = TryFindResource("SurfaceShadowOpacity") switch
        {
            double value when !double.IsNaN(value) && !double.IsInfinity(value) => value,
            _ => 0d
        };
        if (shadowOpacity > 0)
        {
            var shadowBrush = new SolidColorBrush(MediaColor.FromArgb(80, 37, 83, 116));
            var shadowPen = new MediaPen(shadowBrush, thickness);
            drawingContext.PushOpacity(Math.Clamp(shadowOpacity, 0, 1));
            drawingContext.DrawRoundedRectangle(null, shadowPen, rect, radius, radius);
            drawingContext.Pop();
        }

        var pen = new MediaPen(brush, thickness);
        if (brush.IsFrozen && pen.CanFreeze)
        {
            pen.Freeze();
        }

        // A single low-alpha edge pass provides the highlight layer without a
        // second visual tree or an effect applied to the text/content child.
        drawingContext.PushOpacity(Math.Clamp(opacity, 0, 1));
        drawingContext.DrawRoundedRectangle(null, pen, rect, radius, radius);
        drawingContext.Pop();
    }
}
