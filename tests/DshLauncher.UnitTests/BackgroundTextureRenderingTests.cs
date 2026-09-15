using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace DshLauncher.UnitTests;

[Collection("WpfRendering")]
public sealed class BackgroundTextureRenderingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void CachedBackgroundPreservesBlurAppearanceAcrossDpi(int dpi)
    {
        VisualEffectsControllerTests.RunOnSta(() =>
        {
            var original = CreateScene(cached: false);
            var cached = CreateScene(cached: true);
            var reference = Render(original, dpi);
            var actual = Render(cached, dpi);
            long difference = 0;
            var maximum = 0;
            for (var i = 0; i < reference.Length; i++)
            {
                var delta = Math.Abs(reference[i] - actual[i]);
                difference += delta;
                maximum = Math.Max(maximum, delta);
            }
            var mean = (double)difference / reference.Length;
            output.WriteLine($"DPI={dpi}: mean channel difference={mean:F4}/255, max={maximum}/255");
            Assert.InRange(mean, 0, 1.5);
            Assert.InRange(maximum, 0, 12);

            // Opt-in, same-process software-render comparison. This is not a
            // GPU FPS measurement and deliberately has no timing assertion.
            if (Environment.GetEnvironmentVariable("DSH_GRAPHICS_BENCHMARK") == "1")
            {
                const int frames = 24;
                for (var pass = 0; pass < 3; pass++)
                {
                    double Measure(Canvas scene)
                    {
                        var bitmap = CreateTarget(dpi);
                        var clock = Stopwatch.StartNew();
                        for (var frame = 0; frame < frames; frame++)
                        {
                            foreach (FrameworkElement child in scene.Children)
                                ((TranslateTransform)child.RenderTransform).X += 0.4;
                            bitmap.Clear();
                            bitmap.Render(scene);
                        }
                        return clock.Elapsed.TotalMilliseconds / frames;
                    }
                    // Alternate order to reduce warm-up/order bias.
                    var before = pass % 2 == 0 ? Measure(original) : 0;
                    var after = Measure(cached);
                    if (pass % 2 != 0) before = Measure(original);
                    output.WriteLine($"Software render DPI={dpi}, pass={pass}: live blur={before:F3} ms/frame, cached={after:F3} ms/frame");
                }
            }
        });
    }

    private static Canvas CreateScene(bool cached)
    {
        var canvas = new Canvas { Width = 900, Height = 600, Background = Brushes.White };
        for (var i = 0; i < 4; i++)
        {
            var blob = VisualEffectsController.CreateBackgroundBlob(i, cached ? 2 : 0);
            if (!cached) blob.Effect = new BlurEffect { Radius = 26, RenderingBias = RenderingBias.Performance };
            blob.Opacity = 0.84;
            blob.RenderTransform = new TranslateTransform(180 + i * 175 - blob.Width / 2,
                170 + i % 2 * 230 - blob.Height / 2);
            canvas.Children.Add(blob);
        }
        canvas.Measure(new Size(900, 600));
        canvas.Arrange(new Rect(0, 0, 900, 600));
        canvas.UpdateLayout();
        return canvas;
    }

    private static RenderTargetBitmap CreateTarget(int dpi) =>
        new(900 * dpi / 96, 600 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);

    private static byte[] Render(Canvas scene, int dpi)
    {
        var bitmap = CreateTarget(dpi);
        bitmap.Render(scene);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
}
