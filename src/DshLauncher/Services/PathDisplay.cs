namespace DshLauncher.Services;

/// <summary>
/// 路径显示工具：链接式路径只显示尾部，完整值放 Tooltip 与复制按钮
/// （见 docs/UI-DESIGN.md「链接」一节）。前后各窗口共用，避免各写一份。
/// </summary>
internal static class PathDisplay
{
    public static string Tail(string? path, int maxChars = 42) =>
        string.IsNullOrEmpty(path) || path.Length <= maxChars
            ? path ?? string.Empty
            : "…" + path[^(maxChars - 1)..];
}
