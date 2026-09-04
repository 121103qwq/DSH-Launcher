using System.IO;
using System.Text;
using DshLauncher.Models;
using Microsoft.Win32;

namespace DshLauncher.Services;

public sealed class LauncherIntegrationService
{
    private const string ProtocolKey = @"Software\Classes\dsh-launcher";

    public string ExecutablePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("无法确定 DSH Launcher 可执行文件路径。");

    public bool IsProtocolRegistered()
    {
        try
        {
            using var command = Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command");
            var value = command?.GetValue(null) as string;
            return value?.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public void RegisterProtocol()
    {
        using var root = Registry.CurrentUser.CreateSubKey(ProtocolKey, writable: true)
            ?? throw new InvalidOperationException("无法写入当前用户的 URL 协议注册表项。");
        root.SetValue(null, "URL:DSH Launcher Protocol");
        root.SetValue("URL Protocol", string.Empty);
        using (var icon = root.CreateSubKey("DefaultIcon", writable: true))
        {
            icon?.SetValue(null, $"\"{ExecutablePath}\",0");
        }

        using var command = root.CreateSubKey(@"shell\open\command", writable: true);
        command?.SetValue(null, $"\"{ExecutablePath}\" \"%1\"");
    }

    public bool UnregisterProtocol()
    {
        if (!IsProtocolRegistered())
        {
            return false;
        }

        Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false);
        return true;
    }

    public string CreateDesktopShortcut(ManagerInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(desktop);
        var safeName = string.Concat(instance.Name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "DSH 实例";
        }

        var path = Path.Combine(desktop, $"DSH Launcher - {safeName}.url");
        var uri = $"dsh-launcher://start?instanceId={Uri.EscapeDataString(instance.Id)}";
        var content = new StringBuilder()
            .AppendLine("[InternetShortcut]")
            .AppendLine($"URL={uri}")
            .AppendLine($"IconFile={ExecutablePath}")
            .AppendLine("IconIndex=0")
            .ToString();
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
