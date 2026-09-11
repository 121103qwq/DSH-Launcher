namespace DshLauncher.Services;

/// <summary>
/// 页面探针/证据里记录的"页面错误文本"的整理规则。
///
/// 事故教训（work-log/71）：原先这里把文本截断到 **120 字符**，正好切在单词中间
/// （`… client-modules: bun` ← 其实是 `bundle script …` 的开头），排查时被误导到
/// "缺 bun 运行时"这个完全错误的方向。页面错误文本往往是**唯一用户可见证据**，
/// 必须保留到足以定位问题的长度，并且**明确标注**是否被截断。
/// </summary>
public static class PageErrorText
{
    /// <summary>证据里保留的最大字符数（远超单个错误信息，又不会让台账爆掉）。</summary>
    public const int DefaultMaximumLength = 4000;

    /// <summary>
    /// 归一化页面错误文本用于记录：去首尾空白、保留换行，超长时截断并**显式标注**
    /// 截断位置与原始长度。
    /// </summary>
    public static string ForEvidence(string? text, int maximumLength = DefaultMaximumLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var limit = maximumLength <= 0 ? DefaultMaximumLength : maximumLength;
        if (normalized.Length <= limit)
        {
            return normalized;
        }

        return $"{normalized[..limit]}…（已截断，原文共 {normalized.Length} 字符）";
    }

    /// <summary>页面探针失败时给用户/台账看的简述（同样不切在单词中间）。</summary>
    public static string DescribeProbeFailure(string? text, int maximumLength = DefaultMaximumLength) =>
        $"页面出现错误提示：{ForEvidence(text, maximumLength)}";
}
