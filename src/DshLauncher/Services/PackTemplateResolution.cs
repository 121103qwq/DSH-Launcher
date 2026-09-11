using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// work-log/78：整合包版本不一致时的**模板解析**（纯逻辑集中在此，便于自测；界面只负责选择）。
///
/// 分工：<see cref="PackVersionMismatchWindow"/> 负责"问用户"；本类负责"版本比较 / 定位安装后的运行目录 /
/// 按用户选择构造模板"。下载本身复用既有 <see cref="DshInstallService.InstallVersionAsync"/>。
/// </summary>
public static class PackTemplateResolution
{
    /// <summary>版本比较（**精确匹配**，仅忽略大小写、首尾空白与可选前导 v）。</summary>
    public static bool VersionsMatch(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>规范化版本串（去空白、去可选前导 v）。</summary>
    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? trimmed[1..] : trimmed;
    }

    /// <summary>
    /// 安装后的运行目录定位：先按检测器解析；再退回 <c>&lt;目录&gt;/versions/&lt;版本&gt;</c> 形态。
    /// 都拿不到时返回 null（调用方报错，不猜）。
    /// </summary>
    public static string? LocateInstalledPackageRoot(string installDirectory, string version)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var direct = DshRuntimeDetector.TryResolvePackageRoot(installDirectory);
        if (direct is not null && Path.GetFileName(direct).Contains(version, StringComparison.OrdinalIgnoreCase))
        {
            return direct;
        }

        var versionsRoot = Path.Combine(installDirectory, "versions");
        if (Directory.Exists(versionsRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(versionsRoot))
            {
                if (!Path.GetFileName(directory).Contains(version, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var resolved = DshRuntimeDetector.TryResolvePackageRoot(directory);
                if (resolved is not null)
                {
                    return resolved;
                }
            }
        }

        return direct;
    }

    /// <summary>
    /// 按"刚安装好的运行目录"构造**临时模板**：不改动用户实例、不写注册表——
    /// 只把模板实例的运行时三件套（RootPath / 版本 / 启动规格）替换掉。
    /// </summary>
    public static ManagerInstance? BuildTemplateFromRuntime(
        ManagerInstance template,
        string packageRoot,
        string version,
        out string? error)
    {
        error = null;
        var launchSpec = DshRuntimeDetector.CreateLaunchSpecForPackageRoot(packageRoot);
        if (!DshRuntimeCommandFactory.IsUsable(launchSpec))
        {
            error = $"运行目录的启动入口不可用：{packageRoot}";
            return null;
        }

        return template with
        {
            RootPath = packageRoot,
            DetectedVersion = version,
            DshLaunchSpec = launchSpec,
            DshExecutablePath = launchSpec?.HostPath ?? template.DshExecutablePath
        };
    }

    /// <summary>候选模板清单：只列 Installed 实例，并标注版本是否匹配（匹配的排前面）。</summary>
    public static IReadOnlyList<(ManagerInstance Instance, bool Matches)> BuildCandidates(
        IEnumerable<ManagerInstance> instances,
        string? requiredVersion)
    {
        ArgumentNullException.ThrowIfNull(instances);
        return instances
            .Where(instance => instance.Kind == InstanceKind.Installed)
            .Select(instance => (Instance: instance, Matches: VersionsMatch(requiredVersion, instance.DetectedVersion)))
            .OrderByDescending(item => item.Matches)
            .ToList();
    }
}
