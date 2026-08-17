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

    private readonly LauncherPaths _paths;

    public VersionSettingsService(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
    }

    public string GetSettingsPath(ManagerInstance instance) =>
        Path.Combine(instance.DshHome, ".dsh-launcher", "version-settings.json");

    public string LauncherSettingsPath => Path.Combine(_paths.RootDirectory, "launcher-settings.json");

    public string DefaultDshInstallDirectory => _paths.ManagedDshRuntimeDirectory;

    public string ResolveDshInstallDirectory()
    {
        var configured = ReadLauncherSettings().DshInstallDirectory;
        return string.IsNullOrWhiteSpace(configured)
            ? DefaultDshInstallDirectory
            : configured;
    }

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
        WriteSettingsFile(GetSettingsPath(instance), JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static void WriteSettingsFile(string path, string json)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("设置文件没有父目录。");
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                json,
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

    public LauncherSettingsData ReadLauncherSettings()
    {
        if (!File.Exists(LauncherSettingsPath))
        {
            return new LauncherSettingsData();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<LauncherSettingsData>(
                File.ReadAllText(LauncherSettingsPath, Encoding.UTF8), JsonOptions);
            if (settings is null)
            {
                return new LauncherSettingsData();
            }

            NormalizeLauncherSettings(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            // Launcher 级设置损坏时按默认值工作，不阻断设置页。
            return new LauncherSettingsData();
        }
    }

    public void SaveLauncherSettings(LauncherSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        NormalizeLauncherSettings(settings);
        WriteSettingsFile(LauncherSettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>
    /// 添加一个 Launcher 级工作区名称：出现在所有版本“按工作区同步”的下拉中，
    /// 不依赖某个版本先使用它。
    /// </summary>
    public string AddLauncherWorkspace(string name)
    {
        var normalized = NormalizeWorkspaceName(name, nameof(name));

        var settings = ReadLauncherSettings();
        if (!settings.Workspaces.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            settings.Workspaces.Add(normalized);
            SaveLauncherSettings(settings);
        }

        return normalized;
    }

    /// <summary>
    /// 重命名 Launcher 工作区，并同步修改当前使用该工作区的版本设置。
    /// 只改变同步分组名称，不移动或删除任何对话文件。
    /// </summary>
    public int RenameLauncherWorkspace(
        IEnumerable<ManagerInstance> instances,
        string currentName,
        string newName)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var current = NormalizeWorkspaceName(currentName, nameof(currentName));
        var updated = NormalizeWorkspaceName(newName, nameof(newName));
        var versions = instances
            .GroupBy(instance => instance.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var knownNames = GetWorkspaceNames(versions);
        if (!knownNames.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"工作区不存在：{current}。");
        }

        if (!string.Equals(current, updated, StringComparison.OrdinalIgnoreCase)
            && knownNames.Contains(updated, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"已经存在同名工作区：{updated}。");
        }

        var affectedVersions = 0;
        foreach (var instance in versions)
        {
            var versionSettings = Read(instance);
            if (!string.Equals(
                    versionSettings.ConversationWorkspace,
                    current,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            versionSettings.ConversationWorkspace = updated;
            Save(instance, versionSettings);
            affectedVersions++;
        }

        var launcherSettings = ReadLauncherSettings();
        launcherSettings.Workspaces.RemoveAll(name =>
            string.Equals(name, current, StringComparison.OrdinalIgnoreCase));
        if (!launcherSettings.Workspaces.Contains(updated, StringComparer.OrdinalIgnoreCase))
        {
            launcherSettings.Workspaces.Add(updated);
        }

        SaveLauncherSettings(launcherSettings);
        return affectedVersions;
    }

    /// <summary>
    /// 删除 Launcher 工作区，并把原本属于它的版本切回“完全独立”。
    /// 不删除会话文件或其它版本数据。
    /// </summary>
    public int DeleteLauncherWorkspace(IEnumerable<ManagerInstance> instances, string name)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var normalized = NormalizeWorkspaceName(name, nameof(name));
        var versions = instances
            .GroupBy(instance => instance.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (!GetWorkspaceNames(versions).Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"工作区不存在：{normalized}。");
        }

        var affectedVersions = 0;
        foreach (var instance in versions)
        {
            var versionSettings = Read(instance);
            if (!string.Equals(
                    versionSettings.ConversationWorkspace,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            versionSettings.ConversationSyncMode = ConversationSyncMode.Independent;
            versionSettings.ConversationWorkspace = null;
            Save(instance, versionSettings);
            affectedVersions++;
        }

        var launcherSettings = ReadLauncherSettings();
        launcherSettings.Workspaces.RemoveAll(workspace =>
            string.Equals(workspace, normalized, StringComparison.OrdinalIgnoreCase));
        SaveLauncherSettings(launcherSettings);
        return affectedVersions;
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

        foreach (var workspace in ReadLauncherSettings().Workspaces)
        {
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
        return ReadLauncherSettings().SyncAllConfiguration
            || leftSettings.SyncAllConfiguration
            || rightSettings.SyncAllConfiguration;
    }

    public bool ShouldSyncConversations(ManagerInstance left, ManagerInstance right)
    {
        var leftSettings = Read(left);
        var rightSettings = Read(right);
        if (ReadLauncherSettings().SyncAllConfiguration
            || leftSettings.SyncAllConfiguration
            || rightSettings.SyncAllConfiguration)
        {
            return true;
        }

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

    private static string NormalizeWorkspaceName(string? value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("工作区名称不能为空。", parameterName);
        }

        if (normalized.Length > 80)
        {
            throw new ArgumentException("工作区名称不能超过 80 个字符。", parameterName);
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("工作区名称不能包含控制字符。", parameterName);
        }

        return normalized;
    }

    private static void NormalizeLauncherSettings(LauncherSettingsData settings)
    {
        if (!Enum.IsDefined(settings.PluginInstallMode))
        {
            settings.PluginInstallMode = PluginInstallMode.Fast;
        }

        settings.Workspaces ??= new List<string>();
        settings.Workspaces = settings.Workspaces
            .Where(workspace => !string.IsNullOrWhiteSpace(workspace))
            .Select(workspace => workspace.Trim())
            .Where(workspace => workspace.Length <= 80 && !workspace.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(workspace => workspace, StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.DshInstallDirectory = NormalizePath(settings.DshInstallDirectory);
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
