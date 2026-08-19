using System.Diagnostics;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

internal static class VersionOpenTargetService
{
    public static ProcessStartInfo CreateStartInfo(ManagerInstance instance, string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new ArgumentException("请先选择要绑定的程序、脚本或快捷方式。", nameof(configuredPath));
        }

        var selectedPath = Path.GetFullPath(configuredPath.Trim());
        if (!File.Exists(selectedPath))
        {
            throw new FileNotFoundException("绑定的打开方式不存在，请在版本设置中重新选择。", selectedPath);
        }

        var targetPath = selectedPath;
        string? arguments = null;
        string? shortcutWorkingDirectory = null;
        if (string.Equals(Path.GetExtension(selectedPath), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var shortcut = ShortcutTargetResolver.ResolveLaunchTarget(selectedPath);
            targetPath = shortcut.TargetPath;
            arguments = shortcut.Arguments;
            shortcutWorkingDirectory = shortcut.WorkingDirectory;
        }

        var extension = Path.GetExtension(targetPath);
        ProcessStartInfo startInfo;
        if (string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = CreateCommandScriptStartInfo(targetPath, arguments);
        }
        else if (string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = CreatePowerShellScriptStartInfo(targetPath, arguments);
        }
        else if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo
            {
                FileName = targetPath,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = false
            };
        }
        else
        {
            startInfo = CreateAssociatedFileStartInfo(selectedPath);
        }

        startInfo.WorkingDirectory = ResolveWorkingDirectory(
            shortcutWorkingDirectory,
            targetPath,
            instance.RootPath);
        startInfo.Environment["DSH_HOME"] = instance.DshHome;
        startInfo.Environment["DSH_AGENTS_HOME"] = Path.Combine(instance.DshHome, ".agents");
        startInfo.Environment["PATH"] = RuntimeSearchPaths.BuildCurrentPath(targetPath);
        return startInfo;
    }

    private static ProcessStartInfo CreateCommandScriptStartInfo(string scriptPath, string? arguments)
    {
        var payload = $"call \"{scriptPath}\"";
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            payload += $" {arguments}";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCommandProcessor(),
            Arguments = $"/d /c {payload}",
            UseShellExecute = false,
            CreateNoWindow = false
        };
        return startInfo;
    }

    private static ProcessStartInfo CreateAssociatedFileStartInfo(string selectedPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCommandProcessor(),
            Arguments = $"/d /c start \"\" \"{selectedPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return startInfo;
    }

    private static string ResolveCommandProcessor()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        return !string.IsNullOrWhiteSpace(commandProcessor) && File.Exists(commandProcessor)
            ? commandProcessor
            : Path.Combine(Environment.SystemDirectory, "cmd.exe");
    }

    private static ProcessStartInfo CreatePowerShellScriptStartInfo(string scriptPath, string? arguments)
    {
        var powerShell = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            startInfo.ArgumentList.Add(arguments);
        }

        return startInfo;
    }

    private static string ResolveWorkingDirectory(
        string? shortcutWorkingDirectory,
        string targetPath,
        string fallback)
    {
        if (!string.IsNullOrWhiteSpace(shortcutWorkingDirectory)
            && Directory.Exists(shortcutWorkingDirectory))
        {
            return shortcutWorkingDirectory;
        }

        var targetDirectory = Directory.Exists(targetPath)
            ? targetPath
            : Path.GetDirectoryName(targetPath);
        return !string.IsNullOrWhiteSpace(targetDirectory) && Directory.Exists(targetDirectory)
            ? targetDirectory
            : fallback;
    }
}
