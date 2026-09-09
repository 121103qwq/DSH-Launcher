using System.Text;

namespace DshLauncher.Services;

/// <summary>
/// 对话框文本收敛：Win32 MessageBox 按内容自适应且没有滚动条，超长或超宽文本会把窗口撑出
/// 屏幕、按钮落到屏外（见 work-log/47）。这里统一做「限行宽 + 限总长」，完整内容留在
/// 「运行状况 → 运行日志」。把原始日志/堆栈塞进 MessageBox 的地方都应先过这里。
/// </summary>
internal static class DialogText
{
    public const int DefaultMaxChars = 1200;
    public const int DefaultMaxLineChars = 120;

    public static string ForMessageBox(
        string? text,
        int maxChars = DefaultMaxChars,
        int maxLineChars = DefaultMaxLineChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var builder = new StringBuilder(Math.Min(normalized.Length + 8, maxChars + 64));
        var truncated = false;

        foreach (var rawLine in normalized.Split('\n'))
        {
            if (truncated)
            {
                break;
            }

            var line = rawLine.TrimEnd();
            if (line.Length > maxLineChars)
            {
                // 堆栈/路径这类没有空格的长行 MessageBox 不会自动折，按固定宽度硬折。
                for (var offset = 0; offset < line.Length; offset += maxLineChars)
                {
                    var chunk = line.Substring(offset, Math.Min(maxLineChars, line.Length - offset));
                    if (!TryAppendLine(builder, chunk, maxChars))
                    {
                        truncated = true;
                        break;
                    }
                }
            }
            else if (!TryAppendLine(builder, line, maxChars))
            {
                truncated = true;
            }
        }

        if (truncated)
        {
            builder.AppendLine();
            builder.Append("…（已截断，完整内容见「运行状况 → 运行日志」）");
        }

        return builder.ToString().TrimEnd();
    }

    private static bool TryAppendLine(StringBuilder builder, string line, int maxChars)
    {
        if (builder.Length + line.Length + Environment.NewLine.Length > maxChars)
        {
            return false;
        }

        builder.AppendLine(line);
        return true;
    }
}
