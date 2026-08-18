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

        public int Compare(string? left, string? right)
        {
            var leftKey = Parse(left);
            var rightKey = Parse(right);
            var result = leftKey.Base.CompareTo(rightKey.Base);
            if (result != 0)
            {
                return result;
            }

            result = leftKey.Stable.CompareTo(rightKey.Stable);
            return result != 0 ? result : leftKey.PreRelease.CompareTo(rightKey.PreRelease);
        }

        private static (Version Base, int Stable, int PreRelease) Parse(string? value)
        {
            var parts = (value ?? string.Empty).Split('-', 2);
            var baseVersion = Version.TryParse(parts[0], out var parsed) ? parsed : new Version();
            if (parts.Length == 1)
            {
                return (baseVersion, 1, int.MaxValue);
            }

            var suffix = parts[1];
            var lastDot = suffix.LastIndexOf('.');
            var number = lastDot >= 0 && int.TryParse(suffix[(lastDot + 1)..], out var parsedNumber)
                ? parsedNumber
                : 0;
            return (baseVersion, 0, number);
        }
    }
}
