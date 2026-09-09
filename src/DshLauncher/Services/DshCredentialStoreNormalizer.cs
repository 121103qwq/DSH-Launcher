using System.IO;
using System.Text;

namespace DshLauncher.Services;

/// <summary>
/// 旧版 DSh 凭据文件（<c>version: 1</c> / <c>records:</c> / <c>refs:</c> 包装）→ 当前格式
/// （顶层键值映射）的保守转换。思路参考 121103qwq/DSH-Launcher v1.1.2 的
/// DshCredentialStoreNormalizer（该仓库无 LICENSE，此处为独立重写）。
///
/// 背景：当前 dsh-credentials-local 只接受「顶层键 → 字符串」的映射；把旧包装文件整文件
/// 复制/导入到新实例后，dsh 会在健康检查前报「凭据值必须为字符串」直接退出。
///
/// 铁律：
/// - 只转换**明确识别**为旧包装的文件（顶层 version + refs 映射）；
/// - refs 里出现嵌套结构、引号异常、额外顶层键等任何不确定形态 → **原样返回**，绝不猜测改写；
/// - records 段（浏览器会话授权等复合结构）无法映射为字符串 → 丢弃（下次启动由 dsh 重新生成）；
/// - 转换结果原子写入，失败只降级记录日志。
/// </summary>
public static class DshCredentialStoreNormalizer
{
    public const int MaximumBytes = 1024 * 1024;

    /// <summary>把旧包装文本转换为顶层键值映射；无需/无法转换时返回原文本。</summary>
    public static string Normalize(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content ?? string.Empty;
        }

        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var entries = new List<KeyValuePair<string, string>>();
        var sawVersion = false;
        var sawRefsHeader = false;
        var inRefs = false;
        var inSkippedBlock = false;
        int? refsIndent = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            if (indent == 0)
            {
                inRefs = false;
                inSkippedBlock = false;
                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    return content;
                }

                var key = UnquoteKey(line[..separator]);
                var value = line[(separator + 1)..].Trim();
                if (key == "version")
                {
                    sawVersion = true;
                    continue;
                }

                if (key == "refs")
                {
                    if (value.Length > 0)
                    {
                        return content;
                    }

                    sawRefsHeader = true;
                    inRefs = true;
                    refsIndent = null;
                    continue;
                }

                if (key == "records")
                {
                    // 复合结构，无法映射为字符串：跳过整段。
                    inSkippedBlock = true;
                    continue;
                }

                // 出现未知顶层键：不是可确认的旧包装 → 原样返回。
                return content;
            }

            if (inRefs)
            {
                refsIndent ??= indent;
                if (indent != refsIndent)
                {
                    return content;
                }

                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    return content;
                }

                var key = UnquoteKey(line[..separator]);
                if (key.Length == 0)
                {
                    return content;
                }

                var value = line[(separator + 1)..].Trim();
                if (value.Length == 0)
                {
                    // 空值（YAML null）不含凭据，跳过。
                    continue;
                }

                if (!TryUnquoteScalar(value, out var scalar))
                {
                    return content;
                }

                entries.Add(new KeyValuePair<string, string>(key, scalar));
                continue;
            }

            if (inSkippedBlock)
            {
                continue;
            }

            // 缩进行既不在 refs 也不在已知块里：形态无法确认 → 原样返回。
            return content;
        }

        if (!sawVersion || !sawRefsHeader || entries.Count == 0)
        {
            return content;
        }

        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(entry.Key).Append(": ").Append(QuoteScalar(entry.Value)).Append('\n');
        }

        var normalized = builder.ToString();
        return string.Equals(normalized, content, StringComparison.Ordinal) ? content : normalized;
    }

    /// <summary>就地转换文件；返回是否成功（含“无需转换”）。changed 表示确实改写了文件。</summary>
    public static bool TryNormalizeFile(string? path, out bool changed)
    {
        changed = false;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > MaximumBytes)
            {
                return false;
            }

            var original = File.ReadAllText(path, Encoding.UTF8);
            var normalized = Normalize(original);
            if (string.Equals(normalized, original, StringComparison.Ordinal))
            {
                return true;
            }

            WriteAtomically(path, normalized);
            changed = true;
            LauncherLog.Info("旧格式 DSh 凭据文件已转换为当前格式。", ErrorCodes.E1012, new { path });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LauncherLog.Warn("旧格式凭据文件转换失败，保持原样。", ErrorCodes.E1012,
                new { path, error = ex.Message });
            changed = false;
            return false;
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static string UnquoteKey(string value) => value.Trim().Trim('"', '\'');

    private static bool TryUnquoteScalar(string value, out string result)
    {
        result = value;
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var inner = value[1..^1];
            var builder = new StringBuilder(inner.Length);
            for (var index = 0; index < inner.Length; index++)
            {
                var current = inner[index];
                if (current == '\\' && index + 1 < inner.Length)
                {
                    index++;
                    builder.Append(inner[index] switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => inner[index]
                    });
                    continue;
                }

                builder.Append(current);
            }

            result = builder.ToString();
            return true;
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            result = value[1..^1].Replace("''", "'");
            return true;
        }

        // 未加引号：首尾是引号、含“ #”（YAML 注释）或含冒号+空格的形态不可确认 → 放弃。
        if (value[0] is '"' or '\'' || value[^1] is '"' or '\''
            || value.Contains(" #", StringComparison.Ordinal)
            || value.Contains(": ", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string QuoteScalar(string value) => "\"" + value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal) + "\"";
}
