using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DshLauncher.Models;
using Xunit;
using WpfColor = System.Windows.Media.Color;

namespace DshLauncher.UnitTests;

[Collection("WpfRendering")]
public sealed class VisualEffectsControllerTests
{
    [Fact]
    public void RippleAloneStartsTheRealRenderingClockAndBecomesTransparent()
    {
        RunOnSta(() =>
        {
            var window = new Window();
            var background = new Canvas { Width = 900, Height = 600 };
            var interaction = new Canvas { Width = 900, Height = 600 };
            using var controller = new VisualEffectsController(window, background, interaction);
            controller.Apply(new VisualEffectsSettings
            {
                Enabled = true, AmbientMotion = false, Particles = false,
                PointerHalo = false, PointerTrail = false, Parallax = false, ClickRipples = true
            });
            controller.Resume();
            Assert.False(controller.IsRenderingSubscribed);
            controller.TriggerRippleForTest(new Point(200, 150));
            Assert.True(controller.IsRenderingSubscribed);
            var ripple = interaction.Children.OfType<FrameworkElement>().Single(element => element.Visibility == Visibility.Visible);
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            var sawIntermediate = false;
            EventHandler sample = (_, _) =>
            {
                if (ripple.Opacity is > 0 and < 0.75) sawIntermediate = true;
                if (ripple.Visibility == Visibility.Hidden) frame.Continue = false;
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
            Assert.True(sawIntermediate);
            Assert.Equal(Visibility.Hidden, ripple.Visibility);
            Assert.False(controller.IsRenderingSubscribed);
        });
    }

    [Fact]
    public void DisabledApplyRestoresWindowResourcesAndRemovesLayers()
    {
        RunOnSta(() =>
        {
            var window = new Window();
            var originalPage = new SolidColorBrush(WpfColor.FromRgb(1, 2, 3));
            window.Resources["PageBrush"] = originalPage;
            window.Resources["BlueBrush"] = new SolidColorBrush(WpfColor.FromRgb(4, 5, 6));
            var background = new Canvas();
            var interaction = new Canvas();
            using var controller = new VisualEffectsController(window, background, interaction);

            controller.Apply(new VisualEffectsSettings { Enabled = true });
            Assert.True(controller.IsEnabled);
            Assert.NotSame(originalPage, window.Resources["PageBrush"]);
            if (controller.HighContrastProtectionActive)
            {
                Assert.Empty(background.Children);
            }
            else
            {
                Assert.NotEmpty(background.Children);
            }
            var businessBlue = window.Resources["BlueBrush"];

            controller.Apply(new VisualEffectsSettings { Enabled = false });

            Assert.False(controller.IsEnabled);
            Assert.Empty(background.Children);
            Assert.Empty(interaction.Children);
            Assert.Same(originalPage, window.Resources["PageBrush"]);
            Assert.Same(businessBlue, window.Resources["BlueBrush"]);
        });
    }

    [Fact]
    public void EffectTogglesBuildIndependentPools()
    {
        RunOnSta(() =>
        {
            var window = new Window();
            var background = new Canvas();
            var interaction = new Canvas();
            using var controller = new VisualEffectsController(window, background, interaction);
            controller.Apply(new VisualEffectsSettings
            {
                Enabled = true,
                Particles = false,
                PointerHalo = false,
                PointerTrail = false,
                ClickRipples = false
            });

            Assert.Equal(controller.HighContrastProtectionActive ? 0 : 4, controller.BackgroundBlobCount);
            Assert.Equal(0, controller.ParticleCount);
            Assert.Equal(0, controller.TrailNodeCount);
            Assert.Equal(0, controller.RippleCount);
            Assert.DoesNotContain(interaction.Children.Cast<UIElement>(), child => child.IsHitTestVisible);
        });
    }

    [Fact]
    public void PoolsStayWithinConfiguredCapsAndRippleReusesNodes()
    {
        RunOnSta(() =>
        {
            var window = new Window();
            var background = new Canvas { Width = 900, Height = 600 };
            var interaction = new Canvas { Width = 900, Height = 600 };
            using var controller = new VisualEffectsController(window, background, interaction);
            controller.Apply(new VisualEffectsSettings
            {
                Enabled = true,
                PointerTrail = true,
                ClickRipples = true
            });

            Assert.InRange(controller.ParticleCount, 0, VisualEffectsController.MaximumParticles);
            Assert.InRange(controller.TrailNodeCount, 0, VisualEffectsController.MaximumTrailNodes);
            Assert.InRange(controller.RippleCount, 0, VisualEffectsController.MaximumRipples);
            for (var i = 0; i < 20; i++)
            {
                controller.TriggerRippleForTest(new System.Windows.Point(i * 7, i * 5));
            }

            Assert.Equal(VisualEffectsController.MaximumRipples, controller.RippleCount);
            Assert.InRange(interaction.Children.Count, 0, VisualEffectsController.MaximumParticles
                + VisualEffectsController.MaximumTrailNodes + VisualEffectsController.MaximumRipples + 1);
        });
    }

    [Fact]
    public void PausePreservesAnimationPhaseAndDisposeDetachesOwnedVisuals()
    {
        RunOnSta(() =>
        {
            var window = new Window();
            var background = new Canvas { Width = 900, Height = 600 };
            var interaction = new Canvas { Width = 900, Height = 600 };
            var controller = new VisualEffectsController(window, background, interaction);
            controller.Apply(new VisualEffectsSettings { Enabled = true });
            controller.Resume();
            controller.AdvanceForTest(0.3);
            var phase = controller.AnimationTime;
            controller.Pause();
            controller.AdvanceForTest(0.5);

            Assert.Equal(phase, controller.AnimationTime);
            Assert.True(controller.IsPaused);
            controller.Dispose();

            Assert.True(controller.IsDisposed);
            Assert.Empty(background.Children);
            Assert.Empty(interaction.Children);
            Assert.Throws<ObjectDisposedException>(() => controller.Apply(new VisualEffectsSettings { Enabled = true }));
        });
    }

    [Fact]
    public void ResumeAdvancesAnIntermediateBackgroundPositionWithoutChangingHitTesting()
    {
        RunOnSta(() =>
        {
            var window = new Window();
            var background = new Canvas { Width = 1000, Height = 700 };
            var interaction = new Canvas { Width = 1000, Height = 700 };
            using var controller = new VisualEffectsController(window, background, interaction);
            controller.Apply(new VisualEffectsSettings { Enabled = true });
            controller.Resume();
            if (controller.HighContrastProtectionActive)
            {
                Assert.Empty(background.Children);
                Assert.Empty(interaction.Children);
                return;
            }

            controller.AdvanceForTest(0.15);
            var first = background.Children[0].RenderTransform.Value.OffsetX;
            controller.AdvanceForTest(0.15);
            var second = background.Children[0].RenderTransform.Value.OffsetX;

            Assert.True(double.IsFinite(first));
            Assert.True(double.IsFinite(second));
            Assert.NotEqual(first, second);
            Assert.False(background.IsHitTestVisible);
            Assert.False(interaction.IsHitTestVisible);
            Assert.All(interaction.Children.OfType<UIElement>(), child => Assert.False(child.IsHitTestVisible));
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var presentation = new HwndSource(new HwndSourceParameters("Launcher visual effects test")
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
}
