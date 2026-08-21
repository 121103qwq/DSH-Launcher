using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DshLauncher;

public partial class MainWindow
{
    private bool _liveUiRecoveryApplied;
    private bool _liveUiRecoveryClosed;
    private DependencyPropertyDescriptor? _embeddedContentDescriptor;
    private NotifyCollectionChangedEventHandler? _liveInstanceCollectionChanged;
    private Image? _liveTransitionOverlay;
    private RenderTargetBitmap? _lastEmbeddedSnapshot;
    private long _liveTransitionGeneration;
    private long _liveSnapshotGeneration;

    /// <summary>
    /// Initialize the binding source before optional visual material setup runs in
    /// the constructor. This protects visibility bindings from a partial constructor
    /// failure and is intentionally idempotent with the later DataContext assignment.
    /// </summary>
    protected override void OnInitialized(EventArgs e)
    {
        DataContext ??= this;
        base.OnInitialized(e);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ApplyLiveUiRecovery();
    }

    private void ApplyLiveUiRecovery()
    {
        if (_liveUiRecoveryApplied || _liveUiRecoveryClosed)
        {
            return;
        }

        _liveUiRecoveryApplied = true;
        DataContext ??= this;

        ApplyLegacyWorkspaceTemplate();
        EnsureLiveTransitionOverlay();

        _embeddedContentDescriptor = DependencyPropertyDescriptor.FromProperty(
            ContentControl.ContentProperty,
            typeof(ContentControl));
        _embeddedContentDescriptor?.AddValueChanged(
            EmbeddedPageHost,
            EmbeddedPageHost_ContentChanged);

        _liveInstanceCollectionChanged = (_, _) =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(
                RefreshLiveBindingState,
                DispatcherPriority.DataBind);
        };
        Instances.CollectionChanged += _liveInstanceCollectionChanged;

        NavigationBar.PreviewMouseDown += MainWindow_PreviewInputForSnapshot;
        VersionSettingsBackButton.PreviewMouseDown += MainWindow_PreviewInputForSnapshot;
        ContextInstanceSelector.PreviewMouseDown += MainWindow_PreviewInputForSnapshot;
        Closed += MainWindow_LiveUiRecoveryClosed;

        RefreshLiveBindingState();
        ScheduleEmbeddedSnapshotCapture();
    }

    private void ApplyLegacyWorkspaceTemplate()
    {
        var template = new ControlTemplate(typeof(ContentControl));
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(
            Border.BackgroundProperty,
            TryFindResource("PageBrush") as Brush ?? Brushes.White);
        chrome.SetValue(
            Border.BorderBrushProperty,
            TryFindResource("LineBrush") as Brush ?? Brushes.LightGray);
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        chrome.SetValue(Border.ClipToBoundsProperty, true);
        chrome.SetValue(Border.SnapsToDevicePixelsProperty, true);
        chrome.SetValue(
            TextElement.ForegroundProperty,
            TryFindResource("TextBrush") as Brush ?? Brushes.Black);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(
            ContentPresenter.ContentProperty,
            new TemplateBindingExtension(ContentControl.ContentProperty));
        presenter.SetValue(
            ContentPresenter.ContentTemplateProperty,
            new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        presenter.SetValue(
            ContentPresenter.ContentTemplateSelectorProperty,
            new TemplateBindingExtension(ContentControl.ContentTemplateSelectorProperty));
        presenter.SetValue(
            ContentPresenter.ContentStringFormatProperty,
            new TemplateBindingExtension(ContentControl.ContentStringFormatProperty));
        presenter.SetValue(
            FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Stretch);
        presenter.SetValue(
            FrameworkElement.VerticalAlignmentProperty,
            VerticalAlignment.Stretch);
        presenter.SetValue(
            TextElement.ForegroundProperty,
            TryFindResource("TextBrush") as Brush ?? Brushes.Black);
        chrome.AppendChild(presenter);
        template.VisualTree = chrome;

        EmbeddedPageHost.Template = template;
        EmbeddedPageHost.Foreground = TryFindResource("TextBrush") as Brush ?? Brushes.Black;
        EmbeddedPageHost.ApplyTemplate();
    }

    private void EnsureLiveTransitionOverlay()
    {
        if (_liveTransitionOverlay is not null)
        {
            return;
        }

        _liveTransitionOverlay = new Image
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
            Opacity = 1,
            RenderTransform = new TranslateTransform()
        };
        Panel.SetZIndex(_liveTransitionOverlay, 100);
        PageTransitionHost.Children.Add(_liveTransitionOverlay);
    }

    private void RefreshLiveBindingState()
    {
        if (_liveUiRecoveryClosed)
        {
            return;
        }

        // Re-publish the mutually exclusive dashboard states. DataContext is set
        // in OnInitialized, so normal WPF binding invalidation can now do the rest.
        OnPropertyChanged(nameof(NoInstancesVisibility));
        OnPropertyChanged(nameof(InstancesVisibility));
        OnPropertyChanged(nameof(InstanceCountText));
        OnPropertyChanged(nameof(SelectedInstance));
        OnPropertyChanged(nameof(SelectedInstanceName));
        OnPropertyChanged(nameof(SelectedInstanceSummary));
        OnPropertyChanged(nameof(SelectedInstanceStatus));
        OnPropertyChanged(nameof(SelectedInstanceStatusBrush));
        OnPropertyChanged(nameof(InstanceEndpointText));
        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(CanStopInstance));
        OnPropertyChanged(nameof(CanRestartInstance));
        OnPropertyChanged(nameof(LauncherStartVisibility));
        OnPropertyChanged(nameof(DesktopShellVisibility));
        OnPropertyChanged(nameof(PageNoticeVisibility));
        OnPropertyChanged(nameof(PageNoticeDetailVisibility));
    }

    private void MainWindow_PreviewInputForSnapshot(object sender, MouseButtonEventArgs e)
    {
        if (EmbeddedPageHost.Visibility == Visibility.Visible
            && EmbeddedPageHost.Content is not null)
        {
            CaptureCurrentEmbeddedSnapshot();
        }
    }

    private void EmbeddedPageHost_ContentChanged(object? sender, EventArgs e)
    {
        if (_liveUiRecoveryClosed)
        {
            return;
        }

        _liveSnapshotGeneration = unchecked(_liveSnapshotGeneration + 1);
        if (EmbeddedPageHost.Content is null)
        {
            var snapshot = _lastEmbeddedSnapshot;
            _lastEmbeddedSnapshot = null;
            if (snapshot is not null)
            {
                ScheduleOutgoingSnapshot(snapshot);
            }

            return;
        }

        ScheduleEmbeddedSnapshotCapture();
    }

    private void ScheduleEmbeddedSnapshotCapture()
    {
        if (_liveUiRecoveryClosed || EmbeddedPageHost.Content is null)
        {
            return;
        }

        var contentIdentity = EmbeddedPageHost.Content;
        var generation = unchecked(_liveSnapshotGeneration + 1);
        _liveSnapshotGeneration = generation;
        _ = CaptureEmbeddedSnapshotAfterTransitionAsync(contentIdentity, generation);
    }

    private async Task CaptureEmbeddedSnapshotAfterTransitionAsync(
        object contentIdentity,
        long generation)
    {
        try
        {
            var delay = _motionDecision.ReducedMotion || _motionDecision.IsImmediate
                ? TimeSpan.FromMilliseconds(40)
                : TimeSpan.FromMilliseconds(300);
            await Task.Delay(delay, _windowCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_liveUiRecoveryClosed
            || generation != _liveSnapshotGeneration
            || !ReferenceEquals(contentIdentity, EmbeddedPageHost.Content)
            || EmbeddedPageHost.Visibility != Visibility.Visible)
        {
            return;
        }

        await Dispatcher.InvokeAsync(
            CaptureCurrentEmbeddedSnapshot,
            DispatcherPriority.Render);
    }

    private void CaptureCurrentEmbeddedSnapshot()
    {
        if (_liveUiRecoveryClosed
            || EmbeddedPageHost.Visibility != Visibility.Visible
            || EmbeddedPageHost.Content is null
            || EmbeddedPageHost.ActualWidth < 2
            || EmbeddedPageHost.ActualHeight < 2)
        {
            return;
        }

        try
        {
            EmbeddedPageHost.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(EmbeddedPageHost);
            var pixelWidth = Math.Max(
                1,
                (int)Math.Ceiling(EmbeddedPageHost.ActualWidth * dpi.DpiScaleX));
            var pixelHeight = Math.Max(
                1,
                (int)Math.Ceiling(EmbeddedPageHost.ActualHeight * dpi.DpiScaleY));

            // Avoid pathological allocations on malformed layout values while
            // retaining full fidelity for normal and high-DPI desktop windows.
            if (pixelWidth > 8192
                || pixelHeight > 8192
                || (long)pixelWidth * pixelHeight > 24_000_000)
            {
                return;
            }

            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);
            bitmap.Render(EmbeddedPageHost);
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            _lastEmbeddedSnapshot = bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or OutOfMemoryException)
        {
            _lastEmbeddedSnapshot = null;
            System.Diagnostics.Debug.WriteLine(
                $"[DeepSeaGlass] Embedded snapshot unavailable: {ex.Message}");
        }
    }

    private void ScheduleOutgoingSnapshot(RenderTargetBitmap snapshot)
    {
        var generation = unchecked(_liveTransitionGeneration + 1);
        _liveTransitionGeneration = generation;
        _ = Dispatcher.BeginInvoke(
            () => ShowOutgoingSnapshot(snapshot, generation),
            DispatcherPriority.Loaded);
    }

    private void ShowOutgoingSnapshot(RenderTargetBitmap snapshot, long generation)
    {
        if (_liveUiRecoveryClosed
            || generation != _liveTransitionGeneration
            || _liveTransitionOverlay is null)
        {
            return;
        }

        StopLiveTransitionOverlay(clearSource: false);
        _liveTransitionOverlay.Source = snapshot;
        _liveTransitionOverlay.Visibility = Visibility.Visible;
        _liveTransitionOverlay.Opacity = 1;
        var transform = (TranslateTransform)_liveTransitionOverlay.RenderTransform;
        transform.Y = 0;

        if (_motionDecision.ReducedMotion || _motionDecision.IsImmediate)
        {
            StopLiveTransitionOverlay(clearSource: true);
            return;
        }

        var duration = TimeSpan.FromMilliseconds(220);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var opacityAnimation = new DoubleAnimation(1, 0, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var translateAnimation = new DoubleAnimation(0, -8, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        opacityAnimation.Completed += (_, _) =>
        {
            if (!_liveUiRecoveryClosed && generation == _liveTransitionGeneration)
            {
                StopLiveTransitionOverlay(clearSource: true);
            }
        };

        _liveTransitionOverlay.BeginAnimation(
            OpacityProperty,
            opacityAnimation,
            HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(
            TranslateTransform.YProperty,
            translateAnimation,
            HandoffBehavior.SnapshotAndReplace);
        _liveTransitionOverlay.Opacity = 0;
        transform.Y = -8;
    }

    private void StopLiveTransitionOverlay(bool clearSource)
    {
        if (_liveTransitionOverlay is null)
        {
            return;
        }

        _liveTransitionOverlay.BeginAnimation(OpacityProperty, null);
        _liveTransitionOverlay.Opacity = 1;
        if (_liveTransitionOverlay.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0;
        }

        _liveTransitionOverlay.Visibility = Visibility.Collapsed;
        if (clearSource)
        {
            _liveTransitionOverlay.Source = null;
        }
    }

    private void MainWindow_LiveUiRecoveryClosed(object? sender, EventArgs e)
    {
        if (_liveUiRecoveryClosed)
        {
            return;
        }

        _liveUiRecoveryClosed = true;
        _liveTransitionGeneration = unchecked(_liveTransitionGeneration + 1);
        _liveSnapshotGeneration = unchecked(_liveSnapshotGeneration + 1);
        StopLiveTransitionOverlay(clearSource: true);
        _lastEmbeddedSnapshot = null;

        if (_embeddedContentDescriptor is not null)
        {
            _embeddedContentDescriptor.RemoveValueChanged(
                EmbeddedPageHost,
                EmbeddedPageHost_ContentChanged);
            _embeddedContentDescriptor = null;
        }

        if (_liveInstanceCollectionChanged is not null)
        {
            Instances.CollectionChanged -= _liveInstanceCollectionChanged;
            _liveInstanceCollectionChanged = null;
        }

        NavigationBar.PreviewMouseDown -= MainWindow_PreviewInputForSnapshot;
        VersionSettingsBackButton.PreviewMouseDown -= MainWindow_PreviewInputForSnapshot;
        ContextInstanceSelector.PreviewMouseDown -= MainWindow_PreviewInputForSnapshot;
        Closed -= MainWindow_LiveUiRecoveryClosed;
    }
}
