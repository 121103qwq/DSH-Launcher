using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DshLauncher.Services;

/// <summary>实例的**呈现面**（dsh 生态里的 surface）：决定"这个实例该怎么打开"。</summary>
public enum PresentationSurface
{
    /// <summary>无法判定（不是错误；界面显示"未识别"，不提供专用打开方式）。</summary>
    Unknown,

    /// <summary>浏览器面（`web` profile / `@deepseek-ai/dsh-web-app`）。</summary>
    Web,

    /// <summary>终端面（`dsh-tui` profile / `@deepseek-harness-tui/*`），用 Windows Terminal 打开。</summary>
    Terminal,

    /// <summary>无界面面（headless / sdk / acp），不提供打开按钮。</summary>
    Headless
}

/// <summary>
/// 呈现面识别与终端启动参数构造（work-log/80）。
///
/// 判据（按 dsh/生态的公开事实，不猜）：
///   * profile 名 `web` 或 bundles 含 `@deepseek-ai/dsh-web-app` → 浏览器面；
///   * profile 名 `dsh-tui`（或以 `dsh-tui` 开头）或 bundles 含 `dsh-tui` / `@deepseek-harness-tui/` → 终端面；
///   * bundles 含 `dsh-headless` / `dsh-sdk-` / `dsh-acp-app` → 无界面面；
///   * 其余 → **未识别**（界面明确显示"未识别"，不硬套任何一种打开方式）。
///
/// 本类为纯逻辑（无 IO、无进程启动），便于自测；真正的进程启动在调用方。
/// </summary>
public static class PresentationSurfaceService
{
    public const string WebProfileName = "web";
    public const string TerminalProfileName = "dsh-tui";

    /// <summary>判定呈现面：bundles 优先（更准确），profile 名兜底。</summary>
    public static PresentationSurface Detect(string? profileName, IEnumerable<string>? bundles)
    {
        var list = bundles?.Where(item => !string.IsNullOrWhiteSpace(item)).ToList() ?? new List<string>();

        if (list.Any(IsWebBundle))
        {
            return PresentationSurface.Web;
        }

        if (list.Any(IsTerminalBundle))
        {
            return PresentationSurface.Terminal;
        }

        if (list.Any(IsHeadlessBundle))
        {
            return PresentationSurface.Headless;
        }

        var name = profileName?.Trim() ?? string.Empty;
        if (string.Equals(name, WebProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return PresentationSurface.Web;
        }

        if (name.StartsWith(TerminalProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return PresentationSurface.Terminal;
        }

        return PresentationSurface.Unknown;
    }

    /// <summary>界面用中文标签。</summary>
    public static string Describe(PresentationSurface surface) => surface switch
    {
        PresentationSurface.Web => "浏览器面",
        PresentationSurface.Terminal => "终端面",
        PresentationSurface.Headless => "无界面",
        _ => "未识别"
    };

    /// <summary>该呈现面是否适合"在终端打开"。</summary>
    public static bool SupportsTerminalLaunch(PresentationSurface surface) =>
        surface == PresentationSurface.Terminal;

    /// <summary>
    /// 实例卡片是否值得显示呈现面徽标（work-log/84，变更集 101）：
    /// Web 面是常态（不打扰），终端/无界面/未识别才提示。
    /// </summary>
    public static bool NeedsSurfaceBadge(PresentationSurface surface) =>
        surface != PresentationSurface.Web;

    /// <summary>
    /// dsh 运行时自带的 desktop surface（将来可能出现，如 <c>@deepseek-ai/dsh-desktop-app</c>）：
    /// 仅以官方 scope 为准（<c>@deepseek-ai/dsh-desktop</c> 或 <c>@deepseek-ai/dsh-desktop-*</c>），
    /// 社区包里同名字段不命中；判不到一律 false（不隐藏启动器的「Desktop 启动」/「打开窗口」，不猜）
    /// ——work-log/81 §七.5。
    /// </summary>
    public static bool HasVendorDesktopSurface(IEnumerable<string>? bundles) =>
        bundles?.Any(IsVendorDesktopBundle) == true;

    private const string OfficialScopePrefix = "@deepseek-ai/";

    private static bool IsVendorDesktopBundle(string? bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle))
        {
            return false;
        }

        var name = bundle!.Trim();
        if (!name.StartsWith(OfficialScopePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segment = name[OfficialScopePrefix.Length..];
        return segment.Equals("dsh-desktop", StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith("dsh-desktop-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 构造 Windows Terminal 启动参数（不含可执行文件名本身）：
    /// <c>-d &lt;工作目录&gt; &lt;dsh 入口&gt; --profile &lt;profile&gt;</c>。
    /// 缺参数则返回 null（调用方提示，不猜）。
    /// </summary>
    public static IReadOnlyList<string>? BuildWindowsTerminalArguments(
        string? dshExecutable,
        string? profileName,
        string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(dshExecutable) || string.IsNullOrWhiteSpace(profileName))
        {
            return null;
        }

        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            arguments.Add("-d");
            arguments.Add(workingDirectory!);
        }

        arguments.Add(dshExecutable!);
        arguments.Add("--profile");
        arguments.Add(profileName!);
        return arguments;
    }

    private static bool IsWebBundle(string bundle) =>
        bundle.Contains("dsh-web-app", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalBundle(string bundle) =>
        bundle.Contains("dsh-tui", StringComparison.OrdinalIgnoreCase)
        || bundle.Contains("@deepseek-harness-tui/", StringComparison.OrdinalIgnoreCase);

    private static bool IsHeadlessBundle(string bundle) =>
        bundle.Contains("dsh-headless", StringComparison.OrdinalIgnoreCase)
        || bundle.Contains("dsh-sdk-", StringComparison.OrdinalIgnoreCase)
        || bundle.Contains("dsh-acp-app", StringComparison.OrdinalIgnoreCase);
}
