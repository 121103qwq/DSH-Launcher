using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DshLauncher.Models;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WpfMouseButtonEventHandler = System.Windows.Input.MouseButtonEventHandler;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfMouseEventHandler = System.Windows.Input.MouseEventHandler;
using WpfPoint = System.Windows.Point;

namespace DshLauncher;

/// <summary>
/// Owns the optional launcher-only visual layer. The controller intentionally
/// writes palette resources to the window, not Application.Resources, so a
/// separate DSH window cannot inherit the launcher surface treatment.
/// </summary>
internal sealed class VisualEffectsController : IDisposable
{
    internal const int MaximumParticles = 24;
    internal const int MaximumTrailNodes = 12;
    internal const int MaximumRipples = 3;

    private const double AmbientFrameSeconds = 1d / 30d;
    private const double RippleDuration = 0.62;
    private static readonly string[] PaletteKeys =
    {
        "PageBrush",
        "ContentPageBrush",
        "CardBrush",
        "LineBrush",
        "HeaderSurfaceBrush",
        "HeaderControlBrush",
        "SurfaceHighlightBrush",
        "SurfaceShadowOpacity",
        "SurfaceHighlightOpacity"
    };

    private static readonly WpfColor[] BlobColors =
    {
        WpfColor.FromRgb(53, 157, 232),
        WpfColor.FromRgb(99, 185, 238),
        WpfColor.FromRgb(121, 139, 227),
        WpfColor.FromRgb(63, 192, 186)
    };

    // Only four immutable decorative textures, created on first use. Keep
    // them at 96 DPI: these already-soft backgrounds need no full-window or
    // high-DPI render target, and text/content never enters this cache.
    private static readonly Lazy<BitmapSource[]> BlobTextures = new(CreateBlobTextures);

    private readonly Window _window;
    private readonly Canvas _backgroundLayer;
    private readonly Canvas _interactionLayer;
    private bool _renderingSubscribed;
    private readonly Dictionary<string, ResourceSnapshot> _resourceSnapshots = new(StringComparer.Ordinal);
    private readonly List<BlobState> _blobs = new();
    private readonly List<ParticleState> _particles = new();
    private readonly List<TrailState> _trailNodes = new();
    private readonly List<RippleState> _ripples = new();
    private VisualEffectsSettings _settings = new();
    private bool _enabled;
    private bool _paused;
    private bool _disposed;
    private bool _highContrast;
    private int _qualityTier;
    private double _animationTime;
    private WpfPoint _lastPointer;
    private WpfPoint _pointerPosition;
    private bool _hasPointer;
    private bool _hasSmoothedPointer;
    private int _trailCursor;
    private int _rippleCursor;
    private double _ambientAccumulator;
    private TimeSpan? _lastRenderingTime;
    private string _capabilityNotice = string.Empty;
    private LinearGradientBrush? _liquidHighlight;

    public VisualEffectsController(Window window, Canvas backgroundLayer, Canvas interactionLayer)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _backgroundLayer = backgroundLayer ?? throw new ArgumentNullException(nameof(backgroundLayer));
        _interactionLayer = interactionLayer ?? throw new ArgumentNullException(nameof(interactionLayer));

        _qualityTier = Math.Max(0, RenderCapability.Tier >> 16);
        _highContrast = SystemParameters.HighContrast;
        _capabilityNotice = BuildCapabilityNotice();

        // Both layers are overlays. Input is handled on Window so these
        // visuals can never put an invisible hit-test shield over page cards.
        _backgroundLayer.IsHitTestVisible = false;
        _interactionLayer.IsHitTestVisible = false;

        _window.AddHandler(UIElement.PreviewMouseMoveEvent, new WpfMouseEventHandler(OnPreviewMouseMove), true);
        _window.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new WpfMouseButtonEventHandler(OnPreviewMouseDown), true);
        _window.IsVisibleChanged += WindowIsVisibleChanged;
        _window.StateChanged += WindowStateChanged;
        _window.Unloaded += WindowUnloaded;
        _window.MouseLeave += WindowMouseLeave;
        _backgroundLayer.SizeChanged += BackgroundSizeChanged;
    }

    public string CapabilityNotice => _capabilityNotice;

    internal bool IsEnabled => _enabled;

    internal bool IsPaused => _paused;

    internal bool IsDisposed => _disposed;

    internal bool IsAnimationActive => _enabled && !_paused && _renderingSubscribed;

    internal bool IsRenderingSubscribed => _renderingSubscribed;

    internal int QualityTier => _qualityTier;

    internal bool HighContrastProtectionActive => _highContrast;

    internal int BackgroundBlobCount => _blobs.Count;

    internal int ParticleCount => _particles.Count;

    internal int TrailNodeCount => _trailNodes.Count;

    internal int RippleCount => _ripples.Count;

    internal double AnimationTime => _animationTime;

    internal VisualEffectsSettings CurrentSettings => _settings.Clone();

    /// <summary>
    /// Applies the complete snapshot. A disabled snapshot keeps its toggle
    /// choices but removes all resources, visuals and animation clocks.
    /// </summary>
    public void Apply(VisualEffectsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();

        _settings = settings.Clone();
        _enabled = _settings.Enabled;
        _highContrast = SystemParameters.HighContrast;
        _capabilityNotice = BuildCapabilityNotice();

        if (!_enabled)
        {
            StopVisualsAndRendering();
            RestorePalette();
            return;
        }

        // High contrast protection keeps the user's material choice in the
        // settings snapshot, but uses the opaque palette while it is active.
        ApplyPalette(_highContrast ? VisualMaterial.Solid : _settings.Material);
        RebuildVisuals();
        UpdatePausedState();
    }

    /// <summary>Pauses the owned clock without resetting any animation phase.</summary>
    internal void Pause()
    {
        if (_disposed)
        {
            return;
        }

        _paused = true;
        StopRendering();
    }

    /// <summary>Resumes from the exact phase at which <see cref="Pause"/> stopped.</summary>
    internal void Resume()
    {
        if (_disposed || !_enabled)
        {
            return;
        }

        _paused = false;
        UpdateRenderingSubscription();
    }

    /// <summary>
    /// Advances the deterministic animation clock for STA tests without a
    /// visible window, using the same frame scheduling as the compositor.
    /// </summary>
    internal void AdvanceForTest(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        if (_disposed || !_enabled || _paused || seconds <= 0)
        {
            return;
        }

        AdvanceFrame(seconds);
    }

    /// <summary>Injects a ripple for tests or another Window-owned input surface.</summary>
    internal void TriggerRippleForTest(WpfPoint point)
    {
        if (_disposed || !_enabled || !_settings.ClickRipples || _ripples.Count == 0)
        {
            return;
        }

        StartRipple(point);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRendering();
        _window.RemoveHandler(UIElement.PreviewMouseMoveEvent, new WpfMouseEventHandler(OnPreviewMouseMove));
        _window.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new WpfMouseButtonEventHandler(OnPreviewMouseDown));
        _window.IsVisibleChanged -= WindowIsVisibleChanged;
        _window.StateChanged -= WindowStateChanged;
        _window.Unloaded -= WindowUnloaded;
        _window.MouseLeave -= WindowMouseLeave;
        _backgroundLayer.SizeChanged -= BackgroundSizeChanged;
        StopVisualsAndRendering();
        RestorePalette();
    }

    private void ApplyPalette(VisualMaterial material)
    {
        foreach (var key in PaletteKeys)
        {
            if (!_resourceSnapshots.ContainsKey(key))
            {
                _resourceSnapshots[key] = _window.Resources.Contains(key)
                    ? new ResourceSnapshot(true, _window.Resources[key])
                    : new ResourceSnapshot(false, null);
            }
        }

        // PageBrush intentionally remains a light opaque surface for all
        // materials. BlueBrush/TextBrush are business-semantic app resources
        // and are never touched here.
        SetPalette("PageBrush", Brush("#EEF5FD"));
        SetPalette("ContentPageBrush", _highContrast ? Brush("#EEF5FD") : Brush("#EEF5FD", 0));
        SetPalette("CardBrush", material switch
        {
            VisualMaterial.Solid => Brush("#FFFFFF"),
            VisualMaterial.FrostedGlass => Brush("#E6F2FA", 0.88),
            _ => Brush("#F5FBFF", 0.58)
        });
        SetPalette("LineBrush", material switch
        {
            VisualMaterial.Solid => Brush("#D7E4F2"),
            VisualMaterial.FrostedGlass => Brush("#BBD9EC", 0.86),
            _ => Brush("#A6D5F0", 0.7)
        });
        SetPalette("HeaderSurfaceBrush", material switch
        {
            VisualMaterial.Solid => Brush("#1778D5"),
            VisualMaterial.FrostedGlass => Brush("#146FBF", 0.96),
            _ => Brush("#1269B4", 0.95)
        });
        SetPalette("HeaderControlBrush", Brush("#2578C4"));
        _liquidHighlight = material == VisualMaterial.LiquidGlass
            ? new LinearGradientBrush(new GradientStopCollection
            {
                new(WpfColor.FromArgb(25, 255, 255, 255), 0),
                new(WpfColor.FromArgb(95, 255, 255, 255), 0.35),
                new(WpfColor.FromArgb(255, 255, 255, 255), 0.5),
                new(WpfColor.FromArgb(130, 161, 226, 255), 0.65),
                new(WpfColor.FromArgb(25, 255, 255, 255), 1)
            }, new WpfPoint(0, 0), new WpfPoint(1, 1))
            : null;
        SetPalette("SurfaceHighlightBrush", (System.Windows.Media.Brush?)_liquidHighlight ?? Brush("#FFFFFF", 0.82));
        SetPalette("SurfaceShadowOpacity", material switch
        {
            VisualMaterial.Solid => 0d,
            VisualMaterial.FrostedGlass => 0.12d,
            _ => 0.16d
        });
        SetPalette("SurfaceHighlightOpacity", material switch
        {
            VisualMaterial.Solid => 0d,
            VisualMaterial.FrostedGlass => 0.42d,
            _ => 0.68d
        });
    }

    private void RestorePalette()
    {
        foreach (var pair in _resourceSnapshots)
        {
            if (pair.Value.WasPresent)
            {
                _window.Resources[pair.Key] = pair.Value.Value;
            }
            else
            {
                _window.Resources.Remove(pair.Key);
            }
        }
    }

    private void SetPalette(string key, object value) => _window.Resources[key] = value;

    private void RebuildVisuals()
    {
        _backgroundLayer.Children.Clear();
        _interactionLayer.Children.Clear();
        _blobs.Clear();
        _particles.Clear();
        _trailNodes.Clear();
        _ripples.Clear();
        _trailCursor = 0;
        _rippleCursor = 0;
        _hasPointer = false;
        _hasSmoothedPointer = false;
        _ambientAccumulator = 0;
        _lastRenderingTime = null;

        // High-contrast mode retains an opaque palette and input safety but
        // omits low-contrast decoration altogether.
        if (_highContrast)
        {
            return;
        }

        CreateBackgroundBlobs();
        if (_settings.PointerHalo)
        {
            var halo = new WpfEllipse
            {
                Width = 110,
                Height = 110,
                IsHitTestVisible = false,
                Opacity = 0.28,
                Visibility = Visibility.Hidden,
                RenderTransform = new TranslateTransform(),
                Fill = new RadialGradientBrush(
                    WpfColor.FromArgb(95, 71, 176, 234),
                    WpfColor.FromArgb(0, 71, 176, 234))
            };
            _interactionLayer.Children.Add(halo);
            _halo = halo;
        }
        else
        {
            _halo = null;
        }

        if (_settings.Particles)
        {
            var count = _qualityTier < 2 ? 8 : 18;
            for (var i = 0; i < Math.Min(MaximumParticles, count); i++)
            {
                CreateParticle(i);
            }
        }

        if (_settings.PointerTrail)
        {
            for (var i = 0; i < MaximumTrailNodes; i++)
            {
                var trail = new WpfEllipse
                {
                    Width = 8,
                    Height = 8,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Hidden,
                    RenderTransform = new TranslateTransform(),
                    Fill = new SolidColorBrush(WpfColor.FromArgb(125, 78, 164, 229))
                };
                _interactionLayer.Children.Add(trail);
                _trailNodes.Add(new TrailState(trail));
            }
        }

        if (_settings.ClickRipples)
        {
            for (var i = 0; i < MaximumRipples; i++)
            {
                var ripple = new WpfEllipse
                {
                    Width = 22,
                    Height = 22,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Hidden,
                    Stroke = new SolidColorBrush(WpfColor.FromArgb(185, 52, 143, 221)),
                    StrokeThickness = 2
                };
                var scale = new ScaleTransform(1, 1);
                var translation = new TranslateTransform();
                ripple.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
                ripple.RenderTransform = new TransformGroup { Children = { scale, translation } };
                _interactionLayer.Children.Add(ripple);
                _ripples.Add(new RippleState(ripple, scale, translation));
            }
        }

        // Establish deterministic initial coordinates even when ambient motion
        // is disabled; that switch controls movement, not layout placement.
        AdvanceAnimation(0, updateAmbient: true);
    }

    private WpfEllipse? _halo;

    private void CreateBackgroundBlobs()
    {
        var blobCount = 4;
        for (var i = 0; i < blobCount; i++)
        {
            var element = CreateBackgroundBlob(i, _qualityTier);
            element.IsHitTestVisible = false;
            element.Opacity = _settings.Material == VisualMaterial.LiquidGlass ? 0.84 : 0.62;
            element.RenderTransform = new TranslateTransform();
            _backgroundLayer.Children.Add(element);
            // Each blob completes a gentle loop in roughly 12-20 seconds.
            var cycleSeconds = 12d + i * 2.5d;
            _blobs.Add(new BlobState(element, i, Math.PI * 2 / cycleSeconds, 0.08 + i * 0.017));
        }
    }

    internal static FrameworkElement CreateBackgroundBlob(int index, int qualityTier)
    {
        if (qualityTier < 2) return CreateBlobEllipse(index);
        var texture = BlobTextures.Value[index];
        return new System.Windows.Controls.Image
        {
            Source = texture, Width = texture.Width, Height = texture.Height
        };
    }

    private static WpfEllipse CreateBlobEllipse(int index)
    {
        var color = BlobColors[index];
        var brush = new RadialGradientBrush
        {
            Center = new WpfPoint(0.5, 0.5),
            GradientOrigin = new WpfPoint(0.42, 0.38),
            RadiusX = 0.72, RadiusY = 0.72
        };
        brush.GradientStops.Add(new GradientStop(WpfColor.FromArgb(140, color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromArgb(58, color.R, color.G, color.B), 0.5));
        brush.GradientStops.Add(new GradientStop(WpfColor.FromArgb(0, color.R, color.G, color.B), 1));
        brush.Freeze();
        return new WpfEllipse { Width = 330 + index * 42, Height = 280 + index * 38, Fill = brush };
    }

    private static BitmapSource[] CreateBlobTextures()
    {
        var textures = new BitmapSource[BlobColors.Length];
        for (var i = 0; i < textures.Length; i++)
        {
            const int padding = 26;
            var ellipse = CreateBlobEllipse(i);
            ellipse.Effect = new BlurEffect { Radius = padding, RenderingBias = RenderingBias.Performance };
            var size = new System.Windows.Size(ellipse.Width + padding * 2, ellipse.Height + padding * 2);
            var canvas = new Canvas { Width = size.Width, Height = size.Height };
            Canvas.SetLeft(ellipse, padding);
            Canvas.SetTop(ellipse, padding);
            canvas.Children.Add(ellipse);
            canvas.Measure(size);
            canvas.Arrange(new Rect(size));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            bitmap.Freeze();
            textures[i] = bitmap;
        }
        return textures;
    }

    private void CreateParticle(int index)
    {
        var random = new Random(2437 + index * 71);
        var radius = 2.2 + random.NextDouble() * 2.6;
        var brush = new SolidColorBrush(WpfColor.FromArgb(105, 64, 144, 215));
        var element = new WpfEllipse
        {
            Width = radius,
            Height = radius,
            Fill = brush,
            IsHitTestVisible = false,
            RenderTransform = new TranslateTransform(),
            Opacity = 0.35 + random.NextDouble() * 0.4
        };
        _interactionLayer.Children.Add(element);
        _particles.Add(new ParticleState(
            element,
            random.NextDouble(),
            random.NextDouble(),
            random.NextDouble() * Math.PI * 2,
            0.08 + random.NextDouble() * 0.09));
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_disposed || !_enabled || _paused)
        {
            return;
        }

        var renderingTime = e is RenderingEventArgs args
            ? args.RenderingTime
            : (_lastRenderingTime ?? TimeSpan.Zero) + TimeSpan.FromSeconds(1d / 60d);
        var seconds = _lastRenderingTime is { } previous
            ? (renderingTime - previous).TotalSeconds
            : 1d / 60d;
        _lastRenderingTime = renderingTime;
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return;
        }

        AdvanceFrame(seconds);
    }

    private void AdvanceFrame(double seconds)
    {
        // Keep the expensive ambient blob layout near 30 Hz while transient
        // pointer/ripple/trail state follows the compositor's frame rate.
        _ambientAccumulator += Math.Min(seconds, 0.25);
        var updateAmbient = _settings.AmbientMotion && _ambientAccumulator >= AmbientFrameSeconds;
        if (updateAmbient)
        {
            _ambientAccumulator %= AmbientFrameSeconds;
        }

        var pointerChanged = UpdatePointerPosition(seconds);
        AdvanceAnimation(seconds, updateAmbient, pointerChanged);
        UpdateRenderingSubscription();
    }

    private void AdvanceAnimation(double seconds, bool updateAmbient, bool pointerChanged = false)
    {
        _animationTime += Math.Min(seconds, 0.25);
        var width = Math.Max(1, _backgroundLayer.ActualWidth);
        var height = Math.Max(1, _backgroundLayer.ActualHeight);

        if (updateAmbient && _liquidHighlight is not null && _settings.AmbientMotion)
        {
            var sweep = Math.Sin(_animationTime * Math.PI / 8) * 0.65;
            _liquidHighlight.StartPoint = new WpfPoint(sweep - 0.2, 0);
            _liquidHighlight.EndPoint = new WpfPoint(sweep + 0.8, 1);
        }

        if (updateAmbient || (_settings.Parallax && pointerChanged))
        {
            for (var i = 0; i < _blobs.Count; i++)
            {
                var blob = _blobs[i];
                var phase = _settings.AmbientMotion ? _animationTime * blob.Speed : 0;
                var x = (0.12 + i * 0.24) * width + Math.Sin(phase + i) * width * blob.Amplitude;
                var y = (0.15 + (i % 2) * 0.57) * height + Math.Cos(phase * 0.86 + i * 1.7) * height * blob.Amplitude;
                if (_settings.Parallax && _hasPointer)
                {
                    x += (_pointerPosition.X / width - 0.5) * 16;
                    y += (_pointerPosition.Y / height - 0.5) * 12;
                }

                SetPosition(blob.Element, x - blob.Element.Width / 2, y - blob.Element.Height / 2);
            }
        }

        for (var i = 0; i < _particles.Count; i++)
        {
            var particle = _particles[i];
            var phase = _animationTime * particle.Speed + particle.Phase;
            var x = particle.X * width + Math.Sin(phase) * width * 0.035;
            var y = particle.Y * height + Math.Cos(phase * 0.82) * height * 0.06;
            SetPosition(particle.Element, x - particle.Element.Width / 2, y - particle.Element.Height / 2);
            particle.Element.Opacity = 0.2 + (Math.Sin(phase * 1.3) + 1) * 0.2;
        }

        for (var i = 0; i < _trailNodes.Count; i++)
        {
            var trail = _trailNodes[i];
            if (trail.Element.Visibility != Visibility.Visible)
            {
                continue;
            }

            trail.Element.Opacity = Math.Max(0, trail.Element.Opacity - seconds * 2.7);
            if (trail.Element.Opacity <= 0.01)
            {
                trail.Element.Visibility = Visibility.Hidden;
            }
        }

        for (var i = 0; i < _ripples.Count; i++)
        {
            var ripple = _ripples[i];
            if (ripple.Element.Visibility != Visibility.Visible)
            {
                continue;
            }

            ripple.Age += seconds;
            var progress = Math.Clamp(ripple.Age / RippleDuration, 0, 1);
            ripple.Scale.ScaleX = 1 + progress * 3.2;
            ripple.Scale.ScaleY = 1 + progress * 3.2;
            ripple.Element.Opacity = (1 - progress) * 0.75;
            if (progress >= 1)
            {
                ripple.Element.Visibility = Visibility.Hidden;
                ripple.Age = 0;
            }
        }
    }

    private void OnPreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_disposed || !_enabled || _highContrast)
        {
            return;
        }

        WpfPoint point;
        try
        {
            point = e.GetPosition(_interactionLayer);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        UpdatePointer(point);
    }

    internal void UpdatePointer(WpfPoint point)
    {
        if (_disposed || !_enabled || _highContrast) return;
        _lastPointer = point;
        _hasPointer = true;
        if (!_hasSmoothedPointer)
        {
            _pointerPosition = point;
            _hasSmoothedPointer = true;
            UpdatePointerPosition(0);
            AdvanceAnimation(0, updateAmbient: true);
        }
        if (_halo is not null) _halo.Visibility = Visibility.Visible;

        if (_settings.PointerTrail && _trailNodes.Count > 0)
        {
            var trail = _trailNodes[_trailCursor++ % _trailNodes.Count];
            SetPosition(trail.Element, point.X - trail.Element.Width / 2, point.Y - trail.Element.Height / 2);
            trail.Element.Visibility = Visibility.Visible;
            trail.Element.Opacity = 0.72;
        }

        UpdateRenderingSubscription();
    }

    private void OnPreviewMouseDown(object sender, WpfMouseButtonEventArgs e)
    {
        if (_disposed || !_enabled || !_settings.ClickRipples || _highContrast || _ripples.Count == 0)
        {
            return;
        }

        WpfPoint point;
        try
        {
            point = e.GetPosition(_interactionLayer);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        StartRipple(point);
    }

    private void StartRipple(WpfPoint point)
    {
        var ripple = _ripples[_rippleCursor++ % _ripples.Count];
        ripple.Age = 0;
        ripple.Scale.ScaleX = 1;
        ripple.Scale.ScaleY = 1;
        ripple.Element.Opacity = 0.75;
        ripple.Element.Visibility = Visibility.Visible;
        ripple.Translation.X = point.X - ripple.Element.Width / 2;
        ripple.Translation.Y = point.Y - ripple.Element.Height / 2;
        UpdateRenderingSubscription();
    }

    private static void SetPosition(FrameworkElement element, double x, double y)
    {
        var transform = (TranslateTransform)element.RenderTransform;
        transform.X = x;
        transform.Y = y;
    }

    private void BackgroundSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_enabled && !_disposed) AdvanceAnimation(0, updateAmbient: true);
    }

    private void WindowMouseLeave(object sender, WpfMouseEventArgs e)
    {
        _hasPointer = false;
        _hasSmoothedPointer = false;
        if (_halo is not null) _halo.Visibility = Visibility.Hidden;
        if (_enabled && !_disposed) AdvanceAnimation(0, updateAmbient: true);
        UpdateRenderingSubscription();
    }

    private void WindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdatePausedState();

    private void WindowStateChanged(object? sender, EventArgs e) => UpdatePausedState();

    private void WindowUnloaded(object sender, RoutedEventArgs e) => Pause();

    private bool UpdatePointerPosition(double seconds)
    {
        if (!_hasPointer)
        {
            return false;
        }

        var previous = _pointerPosition;
        if (seconds <= 0 || (_lastPointer - _pointerPosition).Length <= 0.05)
        {
            _pointerPosition = _lastPointer;
        }
        else
        {
            // Exponential smoothing is frame-rate independent and avoids a
            // visible jump when a fast pointer move arrives between frames.
            var amount = 1 - Math.Exp(-Math.Min(seconds, 0.25) * 14);
            _pointerPosition = new WpfPoint(
                _pointerPosition.X + (_lastPointer.X - _pointerPosition.X) * amount,
                _pointerPosition.Y + (_lastPointer.Y - _pointerPosition.Y) * amount);
            if ((_lastPointer - _pointerPosition).Length <= 0.05) _pointerPosition = _lastPointer;
        }

        if (_halo is not null)
        {
            SetPosition(_halo, _pointerPosition.X - _halo.Width / 2, _pointerPosition.Y - _halo.Height / 2);
        }
        return previous != _pointerPosition;
    }

    private void UpdateRenderingSubscription()
    {
        var needsRendering = _blobs.Count > 0 && _settings.AmbientMotion
            || _particles.Count > 0
            || _trailNodes.Any(node => node.Element.Visibility == Visibility.Visible)
            || _ripples.Any(ripple => ripple.Element.Visibility == Visibility.Visible)
            || (_hasPointer && (_settings.PointerHalo || _settings.Parallax)
                && (_lastPointer - _pointerPosition).Length > 0.05);

        if (_enabled && !_paused && needsRendering)
        {
            StartRendering();
        }
        else
        {
            StopRendering();
        }
    }

    private void StartRendering()
    {
        if (_renderingSubscribed)
        {
            return;
        }

        _lastRenderingTime = null;
        CompositionTarget.Rendering += OnRendering;
        _renderingSubscribed = true;
    }

    private void StopRendering()
    {
        if (!_renderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _renderingSubscribed = false;
        _lastRenderingTime = null;
    }

    private void UpdatePausedState()
    {
        if (_disposed || !_enabled)
        {
            return;
        }

        var shouldPause = !_window.IsVisible || _window.WindowState == WindowState.Minimized;
        if (shouldPause)
        {
            Pause();
        }
        else
        {
            Resume();
        }
    }

    private void StopVisualsAndRendering()
    {
        StopRendering();
        _backgroundLayer.Children.Clear();
        _interactionLayer.Children.Clear();
        _blobs.Clear();
        _particles.Clear();
        _trailNodes.Clear();
        _ripples.Clear();
        _halo = null;
        _hasPointer = false;
        _hasSmoothedPointer = false;
        _lastPointer = default;
        _pointerPosition = default;
        _ambientAccumulator = 0;
        _animationTime = 0;
        _paused = false;
    }

    private string BuildCapabilityNotice()
    {
        if (_highContrast)
        {
            return "Windows 高对比度模式：已启用不透明表面保护，跳过低对比度装饰。";
        }

        if (_qualityTier < 2)
        {
            return $"图形能力等级 {_qualityTier}：已简化背景模糊与粒子效果。";
        }

        return "视觉效果可用：背景动效与表面材质按当前设置运行。";
    }

    private static SolidColorBrush Brush(string hex, double opacity = 1)
    {
        var color = (WpfColor)WpfColorConverter.ConvertFromString(hex)!;
        var brush = new SolidColorBrush(color) { Opacity = Math.Clamp(opacity, 0, 1) };
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VisualEffectsController));
        }
    }

    private readonly record struct ResourceSnapshot(bool WasPresent, object? Value);

    private sealed class BlobState
    {
        public BlobState(FrameworkElement element, int index, double speed, double amplitude)
        {
            Element = element;
            Index = index;
            Speed = speed;
            Amplitude = amplitude;
        }

        public FrameworkElement Element { get; }
        public int Index { get; }
        public double Speed { get; }
        public double Amplitude { get; }
    }

    private sealed class ParticleState
    {
        public ParticleState(WpfEllipse element, double x, double y, double phase, double speed)
        {
            Element = element;
            X = x;
            Y = y;
            Phase = phase;
            Speed = speed;
        }

        public WpfEllipse Element { get; }
        public double X { get; }
        public double Y { get; }
        public double Phase { get; }
        public double Speed { get; }
    }

    private sealed class TrailState
    {
        public TrailState(WpfEllipse element) => Element = element;

        public WpfEllipse Element { get; }
    }

    private sealed class RippleState
    {
        public RippleState(WpfEllipse element, ScaleTransform scale, TranslateTransform translation)
        {
            Element = element;
            Scale = scale;
            Translation = translation;
        }

        public WpfEllipse Element { get; }
        public ScaleTransform Scale { get; }
        public TranslateTransform Translation { get; }
        public double Age { get; set; }
    }
}
