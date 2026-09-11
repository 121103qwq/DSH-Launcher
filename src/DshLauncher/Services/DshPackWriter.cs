using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DshLauncher.Services;

/// <summary>导出整合包的输入（全部为纯数据，便于单测与往返验证）。</summary>
public sealed record PackExportRequest(
    string PackName,
    string PackVersion,
    string ProfileName,
    string DshVersion,
    IReadOnlyList<string> Bundles,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyDictionary<string, string>? DisplayNames = null,
    IReadOnlyDictionary<string, string>? Descriptions = null,
    string? Author = null,
    string? Icon = null,
    string? Patch = null,
    IReadOnlyList<PackFileEntry>? Files = null,
    IReadOnlyDictionary<string, string>? Overrides = null,
    string? WorkspaceYaml = null,
    string? LockYaml = null,
    string? PackageJsonSnapshot = null);

/// <summary>
/// 整合包导出器（#24 第 5 步）：写 **manifest v4 + .dspack（pack-structure v2）**。
/// 自家旧 v1 导出（<see cref="VersionPackageService"/>）保持原样，作为"旧格式"继续可用。
/// </summary>
public static class DshPackWriter
{
    public const int ContainerVersion = 2;

    public const int ManifestVersion = 4;

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 人工可读；仅本地文件，非 HTML 上下文
    };

    /// <summary>生成 manifest v4（纯函数）。依赖坐标按规范反向转换后写入 dependencies。</summary>
    public static string BuildManifestJson(PackExportRequest request)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in request.Dependencies)
        {
            if (PackFormat.TryParsePackageJsonEntry(pair.Key, pair.Value, out var coordinate, out var pinned))
            {
                dependencies[coordinate] = pinned;
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["manifestVersion"] = ManifestVersion,
            ["type"] = "profile",
            ["name"] = request.PackName,
            ["version"] = request.PackVersion,
            ["dshVersion"] = request.DshVersion,
            ["profileName"] = PackFormat.ResolveProfileName(request.ProfileName),
            ["bundles"] = request.Bundles.ToArray(),
            ["dependencies"] = dependencies
        };

        AddLocalized(payload, "displayName", request.DisplayNames);
        AddLocalized(payload, "description", request.Descriptions);

        if (!string.IsNullOrWhiteSpace(request.Author))
        {
            payload["author"] = request.Author;
        }

        if (!string.IsNullOrWhiteSpace(request.Icon))
        {
            payload["icon"] = request.Icon;
        }

        if (!string.IsNullOrWhiteSpace(request.Patch))
        {
            payload["patch"] = request.Patch;
        }

        if (request.Files is { Count: > 0 })
        {
            payload["files"] = request.Files.Select(file => new Dictionary<string, object?>
            {
                ["path"] = file.Path,
                ["sha256"] = file.Sha256,
                ["size"] = file.Size,
                ["urls"] = file.Urls.ToArray()
            }).ToArray();
        }

        return JsonSerializer.Serialize(payload, ManifestOptions) + "\n";
    }

    /// <summary>
    /// 写出 .dspack：根 dspack.json（容器标记）+ manifest.json + 可选 package.json /
    /// pnpm-workspace.yaml / pnpm-lock.yaml + overrides/**。
    /// </summary>
    public static bool TryWrite(string destinationPath, PackExportRequest request, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            error = "导出路径为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.PackName) || string.IsNullOrWhiteSpace(request.DshVersion))
        {
            error = "整合包 name 与 dshVersion 都是必填项。";
            return false;
        }

        if (request.Overrides is { Count: > 0 })
        {
            foreach (var path in request.Overrides.Keys)
            {
                if (!PackFormat.IsSafeRelativePath(path))
                {
                    error = $"overrides 路径非法：{path}";
                    return false;
                }
            }
        }

        if (request.Files is { Count: > 0 })
        {
            foreach (var file in request.Files)
            {
                if (!PackFormat.IsSafeRelativePath(file.Path)
                    || !PackFormat.IsValidSha256(file.Sha256)
                    || file.Size <= 0
                    || file.Urls.Count == 0)
                {
                    error = $"files[] 条目不合法：{file.Path}";
                    return false;
                }
            }
        }

        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件，成功后原子落位：避免导出中断留下损坏的 .dspack。
            var temporaryPath = destinationPath + ".tmp";
            using (var file = File.Create(temporaryPath))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                WriteEntry(zip, PackFormat.ContainerMarkerFileName,
                    JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["format"] = PackFormat.DspackFormatName,
                        ["version"] = ContainerVersion
                    }) + "\n");
                WriteEntry(zip, PackFormat.ManifestFileName, BuildManifestJson(request));

                if (!string.IsNullOrWhiteSpace(request.PackageJsonSnapshot))
                {
                    WriteEntry(zip, "package.json", request.PackageJsonSnapshot!);
                }

                if (!string.IsNullOrWhiteSpace(request.WorkspaceYaml))
                {
                    WriteEntry(zip, "pnpm-workspace.yaml", request.WorkspaceYaml!);
                }

                if (!string.IsNullOrWhiteSpace(request.LockYaml))
                {
                    WriteEntry(zip, "pnpm-lock.yaml", request.LockYaml!);
                }

                if (request.Overrides is { Count: > 0 })
                {
                    foreach (var pair in request.Overrides)
                    {
                        WriteEntry(zip, $"{PackFormat.OverridesDirectoryName}/{pair.Key}", pair.Value);
                    }
                }
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            error = $"导出失败：{ex.Message}";
            return false;
        }
    }

    private static void AddLocalized(
        Dictionary<string, object?> payload,
        string field,
        IReadOnlyDictionary<string, string>? map)
    {
        if (map is not { Count: > 0 })
        {
            return;
        }

        payload[field] = map.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
