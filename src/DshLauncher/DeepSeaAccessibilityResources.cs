using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfColor = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace DshLauncher;

/// <summary>
/// Applies the DeepSea high-contrast palette without mutating frozen WPF
/// Freezables. Normal startup is a no-op until high contrast was actually
/// applied, preventing an unnecessary restore pass during MainWindow creation.
/// </summary>
internal sealed class DeepSeaAccessibilityResources
{
    private readonly ResourceDictionary _resources;
    private readonly Dictionary<string, WpfColor> _solidColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WpfColor[]> _gradientColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _effectOpacities = new(StringComparer.Ordinal);
    private bool _highContrastApplied;

    internal DeepSeaAccessibilityResources(ResourceDictionary resources)
    {
        _resources = resources;
        foreach (var rawKey in resources.Keys)
        {
            if (rawKey is not string key
                || !key.StartsWith("DeepSea", StringComparison.Ordinal))
            {
                continue;
            }

            switch (resources[key])
            {
                case SolidColorBrush brush:
                    _solidColors[key] = brush.Color;
                    break;
                case GradientBrush gradient:
                    _gradientColors[key] = gradient.GradientStops
                        .Select(stop => stop.Color)
                        .ToArray();
                    break;
                case DropShadowEffect effect:
                    _effectOpacities[key] = effect.Opacity;
                    break;
            }
        }
    }

    internal void Apply(bool highContrast)
    {
        if (!highContrast)
        {
            if (!_highContrastApplied)
            {
                return;
            }

            Restore();
            _highContrastApplied = false;
            return;
        }

        ApplyHighContrast();
        _highContrastApplied = true;
    }

    private void ApplyHighContrast()
    {
        foreach (var key in _solidColors.Keys)
        {
            if (GetMutableResource<SolidColorBrush>(key) is { } brush)
            {
                brush.Color = ResolveHighContrastColor(key);
            }
        }

        foreach (var key in _gradientColors.Keys)
        {
            if (GetMutableResource<GradientBrush>(key) is not { } gradient)
            {
                continue;
            }

            var color = key.Contains("TopHighlight", StringComparison.Ordinal)
                ? Colors.Transparent
                : key.Contains("Border", StringComparison.Ordinal)
                    ? SystemColors.HighlightColor
                    : SystemColors.WindowColor;
            foreach (var stop in gradient.GradientStops)
            {
                stop.Color = color;
            }
        }

        foreach (var key in _effectOpacities.Keys)
        {
            if (GetMutableResource<DropShadowEffect>(key) is { } effect)
            {
                effect.Opacity = 0;
            }
        }
    }

    private void Restore()
    {
        foreach (var pair in _solidColors)
        {
            if (GetMutableResource<SolidColorBrush>(pair.Key) is { } brush)
            {
                brush.Color = pair.Value;
            }
        }

        foreach (var pair in _gradientColors)
        {
            if (GetMutableResource<GradientBrush>(pair.Key) is not { } gradient)
            {
                continue;
            }

            for (var index = 0;
                 index < gradient.GradientStops.Count && index < pair.Value.Length;
                 index++)
            {
                gradient.GradientStops[index].Color = pair.Value[index];
            }
        }

        foreach (var pair in _effectOpacities)
        {
            if (GetMutableResource<DropShadowEffect>(pair.Key) is { } effect)
            {
                effect.Opacity = pair.Value;
            }
        }
    }

    private T? GetMutableResource<T>(string key)
        where T : Freezable
    {
        if (_resources[key] is not T resource)
        {
            return null;
        }

        if (!resource.IsFrozen)
        {
            return resource;
        }

        // Shared application resources can be frozen after XAML materialization.
        // Clone-and-replace keeps the optional accessibility path from throwing
        // during startup. Existing shell elements that need immediate changes are
        // also updated explicitly by MainWindow.ApplyAccessibilityPresentation().
        var mutable = (T)resource.CloneCurrentValue();
        _resources[key] = mutable;
        return mutable;
    }

    private static WpfColor ResolveHighContrastColor(string key)
    {
        if (key.Contains("PrimaryText", StringComparison.Ordinal))
        {
            return SystemColors.HighlightTextColor;
        }

        if (key.Contains("MutedText", StringComparison.Ordinal))
        {
            return SystemColors.GrayTextColor;
        }

        if (key.Contains("Text", StringComparison.Ordinal)
            || key.Contains("Success", StringComparison.Ordinal)
            || key.Contains("Warning", StringComparison.Ordinal)
            || key.Contains("Error", StringComparison.Ordinal))
        {
            return SystemColors.WindowTextColor;
        }

        if (key.Contains("ElectricCyan", StringComparison.Ordinal)
            || key.Contains("LightPurple", StringComparison.Ordinal)
            || key.Contains("Border", StringComparison.Ordinal)
            || key.Contains("Primary", StringComparison.Ordinal))
        {
            return SystemColors.HighlightColor;
        }

        if (key.Contains("Nav", StringComparison.Ordinal)
            || key.Contains("Danger", StringComparison.Ordinal))
        {
            return SystemColors.ControlColor;
        }

        return SystemColors.WindowColor;
    }
}
