using System.IO;
using System.Text.Json;

namespace DshLauncher.Services;

/// <summary>profile 里发现的卫生问题（用于界面提示与一键复位）。</summary>
public sealed record ProfileHygieneIssue(string Kind, string Path, string Detail, bool AutoFixable);

/// <summary>
/// profile 卫生检查（work-log/71 事故的预防措施）。
///
/// 事故：某个已卸载插件在 profile 里留下了一个**空的 `pnpm-lock.yaml`** 与
/// `pnpm-workspace.yaml` 里的 `allowBuilds` 残留，导致 dsh 启动后页面报
/// "Failed to load plugins"（客户端模块没构建），而 dsh 自己不打印任何原因——
/// 排查代价极高。这里把这些"残留物"变成**可检测、可一键复位**的东西。
/// </summary>
public static class ProfileHygiene
{
    public const string IssueEmptyLock = "empty-lock";

    public const string IssueAllowBuildsResidue = "allow-builds-residue";

    public const string IssueMissingDependencies = "missing-dependencies";

    private static readonly string[] KnownLockHeaders =
    {
        "lockfileVersion:",
        "settings:",
        "autoInstallPeers:",
        "excludeLinksFromLockfile:",
        "importers:"
    };

    /// <summary>检查一个 profile 目录（&lt;$DSH_HOME&gt;/profiles/&lt;name&gt;）。</summary>
    public static IReadOnlyList<ProfileHygieneIssue> Inspect(string? profileDirectory)
    {
        var issues = new List<ProfileHygieneIssue>();
        if (string.IsNullOrWhiteSpace(profileDirectory) || !Directory.Exists(profileDirectory))
        {
            return issues;
        }

        var lockPath = Path.Combine(profileDirectory, "pnpm-lock.yaml");
        if (File.Exists(lockPath))
        {
            var lockText = TryReadAllText(lockPath);
            if (lockText is not null && IsEmptyLockfile(lockText))
            {
                issues.Add(new ProfileHygieneIssue(
                    IssueEmptyLock,
                    lockPath,
                    "存在内容为空的 pnpm-lock.yaml（dsh 新建 profile 时并不生成该文件）。"
                    + "它会让 dsh/pnpm 认为“依赖已锁定且为空”，跳过 profile 安装步骤，可能造成插件加载失败。",
                    AutoFixable: true));
            }
        }

        var workspacePath = Path.Combine(profileDirectory, "pnpm-workspace.yaml");
        if (File.Exists(workspacePath))
        {
            var workspaceText = TryReadAllText(workspacePath);
            var packages = workspaceText is null ? Array.Empty<string>() : ReadAllowBuildsPackages(workspaceText);
            if (packages.Count > 0)
            {
                issues.Add(new ProfileHygieneIssue(
                    IssueAllowBuildsResidue,
                    workspacePath,
                    "pnpm-workspace.yaml 里残留 allowBuilds：" + string.Join("、", packages)
                    + "（通常是已卸载插件留下的）。",
                    AutoFixable: true));
            }
        }

        var packagePath = Path.Combine(profileDirectory, "package.json");
        if (File.Exists(packagePath))
        {
            var packageText = TryReadAllText(packagePath);
            if (packageText is not null && !HasDependenciesProperty(packageText))
            {
                issues.Add(new ProfileHygieneIssue(
                    IssueMissingDependencies,
                    packagePath,
                    "package.json 缺少空的 dependencies 键（dsh 新建 profile 时会有）。",
                    AutoFixable: true));
            }
        }

        return issues;
    }

    /// <summary>是否"空 lock"：没有 importers 依赖、也没有 packages 段。</summary>
    public static bool IsEmptyLockfile(string lockText)
    {
        if (string.IsNullOrWhiteSpace(lockText))
        {
            return true;
        }

        var trimmed = lockText.Trim();
        if (trimmed is "{}" or "---")
        {
            return true;
        }

        // 有 packages: 段说明锁了真实依赖，不算空。
        if (trimmed.Contains("\npackages:", StringComparison.Ordinal)
            || trimmed.StartsWith("packages:", StringComparison.Ordinal))
        {
            return false;
        }

        // importers 下的每个条目都必须是空对象 {}（如 "  .: {}"），有非空值就不是空 lock。
        var sawImporter = false;
        foreach (var rawLine in trimmed.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("importers:", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.EndsWith(": {}", StringComparison.Ordinal))
            {
                sawImporter = true;
                continue;
            }

            // 只有已知头部键可以忽略；其它"键: 值"行说明锁了真实内容 → 不是空 lock。
            var isKnownHeader = KnownLockHeaders.Any(header => line.StartsWith(header, StringComparison.Ordinal));
            if (isKnownHeader || line.EndsWith(':'))
            {
                continue;
            }

            return false;
        }

        return sawImporter || trimmed.Contains("lockfileVersion", StringComparison.Ordinal);
    }

    /// <summary>读取 pnpm-workspace.yaml 里 allowBuilds 段列出的包名。</summary>
    public static IReadOnlyList<string> ReadAllowBuildsPackages(string workspaceText)
    {
        var packages = new List<string>();
        if (string.IsNullOrWhiteSpace(workspaceText))
        {
            return packages;
        }

        var insideSection = false;
        foreach (var rawLine in workspaceText.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.StartsWith("allowBuilds:", StringComparison.Ordinal))
            {
                var inline = rawLine["allowBuilds:".Length..].Trim();
                if (inline.Length > 2)
                {
                    packages.Add(inline.Trim('\'', '"', '[', ']', ' '));
                }

                insideSection = true;
                continue;
            }

            if (!insideSection)
            {
                continue;
            }

            // 缩进的行属于该段；回到顶格即结束。
            if (rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]))
            {
                break;
            }

            var entry = rawLine.Trim();
            if (entry.Length == 0 || entry.StartsWith('#'))
            {
                continue;
            }

            // 形如 `'@scope/pkg': true` 或 `pkg:`，取冒号前的键名。
            var name = entry;
            var colon = name.IndexOf(':');
            if (colon >= 0)
            {
                name = name[..colon];
            }

            name = name.Trim();
            if (name.Length > 0)
            {
                packages.Add(name.Trim('\'', '"'));
            }
        }

        return packages;
    }

    private static bool HasDependenciesProperty(string packageJsonText)
    {
        try
        {
            using var document = JsonDocument.Parse(packageJsonText);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("dependencies", out _);
        }
        catch (JsonException)
        {
            return true; // 解析不了就不报这条（另有 JSON 校验负责）
        }
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>去掉 pnpm-workspace.yaml 里的 allowBuilds 段（含其缩进块）。</summary>
    public static string StripAllowBuilds(string workspaceText)
    {
        if (string.IsNullOrWhiteSpace(workspaceText))
        {
            return workspaceText;
        }

        var kept = new List<string>();
        var skipping = false;
        foreach (var rawLine in workspaceText.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.StartsWith("allowBuilds:", StringComparison.Ordinal))
            {
                skipping = true;
                continue;
            }

            if (skipping)
            {
                // 段内所有缩进行都属于它；回到顶格（非空）即结束。
                if (rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]))
                {
                    skipping = false;
                }
                else
                {
                    continue;
                }
            }

            kept.Add(rawLine);
        }

        // 折叠因删除产生的多余空行（最多保留一个）。
        var text = string.Join('\n', kept);
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
        {
            text = text.Replace("\n\n\n", "\n\n");
        }

        return text;
    }

    /// <summary>给 package.json 补空的 dependencies 键（在 private 之后）。</summary>
    public static string EnsureDependenciesProperty(string packageJsonText)
    {
        if (HasDependenciesProperty(packageJsonText))
        {
            return packageJsonText;
        }

        const string marker = "\"private\": true";
        var index = packageJsonText.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return packageJsonText;
        }

        var afterValue = index + marker.Length;
        var probe = afterValue;
        while (probe < packageJsonText.Length && char.IsWhiteSpace(packageJsonText[probe]))
        {
            probe++;
        }

        // 已有尾逗号 → 插在逗号之后；private 是最后一个键 → 自己补逗号。
        if (probe < packageJsonText.Length && packageJsonText[probe] == ',')
        {
            var insertAt = probe + 1;
            return packageJsonText[..insertAt] + "\n  \"dependencies\": {}," + packageJsonText[insertAt..];
        }

        return packageJsonText[..afterValue] + ",\n  \"dependencies\": {}" + packageJsonText[afterValue..];
    }

    /// <summary>
    /// 一键复位：**先快照**（把涉及的文件原样拷进 <paramref name="snapshotDirectory"/>），
    /// 再移走空 lock、清 allowBuilds 残留、补 dependencies 键。
    /// 只碰这三个配置文件，**不动 node_modules、不动会话/凭据**。
    /// </summary>
    public static bool TryReset(
        string? profileDirectory,
        string snapshotDirectory,
        out IReadOnlyList<string> actions,
        out string? error)
    {
        var done = new List<string>();
        actions = done;
        error = null;
        if (string.IsNullOrWhiteSpace(profileDirectory) || !Directory.Exists(profileDirectory))
        {
            error = "profile 目录不存在。";
            return false;
        }

        var issues = Inspect(profileDirectory);
        if (issues.Count == 0)
        {
            done.Add("没有需要复位的内容。");
            return true;
        }

        try
        {
            Directory.CreateDirectory(snapshotDirectory);
            foreach (var issue in issues)
            {
                if (File.Exists(issue.Path))
                {
                    var backupName = Path.GetFileName(issue.Path) + ".bak";
                    File.Copy(issue.Path, Path.Combine(snapshotDirectory, backupName), overwrite: true);
                }
            }

            done.Add($"已快照 {issues.Count} 个文件 → {snapshotDirectory}");

            foreach (var issue in issues)
            {
                switch (issue.Kind)
                {
                    case IssueEmptyLock:
                        var lockTarget = Path.Combine(snapshotDirectory, "pnpm-lock.yaml.removed");
                        File.Move(issue.Path, lockTarget, overwrite: true);
                        done.Add("已移走空的 pnpm-lock.yaml（备份在快照目录）");
                        break;

                    case IssueAllowBuildsResidue:
                        var workspace = File.ReadAllText(issue.Path);
                        File.WriteAllText(issue.Path, StripAllowBuilds(workspace));
                        done.Add("已清除 pnpm-workspace.yaml 的 allowBuilds 残留");
                        break;

                    case IssueMissingDependencies:
                        var package = File.ReadAllText(issue.Path);
                        File.WriteAllText(issue.Path, EnsureDependenciesProperty(package));
                        done.Add("已补 package.json 的空 dependencies 键");
                        break;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return false;
        }
    }
}
