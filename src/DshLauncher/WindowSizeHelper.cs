using System.Windows;

namespace DshLauncher;

internal static class WindowSizeHelper
{
    private const double WidthRatio = 0.92;
    private const double HeightRatio = 0.90;

    public static void FitInitialSize(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        var size = CalculateInitialSize(window.Width, window.Height, workArea.Width, workArea.Height);
        window.MinWidth = Math.Min(window.MinWidth, size.Width);
        window.MinHeight = Math.Min(window.MinHeight, size.Height);
        window.Width = size.Width;
        window.Height = size.Height;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = workArea.Left + Math.Max(0, (workArea.Width - size.Width) / 2);
        window.Top = workArea.Top + Math.Max(0, (workArea.Height - size.Height) / 2);
    }

    internal static System.Windows.Size CalculateInitialSize(
        double requestedWidth,
        double requestedHeight,
        double workingWidth,
        double workingHeight)
    {
        var availableWidth = Math.Max(320, workingWidth * WidthRatio);
        var availableHeight = Math.Max(240, workingHeight * HeightRatio);
        return new System.Windows.Size(
            Math.Min(requestedWidth, availableWidth),
            Math.Min(requestedHeight, availableHeight));
    }
}
