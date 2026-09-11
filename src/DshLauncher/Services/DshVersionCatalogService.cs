using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace DshLauncher.Services;

public sealed class DshVersionCatalogService : IDisposable
{
    public const string OfficialMetadataUrl = "https://registry.npmjs.org/@deepseek-ai%2fdsh";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public DshVersionCatalogService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Launcher/1.0");
        }
    }

    public async Task<IReadOnlyList<string>> ReadOfficialVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(OfficialMetadataUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("versions", out var versions)
            || versions.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("官方 DSh 包元数据没有 versions。 ");
        }

        return versions.EnumerateObject()
            .Select(property => property.Name)
            .Where(static version => !string.IsNullOrWhiteSpace(version))
            .OrderByDescending(static version => version, DshVersionComparer.Instance)
            .ToArray();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private sealed class DshVersionComparer : IComparer<string>
    {
        public static DshVersionComparer Instance { get; } = new();

        /// <summary>
        /// 单一口径：与「更换运行版本」和插件兼容性共用 semver 比较
        /// （预发布标签参与排序：alpha &lt; beta &lt; rc，正式版高于同号预发布）。
        /// 旧实现只比尾号（alpha.2 与 rc.2 视为相等）→ 官方列表顺序随机，
        /// 导致“官方最新”被算成 0.1.5-alpha.2（变更集 77 修复）。
        /// 无法解析时插件侧比较器返回 0，此时退回序号比较，保证仍是确定顺序。
        /// </summary>
        public int Compare(string? left, string? right)
        {
            var leftText = left ?? string.Empty;
            var rightText = right ?? string.Empty;
            var result = PluginCompatibility.Compare(leftText, rightText);
            return result != 0 || string.Equals(leftText, rightText, StringComparison.Ordinal)
                ? result
                : string.CompareOrdinal(leftText, rightText);
        }
    }
}
