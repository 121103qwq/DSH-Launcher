using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Xunit;

namespace DshLauncher.UnitTests;

[Collection("WpfRendering")]
public sealed class UiHoverTests
{
    [Fact]
    public void RapidReentryPreservesCurrentTranslationAndDoesNotNestTransforms()
    {
        RunOnSta(() =>
        {
            var original = new ScaleTransform(1.02, 1.02);
            var element = new Border { RenderTransform = original };
            UiHover.SetEnabled(element, true);
            RaiseMouse(element, Mouse.MouseEnterEvent);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            var group = Assert.IsType<TransformGroup>(element.RenderTransform);
            var translation = Assert.IsType<TranslateTransform>(group.Children[1]);
            var first = translation.Y;
            RaiseMouse(element, Mouse.MouseLeaveEvent);
            Assert.InRange(Math.Abs(translation.Y - first), 0, 0.03);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            var second = translation.Y;
            RaiseMouse(element, Mouse.MouseEnterEvent);
            Assert.InRange(Math.Abs(translation.Y - second), 0, 0.03);
            Assert.Same(group, element.RenderTransform);
            Assert.Equal(2, group.Children.Count);
            RaiseMouse(element, Mouse.MouseLeaveEvent);
            PumpDispatcher(TimeSpan.FromMilliseconds(260));
            Assert.Same(original, element.RenderTransform);
            Assert.Null(element.Effect);
        });
    }

    [Fact]
    public void MouseEnterLiftsElementAndMouseLeaveRestoresIt()
    {
        RunOnSta(() =>
        {
            var originalTransform = new RotateTransform(4);
            var element = new Border
            {
                Width = 80,
                Height = 24,
                Opacity = 0.83,
                RenderTransform = originalTransform
            };

            UiHover.SetEnabled(element, true);
            RaiseMouse(element, Mouse.MouseEnterEvent);

            var ownedTransform = Assert.IsType<TransformGroup>(element.RenderTransform);
            Assert.Same(originalTransform, ownedTransform.Children[0]);
            var translation = Assert.IsType<TranslateTransform>(ownedTransform.Children[1]);
            var shadow = Assert.IsType<DropShadowEffect>(element.Effect);

            PumpDispatcher(TimeSpan.FromMilliseconds(70));
            Assert.InRange(translation.Y, -1.5, -0.01);
            Assert.InRange(shadow.Opacity, 0.01, 0.24);
            Assert.Equal(0.83, element.Opacity);

            RaiseMouse(element, Mouse.MouseLeaveEvent);
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            Assert.InRange(translation.Y, -1.5, -0.01);

            PumpDispatcher(TimeSpan.FromMilliseconds(180));
            Assert.Same(originalTransform, element.RenderTransform);
            Assert.Null(element.Effect);
            Assert.Equal(0.83, element.Opacity);
        });
    }

    [Fact]
    public void FocusKeepsFeedbackActiveUntilFocusLeaves()
    {
        RunOnSta(() =>
        {
            var originalTransform = new RotateTransform(2);
            var element = new Border { RenderTransform = originalTransform };
            UiHover.SetEnabled(element, true);

            RaiseMouse(element, Mouse.MouseEnterEvent);
            RaiseRouted(element, UIElement.GotFocusEvent);
            RaiseMouse(element, Mouse.MouseLeaveEvent);
            PumpDispatcher(TimeSpan.FromMilliseconds(220));

            Assert.IsType<TransformGroup>(element.RenderTransform);
            Assert.NotNull(element.Effect);

            RaiseRouted(element, UIElement.LostFocusEvent);
            PumpDispatcher(TimeSpan.FromMilliseconds(240));

            Assert.Same(originalTransform, element.RenderTransform);
            Assert.Null(element.Effect);
        });
    }

    [Fact]
    public void DisabledAndUnloadedCancelFeedbackImmediately()
    {
        RunOnSta(() =>
        {
            var originalTransform = new ScaleTransform(1.05, 1.05);
            var element = new Border { RenderTransform = originalTransform };
            UiHover.SetEnabled(element, true);

            RaiseMouse(element, Mouse.MouseEnterEvent);
            element.IsEnabled = false;
            Assert.Same(originalTransform, element.RenderTransform);
            Assert.Null(element.Effect);

            element.IsEnabled = true;
            RaiseMouse(element, Mouse.MouseEnterEvent);
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, element));
            Assert.Same(originalTransform, element.RenderTransform);
            Assert.Null(element.Effect);

            // An unloaded element must not restart its behavior until Loaded.
            RaiseMouse(element, Mouse.MouseEnterEvent);
            Assert.Same(originalTransform, element.RenderTransform);
            Assert.Null(element.Effect);
        });
    }

    [Fact]
    public void ExternalTransformAndEffectAreNotOverwrittenDuringReset()
    {
        RunOnSta(() =>
        {
            var originalTransform = new RotateTransform(4);
            var originalEffect = new BlurEffect { Radius = 2 };
            var element = new Border
            {
                RenderTransform = originalTransform,
                Effect = originalEffect
            };
            UiHover.SetEnabled(element, true);
            RaiseMouse(element, Mouse.MouseEnterEvent);

            var replacementTransform = new SkewTransform(3, 1);
            var replacementEffect = new DropShadowEffect
            {
                BlurRadius = 4,
                ShadowDepth = 1,
                Opacity = 0.5
            };
            element.RenderTransform = replacementTransform;
            element.Effect = replacementEffect;

            UiHover.Reset(element);
            Assert.Same(replacementTransform, element.RenderTransform);
            Assert.Same(replacementEffect, element.Effect);
        });
    }

    [Fact]
    public void DisablingBehaviorRestoresItsOriginalVisualState()
    {
        RunOnSta(() =>
        {
            var originalTransform = new TranslateTransform(1, 2);
            var originalEffect = new BlurEffect { Radius = 3 };
            var element = new Border
            {
                RenderTransform = originalTransform,
                Effect = originalEffect
            };
            UiHover.SetEnabled(element, true);
            RaiseMouse(element, Mouse.MouseEnterEvent);
            Assert.IsType<TransformGroup>(element.RenderTransform);

            UiHover.SetEnabled(element, false);

            Assert.False(UiHover.GetEnabled(element));
            Assert.Same(originalTransform, element.RenderTransform);
            Assert.Same(originalEffect, element.Effect);
        });
    }

    private static void RaiseMouse(UIElement element, RoutedEvent routedEvent)
    {
        var args = new MouseEventArgs(Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = routedEvent,
            Source = element
        };
        element.RaiseEvent(args);
    }

    private static void RaiseRouted(UIElement element, RoutedEvent routedEvent) =>
        element.RaiseEvent(new RoutedEventArgs(routedEvent, element));

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var presentation = new HwndSource(new HwndSourceParameters("Launcher hover test")
                {
                    WindowStyle = 0,
                    ExtendedWindowStyle = 0x08000080,
                    PositionX = -32000,
                    PositionY = -32000,
                    Width = 1,
                    Height = 1
                });
                presentation.RootVisual = new Border();
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
