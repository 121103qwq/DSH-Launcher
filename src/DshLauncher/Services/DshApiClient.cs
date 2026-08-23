using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Minimal loopback client for the official DSh /api unary transport.
/// It deliberately supports only the model operations used by the Launcher.
/// </summary>
public sealed class DshApiClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public DshApiClient(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = RequestTimeout };
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Launcher/1.0");
        }
    }

    public async Task<IReadOnlyList<CodingModelOption>> ReadModelsAsync(
        string webUrl,
        CancellationToken cancellationToken = default)
    {
        var value = await CallAsync(webUrl, "llm.models", new { }, cancellationToken);
        if (!value.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("DSh llm.models 没有返回模型分组。");
        }

        var options = new List<CodingModelOption>();
        foreach (var group in groups.EnumerateArray())
        {
            var provider = ReadRequiredString(group, "id", "Provider ID");
            var providerName = ReadOptionalString(group, "name") ?? provider;
            if (!group.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var model in models.EnumerateArray())
            {
                var modelId = ReadRequiredString(model, "id", "模型 ID");
                var modelName = ReadOptionalString(model, "name") ?? modelId;
                options.Add(new CodingModelOption(provider, providerName, modelId, modelName));
            }
        }

        return options
            .GroupBy(option => option.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(option => option.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.ModelName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<DshProviderRuntimeState>> ReadProviderStatesAsync(
        string webUrl,
        CancellationToken cancellationToken = default)
    {
        var value = await CallAsync(webUrl, "llm.providers", new { }, cancellationToken);
        if (!value.TryGetProperty("providers", out var providers)
            || providers.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("DSh llm.providers 没有返回 Provider 列表。");
        }

        var result = new List<DshProviderRuntimeState>();
        foreach (var provider in providers.EnumerateArray())
        {
            var id = ReadRequiredString(provider, "provider", "Provider ID");
            var name = ReadOptionalString(provider, "displayName") ?? id;
            var active = provider.TryGetProperty("active", out var activeElement)
                && activeElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && activeElement.GetBoolean();
            var declared = provider.TryGetProperty("declared", out var declaredElement)
                && declaredElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && declaredElement.GetBoolean();
            result.Add(new DshProviderRuntimeState(id, name, active, declared));
        }

        return result;
    }

    public async Task<CodingModelSelection> SelectSessionModelAsync(
        string webUrl,
        string sessionId,
        CodingModelSelection selection,
        CancellationToken cancellationToken = default)
    {
        var normalized = CodingModelPolicyService.NormalizeSelection(selection);
        var payload = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["provider"] = normalized.Provider,
            ["model"] = normalized.Model
        };
        if (!string.IsNullOrWhiteSpace(normalized.ReasoningEffort))
        {
            payload["reasoningEffort"] = normalized.ReasoningEffort;
        }

        var value = await CallAsync(webUrl, "session.selectModel", payload, cancellationToken);
        if (!value.TryGetProperty("selected", out var selected)
            || selected.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("DSh session.selectModel 没有返回选中的模型。");
        }

        return new CodingModelSelection(
            ReadRequiredString(selected, "provider", "Provider"),
            ReadRequiredString(selected, "model", "模型"),
            ReadOptionalString(selected, "reasoningEffort"));
    }

    private async Task<JsonElement> CallAsync(
        string webUrl,
        string method,
        object payload,
        CancellationToken cancellationToken)
    {
        var baseUri = ValidateLoopbackUrl(webUrl);
        var rpcId = Guid.NewGuid().ToString();
        var envelope = new
        {
            type = "client-request",
            rpcId,
            method,
            payload
        };
        var endpoint = new Uri(baseUri, $"api/{method}");
        using var response = await _client.PostAsJsonAsync(endpoint, envelope, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"DSh {method} 请求失败：HTTP {(int)response.StatusCode}。",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!string.Equals(ReadOptionalString(root, "rpcId"), rpcId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"DSh {method} 返回了不匹配的 rpcId。");
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"DSh {method} 返回格式无效。");
        }

        if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"DSh {method} 没有返回操作状态。");
        }

        if (!ok.GetBoolean())
        {
            var message = result.TryGetProperty("error", out var error)
                ? ReadOptionalString(error, "message")
                : null;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? $"DSh {method} 操作失败。"
                    : message);
        }

        return result.TryGetProperty("value", out var value)
            ? value.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();
    }

    internal static Uri ValidateLoopbackUrl(string webUrl)
    {
        if (!Uri.TryCreate(webUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !uri.IsLoopback)
        {
            throw new ArgumentException("DSh API 只允许访问当前实例的 loopback 地址。", nameof(webUrl));
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static string ReadRequiredString(JsonElement element, string property, string label) =>
        ReadOptionalString(element, property)
        ?? throw new InvalidDataException($"DSh 返回缺少 {label}。");

    private static string? ReadOptionalString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
