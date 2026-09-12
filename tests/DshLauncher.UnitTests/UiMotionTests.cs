using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;
using Xunit.Abstractions;

namespace DshLauncher.UnitTests;

[Collection("WpfRendering")]
public sealed class UiMotionTests
{
    private readonly ITestOutputHelper _output;

    public UiMotionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TransitionProducesIntermediateOpacityInBothDirections()
    {
        RunOnSta(() =>
        {
            var element = new Border();
            var replacements = 0;
            var samples = new List<(bool Exiting, double Opacity)>();
            var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            timer.Tick += (_, _) => samples.Add((UiMotion.HasPendingReplacement(element), element.Opacity));
            try
            {
                UiMotion.Transition(element, () => replacements++);
                timer.Start();
                PumpDispatcher(TimeSpan.FromMilliseconds(500));

                var fadingOut = samples.Where(sample => sample.Exiting && sample.Opacity is > 0.05 and < 0.95).ToArray();
                var fadingIn = samples.Where(sample => !sample.Exiting && sample.Opacity is > 0.05 and < 0.95).ToArray();
                Assert.NotEmpty(fadingOut);
                Assert.NotEmpty(fadingIn);
                Assert.Equal(1, replacements);
                Assert.Equal(1, element.Opacity);
                _output.WriteLine($"Windows ClientAreaAnimation={SystemParameters.ClientAreaAnimation}; "
                    + $"fade-out=[{string.Join(", ", fadingOut.Select(sample => sample.Opacity.ToString("F2")))}]; "
                    + $"fade-in=[{string.Join(", ", fadingIn.Select(sample => sample.Opacity.ToString("F2")))}]");
            }
            finally
            {
                timer.Stop();
                UiMotion.Reset(element);
            }
        });
    }

    [Fact]
    public void ResetWithoutTransitionDoesNotChangeElement()
    {
        RunOnSta(() =>
        {
            var element = new Border
            {
                Opacity = 0.7,
                RenderTransform = new RotateTransform(4)
            };
            var original = element.RenderTransform;

            UiMotion.Reset(element);

            Assert.Same(original, element.RenderTransform);
            Assert.Equal(0.7, element.Opacity);
        });
    }

    [Fact]
    public void EnterPreservesAndResetRestoresExistingTransform()
    {
        RunOnSta(() =>
        {
            var element = new Border
            {
                Opacity = 0.7,
                RenderTransform = new RotateTransform(4)
            };
            var original = element.RenderTransform;

            UiMotion.Enter(element, 8);
            var group = Assert.IsType<TransformGroup>(element.RenderTransform);
            Assert.Same(original, group.Children[0]);
            Assert.IsType<TranslateTransform>(group.Children[1]);

            UiMotion.Reset(element);

            Assert.Same(original, element.RenderTransform);
            Assert.Equal(0.7, element.Opacity);
        });
    }

    [Fact]
    public void EnterCanReplaceAnActiveTransition()
    {
        RunOnSta(() =>
        {
            var original = new ScaleTransform(1.1, 1.1);
            var element = new Border { RenderTransform = original };

            UiMotion.Enter(element, 8);
            var first = element.RenderTransform;
            UiMotion.Enter(element, 4);
            var second = element.RenderTransform;

            Assert.NotSame(first, second);

            UiMotion.Reset(element);
            Assert.Same(original, element.RenderTransform);
        });
    }

    [Fact]
    public void EnterNaturallyCompletesAndRestoresNormalState()
    {
        RunOnSta(() =>
        {
            var element = new Border { Opacity = 0.65 };
            var original = element.RenderTransform;

            UiMotion.Enter(element, 8);
            PumpDispatcher(TimeSpan.FromMilliseconds(320));

            Assert.Equal(0.65, element.Opacity);
            Assert.Same(original, element.RenderTransform);
        });
    }

    [Fact]
    public void UnloadedEventStopsTransitionAndRestoresState()
    {
        RunOnSta(() =>
        {
            var element = new Border { Opacity = 0.65 };
            var original = element.RenderTransform;

            UiMotion.Enter(element, 8);
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, element));

            Assert.Equal(0.65, element.Opacity);
            Assert.Same(original, element.RenderTransform);
        });
    }

    [Fact]
    public void ResetDoesNotOverwriteAnExternalTransformReplacement()
    {
        RunOnSta(() =>
        {
            var element = new Border();
            UiMotion.Enter(element, 8);
            var replacement = new RotateTransform(12);
            element.RenderTransform = replacement;

            UiMotion.Reset(element);

            Assert.Same(replacement, element.RenderTransform);
            Assert.Equal(1, element.Opacity);
        });
    }

    [Fact]
    public void TransitionDoesNotReplaceContentBeforeFadeOutCompletes()
    {
        RunOnSta(() =>
        {
            var element = new Border { Opacity = 0.72 };
            var replacements = 0;

            UiMotion.Transition(element, () => replacements++);
            Assert.Equal(0, replacements);

            PumpDispatcher(TimeSpan.FromMilliseconds(160));
            Assert.Equal(1, replacements);
            PumpDispatcher(TimeSpan.FromMilliseconds(340));
            Assert.Equal(0.72, element.Opacity, precision: 2);
            UiMotion.Reset(element);
        });
    }

    [Fact]
    public void TransitionDuringFadeOutExecutesOnlyTheLatestReplacement()
    {
        RunOnSta(() =>
        {
            var element = new Border();
            var replacements = new List<string>();

            UiMotion.Transition(element, () => replacements.Add("first"));
            UiMotion.Transition(element, () => replacements.Add("second"));
            PumpDispatcher(TimeSpan.FromMilliseconds(160));

            Assert.Equal(new[] { "second" }, replacements);
            UiMotion.Reset(element);
        });
    }

    [Fact]
    public void TransitionDuringFadeInContinuesFromCurrentOpacity()
    {
        RunOnSta(() =>
        {
            var element = new Border { Opacity = 0.8 };
            var replacements = 0;

            UiMotion.Enter(element);
            PumpUntil(() => element.Opacity is > 0.05 and < 0.7);
            var opacityBeforeTransition = element.Opacity;
            Assert.InRange(opacityBeforeTransition, 0.05, 0.75);

            UiMotion.Transition(element, () => replacements++);

            Assert.Equal(0, replacements);
            Assert.InRange(
                Math.Abs(element.Opacity - opacityBeforeTransition),
                0,
                0.08);

            PumpDispatcher(TimeSpan.FromMilliseconds(160));
            Assert.Equal(1, replacements);
            UiMotion.Reset(element);
        });
    }

    [Fact]
    public void ResetCancelsPendingReplacementAndRestoresVisualState()
    {
        RunOnSta(() =>
        {
            var originalTransform = new RotateTransform(4);
            var element = new Border
            {
                Opacity = 0.65,
                IsHitTestVisible = true,
                RenderTransform = originalTransform
            };
            var replacements = 0;

            UiMotion.Transition(element, () => replacements++);
            UiMotion.Reset(element);

            Assert.Equal(0, replacements);
            AssertRestored(element, 0.65, originalTransform);

            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            Assert.Equal(0, replacements);
        });
    }

    [Fact]
    public void UnloadedCancelsPendingReplacementAndRestoresVisualState()
    {
        RunOnSta(() =>
        {
            var originalTransform = new ScaleTransform(1.1, 1.1);
            var element = new Border
            {
                Opacity = 0.65,
                IsHitTestVisible = true,
                RenderTransform = originalTransform
            };
            var replacements = 0;

            UiMotion.Transition(element, () => replacements++);
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, element));

            Assert.Equal(0, replacements);
            AssertRestored(element, 0.65, originalTransform);

            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            Assert.Equal(0, replacements);
        });
    }

    private static void AssertRestored(FrameworkElement element, double opacity, Transform originalTransform)
    {
        Assert.Equal(opacity, element.Opacity);
        Assert.Same(originalTransform, element.RenderTransform);
        Assert.True(element.IsHitTestVisible);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // A presentation target drives WPF's animation clock. It has no
                // WS_VISIBLE style and never opens a visible or active window.
                using var presentation = new HwndSource(new HwndSourceParameters("Launcher motion test")
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

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var observed = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        EventHandler sample = (_, _) =>
        {
            if (condition())
            {
                observed = true;
                frame.Continue = false;
            }
        };
        CompositionTarget.Rendering += sample;
        timeout.Tick += (_, _) => frame.Continue = false;
        timeout.Start();
        try { Dispatcher.PushFrame(frame); }
        finally
        {
            timeout.Stop();
            CompositionTarget.Rendering -= sample;
        }
        Assert.True(observed, "A real intermediate animation frame was not observed.");
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
