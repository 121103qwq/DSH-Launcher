using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace DshLauncher.Services;

/// <summary>
/// 主窗口位置/大小/最大化状态持久化（借鉴 Ruler4396/dsh-launcher 的 WindowStateStore，MIT）。
/// 文件：&lt;Launcher 数据根&gt;\window-state.json。
/// - 关闭时写回最近一次 RestoreBounds（最大化时记录还原尺寸）；
/// - 启动时经虚拟桌面校验后恢复（越界 → 回退默认居中）；
/// - 原子写（.tmp + File.Move），损坏/失败只降级不抛异常。
/// </summary>
public sealed record LauncherWindowState(
    double X,
    double Y,
    double Width,
    double Height,
    bool IsMaximized)
{
    /// <summary>窗口至少要有这么多可见区域，否则视为"跑到屏幕外"。</summary>
    public const double MinVisibleWidth = 160;
    public const double MinVisibleHeight = 120;

    /// <summary>与虚拟桌面（所有显示器）的交集是否足以让用户抓住窗口。</summary>
    public bool IsVisibleOnVirtualScreen(
        double virtualLeft,
        double virtualTop,
        double virtualWidth,
        double virtualHeight)
    {
        if (Width < 240 || Height < 160 || double.IsNaN(X) || double.IsNaN(Y))
        {
            return false;
        }

        var left = Math.Max(X, virtualLeft);
        var top = Math.Max(Y, virtualTop);
        var right = Math.Min(X + Width, virtualLeft + virtualWidth);
        var bottom = Math.Min(Y + Height, virtualTop + virtualHeight);
        return right - left >= MinVisibleWidth && bottom - top >= MinVisibleHeight;
    }

    public bool IsVisibleOnVirtualScreen() =>
        IsVisibleOnVirtualScreen(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
}

public sealed class WindowStateStore
{
    private readonly string _path;

    public WindowStateStore(LauncherPaths? paths = null)
    {
        _path = Path.Combine((paths ?? new LauncherPaths()).RootDirectory, "window-state.json");
    }

    public string FilePath => _path;

    public LauncherWindowState? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LauncherWindowState>(File.ReadAllText(_path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            // 损坏不静默：位置记忆失效要可诊断（对齐 Ruler4396 的质量治理做法）。
            LauncherLog.Warn("window-state.json 损坏或不可读，窗口位置记忆不可用。",
                ErrorCodes.E1001, new { path = _path, error = ex.Message });
            return null;
        }
    }

    public void Save(LauncherWindowState state)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state), new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LauncherLog.Warn("保存窗口位置失败（不影响退出）。", ErrorCodes.E1001,
                new { path = _path, error = ex.Message });
        }
    }

    /// <summary>从窗口当前状态生成待持久化快照（最大化时用 RestoreBounds 记录还原尺寸）。</summary>
    public static LauncherWindowState Capture(Window window)
    {
        var maximized = window.WindowState == WindowState.Maximized;
        var bounds = maximized ? window.RestoreBounds : new Rect(
            window.Left,
            window.Top,
            window.ActualWidth > 0 ? window.ActualWidth : window.Width,
            window.ActualHeight > 0 ? window.ActualHeight : window.Height);
        return new LauncherWindowState(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized);
    }

    /// <summary>恢复窗口状态：越界/损坏时返回 false，调用方保持默认居中。</summary>
    public bool TryRestore(Window window)
    {
        var state = Load();
        if (state is null || !state.IsVisibleOnVirtualScreen())
        {
            if (state is not null)
            {
                LauncherLog.Info("窗口位置记录已越出可见桌面，回退默认居中。",
                    ErrorCodes.E1001, new { state.X, state.Y, state.Width, state.Height });
            }

            return false;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = state.X;
        window.Top = state.Y;
        window.Width = state.Width;
        window.Height = state.Height;
        if (state.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }

        return true;
    }
}
