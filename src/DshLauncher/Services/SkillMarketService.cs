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
    private const string SearchUrl = "https://api.github.com/search/repositories?q=skill%20in%3Aname&sort=stars&order=desc&per_page=20";
    private const int SearchPageSize = 20;
    private const int MaxSearchPages = 2;
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxConcurrentRepositoryScans = 6;
    private const int MaxConcurrentValidations = 12;
    private const int MaxSkillPathsPerRepository = 64;
    private const int MaxSkillPathsTotal = 240;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RepositoryScanTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(4);

    public const int CurrentValidationVersion = 3;

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
                return BuildBundledFallback();
            }

            var cached = JsonSerializer.Deserialize<List<SkillMarketItem>>(
                File.ReadAllText(CachePath, Encoding.UTF8), JsonOptions);
            var normalized = cached?
                .Select(item => item with
                {
                    Verified = item.ValidationVersion == CurrentValidationVersion && item.Verified,
                    Category = NormalizeCategory(item)
                })
                .ToArray()
                ?? Array.Empty<SkillMarketItem>();
            return normalized.Length == 0 ? BuildBundledFallback() : normalized;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return BuildBundledFallback();
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
            catch (JsonException) when (cached.Count > 0)
            {
                return cached;
            }
            catch (InvalidDataException) when (cached.Count > 0)
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
                }

                progress?.Report(new SkillMarketRefreshProgress(
                    reusableSnapshot,
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

                SkillMarketRefreshProgress? progressUpdate = null;
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
                    if (progress is not null
                        && (current == validationCandidates.Length || current % 8 == 0))
                    {
                        progressUpdate = new SkillMarketRefreshProgress(
                            BuildSkillSnapshot(validItems),
                            current,
                            validationCandidates.Length,
                            "正在校验 Skill 文件");
                    }
                }

                if (progressUpdate is not null)
                {
                    progress?.Report(progressUpdate);
                }
            }).ToArray();

            await Task.WhenAll(validationTasks);
        }

        var result = BuildSkillSnapshot(validItems);
        if (result.Count == 0
            && (transientScanFailures > 0 || transientValidationFailures > 0))
        {
            if (cached.Count > 0)
            {
                return cached;
            }

            throw new HttpRequestException("GitHub Skill 目录暂时不可用，未写入空缓存，请稍后重试。 ");
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
        IProgress<SkillInstallProgress>? progress = null,
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
                progress,
                cancellationToken);
            progress?.Report(new SkillInstallProgress(0, null, null, "正在解压"));
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
            var importSource = Path.GetFileName(skillFile).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
                ? skillDirectory
                : skillFile;
            progress?.Report(new SkillInstallProgress(0, null, null, "正在导入"));
            var entry = await _extensionService.ImportSkillAsync(
                instance,
                importSource,
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
        var repositories = new Dictionary<string, SkillMarketItem>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= MaxSearchPages; page++)
        {
            using var response = await _httpClient.GetAsync(
                new Uri($"{SearchUrl}&page={page}"),
                cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), options: default, cancellationToken);
            if (!document.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return repositories.Values.ToList();
            }

            var pageItemCount = items.GetArrayLength();
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

                var repositoryId = fullName.GetString()!;
                var defaultBranch = repository.TryGetProperty("default_branch", out var branch)
                    && branch.ValueKind == JsonValueKind.String
                        ? branch.GetString() ?? "main"
                        : "main";
                if (!repositories.ContainsKey(repositoryId))
                {
                    repositories.Add(repositoryId, new SkillMarketItem(
                        repositoryId,
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
            }

            if (pageItemCount < SearchPageSize)
            {
                break;
            }
        }

        return repositories.Values.ToList();
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

            var metadata = ParseSkillFrontmatter(content);
            if (metadata is not null
                && !Path.GetFileName(candidate.SkillPath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileNameWithoutExtension(candidate.SkillPath).Equals(metadata.Name, StringComparison.Ordinal))
            {
                metadata = null;
            }

            return new SkillValidationResult(metadata, Cacheable: true);
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

        var frontmatter = new List<string>();
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

            frontmatter.Add(line);
        }

        if (!closed)
        {
            return null;
        }

        string? name = null;
        string? description = null;
        for (var index = 0; index < frontmatter.Count; index++)
        {
            var line = frontmatter[index];
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var rawValue = line[(separator + 1)..].Trim();
            if (key.Equals("disable-model-invocation", StringComparison.OrdinalIgnoreCase)
                || key.Equals("user-invocable", StringComparison.OrdinalIgnoreCase)
                || key.Equals("disableModelInvocation", StringComparison.OrdinalIgnoreCase)
                || key.Equals("userInvocable", StringComparison.OrdinalIgnoreCase))
            {
                if ((key != "disable-model-invocation" && key != "user-invocable")
                    || !IsYamlBoolean(rawValue))
                {
                    return null;
                }
            }

            if (key == "name")
            {
                name = UnquoteYamlScalar(rawValue);
            }
            else if (key == "description")
            {
                description = TryReadYamlBlockStyle(rawValue, out var folded)
                    ? ReadYamlBlock(frontmatter, ref index, folded)
                    : UnquoteYamlScalar(rawValue);
            }
        }

        return IsKebabCaseSkillName(name) && !string.IsNullOrWhiteSpace(description)
            ? new SkillMetadata(name!, description!)
            : null;
    }

    private static string UnquoteYamlScalar(string value) => value.Trim().Trim('\'', '"');

    private static bool TryReadYamlBlockStyle(string value, out bool folded)
    {
        var normalized = value.Trim();
        folded = normalized.StartsWith('>');
        return normalized.Length >= 1
            && normalized[0] is '|' or '>'
            && normalized[1..].All(character => character is '+' or '-' || char.IsAsciiDigit(character));
    }

    private static string ReadYamlBlock(IReadOnlyList<string> lines, ref int index, bool folded)
    {
        var values = new List<string>();
        while (index + 1 < lines.Count)
        {
            var next = lines[index + 1];
            if (next.Length > 0 && !char.IsWhiteSpace(next[0]))
            {
                break;
            }

            index++;
            values.Add(next.Trim());
        }

        return string.Join(folded ? " " : Environment.NewLine, values).Trim();
    }

    private static IReadOnlyList<SkillMarketItem> BuildBundledFallback() =>
        new[]
        {
            new SkillMarketItem(
                "mattpocock/skills",
                "ask-matt",
                "Ask which skill or flow fits your situation.",
                0,
                "main",
                null,
                Verified: false,
                ValidationVersion: CurrentValidationVersion - 1,
                SkillPath: "skills/engineering/ask-matt/SKILL.md",
                Category: "开发"),
            new SkillMarketItem(
                "mattpocock/skills",
                "code-review",
                "Review code changes against project standards and the requested specification.",
                0,
                "main",
                null,
                Verified: false,
                ValidationVersion: CurrentValidationVersion - 1,
                SkillPath: "skills/engineering/code-review/SKILL.md",
                Category: "文档"),
            new SkillMarketItem(
                "mattpocock/skills",
                "diagnosing-bugs",
                "A diagnosis loop for bugs and performance regressions.",
                0,
                "main",
                null,
                Verified: false,
                ValidationVersion: CurrentValidationVersion - 1,
                SkillPath: "skills/engineering/diagnosing-bugs/SKILL.md",
                Category: "开发"),
            new SkillMarketItem(
                "mattpocock/skills",
                "grill-me",
                "A relentless interview to sharpen a plan or design.",
                0,
                "main",
                null,
                Verified: false,
                ValidationVersion: CurrentValidationVersion - 1,
                SkillPath: "skills/productivity/grill-me/SKILL.md",
                Category: "设计")
        };

    private static bool IsYamlBoolean(string value)
    {
        var normalized = UnquoteYamlScalar(value);
        return normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("false", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("no", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("on", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("off", StringComparison.OrdinalIgnoreCase)
            || normalized is "1" or "0";
    }

    private static bool IsKebabCaseSkillName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name[0] == '-'
            || name[^1] == '-')
        {
            return false;
        }

        var previousWasDash = false;
        foreach (var character in name)
        {
            if (character == '-')
            {
                if (previousWasDash)
                {
                    return false;
                }

                previousWasDash = true;
                continue;
            }

            if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9'))
            {
                return false;
            }

            previousWasDash = false;
        }

        return true;
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

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.LastOrDefault();
        if (string.Equals(fileName, "SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName is not null
            && fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("CHANGELOG.md", StringComparison.OrdinalIgnoreCase)
            && (segments.Length == 1
                || segments[^2].Equals("skills", StringComparison.OrdinalIgnoreCase));
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

    private async Task DownloadFileAsync(
        Uri uri,
        string destinationPath,
        IProgress<SkillInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long received = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            int? percent = total is > 0
                ? (int)Math.Clamp(received * 100L / total.Value, 0, 100)
                : null;
            progress?.Report(new SkillInstallProgress(received, total, percent, "下载"));
        }

        if (total is > 0 && received != total.Value)
        {
            throw new InvalidDataException($"Skill 下载不完整：应为 {total.Value} 字节，实际收到 {received} 字节。");
        }
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
