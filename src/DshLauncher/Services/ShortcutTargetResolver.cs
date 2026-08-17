using System.IO;
using System.Runtime.InteropServices;

namespace DshLauncher.Services;

public static class ShortcutTargetResolver
{
    public static string ResolveScanDirectory(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath)
            || !string.Equals(Path.GetExtension(shortcutPath), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("请选择 Windows .lnk 快捷方式。", nameof(shortcutPath));
        }

        var normalizedShortcut = Path.GetFullPath(shortcutPath);
        if (!File.Exists(normalizedShortcut))
        {
            throw new FileNotFoundException("所选快捷方式不存在。", normalizedShortcut);
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new NotSupportedException("当前 Windows 环境不能读取快捷方式。 ");
            shell = Activator.CreateInstance(shellType)
                ?? throw new NotSupportedException("当前 Windows 环境不能创建快捷方式解析器。 ");
            shortcut = ((dynamic)shell).CreateShortcut(normalizedShortcut);
            var targetPath = Convert.ToString(((dynamic)shortcut).TargetPath);
            var workingDirectory = Convert.ToString(((dynamic)shortcut).WorkingDirectory);
            return ResolveExistingDirectory(targetPath, workingDirectory)
                ?? throw new DirectoryNotFoundException("快捷方式的目标和工作目录都不存在，无法扫描。 ");
        }
        catch (Exception ex) when (ex is not ArgumentException
            and not FileNotFoundException
            and not DirectoryNotFoundException
            and not NotSupportedException)
        {
            throw new InvalidDataException("Windows 无法读取这个快捷方式。 ", ex);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    internal static string? ResolveExistingDirectory(string? targetPath, string? workingDirectory)
    {
        var target = NormalizeCandidate(targetPath);
        if (target is not null)
        {
            if (Directory.Exists(target))
            {
                return target;
            }

            if (File.Exists(target))
            {
                return Path.GetDirectoryName(target);
            }
        }

        var working = NormalizeCandidate(workingDirectory);
        return working is not null && Directory.Exists(working)
            ? working
            : null;
    }

    private static string? NormalizeCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
