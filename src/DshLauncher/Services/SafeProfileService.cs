using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>安全模式隔离 profile 的降级梯级。</summary>
public enum SafeProfileTier
{
    /// <summary>第一级（默认）：保留用户 web profile 里全部 @deepseek-ai 核心，剥离第三方/本地插件。</summary>
    Tier1KeepDeepSeekCore = 1,

    /// <summary>第二级（兜底）：仅 dsh 官方 web 模板核心（dsh-base + dsh-web-app）。</summary>
    Tier2Minimal = 2
}

public sealed record SafeProfileBuildResult(
    bool Ok,
    SafeProfileTier Tier,
    string ProfileDirectory,
    IReadOnlyList<string> Bundles,
    string? Error);

/// <summary>
/// 安全模式的隔离 profile 构建器（思路参考 Ruler4396/dsh-launcher 的 SafeProfileBuilder，MIT；
/// 本实现按本仓结构独立编写）。
///
/// 核心契约：**绝不修改用户任何文件**，只在 <c>&lt;DSH_HOME&gt;\profiles\.dsh-safe</c> 生成一个
/// "剥离第三方插件、保留 dsh 核心"的隔离 profile，用 <c>dsh --profile .dsh-safe</c> 启动。
/// 用户 profile（profiles\web\*）只读；零污染由 <see cref="CaptureProfilesHash"/> /
/// <see cref="ProfilesUntouched"/> 的前后哈希证据把关。
/// </summary>
public sealed class SafeProfileService
{
    /// <summary>隔离 profile 名（dsh 只接受不含分隔符的名字；"."/".."/"node_modules" 被 dsh 拒绝）。</summary>
    public const string SafeProfileName = ".dsh-safe";

    /// <summary>逐插件定位用的隔离 profile 名（与安全模式分开，避免互相覆盖）。</summary>
    public const string BisectProfileName = ".dsh-bisect";

    /// <summary>dsh 官方 web profile 模板核心。</summary>
    public static readonly IReadOnlyList<string> WebCoreMinimal = new[]
    {
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-web-app"
    };

    private const string DeepSeekScope = "@deepseek-ai/";
    private const long MaximumHashFileBytes = 4 * 1024 * 1024;

    public string GetProfilesDirectory(ManagerInstance instance) =>
        Path.Combine(instance.DshHome, "profiles");

    public string GetSafeProfileDirectory(ManagerInstance instance) =>
        Path.Combine(GetProfilesDirectory(instance), SafeProfileName);

    public string GetSafeProfilePackageJson(ManagerInstance instance) =>
        Path.Combine(GetSafeProfileDirectory(instance), "package.json");

    public bool SafeProfileExists(ManagerInstance instance) => File.Exists(GetSafeProfilePackageJson(instance));

    /// <summary>任意隔离 profile 的目录（逐插件定位用 <see cref="BisectProfileName"/>）。</summary>
    public string GetProfileDirectory(ManagerInstance instance, string profileName) =>
        Path.Combine(GetProfilesDirectory(instance), profileName);

    public string GetProfilePackageJson(ManagerInstance instance, string profileName) =>
        Path.Combine(GetProfileDirectory(instance, profileName), "package.json");

    /// <summary>删除某个隔离 profile 目录（只删隔离目录，不碰用户文件）。</summary>
    public void CleanupProfile(ManagerInstance instance, string profileName)
    {
        try
        {
            var directory = GetProfileDirectory(instance, profileName);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherLog.Warn("隔离 profile 清理失败。", ErrorCodes.E1014,
                new { instance = instance.Name, profile = profileName, error = ex.Message });
        }
    }

    /// <summary>用指定 bundles 写一个隔离 profile（逐插件定位：核心 + 未禁用的第三方）。</summary>
    public SafeProfileBuildResult BuildWithBundles(
        ManagerInstance instance,
        string profileName,
        IReadOnlyList<string> bundles)
    {
        var directory = GetProfileDirectory(instance, profileName);
        try
        {
            Directory.CreateDirectory(directory);
            var manifest = new Dictionary<string, object?>
            {
                ["name"] = $"dsh-profile-{profileName.TrimStart('.')}",
                ["private"] = true,
                ["dsh"] = new Dictionary<string, object?>
                {
                    ["profile"] = new Dictionary<string, object?>
                    {
                        ["bundles"] = bundles
                    }
                }
            };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n";
            var path = GetProfilePackageJson(instance, profileName);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
            return new SafeProfileBuildResult(true, SafeProfileTier.Tier1KeepDeepSeekCore, directory, bundles, null);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            LauncherLog.Warn("隔离 profile 生成失败。", ErrorCodes.E1014,
                new { instance = instance.Name, profile = profileName, error = ex.Message });
            return new SafeProfileBuildResult(false, SafeProfileTier.Tier1KeepDeepSeekCore, directory, Array.Empty<string>(), ex.Message);
        }
    }

    /// <summary>构建（或重建）隔离 profile；幂等，用户文件只读。原子写 package.json。</summary>
    public SafeProfileBuildResult Build(ManagerInstance instance, SafeProfileTier tier)
    {
        var directory = GetSafeProfileDirectory(instance);
        try
        {
            Directory.CreateDirectory(directory);
            var bundles = ResolveBundles(instance, tier);
            var manifest = new Dictionary<string, object?>
            {
                ["name"] = "dsh-profile-safe",
                ["private"] = true,
                ["dsh"] = new Dictionary<string, object?>
                {
                    ["profile"] = new Dictionary<string, object?>
                    {
                        ["bundles"] = bundles
                    }
                }
            };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n";
            var path = GetSafeProfilePackageJson(instance);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
            LauncherLog.Info("安全模式隔离 profile 已生成（未改动用户文件）。", ErrorCodes.E1014,
                new { instance = instance.Name, tier = tier.ToString(), bundles });
            return new SafeProfileBuildResult(true, tier, directory, bundles, null);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            LauncherLog.Warn("安全模式隔离 profile 生成失败。", ErrorCodes.E1014,
                new { instance = instance.Name, tier = tier.ToString(), error = ex.Message });
            return new SafeProfileBuildResult(false, tier, directory, Array.Empty<string>(), ex.Message);
        }
    }

    /// <summary>
    /// 解析该梯级应保留的 bundles：Tier2 直接返回最小核心；Tier1 读用户
    /// <c>profiles\web\package.json</c> 的 <c>dsh.profile.bundles</c>，只保留
    /// <c>@deepseek-ai/</c> 前缀的核心（第三方/本地插件剥离），并确保最小核心始终在最前。
    /// 用户文件损坏 → 回落最小核心（绝不抛出中断启动）。
    /// </summary>
    public IReadOnlyList<string> ResolveBundles(ManagerInstance instance, SafeProfileTier tier)
    {
        if (tier == SafeProfileTier.Tier2Minimal)
        {
            return WebCoreMinimal;
        }

        var kept = new List<string>();
        var webPackage = Path.Combine(GetProfilesDirectory(instance), "web", "package.json");
        if (File.Exists(webPackage))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(webPackage, Encoding.UTF8));
                if (document.RootElement.TryGetProperty("dsh", out var dsh)
                    && dsh.TryGetProperty("profile", out var profile)
                    && profile.TryGetProperty("bundles", out var bundles)
                    && bundles.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in bundles.EnumerateArray())
                    {
                        var name = entry.GetString();
                        if (!string.IsNullOrWhiteSpace(name)
                            && name.StartsWith(DeepSeekScope, StringComparison.Ordinal))
                        {
                            kept.Add(name);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // 用户 package.json 损坏 → 视为空，回落最小核心。
            }
        }

        // 最小核心固定在最前（dsh-base → dsh-web-app），其余 @deepseek-ai 核心按用户顺序追加。
        var merged = new List<string>(WebCoreMinimal);
        foreach (var bundle in kept)
        {
            if (!merged.Contains(bundle, StringComparer.Ordinal))
            {
                merged.Add(bundle);
            }
        }

        return merged;
    }

    /// <summary>用户 web profile 里是否有第三方 bundle（安全模式才有意义）。</summary>
    public bool HasThirdPartyBundles(ManagerInstance instance, out IReadOnlyList<string> thirdParty)
    {
        thirdParty = Array.Empty<string>();
        var webPackage = Path.Combine(GetProfilesDirectory(instance), "web", "package.json");
        if (!File.Exists(webPackage))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(webPackage, Encoding.UTF8));
            if (!document.RootElement.TryGetProperty("dsh", out var dsh)
                || !dsh.TryGetProperty("profile", out var profile)
                || !profile.TryGetProperty("bundles", out var bundles)
                || bundles.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var result = new List<string>();
            foreach (var entry in bundles.EnumerateArray())
            {
                var name = entry.GetString();
                if (!string.IsNullOrWhiteSpace(name)
                    && !name.StartsWith(DeepSeekScope, StringComparison.Ordinal))
                {
                    result.Add(name);
                }
            }

            thirdParty = result;
            return result.Count > 0;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>清理隔离 profile（正常启动成功后调用）。只删 .dsh-safe 目录，用户文件绝不触碰。</summary>
    public void Cleanup(ManagerInstance instance)
    {
        try
        {
            if (SafeProfileExists(instance))
            {
                FileSystemCleanup.DeleteDirectoryRecursive(GetSafeProfileDirectory(instance));
                LauncherLog.Info("安全模式隔离 profile 已清理。", ErrorCodes.E1014, new { instance = instance.Name });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响启动，下次再试。
            LauncherLog.Warn("安全模式隔离 profile 清理失败（不影响使用）。", ErrorCodes.E1014,
                new { instance = instance.Name, error = ex.Message });
        }
    }

    /// <summary>
    /// 对用户 profiles 目录做哈希快照（零污染证据）。排除 .dsh-safe 自身、
    /// node_modules（体积大且与用户配置无关）与超过 4 MiB 的文件；锁文件跳过。
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> CaptureProfilesHash(ManagerInstance instance)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var root = GetProfilesDirectory(instance);
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith(SafeProfileName, StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("node_modules", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaximumHashFileBytes)
                {
                    continue;
                }

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                result[relative] = SHA256.HashData(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 锁文件/不可读文件跳过（不影响零污染判定）。
            }
        }

        return result;
    }

    /// <summary>校验两次快照之间用户 profiles 零污染；changedFiles 列出增删改的文件。</summary>
    public bool ProfilesUntouched(
        ManagerInstance instance,
        IReadOnlyDictionary<string, byte[]> before,
        out IReadOnlyList<string> changedFiles)
    {
        var after = CaptureProfilesHash(instance);
        var changes = new List<string>();
        foreach (var pair in before)
        {
            if (!after.TryGetValue(pair.Key, out var current) || !current.AsSpan().SequenceEqual(pair.Value))
            {
                changes.Add(pair.Key);
            }
        }

        foreach (var key in after.Keys)
        {
            if (!before.ContainsKey(key))
            {
                changes.Add(key);
            }
        }

        changedFiles = changes;
        return changes.Count == 0;
    }
}
