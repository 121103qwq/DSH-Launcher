using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>失败安装回滚的结果（用于错误提示与结构化日志）。</summary>
public sealed record PluginRollbackResult(bool Changed, IReadOnlyList<string> RemovedNames, string Message)
{
    public static PluginRollbackResult None { get; } = new(
        false,
        Array.Empty<string>(),
        "未发现需要清理的残留。");
}

/// <summary>
/// 失败安装的残留清理（借鉴 MarcoG-h/DSH-Launcher 的 rollbackFailedAdd）：
/// pnpm 失败时可能已经把依赖写进 profile 的 package.json、把残缺目录留在
/// node_modules、甚至登记进 dsh.profile.bundles；下一次 dsh 启动时 include-loader
/// 会尝试导入残缺包而整个 boot 崩溃。这里在失败后只移除"本次新增"的包
/// （操作前已存在的同名包一律不动，避免破坏原本可用的插件）。
/// </summary>
internal sealed class PluginProfileResidue
{
    private const string ProfileName = "web";
    private readonly string _profileDirectory;
    private readonly IReadOnlyList<string> _moduleRoots;
    private readonly IReadOnlyList<string> _candidates;
    private readonly HashSet<string> _dependenciesBefore;
    private readonly HashSet<string> _bundlesBefore;
    private readonly HashSet<string> _modulesBefore;
    private readonly string? _patchBefore;

    private PluginProfileResidue(
        string profileDirectory,
        IReadOnlyList<string> moduleRoots,
        IReadOnlyList<string> candidates,
        HashSet<string> dependenciesBefore,
        HashSet<string> bundlesBefore,
        HashSet<string> modulesBefore,
        string? patchBefore)
    {
        _profileDirectory = profileDirectory;
        _moduleRoots = moduleRoots;
        _candidates = candidates;
        _dependenciesBefore = dependenciesBefore;
        _bundlesBefore = bundlesBefore;
        _modulesBefore = modulesBefore;
        _patchBefore = patchBefore;
    }

    /// <summary>捕获操作前的 profile 状态；读取失败返回 null（不阻塞安装本身）。</summary>
    public static PluginProfileResidue? TryCapture(ManagerInstance instance, string packageSpec)
    {
        try
        {
            var candidates = PnpmFailureClassifier.ExtractPackageNameCandidates(packageSpec);
            if (candidates.Count == 0)
            {
                return null;
            }

            var profileDirectory = Path.Combine(instance.DshHome, "profiles", ProfileName);
            var moduleRoots = new[]
            {
                Path.Combine(profileDirectory, "node_modules"),
                Path.Combine(instance.DshHome, "profiles", "node_modules")
            };
            var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manifestPath = Path.Combine(profileDirectory, "package.json");
            if (File.Exists(manifestPath)
                && JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)) is JsonObject root)
            {
                if (root["dependencies"] is JsonObject deps)
                {
                    foreach (var dependency in deps)
                    {
                        dependencies.Add(dependency.Key);
                    }
                }

                foreach (var bundle in ReadBundles(root))
                {
                    bundles.Add(bundle);
                }
            }

            var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (moduleRoots.Any(root => Directory.Exists(Path.Combine(root, candidate))
                    || File.Exists(Path.Combine(root, candidate))))
                {
                    modules.Add(candidate);
                }
            }

            var patchPath = Path.Combine(profileDirectory, "cordis.patch.yml");
            var patchBefore = File.Exists(patchPath) ? File.ReadAllText(patchPath, Encoding.UTF8) : null;
            return new PluginProfileResidue(
                profileDirectory,
                moduleRoots,
                candidates,
                dependencies,
                bundles,
                modules,
                patchBefore);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException
            or ArgumentException)
        {
            LauncherLog.Warn(
                "读取 profile 状态失败，跳过失败安装的残留清理。",
                ErrorCodes.E2005,
                new { package = packageSpec, error = ex.Message });
            return null;
        }
    }

    /// <summary>移除本次新增的残留（package.json 依赖/bundles、node_modules、cordis.patch.yml）。</summary>
    public PluginRollbackResult Rollback()
    {
        var targets = _candidates
            .Where(name => !_dependenciesBefore.Contains(name) && !_bundlesBefore.Contains(name))
            .ToArray();
        if (targets.Length == 0)
        {
            return PluginRollbackResult.None;
        }

        var removed = new List<string>();
        var cleaned = new List<string>();
        try
        {
            var manifestRemoved = CleanManifest(targets);
            if (manifestRemoved.Count > 0)
            {
                cleaned.Add("package.json");
            }

            foreach (var name in targets)
            {
                var moduleDeleted = false;
                if (!_modulesBefore.Contains(name)
                    && _moduleRoots.Any(root => Directory.Exists(Path.Combine(root, name))
                        || File.Exists(Path.Combine(root, name))))
                {
                    foreach (var root in _moduleRoots)
                    {
                        TryDelete(Path.Combine(root, name));
                    }

                    moduleDeleted = true;
                }

                if (moduleDeleted || manifestRemoved.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    removed.Add(name);
                }
            }

            if (removed.Count > 0)
            {
                cleaned.Insert(0, "node_modules");
            }

            if (CleanPatchLayer(targets))
            {
                cleaned.Add("cordis.patch.yml");
                foreach (var name in targets)
                {
                    if (!removed.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        removed.Add(name);
                    }
                }
            }

            var message = removed.Count == 0
                ? PluginRollbackResult.None.Message
                : $"已清理失败安装的残留：{string.Join("、", removed)}（{string.Join(" / ", cleaned)}）。";
            return new PluginRollbackResult(removed.Count > 0, removed, message);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException
            or ArgumentException)
        {
            return new PluginRollbackResult(
                removed.Count > 0,
                removed,
                $"残留清理未完成：{ex.Message}（请手动检查 profile 的 package.json 与 node_modules）。");
        }
    }

    private IReadOnlyList<string> CleanManifest(IReadOnlyList<string> targets)
    {
        var removed = new List<string>();
        var manifestPath = Path.Combine(_profileDirectory, "package.json");
        if (!File.Exists(manifestPath)
            || JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)) is not JsonObject root)
        {
            return removed;
        }

        var changed = false;
        if (root["dependencies"] is JsonObject dependencies)
        {
            foreach (var name in targets)
            {
                var key = dependencies
                    .Select(pair => pair.Key)
                    .FirstOrDefault(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));
                if (key is not null)
                {
                    dependencies.Remove(key);
                    removed.Add(name);
                    changed = true;
                }
            }
        }

        if (root["dsh"] is JsonObject dsh
            && dsh["profile"] is JsonObject profile
            && profile["bundles"] is JsonArray bundles)
        {
            for (var index = bundles.Count - 1; index >= 0; index--)
            {
                if (bundles[index] is JsonValue value
                    && value.TryGetValue<string>(out var bundleName)
                    && targets.FirstOrDefault(name => string.Equals(name, bundleName, StringComparison.OrdinalIgnoreCase)) is { } matched)
                {
                    bundles.RemoveAt(index);
                    if (!removed.Contains(matched, StringComparer.OrdinalIgnoreCase))
                    {
                        removed.Add(matched);
                    }

                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return removed;
        }

        File.WriteAllText(
            manifestPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        return removed;
    }

    private bool CleanPatchLayer(IReadOnlyList<string> targets)
    {
        var patchPath = Path.Combine(_profileDirectory, "cordis.patch.yml");
        if (_patchBefore is null)
        {
            // 操作前没有这个文件：本次失败若新建了它，只在内容确实涉及本次包名时移除。
            if (!File.Exists(patchPath))
            {
                return false;
            }

            var created = File.ReadAllText(patchPath, Encoding.UTF8);
            if (!targets.Any(name => created.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            File.Delete(patchPath);
            return true;
        }

        var current = File.ReadAllText(patchPath, Encoding.UTF8);
        if (string.Equals(current, _patchBefore, StringComparison.Ordinal))
        {
            return false;
        }

        // 只在整个操作前文件里不含本次包名时整体还原，避免误删用户既有条目。
        if (targets.Any(name => _patchBefore.Contains(name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        File.WriteAllText(patchPath, _patchBefore, new UTF8Encoding(false));
        return true;
    }

    private static IEnumerable<string> ReadBundles(JsonObject root)
    {
        if (root["dsh"] is not JsonObject dsh
            || dsh["profile"] is not JsonObject profile
            || profile["bundles"] is not JsonArray bundles)
        {
            yield break;
        }

        foreach (var bundle in bundles)
        {
            if (bundle is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                FileSystemCleanup.DeleteDirectoryRecursive(path);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件被占用时留给下一次 pnpm install / 启动自愈处理。
        }
    }
}
