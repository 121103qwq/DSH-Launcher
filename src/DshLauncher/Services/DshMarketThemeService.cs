using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Optional bridge to the ecosystem's dsh-market theme routes. The Launcher
/// never edits DSh Web UI files or invents a second theme format.
/// </summary>
public sealed class DshMarketThemeService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public DshMarketThemeService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient();
        _ownsClient = client is null;
        _client.Timeout = RequestTimeout;
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
            using var response = await _client.GetAsync(
                new Uri(baseUri, "dsh-market/installed"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return DshMarketThemeState.Unavailable(
                    response.StatusCode == HttpStatusCode.NotFound
                        ? "当前实例没有安装 dsh-market，主题只能作为插件资源管理。"
                        : $"dsh-market 状态接口返回 HTTP {(int)response.StatusCode}。 ");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
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
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
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
