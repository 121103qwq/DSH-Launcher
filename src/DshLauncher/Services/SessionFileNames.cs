using System.IO;

namespace DshLauncher.Services;

/// <summary>
/// dsh 会话文件命名规则（与 dsh 的 <c>CANONICAL_LOG_FILENAME</c> 同源，work-log/51）：
/// <list type="bullet">
/// <item>v0 → <c>session.jsonl[.zstd]</c>（不带 v0 标签；带标签的名字 dsh 不认）</item>
/// <item>v1+ → <c>session.v&lt;N&gt;.jsonl[.zstd]</c>（小写 v、无前导零）</item>
/// </list>
/// 文件名里的版本必须等于会话头的 <c>version</c>，否则 dsh 会直接报错
/// （"generation filename identifies vN, but its header identifies vM"）。
/// </summary>
internal static class SessionFileNames
{
    private const string Stem = "session";
    private const string JsonlSuffix = ".jsonl";
    private const string ZstdSuffix = ".zstd";

    /// <summary>解析 canonical 会话文件名，取出版本与编码（非 canonical 一律返回 false）。</summary>
    public static bool TryParse(string? fileName, out int version, out bool compressed)
    {
        version = 0;
        compressed = false;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = Path.GetFileName(fileName.Trim());
        if (name.EndsWith(ZstdSuffix, StringComparison.OrdinalIgnoreCase))
        {
            compressed = true;
            name = name[..^ZstdSuffix.Length];
        }

        if (!name.EndsWith(JsonlSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = name[..^JsonlSuffix.Length];
        if (!stem.StartsWith(Stem, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tail = stem[Stem.Length..];
        if (tail.Length == 0)
        {
            return true;   // session.jsonl → v0
        }

        // 只接受 ".v123"：小写 v、无前导零、正整数（v0 标签不是 canonical）。
        if (tail.Length < 3
            || tail[0] != '.'
            || tail[1] != 'v'
            || tail[2] == '0')
        {
            return false;
        }

        foreach (var character in tail.AsSpan(2))
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(tail.AsSpan(2), out version) && version > 0;
    }

    /// <summary>
    /// 目标实例的会话编码：跟随该实例已有会话（dsh 同一 sessions 根不允许混用编码，
    /// 否则 dsh 会抛 encodingMismatch）；一个都没有时用 dsh 默认值 zstd。
    /// </summary>
    public static bool ResolveCompression(string sessionsRoot)
    {
        if (string.IsNullOrWhiteSpace(sessionsRoot) || !Directory.Exists(sessionsRoot))
        {
            return true;
        }

        var sawPlain = false;
        try
        {
            foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*", SearchOption.AllDirectories))
            {
                if (!TryParse(Path.GetFileName(path), out _, out var compressed))
                {
                    continue;
                }

                if (compressed)
                {
                    // 只要有 zstd（dsh 默认）就按 zstd；根内不允许混用，不确定时也不要写编码。
                    return true;
                }

                sawPlain = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到时按 dsh 默认编码处理。
        }

        return !sawPlain;
    }

    /// <summary>
    /// 该 DSh 是否支持带版本号的会话代际（0.1.5+ 的 format catalog）。
    /// 优先用自身证据（已有带版本会话 / 运行时存在 catalog 包），版本号只做兑底。
    /// </summary>
    public static bool SupportsVersionedGenerations(
        string? packageRoot,
        string? detectedVersion,
        string? sessionsRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(sessionsRoot) && Directory.Exists(sessionsRoot))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*", SearchOption.AllDirectories))
                {
                    if (TryParse(Path.GetFileName(path), out var version, out _) && version > 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到会话目录时继续看运行时包。
            }
        }

        if (!string.IsNullOrWhiteSpace(packageRoot))
        {
            foreach (var package in new[]
                     {
                         Path.Combine(packageRoot, "node_modules", "@deepseek-ai", "dsh-session-format-catalog"),
                         Path.Combine(packageRoot, "node_modules", "@deepseek-ai", "dsh", "node_modules",
                             "@deepseek-ai", "dsh-session-format-catalog")
                     })
            {
                try
                {
                    if (Directory.Exists(package))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个路径不可读时继续下一个候选。
                }
            }
        }

        // 版本兑底：>= 0.1.5-0 视为带 format catalog。
        // 注意用直接比较而不是 Satisfies：npm 的预发布规则不会让 0.1.6-rc.1 命中 “>=0.1.5-0”。
        return !string.IsNullOrWhiteSpace(detectedVersion)
            && PluginCompatibility.Compare(detectedVersion.Trim().TrimStart('v', 'V'), "0.1.5-0") >= 0;
    }

    /// <summary>目录里最高的一代（没有 canonical 会话文件时返回 -1）。</summary>
    public static int HighestGeneration(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return -1;
        }

        var highest = -1;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if (TryParse(Path.GetFileName(path), out var version, out _) && version > highest)
                {
                    highest = version;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return highest;
        }

        return highest;
    }

    /// <summary>该目录里是否已有 canonical 会话文件（同一会话的不同代际共用一个目录）。</summary>
    public static bool DirectoryHasSessionFile(string directory) => HighestGeneration(directory) >= 0;

    /// <summary>按版本与编码拼 canonical 文件名。</summary>
    public static string Build(int version, bool compressed)
    {
        var stem = version <= 0 ? Stem : $"{Stem}.v{version}";
        return compressed ? stem + JsonlSuffix + ZstdSuffix : stem + JsonlSuffix;
    }

    /// <summary>
    /// 会话目录键：相对路径去掉文件名（同一会话的不同代际在同一目录，是同一个身份）。
    /// 输入/输出都用 '/' 分隔；根目录下的裸文件名返回空串。
    /// </summary>
    public static string DirectoryKey(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }
}
