using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Launcher 级代理（借鉴 dsh-plugins/dsh-launcher 的 proxy.rs 设计，思路借鉴）：
/// 一个开关同时服务两条链路——
/// 1) Launcher 自身 HTTP（市场目录、registry 查询、版本检测、余额）；
/// 2) 启动的 dsh 实例（HTTP(S)_PROXY / NO_PROXY 环境变量注入）。
/// 配置存 LauncherSettingsData，改完即生效（写设置后调用 <see cref="ProxyConfigurator.ApplyGlobal"/>）。
/// </summary>
public sealed record ProxySettings(string Server, string NoProxy)
{
    private static readonly string[] ProxyKeys =
    {
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
        "http_proxy", "https_proxy", "no_proxy"
    };

    /// <summary>从 Launcher 设置构建；未启用/地址非法时返回 null（并记录一次告警）。</summary>
    public static ProxySettings? From(LauncherSettingsData settings)
    {
        if (!settings.ProxyEnabled)
        {
            return null;
        }

        if (!TryNormalizeServer(settings.ProxyUrl, out var server, out var error))
        {
            LauncherLog.Warn($"代理配置无效，已忽略：{error}", ErrorCodes.E3001,
                new { settings.ProxyUrl });
            return null;
        }

        return new ProxySettings(server, (settings.NoProxy ?? string.Empty).Trim());
    }

    public static bool TryNormalizeServer(string? raw, out string server, out string error)
    {
        server = string.Empty;
        error = string.Empty;
        var value = raw?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "代理地址为空";
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "代理地址必须是 http(s)://主机[:端口] 形式";
            return false;
        }

        server = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        return true;
    }

    /// <summary>注入实例环境变量（先移除实例继承来的同类变量，保持单一来源）。</summary>
    public void ApplyTo(ProcessStartInfo startInfo)
    {
        foreach (var key in ProxyKeys)
        {
            startInfo.Environment.Remove(key);
        }

        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
        {
            startInfo.Environment[key] = Server;
        }

        startInfo.Environment["NO_PROXY"] = NoProxy;
        startInfo.Environment["no_proxy"] = NoProxy;
    }

    public IWebProxy ToWebProxy()
    {
        if (!Uri.TryCreate(Server, UriKind.Absolute, out var uri))
        {
            return new LauncherWebProxy(null);
        }

        var proxy = new WebProxy(uri) { BypassProxyOnLocal = true };
        var bypass = NoProxy
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.TrimStart('.'))
            .Where(entry => entry.Length > 0)
            .ToArray();
        if (bypass.Length > 0)
        {
            proxy.BypassList = bypass;
        }

        return new LauncherWebProxy(proxy);
    }
}

/// <summary>
/// 可随时切换的全局代理包装：未启用时直接直连（IsBypassed 恒真），
/// 避免反复给 HttpClient.DefaultProxy 赋 null 的平台差异。
/// </summary>
internal sealed class LauncherWebProxy : IWebProxy
{
    private readonly WebProxy? _inner;

    public LauncherWebProxy(WebProxy? inner) => _inner = inner;

    public ICredentials? Credentials
    {
        get => _inner?.Credentials;
        set
        {
            if (_inner is not null)
            {
                _inner.Credentials = value;
            }
        }
    }

    public Uri GetProxy(Uri destination) => _inner?.GetProxy(destination) ?? destination;

    public bool IsBypassed(Uri host) => _inner?.IsBypassed(host) ?? true;
}

public static class ProxyConfigurator
{
    private static ProxySettings? _current;
    private static IWebProxy _globalProxy = new LauncherWebProxy(null);

    public static ProxySettings? Current => _current;

    /// <summary>从设置读取并应用到进程级 HttpClient.DefaultProxy（对所有新建 HttpClient 生效）。</summary>
    public static void ApplyGlobal(LauncherSettingsData settings)
    {
        _current = ProxySettings.From(settings);
        _globalProxy = _current?.ToWebProxy() ?? new LauncherWebProxy(null);
        try
        {
            HttpClient.DefaultProxy = _globalProxy;
            LauncherLog.Info(_current is null ? "代理未启用（直连）。" : "已启用 Launcher 代理。",
                ErrorCodes.E3001, new { server = _current?.Server, noProxy = _current?.NoProxy });
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            LauncherLog.Warn("应用全局代理失败，已回退直连。", ErrorCodes.E3001, new { error = ex.Message });
        }
    }

    /// <summary>供诊断包展示的脱敏摘要。</summary>
    public static string Describe() =>
        _current is null ? "disabled" : $"enabled server={_current.Server} no_proxy={_current.NoProxy}";
}
