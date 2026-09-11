using System.IO;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// 把实例的某个 profile 导出成 **manifest v4 + .dspack**（#24 第 5 步的 UI 侧桥接）。
/// 只读 profile 的三个配置文件（package.json / cordis.patch.yml / pnpm-workspace.yaml|lock），
/// 组装成 <see cref="PackExportRequest"/> 后交给 <see cref="DshPackWriter"/> 落盘。
/// </summary>
public static class PackExportService
{
    public const string PackageJsonFileName = "package.json";

    public const string PatchFileName = "cordis.patch.yml";

    public const string WorkspaceFileName = "pnpm-workspace.yaml";

    public const string LockFileName = "pnpm-lock.yaml";

    /// <summary>从实例 profile 组装导出请求（纯读取，不写盘）。</summary>
    public static bool TryBuildRequest(
        ManagerInstance instance,
        string profileName,
        string packVersion,
        out PackExportRequest? request,
        out string? error)
    {
        request = null;
        error = null;
        if (instance is null)
        {
            error = "没有选中的实例。";
            return false;
        }

        var profileDirectory = Path.Combine(instance.DshHome, "profiles", PackFormat.ResolveProfileName(profileName));
        if (!Directory.Exists(profileDirectory))
        {
            error = $"profile 目录不存在：{profileDirectory}";
            return false;
        }

        var packagePath = Path.Combine(profileDirectory, PackageJsonFileName);
        if (!File.Exists(packagePath))
        {
            error = "profile 缺少 package.json，无法导出。";
            return false;
        }

        var bundles = new List<string>();
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packagePath));
            var root = document.RootElement;
            if (root.TryGetProperty("dsh", out var dsh)
                && dsh.TryGetProperty("profile", out var profile)
                && profile.TryGetProperty("bundles", out var bundlesElement)
                && bundlesElement.ValueKind == JsonValueKind.Array)
            {
                bundles.AddRange(bundlesElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(value => value.Length > 0));
            }

            if (root.TryGetProperty("dependencies", out var dependenciesElement)
                && dependenciesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in dependenciesElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        dependencies[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            error = $"package.json 解析失败：{ex.Message}";
            return false;
        }

        if (bundles.Count == 0)
        {
            error = "profile 的 package.json 里没有 dsh.profile.bundles，无法导出成整合包。";
            return false;
        }

        request = new PackExportRequest(
            PackName: PackFormat.ResolveProfileName(profileName),
            PackVersion: string.IsNullOrWhiteSpace(packVersion) ? "1.0.0" : packVersion.Trim(),
            ProfileName: PackFormat.ResolveProfileName(profileName),
            DshVersion: instance.DetectedVersion ?? string.Empty,
            Bundles: bundles,
            Dependencies: dependencies,
            DisplayNames: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["zh-CN"] = instance.Name,
                ["en-US"] = instance.Name
            },
            Patch: ReadTextOrNull(Path.Combine(profileDirectory, PatchFileName)),
            WorkspaceYaml: ReadTextOrNull(Path.Combine(profileDirectory, WorkspaceFileName)),
            LockYaml: ReadTextOrNull(Path.Combine(profileDirectory, LockFileName)),
            PackageJsonSnapshot: File.ReadAllText(packagePath));
        return true;
    }

    /// <summary>导出到指定文件（UI 入口直接调用）。</summary>
    public static bool TryExport(
        ManagerInstance instance,
        string profileName,
        string destinationPath,
        string packVersion,
        out IReadOnlyList<string> summary,
        out string? error)
    {
        summary = Array.Empty<string>();
        if (!TryBuildRequest(instance, profileName, packVersion, out var request, out error))
        {
            return false;
        }

        if (!DshPackWriter.TryWrite(destinationPath, request!, out error))
        {
            return false;
        }

        summary = new[]
        {
            $"已导出 {Path.GetFileName(destinationPath)}",
            $"整合包：{request!.PackName} {request.PackVersion}",
            $"dshVersion：{request.DshVersion}",
            $"bundles：{request.Bundles.Count} 个",
            $"依赖：{request.Dependencies.Count} 条",
            request.Patch is null ? "patch：无" : "patch：已包含",
            request.LockYaml is null ? "pnpm-lock：无" : "pnpm-lock：已包含"
        };
        return true;
    }

    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
