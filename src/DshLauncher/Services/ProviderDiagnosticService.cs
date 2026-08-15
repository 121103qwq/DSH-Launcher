using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Performs safe, read-only provider diagnostics. It only calls the provider's
/// model-list endpoint and never sends a chat/completions request.
/// </summary>
public sealed class ProviderDiagnosticService : IDisposable
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public ProviderDiagnosticService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient();
        _ownsClient = client is null;
        _client.Timeout = RequestTimeout;
    }

    public async Task<ProviderDiagnosticResult> CheckAsync(
        ModelProviderInfo provider,
        CancellationToken cancellationToken = default)
    {
        if (!provider.Configured)
        {
            return ProviderDiagnosticResult.Problem(
                "未配置",
                "settings.yaml 中没有完整的 Provider 配置。",
                "当前 Provider 没有可读取的配置段。",
                "在当前版本的 DSh settings.yaml 中填写凭据环境变量、Base URL 和模型后重新检测。\n若使用 DSh 内置 catalog，可直接确认对应路由。" );
        }

        var credential = ResolveCredential(provider.ApiKeyEnvironment);
        if (credential.IsError)
        {
            return ProviderDiagnosticResult.Problem(
                "凭据缺失",
                credential.Message,
                credential.Message,
                $"在系统环境变量中设置 {provider.ApiKeyEnvironment}，或在当前版本的 DSh settings.yaml 中修正凭据引用后重新检测。" );
        }

        var baseUrl = ResolveBaseUrl(provider);
        if (baseUrl is null)
        {
            return provider.Models.Count > 0
                ? ProviderDiagnosticResult.Healthy(
                    "使用 DSh 内置 catalog；当前配置没有可探测的 HTTP 端点。",
                    null,
                    "未声明")
                : ProviderDiagnosticResult.Problem(
                    "缺少端点",
                    "没有 Base URL，也没有手工模型列表。",
                    "DSh 无法从当前配置确定此 Provider 的端点和模型。",
                    "在当前版本的 DSh settings.yaml 中填写 Base URL 和至少一个模型 ID，或选择 DSh 已安装 catalog 中的 Provider。" );
        }

        var endpoints = BuildModelEndpoints(baseUrl);
        ProviderDiagnosticResult? lastResult = null;
        foreach (var endpoint in endpoints)
        {
            lastResult = await CheckEndpointAsync(provider, endpoint, credential.Value, cancellationToken);
            if (lastResult is not null && !IsRetryableEndpointFailure(lastResult, endpoint, endpoints))
            {
                return lastResult;
            }
        }

        return lastResult ?? ProviderDiagnosticResult.Problem(
            "检测失败",
            "没有可用的模型列表端点。",
            "没有构造出可检测的模型列表 URL。",
            "检查 Provider 的 Base URL 是否为 HTTP(S) 地址。" );
    }

    public static ProviderDiagnosticResult AnalyzeModelListing(
        ModelProviderInfo provider,
        HttpStatusCode statusCode,
        string responseBody)
    {
        if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
        {
            return ProviderDiagnosticResult.Problem(
                "认证失败",
                "模型列表接口拒绝了当前凭据。",
                $"接口返回 HTTP {(int)statusCode}。",
                $"检查 {provider.ApiKeyEnvironment ?? "API Key 环境变量"} 是否存在、是否属于当前 Provider，并确认 Base URL 对应的服务。" );
        }

        if (statusCode == (HttpStatusCode)429)
        {
            return ProviderDiagnosticResult.Problem(
                "请求受限",
                "Provider 暂时限流。",
                "模型列表接口返回 HTTP 429。",
                "等待限流窗口恢复，或检查 Provider 的配额、代理和重试策略。" );
        }

        if ((int)statusCode >= 500)
        {
            return ProviderDiagnosticResult.Problem(
                "服务异常",
                $"Provider 服务端返回 HTTP {(int)statusCode}。",
                "远端服务暂时不可用。",
                "稍后重试；如果持续失败，检查 Provider 服务状态页或网关日志。" );
        }

        if (!((int)statusCode >= 200 && (int)statusCode < 300))
        {
            return ProviderDiagnosticResult.Problem(
                "接口异常",
                $"模型列表接口返回 HTTP {(int)statusCode}。",
                $"请求没有成功完成（HTTP {(int)statusCode}）。",
                "确认 Base URL 指向 OpenAI-compatible 的 `/models` 入口，并检查代理或网关配置。" );
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return ProviderDiagnosticResult.Problem(
                    "响应格式异常",
                    "接口可访问，但没有返回 OpenAI-compatible 的 data 模型数组。",
                    "响应 JSON 缺少 `data` 数组。",
                    "检查 Provider 是否支持 `GET /models`；如果它只支持手工模型配置，请在当前版本的 DSh settings.yaml 中填写模型 ID。" );
            }

            var models = data.EnumerateArray()
                .Select(ReadModelId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (models.Length == 0)
            {
                return ProviderDiagnosticResult.Problem(
                    "没有模型",
                    "接口可访问，但没有返回可用模型。",
                    "data 数组为空，或条目缺少字符串形式的 id。",
                    "检查 Provider 的模型权限和网关路由；也可以在当前版本的 DSh settings.yaml 中填写正确模型 ID。" ,
                    0,
                    DetectThinkingText(provider, data));
            }

            var configuredModels = provider.Models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var missing = configuredModels
                .Where(model => !models.Contains(model, StringComparer.Ordinal))
                .ToArray();
            var thinkingText = DetectThinkingText(provider, data);
            if (missing.Length > 0)
            {
                return ProviderDiagnosticResult.Problem(
                    "模型不匹配",
                    $"接口返回 {models.Length} 个模型，但配置中的 {missing.Length} 个模型未找到。",
                    $"未找到：{string.Join("、", missing.Take(8))}{(missing.Length > 8 ? "…" : string.Empty)}。",
                    "更新当前版本 DSh settings.yaml 中的模型 ID，或确认 Provider 的 `/models` 接口是否只返回当前账号可用模型。",
                    models.Length,
                    thinkingText);
            }

            return ProviderDiagnosticResult.Healthy(
                $"连接正常 · 接口返回 {models.Length} 个模型。",
                models.Length,
                thinkingText);
        }
        catch (JsonException)
        {
            return ProviderDiagnosticResult.Problem(
                "响应格式异常",
                "接口可访问，但响应不是有效 JSON。",
                "模型列表响应无法解析为 JSON。",
                "确认 Base URL 指向 JSON API，而不是网页、登录页或反向代理错误页。" );
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private async Task<ProviderDiagnosticResult> CheckEndpointAsync(
        ModelProviderInfo provider,
        Uri endpoint,
        string? credential,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(credential))
        {
            try
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            }
            catch (FormatException)
            {
                return ProviderDiagnosticResult.Problem(
                    "凭据格式错误",
                    "API Key 不能作为 HTTP Bearer 凭据发送。",
                    "API Key 含有 HTTP 标头不允许的字符。",
                    "检查环境变量内容，只保留原始 API Key，不要包含引号、换行或 `Bearer ` 前缀。" );
            }
        }

        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var body = await ReadLimitedBodyAsync(response, cancellationToken);
            return AnalyzeModelListing(provider, response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderDiagnosticResult.Problem(
                "连接超时",
                "模型列表请求超过 6 秒没有完成。",
                "Provider 端点没有在限定时间内返回。",
                "检查网络、代理、Base URL 和 Provider 服务状态；如果服务响应较慢，再次点击检测。" );
        }
        catch (HttpRequestException ex)
        {
            return ProviderDiagnosticResult.Problem(
                "连接失败",
                "无法连接到 Provider 的模型列表接口。",
                ex.Message,
                "检查 Base URL、网络代理、TLS 证书、防火墙和 Provider 服务是否正在运行。" );
        }
        catch (InvalidDataException ex)
        {
            return ProviderDiagnosticResult.Problem(
                "响应过大",
                "Provider 返回的模型列表超过 4 MB。",
                ex.Message,
                "检查反向代理是否把错误页面或调试内容转发到了 `/models`，并限制模型列表响应大小。" );
        }
    }

    private static bool IsRetryableEndpointFailure(
        ProviderDiagnosticResult result,
        Uri endpoint,
        IReadOnlyList<Uri> endpoints) =>
        endpoints.Count > 1
        && endpoint == endpoints[0]
        && result.StatusText == "接口异常"
        && result.Summary.Contains("404", StringComparison.Ordinal);

    private static string? ResolveBaseUrl(ModelProviderInfo provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            return provider.BaseUrl.Trim();
        }

        if (provider.Provider == "deepseek-official")
        {
            return Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL")
                ?? "https://api.deepseek.com";
        }

        return null;
    }

    private static IReadOnlyList<Uri> BuildModelEndpoints(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            return Array.Empty<Uri>();
        }

        var basePart = baseUrl.TrimEnd('/');
        var result = new List<Uri>();
        AddUri(result, $"{basePart}/models");
        if (!basePart.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            AddUri(result, $"{basePart}/v1/models");
        }

        return result;
    }

    private static void AddUri(List<Uri> uris, string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is ("http" or "https")
            && !uris.Contains(uri))
        {
            uris.Add(uri);
        }
    }

    private static (bool IsError, string? Value, string Message) ResolveCredential(string? environmentName)
    {
        if (string.IsNullOrWhiteSpace(environmentName))
        {
            return (false, null, string.Empty);
        }

        var value = Environment.GetEnvironmentVariable(environmentName);
        return string.IsNullOrWhiteSpace(value)
            ? (true, null, $"环境变量 {environmentName} 没有值。")
            : (false, value.Trim(), string.Empty);
    }

    private static async Task<string> ReadLimitedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException($"响应大小超过 {MaxResponseBytes / 1024 / 1024} MB。" );
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException($"响应大小超过 {MaxResponseBytes / 1024 / 1024} MB。" );
            }

            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static string? ReadModelId(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
    }

    private static string DetectThinkingText(ModelProviderInfo provider, JsonElement models)
    {
        if (provider.Provider == "deepseek-official")
        {
            return "off / high / max（DSh 官方适配器）";
        }

        var efforts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declared = false;
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var propertyName in new[] { "reasoningEfforts", "reasoning_efforts", "thinkingBudgets", "thinking_budgets" })
            {
                if (model.TryGetProperty(propertyName, out var values)
                    && values.ValueKind == JsonValueKind.Array)
                {
                    declared = true;
                    foreach (var value in values.EnumerateArray())
                    {
                        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } effort)
                        {
                            efforts.Add(effort);
                        }
                    }
                }
            }

            foreach (var propertyName in new[] { "supportsReasoningEffort", "supports_reasoning_effort", "thinking" })
            {
                if (model.TryGetProperty(propertyName, out var capability)
                    && capability.ValueKind is JsonValueKind.True or JsonValueKind.Object)
                {
                    declared = true;
                }
            }

            if (model.TryGetProperty("supported_parameters", out var parameters)
                && parameters.ValueKind == JsonValueKind.Array
                && parameters.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } text
                    && (text.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("thinking", StringComparison.OrdinalIgnoreCase))))
            {
                declared = true;
            }
        }

        return efforts.Count > 0
            ? string.Join(" / ", efforts.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            : declared ? "接口已声明，但未列出具体档位" : "接口未声明";
    }
}
