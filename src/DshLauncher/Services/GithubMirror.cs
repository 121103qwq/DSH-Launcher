using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DshLauncher.Services;

/// <summary>
/// GitHub 访问的国内镜像：本网络下直连普遍超时/限流（raw 15s 无响应、
/// api 未认证 60 次/小时），镜像稳定约 1s 且 gh-proxy.com 走自带认证配额
/// （core 5000/时）。策略为镜像优先、失败回退直连，仅对 api/raw/codeload
/// 主机生效。trees 偶发 403（镜像共享 token 被二次限流），下次刷新可能恢复。
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

    /// <summary>
    /// 镜像优先的 GitHub 请求：先走镜像（若可用），HttpRequestException 时
    /// 回退直连一次。调用方自带超时预算（LinkedTokenSource）。
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithMirrorFirstAsync(
        HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        var mirror = uri is not null ? TryMirror(uri) : null;
        if (mirror is null)
        {
            return await client.SendAsync(request, completion, cancellationToken);
        }

        request.RequestUri = mirror;
        try
        {
            return await client.SendAsync(request, completion, cancellationToken);
        }
        catch (HttpRequestException)
        {
            request.RequestUri = uri;
            return await client.SendAsync(request, completion, cancellationToken);
        }
    }
}
