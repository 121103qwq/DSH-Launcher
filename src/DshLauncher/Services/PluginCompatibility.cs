using System.IO;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// 插件与当前实例 DSh 运行时的兼容性检查（借鉴上游 v1.1.2 F-06 的思路，独立实现）：
/// 读取插件清单的 <c>peerDependencies</c> 里 <c>@deepseek-ai/*</c> 核心包，与实例里实际
/// 解析到的版本做 npm 语义化范围匹配；不兼容时返回可操作提示（安装后可能让实例起不来）。
/// 只做“能判定才报警”的克制检查：解析不了的范围/找不到的版本一律放行，避免误报。
/// </summary>
internal static class PluginCompatibility
{
    internal sealed record Issue(string Package, string Required, string Installed);

    private const string DeepSeekScope = "@deepseek-ai/";

    /// <summary>检查清单里声明的核心 peerDependencies；返回不兼容项（无则空）。</summary>
    public static IReadOnlyList<Issue> Check(string manifestJson, ManagerInstance instance)
    {
        var issues = new List<Issue>();
        if (string.IsNullOrWhiteSpace(manifestJson) || instance is null)
        {
            return issues;
        }

        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            if (!document.RootElement.TryGetProperty("peerDependencies", out var peers)
                || peers.ValueKind != JsonValueKind.Object)
            {
                return issues;
            }

            foreach (var peer in peers.EnumerateObject())
            {
                if (!peer.Name.StartsWith(DeepSeekScope, StringComparison.OrdinalIgnoreCase)
                    || peer.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var range = peer.Value.GetString();
                if (string.IsNullOrWhiteSpace(range))
                {
                    continue;
                }

                var installed = ResolveInstalledVersion(instance, peer.Name);
                if (string.IsNullOrWhiteSpace(installed))
                {
                    // 实例里没有这个包 / 读不到版本：无法判定，不报警。
                    continue;
                }

                if (!Satisfies(installed!, range!))
                {
                    issues.Add(new Issue(peer.Name, range!.Trim(), installed!.Trim()));
                }
            }
        }
        catch (JsonException)
        {
            // 清单解析失败由调用方的 Rejected 分支处理，这里不重复报错。
        }

        return issues;
    }

    /// <summary>把不兼容项压成一行可读文本。</summary>
    public static string Describe(IReadOnlyList<Issue> issues) =>
        string.Join("；", issues.Select(issue => $"{issue.Package} 需要 {issue.Required}，当前为 {issue.Installed}"));

    /// <summary>在实例的 DSH_HOME / 运行目录里解析某个包的实际版本；找不到返回 null。</summary>
    public static string? ResolveInstalledVersion(ManagerInstance instance, string packageName)
    {
        if (instance is null || string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        var segments = packageName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        foreach (var nodeModules in CandidateNodeModules(instance))
        {
            try
            {
                var manifest = Path.Combine(new[] { nodeModules }.Concat(segments).Append("package.json").ToArray());
                if (!File.Exists(manifest))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.TryGetProperty("version", out var version)
                    && version.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(version.GetString()))
                {
                    return version.GetString();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
            {
                // 单个候选读失败不影响其它候选。
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateNodeModules(ManagerInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.DshHome))
        {
            yield return Path.Combine(instance.DshHome, "profiles", "web", "node_modules");
            yield return Path.Combine(instance.DshHome, "profiles", "node_modules");
        }

        if (!string.IsNullOrWhiteSpace(instance.RootPath))
        {
            yield return Path.Combine(instance.RootPath, "node_modules");
            yield return Path.Combine(instance.RootPath, "node_modules", "@deepseek-ai", "dsh", "node_modules");
        }
    }

    // ---------------------------------------------------------------------
    // npm 语义化范围匹配（含预发布规则）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 判断 <paramref name="version"/> 是否满足 <paramref name="range"/>。
    /// 支持 <c>||</c>、空格/逗号分隔的多约束、<c>^</c>、<c>~</c>、比较符、部分版本与通配符，
    /// 并遵循 npm 的预发布规则（带预发布的版本只被“同 core 元组且含预发布”的比较器接受）。
    /// 无法解析时一律返回 true（保守放行，避免误报）。
    /// </summary>
    internal static bool Satisfies(string version, string range)
    {
        var parsed = ParseVersion(version);
        if (parsed is null || string.IsNullOrWhiteSpace(range))
        {
            return true;
        }

        foreach (var alternative in range.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (SatisfiesAlternative(parsed, alternative.Trim()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SatisfiesAlternative(SemVersion version, string alternative)
    {
        if (alternative.Length == 0)
        {
            return false;
        }

        if (alternative is "*" or "x" or "X")
        {
            // npm：`*` 不匹配预发布版本。
            return version.PreRelease is null;
        }

        if (alternative.Contains(" - ", StringComparison.Ordinal))
        {
            // 连字符范围较少见于 peerDependencies；不判定，保守放行。
            return true;
        }

        var tokens = alternative
            .Replace(',', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bounds = new List<Func<SemVersion, bool>>();
        var prereleaseTuples = new List<(int Major, int Minor, int Patch)>();

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var operation = string.Empty;
            if (token is ">" or ">=" or "<" or "<=" or "=" or "^" or "~")
            {
                if (++index >= tokens.Length)
                {
                    return true; // 畸形范围：保守放行
                }

                operation = token;
                token = tokens[index];
            }
            else
            {
                foreach (var candidate in new[] { ">=", "<=", ">", "<", "=", "^", "~" })
                {
                    if (token.StartsWith(candidate, StringComparison.Ordinal))
                    {
                        operation = candidate;
                        token = token[candidate.Length..];
                        break;
                    }
                }
            }

            if (!TryParseRangeToken(token, out var bound, out var specifiedParts, out var wildcard))
            {
                return true; // 解析不了：保守放行
            }

            if (bound.PreRelease is not null)
            {
                prereleaseTuples.Add((bound.Major, bound.Minor, bound.Patch));
            }

            switch (operation)
            {
                case ">=":
                    bounds.Add(candidate => candidate.CompareTo(bound) >= 0);
                    break;
                case ">":
                    bounds.Add(candidate => candidate.CompareTo(bound) > 0);
                    break;
                case "<=":
                    bounds.Add(candidate => candidate.CompareTo(bound) <= 0);
                    break;
                case "<":
                    bounds.Add(candidate => candidate.CompareTo(bound) < 0);
                    break;
                case "=":
                    bounds.Add(candidate => candidate.CompareTo(bound) == 0);
                    break;
                case "^":
                {
                    var upper = CaretUpperBound(bound, specifiedParts);
                    bounds.Add(candidate => candidate.CompareTo(bound) >= 0 && candidate.CompareTo(upper) < 0);
                    break;
                }
                case "~":
                {
                    var upper = TildeUpperBound(bound, specifiedParts);
                    bounds.Add(candidate => candidate.CompareTo(bound) >= 0 && candidate.CompareTo(upper) < 0);
                    break;
                }
                default:
                {
                    if (wildcard)
                    {
                        var upper = WildcardUpperBound(bound, specifiedParts);
                        bounds.Add(candidate => candidate.CompareTo(bound) >= 0 && candidate.CompareTo(upper) < 0);
                    }
                    else
                    {
                        bounds.Add(candidate => candidate.CompareTo(bound) == 0);
                    }

                    break;
                }
            }
        }

        if (bounds.Count == 0 || !bounds.All(check => check(version)))
        {
            return false;
        }

        if (version.PreRelease is null)
        {
            return true;
        }

        // npm 预发布规则：带预发布的版本只有在“同一 core 元组且带预发布”的比较器存在时才被接受。
        return prereleaseTuples.Any(tuple =>
            tuple.Major == version.Major && tuple.Minor == version.Minor && tuple.Patch == version.Patch);
    }

    private static SemVersion CaretUpperBound(SemVersion lower, int specifiedParts)
    {
        if (lower.Major > 0)
        {
            return new SemVersion(lower.Major + 1, 0, 0, null);
        }

        if (lower.Minor > 0)
        {
            return new SemVersion(0, lower.Minor + 1, 0, null);
        }

        if (specifiedParts >= 3)
        {
            return new SemVersion(0, 0, lower.Patch + 1, null);
        }

        if (specifiedParts == 2)
        {
            return new SemVersion(0, 1, 0, null);
        }

        return new SemVersion(1, 0, 0, null);
    }

    private static SemVersion TildeUpperBound(SemVersion lower, int specifiedParts) =>
        specifiedParts >= 2
            ? new SemVersion(lower.Major, lower.Minor + 1, 0, null)
            : new SemVersion(lower.Major + 1, 0, 0, null);

    private static SemVersion WildcardUpperBound(SemVersion lower, int specifiedParts) =>
        specifiedParts >= 2
            ? new SemVersion(lower.Major, lower.Minor + 1, 0, null)
            : new SemVersion(lower.Major + 1, 0, 0, null);

    private static bool TryParseRangeToken(
        string token,
        out SemVersion version,
        out int specifiedParts,
        out bool wildcard)
    {
        version = new SemVersion(0, 0, 0, null);
        specifiedParts = 0;
        wildcard = false;

        var text = token.Trim().TrimStart('v', 'V');
        if (text.Length == 0)
        {
            return false;
        }

        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var dash = text.IndexOf('-');
        var preRelease = dash >= 0 ? text[(dash + 1)..] : null;
        var core = (dash >= 0 ? text[..dash] : text).Split('.');
        var numbers = new int[3];
        for (var index = 0; index < core.Length && index < 3; index++)
        {
            var part = core[index].Trim();
            if (part is "*" or "x" or "X" || part.Length == 0)
            {
                wildcard = true;
                break;
            }

            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0 || !int.TryParse(digits, out numbers[index]))
            {
                return false;
            }

            specifiedParts++;
        }

        if (specifiedParts == 0)
        {
            return false;
        }

        version = new SemVersion(numbers[0], numbers[1], numbers[2], string.IsNullOrWhiteSpace(preRelease) ? null : preRelease);
        return true;
    }

    /// <summary>
    /// 比较两个版本号（npm semver，含预发布顺序：预发布 &lt; 正式版）。
    /// 任一无法解析时返回 0（调用方自行决定降级/中性处理）。
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var first = ParseVersion((left ?? string.Empty).Trim().TrimStart('v', 'V'));
        var second = ParseVersion((right ?? string.Empty).Trim().TrimStart('v', 'V'));
        return first is null || second is null ? 0 : first.CompareTo(second);
    }

    private static SemVersion? ParseVersion(string value)
    {
        var text = value.Trim().TrimStart('v', 'V');
        if (text.Length == 0)
        {
            return null;
        }

        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var dash = text.IndexOf('-');
        var preRelease = dash >= 0 ? text[(dash + 1)..] : null;
        var core = (dash >= 0 ? text[..dash] : text).Split('.');
        var numbers = new int[3];
        for (var index = 0; index < 3; index++)
        {
            if (index >= core.Length)
            {
                numbers[index] = 0;
                continue;
            }

            var digits = new string(core[index].TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0 || !int.TryParse(digits, out numbers[index]))
            {
                return null;
            }
        }

        return new SemVersion(numbers[0], numbers[1], numbers[2], string.IsNullOrWhiteSpace(preRelease) ? null : preRelease);
    }

    private sealed record SemVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemVersion>
    {
        public int CompareTo(SemVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            var diff = Major.CompareTo(other.Major);
            if (diff != 0)
            {
                return diff;
            }

            diff = Minor.CompareTo(other.Minor);
            if (diff != 0)
            {
                return diff;
            }

            diff = Patch.CompareTo(other.Patch);
            if (diff != 0)
            {
                return diff;
            }

            if (PreRelease is null && other.PreRelease is null)
            {
                return 0;
            }

            if (PreRelease is null)
            {
                return 1; // 正式版 > 预发布版
            }

            return other.PreRelease is null ? -1 : ComparePreRelease(PreRelease, other.PreRelease);
        }

        private static int ComparePreRelease(string left, string right)
        {
            var leftParts = left.Split('.');
            var rightParts = right.Split('.');
            var length = Math.Max(leftParts.Length, rightParts.Length);
            for (var index = 0; index < length; index++)
            {
                if (index >= leftParts.Length)
                {
                    return -1;
                }

                if (index >= rightParts.Length)
                {
                    return 1;
                }

                var leftNumeric = int.TryParse(leftParts[index], out var leftNumber);
                var rightNumeric = int.TryParse(rightParts[index], out var rightNumber);
                int diff;
                if (leftNumeric && rightNumeric)
                {
                    diff = leftNumber.CompareTo(rightNumber);
                }
                else if (leftNumeric)
                {
                    diff = -1; // 数字标识符 < 字母标识符
                }
                else if (rightNumeric)
                {
                    diff = 1;
                }
                else
                {
                    diff = string.CompareOrdinal(leftParts[index], rightParts[index]);
                }

                if (diff != 0)
                {
                    return diff;
                }
            }

            return 0;
        }
    }
}
