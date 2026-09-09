namespace DshLauncher.Models;

/// <summary>
/// DSH 核心 bundle（不是用户插件）。插件计数、插件管理列表、插件 × 实例矩阵、安全模式
/// 隔离 profile 共用这一份口径，避免各处再抄一遍名字导致统计/展示不一致。
/// </summary>
public static class DshCoreBundles
{
    public const string Base = "@deepseek-ai/dsh-base";

    public const string WebApp = "@deepseek-ai/dsh-web-app";

    /// <summary>官方 web profile 的最小核心集合（顺序固定：base → web-app）。</summary>
    public static readonly IReadOnlyList<string> Minimal = new[] { Base, WebApp };

    public static bool IsCore(string? packageName) =>
        string.Equals(packageName, Base, StringComparison.OrdinalIgnoreCase)
        || string.Equals(packageName, WebApp, StringComparison.OrdinalIgnoreCase);
}
