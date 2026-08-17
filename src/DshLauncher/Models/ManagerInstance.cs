using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshLauncher.Models;

public enum InstanceKind
{
    Installed,
    Source
}

public enum InstanceRuntimeStatus
{
    Unknown,
    Ready,
    Missing,
    Error,
    Running,
    Stopped
}

public enum InstanceRuntimeOwnership
{
    None,
    Managed,
    Attached
}

public sealed record ManagerInstance(
    string Id,
    string Name,
    string RootPath,
    InstanceKind Kind,
    string DshHome,
    string? DshExecutablePath,
    string? DetectedVersion,
    InstanceRuntimeStatus RuntimeStatus,
    string? PackageManager,
    string? LastError,
    DateTimeOffset RegisteredAt,
    int? ProcessId = null,
    int? Port = null,
    string? WebUrl = null,
    DshRuntimeLaunchSpec? DshLaunchSpec = null)
{
    [JsonIgnore]
    public string KindText => Kind == InstanceKind.Installed ? "installed" : "source";

    [JsonIgnore]
    public InstanceRuntimeOwnership RuntimeOwnership { get; init; }

    [JsonIgnore]
    public string RuntimeOwnershipText => RuntimeOwnership switch
    {
        InstanceRuntimeOwnership.Managed => "Launcher 管理",
        InstanceRuntimeOwnership.Attached => "外部服务",
        _ => "未连接"
    };

    [JsonIgnore]
    public string StatusText => RuntimeStatus switch
    {
        InstanceRuntimeStatus.Ready => "可用",
        InstanceRuntimeStatus.Missing => "缺少运行环境",
        InstanceRuntimeStatus.Error => "检查失败",
        InstanceRuntimeStatus.Running when RuntimeOwnership == InstanceRuntimeOwnership.Attached => "已连接（外部）",
        InstanceRuntimeStatus.Running when RuntimeOwnership == InstanceRuntimeOwnership.Managed => "运行中（Launcher）",
        InstanceRuntimeStatus.Running => "运行中",
        InstanceRuntimeStatus.Stopped => "已停止",
        _ => "待检查"
    };

    [JsonIgnore]
    public string DshVersionText => string.IsNullOrWhiteSpace(DetectedVersion)
        ? "DSh 版本未标记"
        : $"DSh {DetectedVersion}";

    [JsonIgnore]
    public DshRuntimeLaunchSpec? EffectiveDshLaunchSpec => DshLaunchSpec
        ?? (string.IsNullOrWhiteSpace(DshExecutablePath)
            ? null
            : new DshRuntimeLaunchSpec(DshRuntimeLaunchMode.DirectCommand, DshExecutablePath));

    [JsonIgnore]
    public bool CanOpenDesktopShell => EffectiveDshLaunchSpec?.SupportsDesktopShell == true;

    [JsonIgnore]
    public string ResourceSummaryText => $"{ReadPluginCount()} Plugins · {ReadSkillCount()} Skills";

    private int ReadPluginCount()
    {
        var path = Path.Combine(DshHome, "profiles", "web", "package.json");
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.TryGetProperty("dependencies", out var dependencies)
                && dependencies.ValueKind == JsonValueKind.Object)
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    names.Add(dependency.Name);
                }
            }

            if (document.RootElement.TryGetProperty("dsh", out var dsh)
                && dsh.ValueKind == JsonValueKind.Object
                && dsh.TryGetProperty("profile", out var profile)
                && profile.ValueKind == JsonValueKind.Object
                && profile.TryGetProperty("bundles", out var bundles)
                && bundles.ValueKind == JsonValueKind.Array)
            {
                foreach (var bundle in bundles.EnumerateArray())
                {
                    if (bundle.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(bundle.GetString()))
                    {
                        names.Add(bundle.GetString()!);
                    }
                }
            }

            return names.Count;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private int ReadSkillCount()
    {
        return CountSkillRoot(Path.Combine(DshHome, "skills"))
            + CountSkillRoot(Path.Combine(DshHome, ".agents", "skills"));
    }

    private static int CountSkillRoot(string root)
    {
        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            return 0;
        }

        var count = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (IsReparsePoint(entry))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                if (File.Exists(Path.Combine(entry, "SKILL.md")))
                {
                    count++;
                }
            }
            else if (entry.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
