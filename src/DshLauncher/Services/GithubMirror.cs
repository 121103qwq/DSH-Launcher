using System;

namespace DshLauncher.Services;

/// <summary>
/// GitHub 访问的国内镜像回退：直连失败（403 限流/超时/网络）时把请求
/// 重定向到镜像前缀。镜像（gh-proxy.com）走自带认证配额，可绕过未认证
/// 60 次/小时的核心 API 限制；search/raw/codeload 已验证可用，trees 偶发
/// 403（镜像共享 token 被二次限流），失败后下次刷新可能恢复。
/// </summary>
internal static class GithubMirror
{
    private static readonly string[] ApiCandidates =
    {
        "https://gh-proxy.com"
    };

    private static readonly string[] RawCandidates =
    {
        "https://gh-proxy.com",
        "https://ghproxy.net",
        "https://ghfast.top"
    };

    private static readonly string[] ArchiveCandidates =
    {
        "https://gh-proxy.com"
    };

    public static Uri? TryMirror(Uri original)
    {
        var candidates = original.Host switch
        {
            "api.github.com" => ApiCandidates,
            "raw.githubusercontent.com" => RawCandidates,
            "codeload.github.com" => ArchiveCandidates,
            _ => null
        };
        if (candidates is null || candidates.Length == 0)
        {
            return null;
        }

        foreach (var prefix in candidates)
        {
            if (Uri.TryCreate($"{prefix}/{original.AbsoluteUri}", UriKind.Absolute, out var mirrored))
            {
                return mirrored;
            }
        }

        return null;
    }
}
