using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DshLauncher.Services;

/// <summary>体检命中（**只描述位置，不携带凭据值**）。</summary>
/// <param name="FilePath">命中文件（绝对路径）。</param>
/// <param name="Line">1 起始的行号。</param>
/// <param name="Column">1 起始的列号。</param>
/// <param name="PatternName">命中的模式名（如「OpenAI 风格密钥」），用于用户理解风险类型。</param>
/// <param name="RedactedPreview">仅当显式开启脱敏预览时非空；形如 <c>sk-••••••12ab</c>（≤7 个真实字符）。</param>
public sealed record CredentialAuditHit(
    string FilePath,
    int Line,
    int Column,
    string PatternName,
    string? RedactedPreview);

/// <summary>体检清单条目：疑似凭据文件或配置文件的存在性/大小/修改时间与权限提示。</summary>
public sealed record CredentialAuditFileInfo(
    string FilePath,
    string Kind,
    long Size,
    DateTime LastWriteTimeUtc,
    bool ReadOnly,
    IReadOnlyList<string> Flags);

/// <summary>体检报告（纯内存对象；不含任何凭据原值）。</summary>
public sealed record CredentialAuditReport(
    IReadOnlyList<CredentialAuditHit> Hits,
    IReadOnlyList<CredentialAuditFileInfo> Files,
    int FilesScanned,
    bool RedactedPreviewsIncluded,
    IReadOnlyList<string> Notes,
    DateTime CompletedAtUtc);

/// <summary>
/// #20 安全体检（Launcher 侧被动）：扫描本机 DSH_HOME 里**已知的**配置/凭据文件，
/// 报告「疑似密钥出现的**位置**」与凭据文件的存在性/大小/时间/只读标志。
///
/// **安全契约（硬约束，改动前先看这里）**：
/// 1. 默认（Q5 (i)）**只输出位置**：文件、行号、列号、模式名；凭据的值**不进入**任何返回值字段。
/// 2. 只有显式 <see cref="CredentialAuditRequest.IncludeRedactedPreview"/> 为 true 时才生成**脱敏预览**，
///    且预览函数最多保留前 3 + 后 4 个字符，中间一律替换为 <c>•</c>。
/// 3. 本类**不写任何文件、不写日志、不联网**（没有 logger/HttpClient 依赖）；导出物由调用方负责（Q6）。
/// 4. 扫描范围有硬上限（文件数 / 单文件字节 / 单行长度），避免误扫 node_modules 或超大文件。
/// </summary>
public static class CredentialAuditService
{
    /// <summary>体检范围（默认值刻意保守）。</summary>
    public sealed record CredentialAuditRequest(
        string DshHome,
        string? ProfileName = null,
        bool IncludeRedactedPreview = false,
        int MaxFiles = 200,
        long MaxFileBytes = 1024 * 1024,
        int MaxLineLength = 8192);

    private sealed record Pattern(string Name, Regex Regex);

    // 只认「高置信度」模式：宁可少报，不要用宽泛规则把正常文本刷成风险项。
    private static readonly Pattern[] Patterns =
    {
        new("OpenAI 风格密钥（sk-）", new Regex(@"\bsk-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled)),
        new("GitHub 令牌", new Regex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.Compiled)),
        new("AWS Access Key ID", new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        new("Slack 令牌", new Regex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled)),
        new("私钥文件内容", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled)),
        new("Anthropic 风格密钥", new Regex(@"\bsk-ant-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled)),
        new("疑似键值形式的凭据", new Regex(
            @"(?i)[\u0027\u0022]?\b(?:api[_-]?key|apikey|secret[_-]?key|access[_-]?token|auth[_-]?token|password|passwd)\b[\u0027\u0022]?\s*[:=]\s*[\u0027\u0022]?[A-Za-z0-9_\-\.\/\+]{12,}",
            RegexOptions.Compiled))
    };

    /// <summary>疑似凭据文件（存在性本身就是需要用户知道的事实）。</summary>
    private static readonly string[] CredentialFileNames =
    {
        ".credentials.yaml",
        ".credentials.yml",
        "credentials.json",
        "auth.json",
        ".env",
        ".env.local"
    };

    /// <summary>需要一并扫描的配置文件（相对 DSH_HOME）。不含 node_modules 与 sessions。</summary>
    private static readonly string[] HomeConfigFiles =
    {
        "settings.yaml",
        "settings.yml",
        "settings.json"
    };

    private static readonly string[] ProfileConfigExtensions = { ".json", ".yaml", ".yml", ".env", ".txt", ".toml" };

    /// <summary>执行体检。纯读取；任何 IO 异常都被吞掉并记录到 Notes，绝不抛出。</summary>
    public static CredentialAuditReport Run(CredentialAuditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var notes = new List<string>();
        var files = new List<CredentialAuditFileInfo>();
        var hits = new List<CredentialAuditHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.DshHome) || !Directory.Exists(request.DshHome))
        {
            notes.Add("DSH_HOME 不存在，无法体检。");
            return new CredentialAuditReport(hits, files, 0, request.IncludeRedactedPreview, notes, DateTime.UtcNow);
        }

        var home = Path.GetFullPath(request.DshHome);

        // 1) 凭据/配置文件清单（只取元数据；不读内容也能给出结论）
        foreach (var name in CredentialFileNames)
        {
            var path = Path.Combine(home, name);
            if (File.Exists(path))
            {
                files.Add(DescribeCredentialFile(path, "疑似凭据文件"));
            }
        }

        foreach (var name in HomeConfigFiles)
        {
            var path = Path.Combine(home, name);
            if (File.Exists(path))
            {
                files.Add(DescribeCredentialFile(path, "配置文件"));
            }
        }

        // 2) 扫描目标：DSH_HOME 顶层配置 + 指定 profile 的配置（跳过 node_modules / sessions / storages）
        var targets = new List<string>();
        foreach (var name in HomeConfigFiles)
        {
            targets.Add(Path.Combine(home, name));
        }

        targets.Add(Path.Combine(home, ".credentials.yaml"));
        targets.Add(Path.Combine(home, ".credentials.yml"));

        var profileNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.ProfileName))
        {
            profileNames.Add(request.ProfileName!);
        }
        else
        {
            var profilesRoot = Path.Combine(home, "profiles");
            if (Directory.Exists(profilesRoot))
            {
                try
                {
                    profileNames.AddRange(Directory.EnumerateDirectories(profilesRoot)
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name!));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    notes.Add($"读取 profiles 目录失败：{ex.Message}");
                }
            }
        }

        foreach (var profileName in profileNames)
        {
            var profileDirectory = Path.Combine(home, "profiles", profileName);
            if (!Directory.Exists(profileDirectory))
            {
                notes.Add($"未找到 profile 目录：{profileName}");
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(profileDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (ProfileConfigExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    {
                        targets.Add(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"枚举 profile「{profileName}」失败：{ex.Message}");
            }
        }

        var scanned = 0;
        foreach (var target in targets)
        {
            if (scanned >= request.MaxFiles)
            {
                notes.Add($"已达扫描上限（{request.MaxFiles} 个文件），其余未扫描。");
                break;
            }

            var full = Path.GetFullPath(target);
            if (!seen.Add(full) || !File.Exists(full))
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(full).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (size > request.MaxFileBytes)
            {
                notes.Add($"{Path.GetFileName(full)} 超过单文件上限（{request.MaxFileBytes / 1024} KB），已跳过扫描。");
                continue;
            }

            scanned++;
            ScanFile(full, request, hits, notes);
        }

        if (hits.Count == 0)
        {
            notes.Add("未在扫描范围内发现高置信度的凭据模式。");
        }

        return new CredentialAuditReport(
            hits,
            files,
            scanned,
            request.IncludeRedactedPreview,
            notes,
            DateTime.UtcNow);
    }

    private static CredentialAuditFileInfo DescribeCredentialFile(string path, string kind)
    {
        var flags = new List<string>();
        long size = 0;
        var lastWrite = DateTime.MinValue;
        var readOnly = false;
        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            lastWrite = info.LastWriteTimeUtc;
            readOnly = info.IsReadOnly;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            flags.Add("无法读取文件元数据：" + ex.Message);
        }

        if (size == 0)
        {
            flags.Add("文件为空");
        }

        if (!readOnly)
        {
            flags.Add("可写（建议只在需要时保留；整合包导出与快照都不会包含此文件）");
        }

        return new CredentialAuditFileInfo(path, kind, size, lastWrite, readOnly, flags);
    }

    private static void ScanFile(
        string path,
        CredentialAuditRequest request,
        List<CredentialAuditHit> hits,
        List<string> notes)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"{Path.GetFileName(path)} 读取失败：{ex.Message}");
            return;
        }

        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed.Length > request.MaxLineLength)
            {
                continue;
            }

            foreach (var pattern in Patterns)
            {
                foreach (Match match in pattern.Regex.Matches(trimmed))
                {
                    if (!match.Success)
                    {
                        continue;
                    }

                    // 关键安全点：默认分支只取位置，不把 match.Value 放进任何字段。
                    var preview = request.IncludeRedactedPreview ? BuildRedactedPreview(match.Value) : null;
                    hits.Add(new CredentialAuditHit(path, lineNumber, match.Index + 1, pattern.Name, preview));
                }
            }
        }
    }

    /// <summary>
    /// 命中项的**展示文本**：只含位置与模式名；有脱敏预览时追加预览（≤13 字符）。
    /// 该函数只接收 <see cref="CredentialAuditHit"/>（其本身不携带原值），因此不可能泄漏完整凭据。
    /// </summary>
    public static string DescribeHit(CredentialAuditHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var location = $"{hit.FilePath}:{hit.Line}:{hit.Column}";
        return hit.RedactedPreview is { Length: > 0 } preview
            ? $"{location} · {hit.PatternName} · 预览 {preview}"
            : $"{location} · {hit.PatternName}";
    }

    /// <summary>凭据/配置文件清单的展示文本。</summary>
    public static string DescribeFileInfo(CredentialAuditFileInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var size = info.Size < 1024 ? $"{info.Size} B" : $"{info.Size / 1024.0:F1} KB";
        var flags = info.Flags.Count > 0 ? "（" + string.Join("；", info.Flags) + "）" : string.Empty;
        return $"{info.Kind}：{info.FilePath} · {size} · 修改于 {info.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}{flags}";
    }

    /// <summary>一行摘要（用于页面顶部与日志无关的纯展示）。</summary>
    public static string Summarize(CredentialAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return $"疑似凭据命中 {report.Hits.Count} 处 · 凭据/配置文件 {report.Files.Count} 个 · 已扫描 {report.FilesScanned} 个文件"
            + (report.RedactedPreviewsIncluded ? " · 含脱敏预览" : " · 未含任何前缀/片段");
    }

    /// <summary>
    /// 脱敏预览：最多保留前 3 + 后 4 个字符，中间用 <c>•</c> 遮蔽（最多 6 个）。
    /// 短值一律全遮蔽。**除本函数外，服务内任何地方都不得引用 match.Value。**
    /// </summary>
    public static string BuildRedactedPreview(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new string('•', 6);
        }

        if (value.Length <= 8)
        {
            return new string('•', 6);
        }

        var head = value[..3];
        var tail = value[^4..];
        var masked = Math.Min(6, Math.Max(1, value.Length - head.Length - tail.Length));
        return head + new string('•', masked) + tail;
    }
}
