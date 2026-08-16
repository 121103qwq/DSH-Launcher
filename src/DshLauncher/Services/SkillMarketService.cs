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
/// Skill 市场：从 GitHub 仓库树发现实际 SKILL.md，校验后按单个 Skill 展示；
/// 安装时只导入所选 SKILL.md 所在目录及其配套文件。
/// </summary>
public sealed class SkillMarketService
{
    private const string SearchUrl = "https://api.github.com/search/repositories?q=skill%20in%3Aname&sort=stars&order=desc&per_page=30";
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxConcurrentRepositoryScans = 6;
    private const int MaxConcurrentValidations = 12;
    private const int MaxSkillPathsPerRepository = 64;
    private const int MaxSkillPathsTotal = 240;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RepositoryScanTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(4);

    public const int CurrentValidationVersion = 2;

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
            return cached?
                .Select(item => item with
                {
                    Verified = item.ValidationVersion == CurrentValidationVersion && item.Verified,
                    Category = NormalizeCategory(item)
                })
                .ToArray()
                ?? Array.Empty<SkillMarketItem>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<SkillMarketItem>();
        }
    }

    public async Task<IReadOnlyList<SkillMarketItem>> SearchAsync(
        CancellationToken cancellationToken = default,
        IProgress<SkillMarketRefreshProgress>? progress = null)
    {
        var cached = ReadCached();
        List<SkillMarketItem> candidates;
        using (var searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            searchCancellation.CancelAfter(SearchTimeout);
            try
            {
                candidates = await SearchRepositoriesAsync(searchCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (cached.Count > 0)
                {
                    return cached;
                }

                throw new TimeoutException("GitHub Skill 仓库搜索超时，请检查网络后重试。");
            }
            catch (HttpRequestException) when (cached.Count > 0)
            {
                return cached;
            }
        }

        var cachedByRepository = cached
            .GroupBy(item => item.Repository, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var reusable = new List<SkillMarketItem>();
        var pendingRepositories = new List<SkillMarketItem>();
        foreach (var candidate in candidates)
        {
            if (cachedByRepository.TryGetValue(candidate.Repository, out var previous)
                && previous.Length > 0
                && previous.All(item => item.Verified
                    && item.ValidationVersion == CurrentValidationVersion
                    && !string.IsNullOrWhiteSpace(item.SkillPath)
                    && string.Equals(item.DefaultBranch, candidate.DefaultBranch, StringComparison.Ordinal)
                    && item.UpdatedAt == candidate.UpdatedAt))
            {
                reusable.AddRange(previous.Select(item => item with
                {
                    Stars = candidate.Stars,
                    DefaultBranch = candidate.DefaultBranch,
                    UpdatedAt = candidate.UpdatedAt,
                    Category = NormalizeCategory(item)
                }));
            }
            else
            {
                pendingRepositories.Add(candidate);
            }
        }

        var reusableSnapshot = BuildSkillSnapshot(reusable);
        var scanCompleted = candidates.Count - pendingRepositories.Count;
        progress?.Report(new SkillMarketRefreshProgress(
            reusableSnapshot,
            scanCompleted,
            candidates.Count,
            "正在扫描仓库目录"));

        var discovered = new List<SkillPathCandidate>();
        var scanSync = new object();
        var transientScanFailures = 0;
        using (var scanGate = new SemaphoreSlim(MaxConcurrentRepositoryScans))
        {
            var scanTasks = pendingRepositories.Select(async candidate =>
            {
                await scanGate.WaitAsync(cancellationToken);
                RepositoryScanResult scan;
                try
                {
                    scan = await DiscoverSkillPathsAsync(candidate, cancellationToken);
                }
                finally
                {
                    scanGate.Release();
                }

                IReadOnlyList<SkillMarketItem> snapshot;
                int current;
                lock (scanSync)
                {
                    if (scan.Succeeded)
                    {
                        discovered.AddRange(scan.Paths.Select(path => new SkillPathCandidate(candidate, path)));
                    }
                    else
                    {
                        transientScanFailures++;
                    }

                    current = ++scanCompleted;
                    snapshot = BuildSkillSnapshot(reusable);
                }

                progress?.Report(new SkillMarketRefreshProgress(
                    snapshot,
                    current,
                    candidates.Count,
                    "正在扫描仓库目录"));
            }).ToArray();

            await Task.WhenAll(scanTasks);
        }

        var validationCandidates = discovered
            .GroupBy(item => item.Repository.Repository, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group
                .GroupBy(item => item.SkillPath, StringComparer.OrdinalIgnoreCase)
                .Select(pathGroup => pathGroup.First())
                .OrderBy(item => SkillPathRank(item.SkillPath))
                .ThenBy(item => item.SkillPath, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSkillPathsPerRepository))
            .OrderByDescending(item => item.Repository.Stars)
            .ThenBy(item => SkillPathRank(item.SkillPath))
            .ThenBy(item => item.SkillPath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSkillPathsTotal)
            .ToArray();

        var validItems = new List<SkillMarketItem>(reusable);
        var validationSync = new object();
        var validationCompleted = 0;
        var transientValidationFailures = 0;
        progress?.Report(new SkillMarketRefreshProgress(
            BuildSkillSnapshot(validItems),
            0,
            validationCandidates.Length,
            "正在校验 Skill 文件"));

        using (var validationGate = new SemaphoreSlim(MaxConcurrentValidations))
        {
            var validationTasks = validationCandidates.Select(async candidate =>
            {
                await validationGate.WaitAsync(cancellationToken);
                SkillValidationResult validation;
                try
                {
                    validation = await ValidateSkillAsync(candidate, cancellationToken);
                }
                finally
                {
                    validationGate.Release();
                }

                IReadOnlyList<SkillMarketItem> snapshot;
                int current;
                lock (validationSync)
                {
                    if (validation.Metadata is { } metadata)
                    {
                        validItems.Add(candidate.Repository with
                        {
                            Name = metadata.Name,
                            Description = metadata.Description,
                            Verified = true,
                            ValidationVersion = CurrentValidationVersion,
                            SkillPath = candidate.SkillPath,
                            Category = ClassifySkill(metadata.Name, metadata.Description, candidate.SkillPath)
                        });
                    }
                    else if (!validation.Cacheable)
                    {
                        transientValidationFailures++;
                    }

                    current = ++validationCompleted;
                    snapshot = BuildSkillSnapshot(validItems);
                }

                if (current == validationCandidates.Length || current % 4 == 0)
                {
                    progress?.Report(new SkillMarketRefreshProgress(
                        snapshot,
                        current,
                        validationCandidates.Length,
                        "正在校验 Skill 文件"));
                }
            }).ToArray();

            await Task.WhenAll(validationTasks);
        }

        var result = BuildSkillSnapshot(validItems);
        if (result.Count == 0
            && cached.Count > 0
            && (transientScanFailures > 0 || transientValidationFailures > 0))
        {
            return cached;
        }

        TryWriteCache(result);
        return result;
    }

    /// <summary>
    /// 下载仓库 zip，并把所选 SKILL.md 所在目录导入实例 skills 目录。
    /// 返回导入后的 Skill 名称。
    /// </summary>
    public async Task<string> InstallAsync(
        ManagerInstance instance,
        SkillMarketItem item,
        CancellationToken cancellationToken = default)
    {
        if (!item.Verified
            || item.ValidationVersion != CurrentValidationVersion
            || !IsSkillMarkdownPath(item.SkillPath))
        {
            throw new InvalidDataException("所选 Skill 尚未通过格式校验，无法安装。");
        }

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
            var extractedRoot = Directory.EnumerateDirectories(temporaryRoot).FirstOrDefault();
            if (extractedRoot is null)
            {
                throw new InvalidDataException($"{item.Repository} 的下载内容为空，无法安装 Skill。");
            }

            var relativeSkillPath = item.SkillPath.Replace('/', Path.DirectorySeparatorChar);
            var skillFile = Path.GetFullPath(Path.Combine(extractedRoot, relativeSkillPath));
            var normalizedRoot = Path.GetFullPath(extractedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
            if (!skillFile.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(skillFile))
            {
                throw new InvalidDataException($"{item.Repository} 中找不到已校验的 {item.SkillPath}。");
            }

            var skillDirectory = Path.GetDirectoryName(skillFile)
                ?? throw new InvalidDataException("无法确定 Skill 所在目录。");
            var entry = await _extensionService.ImportSkillAsync(
                instance,
                skillDirectory,
                item.Name,
                cancellationToken);
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

    private async Task<RepositoryScanResult> DiscoverSkillPathsAsync(
        SkillMarketItem repository,
        CancellationToken cancellationToken)
    {
        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scanCancellation.CancelAfter(RepositoryScanTimeout);
        try
        {
            var escapedRepository = string.Join('/', repository.Repository
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
            using var response = await _httpClient.GetAsync(
                new Uri($"https://api.github.com/repos/{escapedRepository}/git/trees/{Uri.EscapeDataString(repository.DefaultBranch)}?recursive=1"),
                HttpCompletionOption.ResponseHeadersRead,
                scanCancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new RepositoryScanResult(Array.Empty<string>(), Succeeded: response.StatusCode == System.Net.HttpStatusCode.NotFound);
            }

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                return new RepositoryScanResult(Array.Empty<string>(), Succeeded: true);
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(scanCancellation.Token), options: default, scanCancellation.Token);
            if (!document.RootElement.TryGetProperty("tree", out var tree)
                || tree.ValueKind != JsonValueKind.Array)
            {
                return new RepositoryScanResult(Array.Empty<string>(), Succeeded: true);
            }

            var paths = tree.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object
                    && string.Equals(ReadStringProperty(entry, "type"), "blob", StringComparison.OrdinalIgnoreCase))
                .Select(entry => ReadStringProperty(entry, "path"))
                .Where(path => path is not null && IsSkillMarkdownPath(path))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(SkillPathRank)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSkillPathsPerRepository)
                .ToArray();
            return new RepositoryScanResult(paths, Succeeded: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RepositoryScanResult(Array.Empty<string>(), Succeeded: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new RepositoryScanResult(Array.Empty<string>(), Succeeded: false);
        }
    }

    private async Task<SkillValidationResult> ValidateSkillAsync(
        SkillPathCandidate candidate,
        CancellationToken cancellationToken)
    {
        using var validationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        validationCancellation.CancelAfter(ValidationTimeout);
        try
        {
            var escapedPath = string.Join('/', candidate.SkillPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
            using var response = await _httpClient.GetAsync(
                new Uri($"https://raw.githubusercontent.com/{candidate.Repository.Repository}/{Uri.EscapeDataString(candidate.Repository.DefaultBranch)}/{escapedPath}"),
                HttpCompletionOption.ResponseHeadersRead,
                validationCancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new SkillValidationResult(
                    Metadata: null,
                    Cacheable: response.StatusCode == System.Net.HttpStatusCode.NotFound);
            }

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                return new SkillValidationResult(null, Cacheable: true);
            }

            var content = await response.Content.ReadAsStringAsync(validationCancellation.Token);
            if (Encoding.UTF8.GetByteCount(content) > MaxResponseBytes)
            {
                return new SkillValidationResult(null, Cacheable: true);
            }

            return new SkillValidationResult(ParseSkillFrontmatter(content), Cacheable: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SkillValidationResult(null, Cacheable: false);
        }
        catch (HttpRequestException)
        {
            return new SkillValidationResult(null, Cacheable: false);
        }
    }

    private static SkillMetadata? ParseSkillFrontmatter(string content)
    {
        using var reader = new StringReader(content);
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
        {
            return null;
        }

        string? name = null;
        string? description = null;
        var closed = false;
        for (var count = 0; count < 128; count++)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (line.Trim() == "---")
            {
                closed = true;
                break;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('\'', '"');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
            if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
        }

        return closed && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description)
            ? new SkillMetadata(name, description)
            : null;
    }

    private static IReadOnlyList<SkillMarketItem> BuildSkillSnapshot(IEnumerable<SkillMarketItem> items) =>
        items
            .Where(item => item.Verified
                && item.ValidationVersion == CurrentValidationVersion
                && !string.IsNullOrWhiteSpace(item.SkillPath))
            .GroupBy(
                item => $"{item.Repository}\n{item.Name}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => SkillPathRank(item.SkillPath))
                .ThenBy(item => item.SkillPath.Length)
                .ThenBy(item => item.SkillPath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(item => item.Stars)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeCategory(SkillMarketItem item) =>
        string.IsNullOrWhiteSpace(item.Category) || item.Category == "其他"
            ? ClassifySkill(item.Name, item.Description, item.SkillPath)
            : item.Category;

    private static string ClassifySkill(string name, string? description, string path)
    {
        var text = $"{name} {description} {path}".ToLowerInvariant();
        if (ContainsAnyKeyword(text, "ui", "ux", "design", "brand", "art", "canvas", "theme", "visual", "image", "video", "slide", "presentation", "设计", "界面", "主题", "图像"))
        {
            return "设计";
        }

        if (ContainsAnyKeyword(text, "document", "documentation", "docs", "docx", "pdf", "pptx", "xlsx", "spreadsheet", "writing", "word", "文档", "写作", "表格"))
        {
            return "文档";
        }

        if (ContainsAnyKeyword(text, "agent", "mcp", "prompt", "claude", "llm", "model", "memory", "context", "智能体", "提示词", "模型"))
        {
            return "Agent";
        }

        if (ContainsAnyKeyword(text, "engineering", "code", "coding", "development", "developer", "frontend", "backend", "api", "tdd", "debug", "github", "git", "test", "architecture", "security", "database", "web", "mobile", "python", "javascript", "typescript", "开发", "编程", "测试", "调试"))
        {
            return "开发";
        }

        if (ContainsAnyKeyword(text, "productivity", "planning", "plan", "workflow", "research", "communication", "handoff", "interview", "meeting", "automation", "organize", "效率", "计划", "工作流", "研究", "自动化"))
        {
            return "效率";
        }

        return "其他";
    }

    private static bool ContainsAnyKeyword(string text, params string[] keywords) =>
        keywords.Any(keyword => ContainsKeyword(text, keyword));

    private static bool ContainsKeyword(string text, string keyword)
    {
        if (keyword.Any(character => character > 127) || keyword.Length > 3)
        {
            return text.Contains(keyword, StringComparison.Ordinal);
        }

        var start = 0;
        while ((start = text.IndexOf(keyword, start, StringComparison.Ordinal)) >= 0)
        {
            var beforeIsBoundary = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
            var end = start + keyword.Length;
            var afterIsBoundary = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (beforeIsBoundary && afterIsBoundary)
            {
                return true;
            }

            start++;
        }

        return false;
    }

    private static bool IsSkillMarkdownPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment == ".." || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return string.Equals(normalized.Split('/').LastOrDefault(), "SKILL.md", StringComparison.OrdinalIgnoreCase);
    }

    private static int SkillPathRank(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("skills/", StringComparison.OrdinalIgnoreCase)) return 0;
        if (normalized.StartsWith(".agents/skills/", StringComparison.OrdinalIgnoreCase)) return 1;
        if (normalized.StartsWith(".claude/skills/", StringComparison.OrdinalIgnoreCase)) return 2;
        if (normalized.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
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

    private sealed record SkillPathCandidate(SkillMarketItem Repository, string SkillPath);
    private sealed record RepositoryScanResult(IReadOnlyList<string> Paths, bool Succeeded);
    private sealed record SkillValidationResult(SkillMetadata? Metadata, bool Cacheable);
    private sealed record SkillMetadata(string Name, string Description);
}
