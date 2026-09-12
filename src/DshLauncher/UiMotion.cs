using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DshLauncher;

/// <summary>
/// Interruptible page motion. Content is replaced only after the old page fades out.
/// </summary>
public static class UiMotion
{
    private static readonly DependencyProperty MotionStateProperty =
        DependencyProperty.RegisterAttached(
            "MotionState",
            typeof(MotionState),
            typeof(UiMotion),
            new PropertyMetadata(null));

    private sealed class MotionState
    {
        public MotionState(
            Transform? originalTransform,
            Transform ownedTransform,
            TranslateTransform translation,
            bool hitTestVisible)
        {
            OriginalTransform = originalTransform;
            OwnedTransform = ownedTransform;
            Translation = translation;
            HitTestVisible = hitTestVisible;
        }

        public Transform? OriginalTransform { get; }

        public Transform OwnedTransform { get; }

        public TranslateTransform Translation { get; }

        public bool HitTestVisible { get; }

        public Action? Replacement { get; set; }

        public DoubleAnimation? OpacityAnimation { get; set; }

        public EventHandler? CompletedHandler { get; set; }

        public RoutedEventHandler? UnloadedHandler { get; set; }
    }

    /// <summary>
    /// Enters an element with a short fade and vertical slide.
    /// </summary>
    public static void Enter(FrameworkElement element, double offset = 12)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (double.IsNaN(offset) || double.IsInfinity(offset))
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        // Replacing a transition always leaves the previous one in a known state.
        Reset(element);
        var state = CreateState(element);
        AnimateStage(element, state, 0, offset);
    }

    /// <summary>
    /// Fades the old content out, applies the latest replacement, then fades in.
    /// A new request during fade-out replaces the callback, not the animation.
    /// </summary>
    public static void Transition(FrameworkElement element, Action replaceContent)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(replaceContent);
        // Launcher motion is an explicit product behavior, independent of the
        // Windows client-area animation setting (which may be disabled globally).
        var previous = GetMotionState(element);
        if (previous?.Replacement is not null)
        {
            previous.Replacement = replaceContent;
            return;
        }

        // Keep the currently displayed opacity/position when interrupting fade-in.
        var opacity = element.Opacity;
        var offset = previous?.Translation.Y ?? 0;
        ClearState(element, preserveOpacity: true);
        var state = CreateState(element);
        state.Replacement = replaceContent;
        element.SetCurrentValue(UIElement.IsHitTestVisibleProperty, false);
        AnimateStage(element, state, opacity, offset);
    }

    public static bool HasPendingReplacement(FrameworkElement element) =>
        GetMotionState(element)?.Replacement is not null;

    private static MotionState CreateState(FrameworkElement element)
    {
        var originalTransform = element.RenderTransform;
        var translation = new TranslateTransform();
        var transform = new TransformGroup();
        if (originalTransform is not null)
        {
            transform.Children.Add(originalTransform);
        }
        transform.Children.Add(translation);
        var state = new MotionState(originalTransform, transform, translation, element.IsHitTestVisible);
        element.SetCurrentValue(UIElement.RenderTransformProperty, transform);
        SetMotionState(element, state);
        RoutedEventHandler unloaded = (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, element))
            {
                Reset(element);
            }
        };
        state.UnloadedHandler = unloaded;
        element.Unloaded += unloaded;
        return state;
    }

    private static void AnimateStage(FrameworkElement element, MotionState state, double opacity, double offset)
    {
        if (state.OpacityAnimation is not null && state.CompletedHandler is not null)
        {
            state.OpacityAnimation.Completed -= state.CompletedHandler;
        }

        var exiting = state.Replacement is not null;
        var duration = new Duration(TimeSpan.FromMilliseconds(exiting ? 120 : 240));
        var easing = new QuadraticEase { EasingMode = exiting ? EasingMode.EaseIn : EasingMode.EaseOut };
        var opacityAnimation = new DoubleAnimation(opacity, exiting ? 0 : GetBaseOpacity(element), duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        var translationAnimation = new DoubleAnimation(offset, exiting ? -6 : 0, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        state.OpacityAnimation = opacityAnimation;
        state.CompletedHandler = (_, _) => Complete(element, state);
        opacityAnimation.Completed += state.CompletedHandler;

        state.Translation.Y = offset;
        element.BeginAnimation(UIElement.OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        state.Translation.BeginAnimation(TranslateTransform.YProperty, translationAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Stops an active transition and restores the element's normal visual state.
    /// </summary>
    public static void Reset(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        ClearState(element, preserveOpacity: false);
    }

    private static void ClearState(FrameworkElement element, bool preserveOpacity)
    {
        var state = GetMotionState(element);
        if (state is null)
        {
            return;
        }

        SetMotionState(element, null);
        state.Replacement = null;
        if (state.OpacityAnimation is not null && state.CompletedHandler is not null)
        {
            state.OpacityAnimation.Completed -= state.CompletedHandler;
        }
        if (state.UnloadedHandler is not null)
        {
            element.Unloaded -= state.UnloadedHandler;
        }

        if (!preserveOpacity)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
        }
        state.Translation.BeginAnimation(TranslateTransform.YProperty, null);
        state.Translation.Y = 0;
        element.SetCurrentValue(UIElement.IsHitTestVisibleProperty, state.HitTestVisible);

        // Only restore a transform that is still ours. A caller that replaced it
        // while the transition was running keeps its replacement.
        if (ReferenceEquals(element.RenderTransform, state.OwnedTransform))
        {
            element.SetCurrentValue(UIElement.RenderTransformProperty, state.OriginalTransform);
        }
    }

    private static double GetBaseOpacity(FrameworkElement element)
    {
        var value = element.GetAnimationBaseValue(UIElement.OpacityProperty);
        return value is double opacity && !double.IsNaN(opacity) && !double.IsInfinity(opacity)
            ? opacity
            : 1;
    }

    private static void Complete(FrameworkElement element, MotionState state)
    {
        if (!ReferenceEquals(GetMotionState(element), state))
        {
            return;
        }

        if (state.Replacement is { } replaceContent)
        {
            state.Replacement = null;
            try
            {
                replaceContent();
            }
            catch
            {
                Reset(element);
                throw;
            }

            if (ReferenceEquals(GetMotionState(element), state))
            {
                element.SetCurrentValue(UIElement.IsHitTestVisibleProperty, state.HitTestVisible);
                AnimateStage(element, state, 0, 8);
            }
        }
        else
        {
            Reset(element);
        }
    }

    private static MotionState? GetMotionState(FrameworkElement element) =>
        (MotionState?)element.GetValue(MotionStateProperty);

    private static void SetMotionState(FrameworkElement element, MotionState? state) =>
        element.SetValue(MotionStateProperty, state);
}
