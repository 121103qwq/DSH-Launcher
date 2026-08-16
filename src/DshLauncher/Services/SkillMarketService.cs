using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Skill 市场：在 GitHub 搜索名称含 skill 的仓库，校验根目录 SKILL.md 后作为
/// 可安装 Skill 提供；安装即下载仓库 zip 并导入当前实例的 skills 目录。
/// 校验走 raw.githubusercontent.com，不消耗 GitHub API 配额。
/// </summary>
public sealed class SkillMarketService
{
    private const string SearchUrl = "https://api.github.com/search/repositories?q=skill%20in%3Aname&sort=stars&order=desc&per_page=30";
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ExtensionService _extensionService;
    private readonly LauncherPaths _paths;
    private readonly HttpClient _httpClient;

    public SkillMarketService(
        ExtensionService extensionService,
        LauncherPaths? paths = null,
        HttpClient? httpClient = null)
    {
        _extensionService = extensionService;
        _paths = paths ?? new LauncherPaths();
        if (httpClient is null)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DSH-Launcher", "0.1"));
        }
        else
        {
            _httpClient = httpClient;
        }
    }

    private string CachePath => Path.Combine(_paths.RootDirectory, "skill-market-cache.json");

    public IReadOnlyList<SkillMarketItem> ReadCached()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return Array.Empty<SkillMarketItem>();
            }

            var cached = JsonSerializer.Deserialize<List<SkillMarketItem>>(
                File.ReadAllText(CachePath, Encoding.UTF8), JsonOptions);
            return cached ?? new List<SkillMarketItem>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<SkillMarketItem>();
        }
    }

    public async Task<IReadOnlyList<SkillMarketItem>> SearchAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await SearchRepositoriesAsync(cancellationToken);
        var result = new List<SkillMarketItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verified = false;
            try
            {
                verified = await HasRootSkillMarkdownAsync(candidate, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 单个仓库校验超时不阻断整个目录。
            }
            catch (HttpRequestException)
            {
                // 单个仓库校验失败不阻断整个目录。
            }

            result.Add(candidate with { Verified = verified });
        }

        TryWriteCache(result);
        return result;
    }

    /// <summary>
    /// 下载仓库 zip 并把根目录含 SKILL.md 的内容导入实例 skills 目录。
    /// 返回导入后的 Skill 名称。
    /// </summary>
    public async Task<string> InstallAsync(
        ManagerInstance instance,
        SkillMarketItem item,
        CancellationToken cancellationToken = default)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"dsh-skill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var zipPath = Path.Combine(temporaryRoot, "repo.zip");
            await DownloadFileAsync(
                new Uri($"https://codeload.github.com/{item.Repository}/zip/refs/heads/{Uri.EscapeDataString(item.DefaultBranch)}"),
                zipPath,
                cancellationToken);
            ZipFile.ExtractToDirectory(zipPath, temporaryRoot, overwriteFiles: true);
            var extractedRoot = Directory.EnumerateDirectories(temporaryRoot)
                .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "SKILL.md")));
            if (extractedRoot is null)
            {
                throw new InvalidDataException($"{item.Repository} 的仓库根目录没有 SKILL.md，无法作为 Skill 安装。");
            }

            var entry = await _extensionService.ImportSkillAsync(instance, extractedRoot, cancellationToken: cancellationToken);
            return entry.Name;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<List<SkillMarketItem>> SearchRepositoriesAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(SearchUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), options: default, cancellationToken);
        var result = new List<SkillMarketItem>();
        if (!document.RootElement.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var repository in items.EnumerateArray())
        {
            if (repository.ValueKind != JsonValueKind.Object
                || !repository.TryGetProperty("full_name", out var fullName)
                || fullName.ValueKind != JsonValueKind.String
                || !repository.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var repositoryName = name.GetString()!;
            if (!repositoryName.Contains("skill", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var defaultBranch = repository.TryGetProperty("default_branch", out var branch)
                && branch.ValueKind == JsonValueKind.String
                    ? branch.GetString() ?? "main"
                    : "main";
            result.Add(new SkillMarketItem(
                fullName.GetString()!,
                repositoryName,
                ReadStringProperty(repository, "description"),
                repository.TryGetProperty("stargazers_count", out var stars)
                    && stars.ValueKind == JsonValueKind.Number
                        ? stars.GetInt32()
                        : 0,
                defaultBranch,
                DateTimeOffset.TryParse(ReadStringProperty(repository, "pushed_at"), out var pushedAt)
                    ? pushedAt
                    : null,
                Verified: false));
        }

        return result;
    }

    private async Task<bool> HasRootSkillMarkdownAsync(SkillMarketItem item, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            new Uri($"https://raw.githubusercontent.com/{item.Repository}/{Uri.EscapeDataString(item.DefaultBranch)}/SKILL.md"),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (content.Length > MaxResponseBytes)
        {
            return false;
        }

        // SKILL.md 需要可识别的 frontmatter 才能被 ExtensionService 接受。
        return content.StartsWith("---", StringComparison.Ordinal);
    }

    private async Task DownloadFileAsync(Uri uri, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destinationPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void TryWriteCache(IReadOnlyList<SkillMarketItem> items)
    {
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            var temporary = $"{CachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(
                    temporary,
                    JsonSerializer.Serialize(items, JsonOptions),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporary, CachePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存失败只影响下次打开时的初始内容。
        }
    }
}
