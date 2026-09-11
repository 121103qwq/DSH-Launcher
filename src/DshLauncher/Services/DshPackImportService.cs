using System.IO;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>导入计划（纯计算，不落盘）：界面确认面板与导入动作都据此执行。</summary>
public sealed record DshPackImportPlan(
    string InstanceName,
    string ProfileName,
    bool RequiresNewInstance,
    string? RequiredDshVersion,
    bool TemplateVersionMatches,
    IReadOnlyList<string> ProfileFiles,
    IReadOnlyList<string> HomeFiles,
    IReadOnlyList<string> Warnings);

/// <summary>导入结果。</summary>
public sealed record DshPackImportOutcome(
    bool Succeeded,
    string? InstanceId,
    string? InstanceName,
    string? ProfileName,
    int FilesWritten,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    public static DshPackImportOutcome Failed(string error, IReadOnlyList<string>? warnings = null) =>
        new(false, null, null, null, 0, warnings ?? Array.Empty<string>(), error);
}

/// <summary>
/// 整合包导入（#24 第 3 步）：把已校验的 <see cref="PackArchive"/> 落成一个**新建实例**
/// 的专属 DSH_HOME 与 pack profile，任一步失败则整体回滚（删目录 + 注销实例）。
///
/// 本步边界（刻意收窄，稳定优先）：
///   * 只写文本类配置（package.json / cordis.patch.yml / pnpm-lock.yaml / pnpm-workspace.yaml / overrides/** / home/**）；
///   * **不**执行 pnpm install，**不**下载 files[]（第 4 步），**不**联网；
///   * 遇到二进制条目（含 NUL）直接判定失败并回滚，绝不半成品落盘。
/// </summary>
public sealed class DshPackImportService
{
    private const string PackageJsonFileName = "package.json";

    private const string PatchFileName = "cordis.patch.yml";

    private const string PnpmLockFileName = "pnpm-lock.yaml";

    private const string PnpmWorkspaceFileName = "pnpm-workspace.yaml";

    private readonly InstanceRegistry _registry;

    private readonly PackFileDownloader _downloader;

    public DshPackImportService(InstanceRegistry? registry = null, PackFileDownloader? downloader = null)
    {
        _registry = registry ?? new InstanceRegistry();
        _downloader = downloader ?? new PackFileDownloader();
    }

    /// <summary>按 manifest 权威重建 profile 的 package.json（规范：package.json 由 manifest 重建）。</summary>
    public static string BuildPackageJson(PackManifest manifest, string profileName)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in manifest.Dependencies)
        {
            if (PackFormat.TryConvertToPackageJsonEntry(pair.Key, pair.Value, out var name, out var spec))
            {
                dependencies[name] = spec;
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["name"] = $"dsh-profile-{profileName}",
            ["private"] = true,
            ["dsh"] = new Dictionary<string, object?>
            {
                ["profile"] = new Dictionary<string, object?>
                {
                    ["bundles"] = manifest.Bundles.ToArray()
                }
            }
        };

        if (dependencies.Count > 0)
        {
            payload["dependencies"] = dependencies;
        }

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>生成导入计划：实例名去重、profile 名、要写的文件清单与提示。不落盘。</summary>
    public DshPackImportPlan BuildPlan(
        PackArchive archive,
        IReadOnlyList<ManagerInstance> existingInstances,
        ManagerInstance? template,
        string? preferredLanguage = null)
    {
        var manifest = archive.Manifest;
        var warnings = new List<string>(manifest.Notes);
        var profileName = PackFormat.ResolveProfileName(manifest.ProfileName);
        var displayName = manifest.ResolveDisplayName(preferredLanguage);
        var instanceName = CreateUniqueName(SanitizeName(displayName), existingInstances);

        var versionMatches = true;
        if (!string.IsNullOrWhiteSpace(manifest.DshVersion) && template is not null)
        {
            versionMatches = string.Equals(
                NormalizeVersion(manifest.DshVersion),
                NormalizeVersion(template.DetectedVersion),
                StringComparison.OrdinalIgnoreCase);
            if (!versionMatches)
            {
                warnings.Add(
                    $"整合包要求 DSh {manifest.DshVersion}，模板实例是 {template.DetectedVersion ?? "未知"}；"
                    + "本步不会自动安装该版本，导入后将先按模板版本运行。");
            }
        }

        var profileFiles = new List<string> { PackageJsonFileName };
        if (archive.HasPnpmLock)
        {
            profileFiles.Add(PnpmLockFileName);
        }

        if (archive.HasPnpmWorkspace)
        {
            profileFiles.Add(PnpmWorkspaceFileName);
        }

        if (archive.OverridePaths.Contains(PatchFileName, StringComparer.Ordinal) || !string.IsNullOrWhiteSpace(manifest.Patch))
        {
            profileFiles.Add(PatchFileName);
        }

        foreach (var path in archive.OverridePaths.Where(path => !string.Equals(path, PatchFileName, StringComparison.Ordinal)))
        {
            profileFiles.Add($"{PackFormat.OverridesDirectoryName}/{path}");
        }

        warnings.Add("本步只落盘配置，不执行 pnpm install；依赖安装与 files[] 下载见后续步骤。");

        return new DshPackImportPlan(
            instanceName,
            profileName,
            RequiresNewInstance: true, // 规范：整合包一律新建实例（dshhome 形态尤其如此）
            manifest.DshVersion,
            versionMatches,
            profileFiles,
            archive.HomePaths,
            warnings);
    }

    /// <summary>执行导入；失败时回滚（注销实例 + 删除新建的 HOME 目录）。</summary>
    public async Task<DshPackImportOutcome> ImportAsync(
        PackArchive archive,
        ManagerInstance template,
        IReadOnlyList<ManagerInstance> existingInstances,
        string? preferredLanguage = null,
        bool allowDownloads = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (template.Kind != InstanceKind.Installed
            || !DshRuntimeCommandFactory.IsUsable(template.EffectiveDshLaunchSpec))
        {
            return DshPackImportOutcome.Failed("没有可用的 DSh 运行版本，无法导入整合包。");
        }

        var plan = BuildPlan(archive, existingInstances, template, preferredLanguage);
        if (!plan.TemplateVersionMatches)
        {
            // 稳定优先：本步不自动下载其它 DSh 版本（第 4/5 步再接），先明确拒绝而不是装作成功。
            return DshPackImportOutcome.Failed(
                $"整合包要求 DSh {plan.RequiredDshVersion}，当前模板实例是 {template.DetectedVersion ?? "未知"}；"
                + "请先安装并使用该版本，或改用对应版本的整合包。",
                plan.Warnings);
        }

        if (archive.Manifest.Files.Count > 0 && !allowDownloads)
        {
            // 用户决策 Q2：默认只认包内载荷，下载必须显式同意（这里宁可拒绝，也不交付半成品实例）。
            return DshPackImportOutcome.Failed(
                $"该整合包还有 {archive.Manifest.Files.Count} 个需要下载的文件（合计约 "
                + $"{archive.Manifest.Files.Sum(file => file.Size) / 1024 / 1024} MB），按安全约定默认不下载；"
                + "请在导入确认里显式允许下载后重试。",
                plan.Warnings);
        }

        if (existingInstances.Any(instance =>
                string.Equals(instance.Name, plan.InstanceName, StringComparison.OrdinalIgnoreCase)))
        {
            return DshPackImportOutcome.Failed($"实例名已被占用：{plan.InstanceName}", plan.Warnings);
        }

        var warnings = new List<string>(plan.Warnings);
        var textEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in archive.Entries.Select(entry => entry.Path))
        {
            if (!archive.TryReadTextEntry(relative, out var text, out var readError))
            {
                return DshPackImportOutcome.Failed($"读取条目失败：{readError}", warnings);
            }

            if (text!.Contains('\0'))
            {
                return DshPackImportOutcome.Failed(
                    $"条目 {relative} 看起来是二进制内容；本步只导入文本配置（二进制载荷走后续步骤）。",
                    warnings);
            }

            textEntries[relative] = text;
        }

        ManagerInstance? instance = null;
        var written = 0;
        try
        {
            instance = _registry.Register(
                plan.InstanceName,
                template.RootPath,
                InstanceKind.Installed,
                template.DshExecutablePath,
                template.DetectedVersion,
                template.PackageManager,
                dshHome: null,
                dshLaunchSpec: template.EffectiveDshLaunchSpec);

            var profileDirectory = Path.Combine(instance.DshHome, "profiles", plan.ProfileName);
            Directory.CreateDirectory(profileDirectory);

            WriteText(
                Path.Combine(profileDirectory, PackageJsonFileName),
                BuildPackageJson(archive.Manifest, plan.ProfileName));
            written++;

            if (textEntries.ContainsKey(PackageJsonFileName))
            {
                // 归档自带的 package.json 只作快照参考，落盘版本以 manifest 为准。
                warnings.Add("已忽略归档内 package.json 快照，按 manifest 重建。");
            }

            if (textEntries.TryGetValue(PnpmLockFileName, out var lockText))
            {
                WriteText(Path.Combine(profileDirectory, PnpmLockFileName), lockText);
                written++;
            }

            if (textEntries.TryGetValue(PnpmWorkspaceFileName, out var workspaceText))
            {
                WriteText(Path.Combine(profileDirectory, PnpmWorkspaceFileName), workspaceText);
                written++;
            }

            if (textEntries.TryGetValue($"{PackFormat.OverridesDirectoryName}/{PatchFileName}", out var overridePatch))
            {
                WriteText(Path.Combine(profileDirectory, PatchFileName), overridePatch);
                written++;
            }
            else if (!string.IsNullOrWhiteSpace(archive.Manifest.Patch))
            {
                WriteText(Path.Combine(profileDirectory, PatchFileName), archive.Manifest.Patch!);
                written++;
            }

            foreach (var path in archive.OverridePaths)
            {
                if (string.Equals(path, PatchFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!textEntries.TryGetValue($"{PackFormat.OverridesDirectoryName}/{path}", out var content))
                {
                    continue;
                }

                var destination = Path.Combine(profileDirectory, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                WriteText(destination, content);
                written++;
            }

            foreach (var file in archive.Manifest.Files)
            {
                var destination = Path.Combine(profileDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var download = await _downloader.DownloadAsync(file, destination, cancellationToken).ConfigureAwait(false);
                if (!download.Succeeded)
                {
                    throw new InvalidDataException($"下载失败：{file.Path}（{download.Error}）");
                }

                written++;
            }

            foreach (var path in archive.HomePaths)
            {
                if (!textEntries.TryGetValue($"{PackFormat.HomeDirectoryName}/{path}", out var content))
                {
                    continue;
                }

                var destination = Path.Combine(instance.DshHome, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                WriteText(destination, content);
                written++;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Rollback(instance);
            return DshPackImportOutcome.Failed($"导入失败并已回滚：{ex.Message}", warnings);
        }

        return new DshPackImportOutcome(
            true,
            instance?.Id,
            instance?.Name,
            plan.ProfileName,
            written,
            warnings,
            null);
    }

    /// <summary>回滚：删除新建的实例 HOME 并注销实例记录（顺序：先注销再删目录，避免留下台账孤儿）。</summary>
    private void Rollback(ManagerInstance? instance)
    {
        if (instance is null)
        {
            return;
        }

        try
        {
            _registry.Unregister(instance.Id);
        }
        catch (IOException)
        {
            // 注销失败也要尽力删目录；调用方拿到的是原始失败原因，不被这里的清理错误掩盖。
        }
        catch (InvalidDataException)
        {
        }

        try
        {
            if (Directory.Exists(instance.DshHome)
                && instance.DshHome.Contains($"{Path.DirectorySeparatorChar}instances{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(instance.DshHome, recursive: true);

                // HOME 只是 instances/<id>/ 下的一层；空掉的实例目录也要清掉，否则留下孤儿目录。
                var instanceDirectory = Directory.GetParent(instance.DshHome);
                if (instanceDirectory is not null
                    && string.Equals(instanceDirectory.Name, instance.Id, StringComparison.OrdinalIgnoreCase)
                    && instanceDirectory.FullName.Contains($"{Path.DirectorySeparatorChar}instances{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(instanceDirectory.FullName)
                    && instanceDirectory.GetFileSystemInfos().Length == 0)
                {
                    instanceDirectory.Delete();
                }
            }
        }
        catch (IOException)
        {
            // 目录删除失败不掩盖原始错误（HOME 位于 Launcher 自己的 instances/ 下，失败只影响残留）。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string NormalizeVersion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimStart('v', 'V');

    /// <summary>实例名去重：重名时追加 " 2"、" 3"…（与既有导入链路同款策略）。</summary>
    private static string CreateUniqueName(string baseName, IReadOnlyList<ManagerInstance> existing)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "整合包" : baseName;
        if (existing.All(instance => !string.Equals(instance.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return name;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{name} {index}";
            if (existing.All(instance => !string.Equals(instance.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"{name} {Guid.NewGuid():N}"[..Math.Min(60, name.Length + 33)];
    }

    /// <summary>实例名净化：去掉路径非法字符与控制字符，压缩空白，限长 60。</summary>
    private static string SanitizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "整合包";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(invalid.Contains(character) || char.IsControl(character) ? ' ' : character);
        }

        var collapsed = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
        {
            return "整合包";
        }

        return collapsed.Length <= 60 ? collapsed : collapsed[..60];
    }
}
