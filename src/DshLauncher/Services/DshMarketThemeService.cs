using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Optional bridge to the ecosystem's dsh-market loopback routes. The Launcher
/// uses these routes for live Plugin installation and theme activation instead
/// of editing DSh Web UI files or inventing a second format.
/// </summary>
public sealed class DshMarketThemeService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MutationTimeout = TimeSpan.FromMinutes(15);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public DshMarketThemeService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient();
        _ownsClient = client is null;
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<DshMarketThemeState> ReadAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetBaseUri(instance, out var baseUri, out var reason))
        {
            return DshMarketThemeState.Unavailable(reason);
        }

        try
        {
            using var timeout = CreateTimeout(cancellationToken, RequestTimeout);
            using var response = await _client.GetAsync(
                new Uri(baseUri, "dsh-market/installed"),
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return DshMarketThemeState.Unavailable(
                    response.StatusCode == HttpStatusCode.NotFound
                        ? "当前实例没有安装或没有启用 dsh-market。"
                        : $"dsh-market 状态接口返回 HTTP {(int)response.StatusCode}。 ");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            var root = document.RootElement;
            return new DshMarketThemeState(
                true,
                ReadObjectNames(root, "installed"),
                ReadStringSet(root, "live"),
                null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DshMarketThemeState.Unavailable("dsh-market 状态检测超时。 ");
        }
        catch (HttpRequestException ex)
        {
            return DshMarketThemeState.Unavailable($"当前实例无法访问 dsh-market：{ex.Message}");
        }
        catch (JsonException ex)
        {
            return DshMarketThemeState.Unavailable($"dsh-market 返回的数据格式无效：{ex.Message}");
        }
    }

    public async Task<DshMarketThemeApplyResult> ApplyAsync(
        ManagerInstance instance,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetBaseUri(instance, out var baseUri, out var reason))
        {
            return new DshMarketThemeApplyResult(false, EmptyNames(), reason);
        }

        if (string.IsNullOrWhiteSpace(packageName)
            || packageName.Length > 214
            || packageName.Any(char.IsControl))
        {
            return new DshMarketThemeApplyResult(false, EmptyNames(), "主题包名无效。 ");
        }

        try
        {
            using var timeout = CreateTimeout(cancellationToken, RequestTimeout);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(baseUri, "dsh-market/use-skin"));
            request.Headers.TryAddWithoutValidation(
                "Origin",
                baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/'));
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { name = packageName }),
                Encoding.UTF8,
                "application/json");

            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var live = ReadStringSet(root, "live");
            var ok = response.IsSuccessStatusCode
                && root.TryGetProperty("ok", out var okValue)
                && okValue.ValueKind == JsonValueKind.True;
            var error = root.TryGetProperty("error", out var errorValue)
                && errorValue.ValueKind == JsonValueKind.String
                ? errorValue.GetString()
                : ok
                    ? null
                    : $"dsh-market 应用主题失败（HTTP {(int)response.StatusCode}）。";
            return new DshMarketThemeApplyResult(ok, live, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DshMarketThemeApplyResult(false, EmptyNames(), "应用主题超时。 ");
        }
        catch (HttpRequestException ex)
        {
            return new DshMarketThemeApplyResult(false, EmptyNames(), $"当前实例无法访问 dsh-market：{ex.Message}");
        }
        catch (JsonException ex)
        {
            return new DshMarketThemeApplyResult(false, EmptyNames(), $"dsh-market 返回的数据格式无效：{ex.Message}");
        }
    }

    /// <summary>
    /// Probes the upstream Host settings API for the ui-theme namespace. The
    /// namespace and preference field are the observable contract exposed by
    /// @deepseek-ai/dsh-client-ui-theme; no Launcher-private route is used.
    /// </summary>
    public async Task<ThemeCapabilityProbeResult> ProbeThemeCapabilityAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetBaseUri(instance, out var baseUri, out var reason))
        {
            return ThemeCapabilityProbeResult.Unknown(reason);
        }

        var rpcId = Guid.NewGuid().ToString("D");
        try
        {
            using var timeout = CreateTimeout(cancellationToken, RequestTimeout);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(baseUri, "api/settings.describe"));
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    type = "client-request",
                    rpcId,
                    method = "settings.describe",
                    payload = new { }
                }),
                Encoding.UTF8,
                "application/json");

            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ThemeCapabilityProbeResult.Unsupported(
                    "当前实例没有上游 settings.describe 能力，无法确认 Chat 主题联动。 ");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ThemeCapabilityProbeResult.Unknown(
                    $"主题能力探测接口返回 HTTP {(int)response.StatusCode}，暂不启用联动。 ");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "server-response", StringComparison.Ordinal)
                || !root.TryGetProperty("rpcId", out var responseRpcId)
                || responseRpcId.ValueKind != JsonValueKind.String
                || !string.Equals(responseRpcId.GetString(), rpcId, StringComparison.Ordinal)
                || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object)
            {
                return ThemeCapabilityProbeResult.Unknown(
                    "主题能力探测返回的数据不是上游 settings.describe 响应，暂不启用联动。 ");
            }

            if (!result.TryGetProperty("ok", out var ok)
                || ok.ValueKind != JsonValueKind.True)
            {
                return ThemeCapabilityProbeResult.Unsupported(
                    $"当前实例拒绝 settings.describe：{ReadRpcError(result)}");
            }

            if (!result.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("namespaces", out var namespaces)
                || namespaces.ValueKind != JsonValueKind.Array)
            {
                return ThemeCapabilityProbeResult.Unknown(
                    "settings.describe 未返回可识别的主题命名空间，暂不启用联动。 ");
            }

            foreach (var item in namespaces.EnumerateArray())
            {
                if (!item.TryGetProperty("ns", out var ns)
                    || ns.ValueKind != JsonValueKind.String
                    || !string.Equals(ns.GetString(), "ui-theme", StringComparison.Ordinal)
                    || !item.TryGetProperty("value", out var settings)
                    || settings.ValueKind != JsonValueKind.Object
                    || !settings.TryGetProperty("preference", out var preference)
                    || preference.ValueKind != JsonValueKind.String
                    || !IsThemePreference(preference.GetString()))
                {
                    continue;
                }

                var revision = item.TryGetProperty("revision", out var revisionValue)
                    && revisionValue.ValueKind == JsonValueKind.Number
                    && revisionValue.TryGetInt32(out var parsedRevision)
                    ? parsedRevision
                    : (int?)null;
                return ThemeCapabilityProbeResult.Supported(
                    preference.GetString()!,
                    revision,
                    "已通过上游 settings.describe 探测到 ui-theme.preference；可启用 Chat 主题联动。 ");
            }

            return ThemeCapabilityProbeResult.Unsupported(
                "当前实例的 settings.describe 未暴露 ui-theme.preference，无法确认 Chat 主题联动。 ");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ThemeCapabilityProbeResult.Unknown("主题能力探测超时，暂不启用联动。 ");
        }
        catch (HttpRequestException ex)
        {
            return ThemeCapabilityProbeResult.Unknown($"当前实例无法访问上游主题能力接口：{ex.Message}");
        }
        catch (JsonException ex)
        {
            return ThemeCapabilityProbeResult.Unknown($"主题能力探测返回的数据格式无效：{ex.Message}");
        }
    }

    public Task<DshMarketPluginMutationResult> InstallPluginAsync(
        ManagerInstance instance,
        string catalogUrl,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(catalogUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            return Task.FromResult(new DshMarketPluginMutationResult(
                false,
                false,
                "该插件不在 dsh-market 目录中，无法热加载。请停止实例后再普通安装。",
                string.Empty));
        }

        return MutatePluginAsync(
            instance,
            "dsh-market/install",
            new { url = catalogUrl.Trim() },
            cancellationToken);
    }

    public Task<DshMarketPluginMutationResult> UpdatePluginAsync(
        ManagerInstance instance,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageName)
            || packageName.Length > 214
            || packageName.Any(char.IsControl))
        {
            return Task.FromResult(new DshMarketPluginMutationResult(
                false,
                false,
                "Plugin 包名无效。",
                string.Empty));
        }

        return MutatePluginAsync(
            instance,
            "dsh-market/update",
            new { name = packageName },
            cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static bool TryGetBaseUri(ManagerInstance instance, out Uri baseUri, out string reason)
    {
        if (!Uri.TryCreate(instance.WebUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || !parsed.IsLoopback)
        {
            baseUri = null!;
            reason = "实例尚未运行，或 Web 地址不是受支持的本机地址。";
            return false;
        }

        baseUri = parsed.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? parsed
            : new Uri(parsed.AbsoluteUri + "/", UriKind.Absolute);
        reason = string.Empty;
        return true;
    }

    private async Task<DshMarketPluginMutationResult> MutatePluginAsync(
        ManagerInstance instance,
        string route,
        object body,
        CancellationToken cancellationToken)
    {
        if (!TryGetBaseUri(instance, out var baseUri, out var reason))
        {
            return new DshMarketPluginMutationResult(false, false, reason, string.Empty);
        }

        try
        {
            using var timeout = CreateTimeout(cancellationToken, MutationTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, route));
            request.Headers.TryAddWithoutValidation(
                "Origin",
                baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/'));
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");

            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var ok = response.IsSuccessStatusCode
                && root.TryGetProperty("ok", out var okValue)
                && okValue.ValueKind == JsonValueKind.True;
            var hot = root.TryGetProperty("hot", out var hotValue)
                && hotValue.ValueKind == JsonValueKind.True;
            var error = root.TryGetProperty("error", out var errorValue)
                && errorValue.ValueKind == JsonValueKind.String
                ? errorValue.GetString()
                : ok
                    ? null
                    : $"dsh-market Plugin 操作失败（HTTP {(int)response.StatusCode}）。";
            var output = string.Join(
                Environment.NewLine,
                ReadString(root, "stdout"),
                ReadString(root, "stderr"));
            return new DshMarketPluginMutationResult(ok, hot, error, output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DshMarketPluginMutationResult(false, false, "dsh-market Plugin 操作超时。", string.Empty);
        }
        catch (HttpRequestException ex)
        {
            return new DshMarketPluginMutationResult(false, false, $"当前实例无法访问 dsh-market：{ex.Message}", string.Empty);
        }
        catch (JsonException ex)
        {
            return new DshMarketPluginMutationResult(false, false, $"dsh-market 返回的数据格式无效：{ex.Message}", string.Empty);
        }
    }

    private static CancellationTokenSource CreateTimeout(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadRpcError(JsonElement result)
    {
        if (!result.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object)
        {
            return "未提供错误详情。 ";
        }

        var code = ReadString(error, "code");
        var message = ReadString(error, "message");
        return string.IsNullOrWhiteSpace(message)
            ? string.IsNullOrWhiteSpace(code) ? "未提供错误详情。 " : code
            : string.IsNullOrWhiteSpace(code) ? message : $"{code}：{message}";
    }

    private static bool IsThemePreference(string? preference) =>
        preference is "light" or "dark" or "system";

    private static IReadOnlySet<string> ReadObjectNames(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return EmptyNames();
        }

        return new HashSet<string>(
            value.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string> ReadStringSet(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return EmptyNames();
        }

        return new HashSet<string>(
            value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string> EmptyNames() =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
