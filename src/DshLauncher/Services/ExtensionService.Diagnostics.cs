using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed record PluginDoctorFinding(string Level, string Code, string Message);

public sealed record PluginUpdateInfo(string Name, string? Current, string? Latest, bool HasUpdate);

public sealed record PluginBatchProgress(int Index, int Total, string Name, string? Error);

/// <summary>
/// 插件依赖自检（doctor）与更新检查/批量更新。
/// 借鉴 dsh-plugins/dsh-launcher 的 doctor.rs / check_plugin_updates（思路借鉴，无 LICENSE 文件不复制代码）。
/// </summary>
public sealed partial class ExtensionService
{
    private const string DeepSeekScope = "@deepseek-ai/";

    private static readonly HttpClient RegistryHttp = CreateRegistryHttpClient();

    private static HttpClient CreateRegistryHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Launcher/1.0");
        return client;
    }

    // ---------------------------------------------------------------------
    // doctor：核心包代际检测（profile 里混入 @deepseek-ai 包是工具调用全失败的常见根因）
    // ---------------------------------------------------------------------

    public Task<IReadOnlyList<PluginDoctorFinding>> RunDoctorAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var findings = new List<PluginDoctorFinding>();
        var profileManifestPath = GetProfileManifestPath(instance);
        if (!File.Exists(profileManifestPath))
        {
            findings.Add(new PluginDoctorFinding("error", "profile-missing",
                $"找不到 profile 清单：{profileManifestPath}"));
            return Task.FromResult<IReadOnlyList<PluginDoctorFinding>>(findings);
        }

        JsonObject root;
        try
        {
            root = ReadJsonObject(profileManifestPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            findings.Add(new PluginDoctorFinding("error", "profile-unreadable",
                $"profile 清单无法解析：{ex.Message}"));
            return Task.FromResult<IReadOnlyList<PluginDoctorFinding>>(findings);
        }

        var profileDirectory = Path.GetDirectoryName(profileManifestPath)!;
        var treeVersions = ReadCliTreeCoreVersions(instance);
        foreach (var (package, version) in ReadProfileCoreCopies(profileDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var treeVersion = treeVersions.TryGetValue(package, out var value) ? value : null;
            if (treeVersion is not null
                && version is not null
                && string.Equals(treeVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new PluginDoctorFinding("warn", "profile-core-copy",
                    $"profile 的 node_modules 中存在核心包 {package}@{version}；核心包应由 CLI 依赖树提供，profile 中不应出现。"));
            }
            else if (treeVersion is not null && version is not null)
            {
                findings.Add(new PluginDoctorFinding("error", "profile-core-mixed",
                    $"profile 中存在核心包 {package}@{version}，与 CLI 树中的 {treeVersion} 不同代；" +
                    "该 profile 的工具调用可能全部失败，请卸载该包后重装插件。"));
            }
            else if (treeVersion is null)
            {
                findings.Add(new PluginDoctorFinding("warn", "profile-core-orphan",
                    $"profile 的 node_modules 中存在核心包 {package}@{version ?? "未知版本"}，" +
                    "但 CLI 依赖树未提供该包，无法判断代际；核心包不应出现在 profile 中。"));
            }
            else
            {
                findings.Add(new PluginDoctorFinding("warn", "profile-core-copy",
                    $"profile 的 node_modules 中存在核心包 {package}（版本未知）；核心包不应出现在 profile 中。"));
            }
        }

        // bundle 声明与实体一致性：
        // - @deepseek-ai/* 是核心 bundle，由 CLI 依赖树（含 dsh 包自身的嵌套 node_modules）提供，
        //   profile 中不存在是正常的；仅当两处都找不到时才告警。
        // - 第三方 bundle 必须装在 profile 的 node_modules 中。
        foreach (var bundle in GetBundles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = GetString(bundle);
            if (string.IsNullOrWhiteSpace(package))
            {
                continue;
            }

            var installed = Path.Combine(profileDirectory, "node_modules", package);
            var isCore = package.StartsWith(DeepSeekScope, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(installed))
            {
                continue;
            }

            if (isCore && treeVersions.ContainsKey(package))
            {
                continue;
            }

            findings.Add(new PluginDoctorFinding(
                "error",
                isCore ? "core-bundle-missing" : "bundle-missing",
                isCore
                    ? $"核心 bundle {package} 在 profile 与 CLI 依赖树中都不存在，dsh 启动会因 bundle 不可解析而失败。"
                    : $"bundle {package} 已声明但未安装（profile 的 node_modules 缺失），dsh 启动会因 bundle 不可解析而失败。"));
        }

        foreach (var finding in findings)
        {
            if (finding.Level == "error")
            {
                LauncherLog.Error($"[插件自检] {finding.Message}", ErrorCodes.E2001,
                    new { instance = instance.Name, finding.Code });
            }
            else
            {
                LauncherLog.Warn($"[插件自检] {finding.Message}", ErrorCodes.E2001,
                    new { instance = instance.Name, finding.Code });
            }
        }

        if (findings.Count == 0)
        {
            LauncherLog.Info($"[插件自检] 实例 {instance.Name} 未发现异常。", ErrorCodes.E2001);
        }

        return Task.FromResult<IReadOnlyList<PluginDoctorFinding>>(findings);
    }

    private static Dictionary<string, string?> ReadProfileCoreCopies(string profileDirectory)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var scopeDirectory = Path.Combine(profileDirectory, "node_modules", "@deepseek-ai");
        if (!Directory.Exists(scopeDirectory) || IsReparsePoint(scopeDirectory))
        {
            return result;
        }

        foreach (var packageDirectory in Directory.EnumerateDirectories(scopeDirectory))
        {
            if (IsReparsePoint(packageDirectory))
            {
                continue;
            }

            var name = "@deepseek-ai/" + Path.GetFileName(packageDirectory);
            var manifest = ReadPackageManifest(Path.Combine(packageDirectory, "package.json"));
            result[name] = GetString(manifest, "version");
        }

        return result;
    }

    private static Dictionary<string, string?> ReadCliTreeCoreVersions(ManagerInstance instance)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var nodeModulesDirectory in EnumerateCliNodeModules(instance))
        {
            var scopeDirectory = Path.Combine(nodeModulesDirectory, "@deepseek-ai");
            if (!Directory.Exists(scopeDirectory))
            {
                continue;
            }

            foreach (var packageDirectory in Directory.EnumerateDirectories(scopeDirectory))
            {
                var name = "@deepseek-ai/" + Path.GetFileName(packageDirectory);
                if (result.ContainsKey(name))
                {
                    continue;
                }

                var manifest = ReadPackageManifest(Path.Combine(packageDirectory, "package.json"));
                result[name] = GetString(manifest, "version");
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateCliNodeModules(ManagerInstance instance)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        var entryPoint = instance.EffectiveDshLaunchSpec?.EntryPointPath;
        if (!string.IsNullOrWhiteSpace(entryPoint))
        {
            var current = new DirectoryInfo(Path.GetDirectoryName(entryPoint) ?? string.Empty);
            while (current is not null)
            {
                if (string.Equals(current.Name, "node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(current.FullName);
                    break;
                }

                current = current.Parent;
            }
        }

        // DirectCommand（npm 全局 shim：<prefix>\dsh.cmd）→ <prefix>\node_modules。
        var hostPath = instance.EffectiveDshLaunchSpec?.HostPath;
        if (!string.IsNullOrWhiteSpace(hostPath))
        {
            var hostDirectory = Path.GetDirectoryName(hostPath);
            if (!string.IsNullOrWhiteSpace(hostDirectory))
            {
                candidates.Add(Path.Combine(hostDirectory, "node_modules"));
            }
        }

        candidates.Add(Path.Combine(instance.RootPath, "node_modules"));

        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate) || !seen.Add(candidate))
            {
                continue;
            }

            yield return candidate;
            // 核心 bundle 常驻于 dsh 包自身的嵌套 node_modules（npm 布局无提升时）。
            var nested = Path.Combine(candidate, "@deepseek-ai", "dsh", "node_modules");
            if (Directory.Exists(nested) && seen.Add(nested))
            {
                yield return nested;
            }
        }
    }

    // ---------------------------------------------------------------------
    // 更新检查：installed node_modules 真实版本 vs registry latest
    // ---------------------------------------------------------------------

    public async Task<IReadOnlyList<PluginUpdateInfo>> CheckPluginUpdatesAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var profileManifestPath = GetProfileManifestPath(instance);
        if (!File.Exists(profileManifestPath))
        {
            return Array.Empty<PluginUpdateInfo>();
        }

        JsonObject root;
        try
        {
            root = ReadJsonObject(profileManifestPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LauncherLog.Warn("插件更新检查：profile 清单无法解析。", ErrorCodes.E2002,
                new { instance = instance.Name, error = ex.Message });
            return Array.Empty<PluginUpdateInfo>();
        }

        var profileDirectory = Path.GetDirectoryName(profileManifestPath)!;
        var dependencies = root["dependencies"] as JsonObject;
        if (dependencies is null)
        {
            return Array.Empty<PluginUpdateInfo>();
        }

        var targets = new List<(string Name, string Spec)>();
        foreach (var dependency in dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = dependency.Key;
            if (name.StartsWith(DeepSeekScope, StringComparison.OrdinalIgnoreCase)
                || !SafePackageName.IsMatch(name))
            {
                continue;
            }

            var spec = GetString(dependency.Value) ?? string.Empty;
            // git/tgz/local/workspace 规格没有可比对的 registry 版本。
            if (spec.Contains(':')
                || spec.StartsWith('.')
                || spec.StartsWith('/')
                || spec.StartsWith('\\'))
            {
                continue;
            }

            targets.Add((name, spec));
        }

        var results = new List<PluginUpdateInfo>();
        foreach (var (name, spec) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadInstalledVersion(profileDirectory, name) ?? NormalizeSpecVersion(spec);
            string? latest = null;
            try
            {
                latest = await FetchLatestVersionAsync(name, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
            {
                LauncherLog.Warn($"插件 {name} 最新版本查询失败。", ErrorCodes.E2002, new { error = ex.Message });
            }

            var hasUpdate = current is not null
                && latest is not null
                && CompareSemver(latest, current) > 0;
            results.Add(new PluginUpdateInfo(name, current, latest, hasUpdate));
        }

        return results
            .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ReadInstalledVersion(string profileDirectory, string packageName)
    {
        var manifest = ReadPackageManifest(Path.Combine(
            profileDirectory, "node_modules", packageName, "package.json"));
        return GetString(manifest, "version");
    }

    private static string? NormalizeSpecVersion(string spec)
    {
        var value = spec.Trim().TrimStart('^', '~', '=');
        if (value.StartsWith(">=", StringComparison.Ordinal) || value.StartsWith("<=", StringComparison.Ordinal))
        {
            value = value[2..].Trim();
        }

        return value.Length == 0 || value is "*" or "latest" ? null : value;
    }

    private static async Task<string?> FetchLatestVersionAsync(string packageName, CancellationToken cancellationToken)
    {
        var encoded = packageName.StartsWith('@')
            ? "@" + packageName[1..].Replace("/", "%2f", StringComparison.Ordinal)
            : packageName;
        foreach (var registry in new[] { "https://registry.npmmirror.com", "https://registry.npmjs.org" })
        {
            try
            {
                using var response = await RegistryHttp.GetAsync(
                    $"{registry}/{encoded}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("dist-tags", out var tags)
                    && tags.TryGetProperty("latest", out var latest)
                    && latest.ValueKind == JsonValueKind.String)
                {
                    return latest.GetString();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
            {
                // 换下一个源。
            }
        }

        return null;
    }

    /// <summary>语义化版本比较（支持 v 前缀与 -prerelease）；无法解析时返回 0。</summary>
    internal static int CompareSemver(string left, string right)
    {
        var leftParsed = ParseSemver(left);
        var rightParsed = ParseSemver(right);
        if (leftParsed is null || rightParsed is null)
        {
            return 0;
        }

        for (var index = 0; index < 4; index++)
        {
            var diff = leftParsed.Value.Numbers[index].CompareTo(rightParsed.Value.Numbers[index]);
            if (diff != 0)
            {
                return diff;
            }
        }

        // 正式版 > 预发布版。
        var leftPre = leftParsed.Value.PreRelease;
        var rightPre = rightParsed.Value.PreRelease;
        if (leftPre is null && rightPre is null)
        {
            return 0;
        }

        if (leftPre is null)
        {
            return 1;
        }

        if (rightPre is null)
        {
            return -1;
        }

        return string.Compare(leftPre, rightPre, StringComparison.OrdinalIgnoreCase);
    }

    private static (int[] Numbers, string? PreRelease)? ParseSemver(string value)
    {
        var text = value.Trim().TrimStart('v', 'V');
        if (text.Length == 0)
        {
            return null;
        }

        var parts = text.Split('-', 2);
        var core = parts[0].Split('.');
        var numbers = new int[4];
        for (var index = 0; index < core.Length && index < 4; index++)
        {
            var segment = new string(core[index].TakeWhile(char.IsDigit).ToArray());
            if (segment.Length == 0 || !int.TryParse(segment, out numbers[index]))
            {
                return null;
            }
        }

        return (numbers, parts.Length > 1 ? parts[1] : null);
    }

    // ---------------------------------------------------------------------
    // 批量更新：逐个执行 dsh plugin update，失败不中断其余插件
    // ---------------------------------------------------------------------

    public async Task<string> UpdatePluginsAsync(
        ManagerInstance instance,
        IReadOnlyList<string> packageNames,
        NodeRuntimeInfo? nodeRuntime,
        PluginInstallMode installMode,
        IProgress<PluginBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(packageNames);
        var succeeded = new List<string>();
        var failed = new List<string>();
        for (var index = 0; index < packageNames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = packageNames[index];
            try
            {
                await UpdatePluginAsync(instance, name, nodeRuntime, installMode, cancellationToken);
                succeeded.Add(name);
                progress?.Report(new PluginBatchProgress(index + 1, packageNames.Count, name, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or TimeoutException
                or System.ComponentModel.Win32Exception)
            {
                failed.Add($"{name}（{ex.Message}）");
                progress?.Report(new PluginBatchProgress(index + 1, packageNames.Count, name, ex.Message));
                LauncherLog.Warn($"批量更新：{name} 更新失败。", ErrorCodes.E2003,
                    new { instance = instance.Name, error = ex.Message });
            }
        }

        var builder = new StringBuilder();
        builder.Append($"批量更新完成：成功 {succeeded.Count} 个");
        if (failed.Count > 0)
        {
            builder.Append($"，失败 {failed.Count} 个：{string.Join("；", failed)}");
        }
        else
        {
            builder.Append('。');
        }

        return builder.ToString();
    }
}
