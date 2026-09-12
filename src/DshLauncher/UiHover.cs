using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Color = System.Windows.Media.Color;

namespace DshLauncher;

/// <summary>
/// Small, bounded hover/focus feedback for interactive chrome.
///
/// The behavior owns only a translation appended to the element's existing
/// transform and a temporary shadow when the element has no effect. It never
/// changes layout, hit testing, or the element's opacity.
/// </summary>
public static class UiHover
{
    private const double LiftDistance = 1.5;
    private const double HoverShadowOpacity = 0.24;
    private const double HoverShadowDepth = 2;
    private const double HoverShadowBlurRadius = 10;
    private const double HoverAnimationMilliseconds = 140;
    private const double LeaveAnimationMilliseconds = 180;

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(HoverState),
            typeof(UiHover),
            new PropertyMetadata(null));

    /// <summary>
    /// Enables bounded feedback on an interactive element such as a button
    /// chrome border or a clickable list-item border.
    /// </summary>
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(UiHover),
            new FrameworkPropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(EnabledProperty);
    }

    public static void SetEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(EnabledProperty, value);
    }

    /// <summary>
    /// Cancels feedback immediately while leaving the attached Enabled value
    /// unchanged. This is useful for an owner that is being recycled.
    /// </summary>
    public static void Reset(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element is not FrameworkElement frameworkElement ||
            GetState(frameworkElement) is not { } state)
        {
            return;
        }

        state.MouseOver = false;
        state.Focused = false;
        state.Active = false;
        state.StopVisuals();
    }

    private static void OnEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            if (GetState(element) is null)
            {
                var state = new HoverState(element);
                SetState(element, state);
                state.Attach();
            }

            return;
        }

        if (GetState(element) is { } current)
        {
            current.Detach();
            SetState(element, null);
        }
    }

    private static HoverState? GetState(FrameworkElement element) =>
        (HoverState?)element.GetValue(StateProperty);

    private static void SetState(FrameworkElement element, HoverState? state) =>
        element.SetValue(StateProperty, state);

    private sealed class HoverState
    {
        private readonly FrameworkElement _element;
        private readonly FrameworkElement _interactionOwner;

        private int _animationGeneration;
        private Transform? _originalTransform;
        private TransformGroup? _ownedTransform;
        private TranslateTransform? _translation;
        private Effect? _originalEffect;
        private DropShadowEffect? _ownedEffect;
        private bool _visualsCaptured;
        private bool _transformDetached;
        private bool _effectDetached;
        private bool _attached;

        public HoverState(FrameworkElement element)
        {
            _element = element;
            // Track the stable control, not the translated chrome. This also
            // gives keyboard focus the same feedback as a pointer hover.
            _interactionOwner = element.TemplatedParent as System.Windows.Controls.Control ?? element;
        }

        public bool MouseOver { get; set; }

        public bool Focused { get; set; }

        public bool Active { get; set; }

        public bool IsSuspended { get; set; }

        public void Attach()
        {
            if (_attached)
            {
                return;
            }

            _interactionOwner.MouseEnter += OnMouseEnter;
            _interactionOwner.MouseLeave += OnMouseLeave;
            _interactionOwner.GotFocus += OnGotFocus;
            _interactionOwner.LostFocus += OnLostFocus;
            _interactionOwner.IsEnabledChanged += OnIsEnabledChanged;
            _element.Loaded += OnLoaded;
            _element.Unloaded += OnUnloaded;
            _attached = true;
            // Keep the behavior usable for template probes and hidden WPF
            // presentation hosts, whose test element is not in a live tree.
            IsSuspended = false;
        }

        public void Detach()
        {
            if (_attached)
            {
                _interactionOwner.MouseEnter -= OnMouseEnter;
                _interactionOwner.MouseLeave -= OnMouseLeave;
                _interactionOwner.GotFocus -= OnGotFocus;
                _interactionOwner.LostFocus -= OnLostFocus;
                _interactionOwner.IsEnabledChanged -= OnIsEnabledChanged;
                _element.Loaded -= OnLoaded;
                _element.Unloaded -= OnUnloaded;
                _attached = false;
            }

            MouseOver = false;
            Focused = false;
            Active = false;
            IsSuspended = false;
            StopVisuals();
        }

        public void StopVisuals()
        {
            _animationGeneration++;
            ClearTranslationAnimation();
            ClearEffectAnimation();

            if (_translation is not null)
            {
                _translation.Y = 0;
                if (!_transformDetached && ReferenceEquals(_element.RenderTransform, _ownedTransform))
                {
                    _element.SetCurrentValue(UIElement.RenderTransformProperty, _originalTransform);
                }
            }

            if (_ownedEffect is not null)
            {
                _ownedEffect.Opacity = 0;
                if (!_effectDetached && ReferenceEquals(_element.Effect, _ownedEffect))
                {
                    _element.SetCurrentValue(UIElement.EffectProperty, _originalEffect);
                }
            }

            _originalTransform = null;
            _ownedTransform = null;
            _translation = null;
            _originalEffect = null;
            _ownedEffect = null;
            _visualsCaptured = false;
            _transformDetached = false;
            _effectDetached = false;
        }

        private void OnMouseEnter(object? sender, MouseEventArgs args)
        {
            MouseOver = true;
            Refresh();
        }

        private void OnMouseLeave(object? sender, MouseEventArgs args)
        {
            MouseOver = false;
            Refresh();
        }

        private void OnGotFocus(object? sender, RoutedEventArgs args)
        {
            Focused = true;
            Refresh();
        }

        private void OnLostFocus(object? sender, RoutedEventArgs args)
        {
            Focused = false;
            Refresh();
        }

        private void OnIsEnabledChanged(object? sender, DependencyPropertyChangedEventArgs args)
        {
            if (!_element.IsEnabled)
            {
                MouseOver = false;
                Focused = false;
                Active = false;
                StopVisuals();
                return;
            }

            if (!IsSuspended)
            {
                // Re-read these properties because disabling a control can
                // clear focus without sending a matching routed event.
                MouseOver = _interactionOwner.IsMouseOver;
                Focused = _interactionOwner.IsKeyboardFocusWithin;
                Refresh();
            }
        }

        private void OnLoaded(object? sender, RoutedEventArgs args)
        {
            if (!ReferenceEquals(args.OriginalSource, _element))
            {
                return;
            }

            IsSuspended = false;
            MouseOver = _interactionOwner.IsMouseOver;
            Focused = _interactionOwner.IsKeyboardFocusWithin;
            Refresh();
        }

        private void OnUnloaded(object? sender, RoutedEventArgs args)
        {
            if (!ReferenceEquals(args.OriginalSource, _element))
            {
                return;
            }

            IsSuspended = true;
            MouseOver = false;
            Focused = false;
            Active = false;
            StopVisuals();
        }

        private void Refresh()
        {
            if (IsSuspended || !_element.IsEnabled)
            {
                if (Active)
                {
                    Active = false;
                    StopVisuals();
                }

                return;
            }

            var active = MouseOver || Focused;
            if (active == Active)
            {
                return;
            }

            Active = active;
            if (active)
            {
                AnimateVisuals(isActive: true);
            }
            else
            {
                AnimateVisuals(isActive: false);
            }
        }

        private void AnimateVisuals(bool isActive)
        {
            if (isActive)
            {
                CaptureVisuals();
            }

            var generation = ++_animationGeneration;
            var duration = TimeSpan.FromMilliseconds(
                isActive ? HoverAnimationMilliseconds : LeaveAnimationMilliseconds);
            var easing = new QuadraticEase
            {
                EasingMode = isActive ? EasingMode.EaseOut : EasingMode.EaseIn
            };
            DoubleAnimation? completionAnimation = null;
            void OnCompleted(object? sender, EventArgs args)
            {
                if (generation == _animationGeneration && !Active)
                {
                    StopVisuals();
                }
            }

            if (_translation is not null &&
                !_transformDetached &&
                ReferenceEquals(_element.RenderTransform, _ownedTransform))
            {
                var animation = new DoubleAnimation
                {
                    From = _translation.Y,
                    To = isActive ? -LiftDistance : 0,
                    Duration = new Duration(duration),
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                };
                completionAnimation = animation;
                animation.Completed += OnCompleted;
                _translation.BeginAnimation(
                    TranslateTransform.YProperty,
                    animation,
                    HandoffBehavior.SnapshotAndReplace);
            }

            if (_ownedEffect is not null &&
                !_effectDetached &&
                ReferenceEquals(_element.Effect, _ownedEffect))
            {
                var animation = new DoubleAnimation
                {
                    From = _ownedEffect.Opacity,
                    To = isActive ? HoverShadowOpacity : 0,
                    Duration = new Duration(duration),
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                };
                completionAnimation ??= animation;
                animation.Completed += OnCompleted;
                _ownedEffect.BeginAnimation(
                    DropShadowEffect.OpacityProperty,
                    animation,
                    HandoffBehavior.SnapshotAndReplace);
            }

            if (completionAnimation is null)
            {
                if (!isActive)
                {
                    StopVisuals();
                }

                return;
            }

        }

        private void CaptureVisuals()
        {
            if (!_visualsCaptured)
            {
                _originalTransform = _element.RenderTransform;
                _originalEffect = _element.Effect;
                _visualsCaptured = true;
            }

            if (!_transformDetached && _ownedTransform is null)
            {
                var group = new TransformGroup();
                if (_element.RenderTransform is { } existingTransform)
                {
                    group.Children.Add(existingTransform);
                }

                _translation = new TranslateTransform();
                group.Children.Add(_translation);
                _ownedTransform = group;
                _element.SetCurrentValue(UIElement.RenderTransformProperty, group);
            }
            else if (!_transformDetached &&
                     _ownedTransform is not null &&
                     !ReferenceEquals(_element.RenderTransform, _ownedTransform))
            {
                // UiMotion or a control template may legitimately replace the
                // transform. Keep that replacement and stop owning this layer.
                _transformDetached = true;
            }

            if (!_effectDetached && _ownedEffect is null && _element.Effect is null)
            {
                _ownedEffect = new DropShadowEffect
                {
                    Color = Color.FromArgb(92, 24, 57, 90),
                    Direction = 270,
                    ShadowDepth = HoverShadowDepth,
                    BlurRadius = HoverShadowBlurRadius,
                    Opacity = 0,
                    RenderingBias = RenderingBias.Performance
                };
                _element.SetCurrentValue(UIElement.EffectProperty, _ownedEffect);
            }
            else if (!_effectDetached &&
                     _ownedEffect is not null &&
                     !ReferenceEquals(_element.Effect, _ownedEffect))
            {
                // WPF has no general EffectGroup. An existing or later effect
                // is left untouched rather than replacing another owner.
                _effectDetached = true;
            }
        }

        private void ClearTranslationAnimation()
        {
            _translation?.BeginAnimation(TranslateTransform.YProperty, null);
        }

        private void ClearEffectAnimation()
        {
            _ownedEffect?.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        }
    }
}
