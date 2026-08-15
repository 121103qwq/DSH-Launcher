using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class VersionSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string GetSettingsPath(ManagerInstance instance) =>
        Path.Combine(instance.DshHome, ".dsh-launcher", "version-settings.json");

    public VersionSettingsData Read(ManagerInstance instance)
    {
        var path = GetSettingsPath(instance);
        if (!File.Exists(path))
        {
            return new VersionSettingsData();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<VersionSettingsData>(
                File.ReadAllText(path, Encoding.UTF8),
                JsonOptions) ?? new VersionSettingsData();
            Normalize(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidDataException($"版本设置文件格式无效：{path}", ex);
        }
    }

    public void Save(ManagerInstance instance, VersionSettingsData settings)
    {
        Normalize(settings);
        var path = GetSettingsPath(instance);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("版本设置没有父目录。 ");
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(settings, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public IReadOnlyList<string> GetWorkspaceNames(IEnumerable<ManagerInstance> instances)
    {
        var workspaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            var workspace = Read(instance).ConversationWorkspace?.Trim();
            if (!string.IsNullOrWhiteSpace(workspace))
            {
                workspaces.Add(workspace);
            }
        }

        return workspaces.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool ShouldSyncConfiguration(ManagerInstance left, ManagerInstance right)
    {
        var leftSettings = Read(left);
        var rightSettings = Read(right);
        return leftSettings.SyncAllConfiguration || rightSettings.SyncAllConfiguration;
    }

    public bool ShouldSyncConversations(ManagerInstance left, ManagerInstance right)
    {
        var leftSettings = Read(left);
        var rightSettings = Read(right);
        if (leftSettings.ConversationSyncMode == ConversationSyncMode.All
            || rightSettings.ConversationSyncMode == ConversationSyncMode.All)
        {
            return true;
        }

        if (leftSettings.ConversationSyncMode != ConversationSyncMode.Workspace
            || rightSettings.ConversationSyncMode != ConversationSyncMode.Workspace)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(leftSettings.ConversationWorkspace)
            && string.Equals(
                leftSettings.ConversationWorkspace,
                rightSettings.ConversationWorkspace,
                StringComparison.OrdinalIgnoreCase);
    }

    public bool ShouldSyncModelProviders(ManagerInstance left, ManagerInstance right)
    {
        if (ShouldSyncConfiguration(left, right))
        {
            return true;
        }

        var leftSettings = Read(left);
        var rightSettings = Read(right);
        return leftSettings.SyncModelProviders
            && rightSettings.SyncModelProviders;
    }

    private static void Normalize(VersionSettingsData settings)
    {
        if (!Enum.IsDefined(settings.ConversationSyncMode))
        {
            settings.ConversationSyncMode = ConversationSyncMode.Independent;
        }

        settings.ConversationWorkspace = string.IsNullOrWhiteSpace(settings.ConversationWorkspace)
            ? null
            : settings.ConversationWorkspace.Trim();
        settings.WindowTitle = string.IsNullOrWhiteSpace(settings.WindowTitle)
            ? null
            : settings.WindowTitle.Trim();
        settings.NodeExecutablePath = NormalizePath(settings.NodeExecutablePath);

        if (settings.ConversationWorkspace?.Length > 80)
        {
            throw new ArgumentException("工作区名称不能超过 80 个字符。", nameof(settings));
        }

        if (settings.WindowTitle?.Length > 120)
        {
            throw new ArgumentException("窗口标题不能超过 120 个字符。", nameof(settings));
        }

        if (settings.WindowTitle?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("窗口标题不能包含控制字符。", nameof(settings));
        }
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
