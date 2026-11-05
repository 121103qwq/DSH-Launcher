using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class OfficialDesktopService
{
    public DeepSeekDesktopInstallation? Detect() =>
        DeepSeekDesktopDetector.DetectInstallations().FirstOrDefault(item => item.IsOfficial);

    public bool IsRunning(DeepSeekDesktopInstallation installation)
    {
        if (!installation.IsOfficial || installation.LaunchSpec is null) return false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(installation.LaunchSpec.HostPath)))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.MainModule?.FileName is { } path
                        && ExternalDshHomeGuard.SameHome(path, installation.LaunchSpec.HostPath))
                        return true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                    or UnauthorizedAccessException or NotSupportedException)
                {
                    // A process may exit or deny access while being inspected.
                }
            }
        }
        return false;
    }

    public void Launch(DeepSeekDesktopInstallation installation, string? dshHome = null)
    {
        // Starting again lets Electron focus its existing window, including from the tray.
        using var process = Process.Start(CreateStartInfo(installation, dshHome))
            ?? throw new InvalidOperationException("无法启动官方 DeepSeek Harness 桌面端。");
    }

    internal static ProcessStartInfo CreateStartInfo(DeepSeekDesktopInstallation installation, string? dshHome = null)
    {
        if (!installation.IsOfficial || installation.LaunchSpec?.Mode != DshRuntimeLaunchMode.OfficialDesktopApp)
            throw new ArgumentException("所选安装不是官方 DeepSeek Harness 桌面端。", nameof(installation));
        var executable = installation.LaunchSpec.HostPath;
        if (!File.Exists(executable))
            throw new FileNotFoundException("官方 DeepSeek Harness 桌面端程序不存在，请重新检测安装。", executable);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            WorkingDirectory = installation.InstallRoot
        };
        // An inherited Run-as-Node flag would prevent the Electron app from opening.
        startInfo.Environment.Remove("ELECTRON_RUN_AS_NODE");
        startInfo.Environment["DSH_HOME"] = string.IsNullOrWhiteSpace(dshHome)
            ? ResolveDefaultHome()
            : Path.GetFullPath(dshHome);
        return startInfo;
    }

    public static string ResolveDefaultHome() => ResolveDefaultHome(
        Environment.GetEnvironmentVariable("DSH_HOME"),
        OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("DSH_HOME", EnvironmentVariableTarget.User) : null,
        Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string ResolveDefaultHome(string? processHome, string? userHome, string userProfile) =>
        Path.GetFullPath(!string.IsNullOrWhiteSpace(processHome) ? processHome
            : !string.IsNullOrWhiteSpace(userHome) ? userHome
            : Path.Combine(userProfile, ".dsh"));
}
