using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Small, format-aware editor for the two model settings sections DSh exposes.
/// It preserves unrelated top-level YAML sections and never stores an API key;
/// only the credential environment-variable name is written.
/// </summary>
public sealed class ModelService
{
    private static readonly Regex TopLevelSection = new("^(?<name>[A-Za-z0-9_-]+):\\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InlineModelId = new(
        @"\bid\s*:\s*(?<value>""(?:\\.|[^""\\])*""|'[^']*'|[^,\]}\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BlockScalarIndicator = new(
        @":\s*[>|][0-9+-]*\s*(?:#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly Func<string, bool> _isRunning;

    public ModelService(Func<string, bool>? isRunning = null)
    {
        _isRunning = isRunning ?? (_ => false);
    }

    public string GetSettingsPath(ManagerInstance instance) =>
        Path.Combine(instance.DshHome, "settings.yaml");

    public IReadOnlyList<ModelProviderInfo> Read(ManagerInstance instance)
    {
        var path = GetSettingsPath(instance);
        if (!File.Exists(path))
        {
            return new[]
            {
                new ModelProviderInfo(
                    "deepseek-official",
                    "DeepSeek 官方",
                    "llm-deepseek",
                    null,
                    null,
                    Array.Empty<string>(),
                    false)
            };
        }

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var result = new List<ModelProviderInfo>();
        var deepseek = ReadSection(lines, "llm-deepseek");
        result.Add(new ModelProviderInfo(
            "deepseek-official",
            "DeepSeek 官方",
            "llm-deepseek",
            ReadScalar(deepseek, "apiKeyEnv"),
            ReadScalar(deepseek, "baseURL"),
            ReadModels(deepseek),
            deepseek.Count > 0));

        var piAi = ReadSection(lines, "llm-pi-ai");
        var providerNames = FindProviderNames(piAi);
        foreach (var provider in providerNames)
        {
            var providerLines = ReadNestedSection(piAi, "providers", provider);
            result.Add(new ModelProviderInfo(
                provider,
                ReadScalar(providerLines, "displayName") ?? provider,
                "llm-pi-ai",
                ReadScalar(providerLines, "apiKeyEnv"),
                ReadScalar(providerLines, "baseURL"),
                ReadModels(providerLines),
                providerLines.Count > 0));
        }

        return result;
    }

    public Task SaveDeepSeekAsync(
        ManagerInstance instance,
        string? apiKeyEnvironment,
        string? baseUrl,
        IReadOnlyList<string> modelIds,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        cancellationToken.ThrowIfCancellationRequested();
        SaveDeepSeekCore(instance, apiKeyEnvironment, baseUrl, modelIds);
        return Task.CompletedTask;
    }

    public Task SaveOpenAiCompatibleAsync(
        ManagerInstance instance,
        string provider,
        string? apiKeyEnvironment,
        string? baseUrl,
        IReadOnlyList<string> modelIds,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedProvider = NormalizeKey(provider, "Provider");
        SaveOpenAiCompatibleCore(instance, normalizedProvider, null, apiKeyEnvironment, baseUrl, modelIds);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Copies only the model-provider sections understood by this service.
    /// Other DSh settings remain in the target settings.yaml, and credentials
    /// are represented only by their environment-variable names.
    /// </summary>
    public void CopyProviderConfiguration(ManagerInstance source, ManagerInstance target)
    {
        var text = BuildProviderConfigurationText(source, target);
        WriteSettingsText(GetSettingsPath(target), text);
    }

    public string BuildProviderConfigurationText(ManagerInstance source, ManagerInstance target)
    {
        EnsureStopped(source);
        EnsureStopped(target);

        var existing = File.Exists(GetSettingsPath(target))
            ? File.ReadAllLines(GetSettingsPath(target), Encoding.UTF8).ToList()
            : new List<string>();
        var sourceProviders = Read(source);
        var sourceDeepSeek = sourceProviders.FirstOrDefault(provider =>
            string.Equals(provider.SettingsNamespace, "llm-deepseek", StringComparison.Ordinal));
        if (sourceDeepSeek is { Configured: true })
        {
            ReplaceTopLevelSection(
                existing,
                "llm-deepseek",
                RenderDeepSeek(
                sourceDeepSeek.ApiKeyEnvironment,
                sourceDeepSeek.BaseUrl,
                sourceDeepSeek.Models));
        }
        else
        {
            RemoveTopLevelSection(existing, "llm-deepseek");
        }

        var sourceCompatible = sourceProviders
            .Where(provider => string.Equals(provider.SettingsNamespace, "llm-pi-ai", StringComparison.Ordinal))
            .ToDictionary(provider => provider.Provider, StringComparer.Ordinal);
        ReplaceOpenAiProviders(existing, sourceCompatible.Values);
        return string.Join(Environment.NewLine, existing) + Environment.NewLine;
    }

    private void EnsureStopped(ManagerInstance instance)
    {
        if (_isRunning(instance.Id))
        {
            throw new InvalidOperationException("实例正在运行，请先停止实例再修改模型配置。");
        }
    }

    private void SaveDeepSeekCore(
        ManagerInstance instance,
        string? apiKeyEnvironment,
        string? baseUrl,
        IReadOnlyList<string> modelIds)
    {
        var section = RenderDeepSeek(apiKeyEnvironment, baseUrl, modelIds);
        UpsertSection(GetSettingsPath(instance), "llm-deepseek", section);
    }

    private void SaveOpenAiCompatibleCore(
        ManagerInstance instance,
        string provider,
        string? displayName,
        string? apiKeyEnvironment,
        string? baseUrl,
        IReadOnlyList<string> modelIds)
    {
        var section = RenderOpenAiProvider(provider, displayName, apiKeyEnvironment, baseUrl, modelIds);
        UpsertSection(GetSettingsPath(instance), "llm-pi-ai", section, provider);
    }

    private static void UpsertSection(
        string path,
        string sectionName,
        IReadOnlyList<string> rendered,
        string? nestedProvider = null)
    {
        var existing = File.Exists(path)
            ? File.ReadAllLines(path, Encoding.UTF8).ToList()
            : new List<string>();
        if (nestedProvider is null)
        {
            ReplaceTopLevelSection(existing, sectionName, rendered);
        }
        else
        {
            UpsertNestedProvider(existing, sectionName, nestedProvider, rendered);
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("settings.yaml 没有父目录。");
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, string.Join(Environment.NewLine, existing) + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RemoveTopLevelSection(string path, string sectionName)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var existing = File.ReadAllLines(path, Encoding.UTF8).ToList();
        var start = FindTopLevelStart(existing, sectionName);
        if (start < 0)
        {
            return;
        }

        var end = FindTopLevelEnd(existing, start);
        existing.RemoveRange(start, end - start);
        WriteSettings(path, existing);
    }

    private static void RemoveTopLevelSection(List<string> lines, string sectionName)
    {
        var start = FindTopLevelStart(lines, sectionName);
        if (start < 0)
        {
            return;
        }

        var end = FindTopLevelEnd(lines, start);
        lines.RemoveRange(start, end - start);
    }

    private static void RemoveNestedProvider(string path, string sectionName, string provider)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var existing = File.ReadAllLines(path, Encoding.UTF8).ToList();
        var sectionStart = FindTopLevelStart(existing, sectionName);
        if (sectionStart < 0)
        {
            return;
        }

        var sectionEnd = FindTopLevelEnd(existing, sectionStart);
        var section = existing.GetRange(sectionStart, sectionEnd - sectionStart);
        var providersStart = FindIndentedSectionStart(section, "providers", 2);
        if (providersStart < 0)
        {
            return;
        }

        NormalizeNestedProviderMapping(section, providersStart);

        var providersEnd = FindIndentedSectionEnd(section, providersStart, 2);
        var providerStart = FindProviderStart(section, providersStart + 1, providersEnd, provider);
        if (providerStart < 0)
        {
            return;
        }

        var providerEnd = FindProviderEnd(section, providerStart, providersEnd);
        section.RemoveRange(providerStart, providerEnd - providerStart);
        existing.RemoveRange(sectionStart, sectionEnd - sectionStart);
        existing.InsertRange(sectionStart, section);
        WriteSettings(path, existing);
    }

    private static void ReplaceOpenAiProviders(
        string path,
        IEnumerable<ModelProviderInfo> providers)
    {
        var existing = File.Exists(path)
            ? File.ReadAllLines(path, Encoding.UTF8).ToList()
            : new List<string>();
        ReplaceOpenAiProviders(existing, providers);
        WriteSettings(path, existing);
    }

    private static void ReplaceOpenAiProviders(
        List<string> existing,
        IEnumerable<ModelProviderInfo> providers)
    {
        var rendered = providers
            .OrderBy(provider => provider.Provider, StringComparer.Ordinal)
            .SelectMany(provider => RenderOpenAiProvider(
                provider.Provider,
                provider.DisplayName,
                provider.ApiKeyEnvironment,
                provider.BaseUrl,
                provider.Models))
            .ToList();
        var sectionStart = FindTopLevelStart(existing, "llm-pi-ai");
        if (sectionStart < 0)
        {
            if (existing.Count > 0 && existing[^1].Length > 0)
            {
                existing.Add(string.Empty);
            }

            existing.Add("llm-pi-ai:");
            existing.Add("  providers:");
            existing.AddRange(rendered);
            return;
        }

        var sectionEnd = FindTopLevelEnd(existing, sectionStart);
        var section = existing.GetRange(sectionStart, sectionEnd - sectionStart);
        var providersStart = FindIndentedSectionStart(section, "providers", 2);
        if (providersStart < 0)
        {
            section.Add("  providers:");
            section.AddRange(rendered);
        }
        else
        {
            var providersEnd = FindIndentedSectionEnd(section, providersStart, 2);
            section.RemoveRange(providersStart, providersEnd - providersStart);
            section.Insert(providersStart, "  providers:");
            section.InsertRange(providersStart + 1, rendered);
        }

        existing.RemoveRange(sectionStart, sectionEnd - sectionStart);
        existing.InsertRange(sectionStart, section);
    }

    private static void WriteSettings(string path, IReadOnlyList<string> lines)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("settings.yaml 没有父目录。");
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                string.Join(Environment.NewLine, lines) + Environment.NewLine,
                new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void WriteSettingsText(string path, string text)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("settings.yaml 没有父目录。");
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static List<string> UpsertNestedProvider(
        List<string> existing,
        string sectionName,
        string provider,
        IReadOnlyList<string> rendered)
    {
        var sectionStart = FindTopLevelStart(existing, sectionName);
        if (sectionStart < 0)
        {
            var top = new List<string> { $"{sectionName}:" };
            if (sectionName == "llm-pi-ai") top.Add("  providers:");
            top.AddRange(rendered);
            if (existing.Count > 0 && existing[^1].Length > 0) existing.Add(string.Empty);
            existing.AddRange(top);
            return existing;
        }

        var sectionEnd = FindTopLevelEnd(existing, sectionStart);
        var section = existing.GetRange(sectionStart, sectionEnd - sectionStart);
        var providersStart = FindIndentedSectionStart(section, "providers", 2);
        if (providersStart < 0)
        {
            section.Add("  providers:");
            section.AddRange(rendered);
        }
        else
        {
            NormalizeNestedProviderMapping(section, providersStart);
            var providersEnd = FindIndentedSectionEnd(section, providersStart, 2);
            var providerStart = FindProviderStart(section, providersStart + 1, providersEnd, provider);
            if (providerStart < 0)
            {
                section.InsertRange(providersEnd, rendered);
            }
            else
            {
                var providerEnd = FindProviderEnd(section, providerStart, providersEnd);
                section.RemoveRange(providerStart, providerEnd - providerStart);
                section.InsertRange(providerStart, rendered);
            }
        }

        existing.RemoveRange(sectionStart, sectionEnd - sectionStart);
        existing.InsertRange(sectionStart, section);
        return existing;
    }

    private static void NormalizeNestedProviderMapping(List<string> section, int providersStart)
    {
        var providersEnd = FindIndentedSectionEnd(section, providersStart, 2);
        var body = section
            .Skip(providersStart + 1)
            .Take(providersEnd - providersStart - 1)
            .ToArray();
        if (!IsFlowProviderMapping(section[providersStart], body))
        {
            return;
        }

        // DSh Web UI currently writes this mapping in flow style, with standalone
        // braces and commas. Convert only the providers body to ordinary block YAML
        // before line-based edits so the two styles can never be mixed.
        var normalized = NormalizeMappingBraces(body);
        section.RemoveRange(providersStart + 1, providersEnd - providersStart - 1);
        section.InsertRange(providersStart + 1, normalized);
    }

    private static bool IsFlowProviderMapping(string providersLine, IReadOnlyList<string> body)
    {
        var first = Enumerable.Range(0, body.Count)
            .FirstOrDefault(index => !string.IsNullOrWhiteSpace(body[index]), -1);
        var last = Enumerable.Range(0, body.Count)
            .LastOrDefault(index => !string.IsNullOrWhiteSpace(body[index]), -1);
        if (first < 0 || last < 0)
        {
            return false;
        }

        var providersIndent = providersLine.Length - providersLine.TrimStart().Length;
        var expectedBraceIndent = providersIndent + 2;
        return body[first].Length - body[first].TrimStart().Length == expectedBraceIndent
            && body[last].Length - body[last].TrimStart().Length == expectedBraceIndent
            && string.Equals(body[first].Trim().TrimEnd(','), "{", StringComparison.Ordinal)
            && string.Equals(body[last].Trim().TrimEnd(','), "}", StringComparison.Ordinal);
    }

    private static bool IsStandaloneMappingBrace(string line)
    {
        var trimmed = line.Trim().TrimEnd(',');
        return trimmed is "{" or "}";
    }

    private static void ReplaceTopLevelSection(List<string> lines, string name, IReadOnlyList<string> replacement)
    {
        var start = FindTopLevelStart(lines, name);
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
            lines.AddRange(replacement);
            return;
        }

        var end = FindTopLevelEnd(lines, start);
        lines.RemoveRange(start, end - start);
        lines.InsertRange(start, replacement);
    }

    private static int FindTopLevelStart(IReadOnlyList<string> lines, string name)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var match = TopLevelSection.Match(lines[index]);
            if (match.Success && string.Equals(match.Groups["name"].Value, name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindTopLevelEnd(IReadOnlyList<string> lines, int start)
    {
        for (var index = start + 1; index < lines.Count; index++)
        {
            if (TopLevelSection.IsMatch(lines[index])) return index;
        }

        return lines.Count;
    }

    private static int FindIndentedSectionStart(IReadOnlyList<string> lines, string name, int indent)
    {
        var prefix = new string(' ', indent);
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].StartsWith(prefix, StringComparison.Ordinal)
                && lines[index][indent..].StartsWith(name + ":", StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindIndentedSectionEnd(IReadOnlyList<string> lines, int start, int indent)
    {
        var prefix = new string(' ', indent);
        for (var index = start + 1; index < lines.Count; index++)
        {
            if (lines[index].StartsWith(prefix, StringComparison.Ordinal)
                && !lines[index].StartsWith(new string(' ', indent + 1), StringComparison.Ordinal))
            {
                return index;
            }
        }

        return lines.Count;
    }

    private static int FindProviderStart(IReadOnlyList<string> lines, int start, int end, string provider)
    {
        var prefix = "    ";
        for (var index = start; index < end; index++)
        {
            if (!lines[index].StartsWith(prefix, StringComparison.Ordinal)) continue;
            var key = lines[index][prefix.Length..].Split(':', 2)[0].Trim();
            if (string.Equals(key, provider, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private static int FindProviderEnd(IReadOnlyList<string> lines, int start, int end)
    {
        for (var index = start + 1; index < end; index++)
        {
            if (lines[index].StartsWith("    ", StringComparison.Ordinal)
                && !lines[index].StartsWith("     ", StringComparison.Ordinal))
            {
                return index;
            }
        }

        return end;
    }

    private static List<string> RenderDeepSeek(string? apiKeyEnvironment, string? baseUrl, IReadOnlyList<string> modelIds)
    {
        var lines = new List<string> { "llm-deepseek:" };
        AddScalar(lines, 2, "apiKeyEnv", NormalizeEnvironmentName(apiKeyEnvironment));
        AddScalar(lines, 2, "baseURL", NormalizeBaseUrl(baseUrl));
        AddModels(lines, 2, modelIds);
        return lines;
    }

    private static List<string> RenderOpenAiProvider(
        string provider,
        string? displayName,
        string? apiKeyEnvironment,
        string? baseUrl,
        IReadOnlyList<string> modelIds)
    {
        var lines = new List<string> { $"    {provider}:" };
        if (!string.IsNullOrWhiteSpace(displayName)
            && !string.Equals(displayName, provider, StringComparison.Ordinal))
        {
            AddScalar(lines, 6, "displayName", displayName);
        }
        AddScalar(lines, 6, "apiKeyEnv", NormalizeEnvironmentName(apiKeyEnvironment));
        AddScalar(lines, 6, "baseURL", NormalizeBaseUrl(baseUrl));
        AddScalar(lines, 6, "api", "openai-completions");
        AddModels(lines, 6, modelIds);
        return lines;
    }

    private static void AddScalar(List<string> lines, int indent, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var normalized = value.Trim();
        if (normalized.Any(char.IsControl)) throw new ArgumentException($"模型配置 {key} 不能包含控制字符。", nameof(value));
        lines.Add($"{new string(' ', indent)}{key}: {YamlString(normalized)}");
    }

    private static void AddModels(List<string> lines, int indent, IReadOnlyList<string> modelIds)
    {
        var models = modelIds
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (models.Length > 128)
        {
            throw new ArgumentException("模型数量不能超过 128 个。", nameof(modelIds));
        }
        if (models.Length == 0) return;
        lines.Add($"{new string(' ', indent)}models:");
        foreach (var model in models)
        {
            if (model.Length > 256 || model.Any(char.IsControl)) throw new ArgumentException("模型 ID 不能为空、不能超过 256 个字符，也不能包含控制字符。", nameof(modelIds));
            lines.Add($"{new string(' ', indent + 2)}- id: {YamlString(model)}");
            lines.Add($"{new string(' ', indent + 4)}name: {YamlString(model)}");
        }
    }

    private static string YamlString(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static List<string> ReadSection(IReadOnlyList<string> lines, string name)
    {
        var start = FindTopLevelStart(lines, name);
        if (start < 0) return new List<string>();
        var end = FindTopLevelEnd(lines, start);
        return NormalizeMappingBraces(lines.Skip(start + 1).Take(end - start - 1));
    }

    private static List<string> NormalizeMappingBraces(IEnumerable<string> lines)
    {
        var result = new List<string>();
        var braceDepth = 0;
        int? blockScalarIndent = null;
        foreach (var line in lines)
        {
            var indentation = line.Length - line.TrimStart().Length;
            if (blockScalarIndent is not null)
            {
                if (string.IsNullOrWhiteSpace(line) || indentation > blockScalarIndent.Value)
                {
                    result.Add(RemoveStructuralIndent(line, braceDepth, trimTrailingComma: false));
                    continue;
                }

                blockScalarIndent = null;
            }

            var trimmed = line.Trim();
            if (trimmed == "{")
            {
                braceDepth++;
                continue;
            }

            if (trimmed.TrimEnd(',') == "}")
            {
                braceDepth = Math.Max(0, braceDepth - 1);
                continue;
            }

            var normalized = RemoveStructuralIndent(line, braceDepth, trimTrailingComma: true);
            result.Add(normalized);
            if (BlockScalarIndicator.IsMatch(line.TrimEnd()))
            {
                blockScalarIndent = indentation;
            }
        }

        return result;
    }

    public CodingModelSelection? ReadDefaultModel(ManagerInstance instance)
    {
        var path = GetSettingsPath(instance);
        if (!File.Exists(path))
        {
            return null;
        }

        var section = ReadSection(File.ReadAllLines(path, Encoding.UTF8), "agent-default-model");
        var provider = ReadScalar(section, "provider");
        var model = ReadScalar(section, "model");
        return string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)
            ? null
            : new CodingModelSelection(provider, model, ReadScalar(section, "reasoningEffort"));
    }

    public Task SaveDefaultModelAsync(
        ManagerInstance instance,
        CodingModelSelection selection,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        return SaveDefaultModelCoreAsync(instance, selection, cancellationToken);
    }

    /// <summary>
    /// Writes only the official agent-default-model settings section. The
    /// settings-file provider watches this document and hot-publishes external
    /// edits, so this narrow write is also valid while Web DSh is running.
    /// </summary>
    public Task SaveDefaultModelLiveAsync(
        ManagerInstance instance,
        CodingModelSelection selection,
        CancellationToken cancellationToken = default) =>
        SaveDefaultModelCoreAsync(instance, selection, cancellationToken);

    private Task SaveDefaultModelCoreAsync(
        ManagerInstance instance,
        CodingModelSelection selection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = CodingModelPolicyService.NormalizeSelection(selection);
        var section = new List<string>
        {
            "agent-default-model:",
            $"  provider: {YamlString(normalized.Provider)}",
            $"  model: {YamlString(normalized.Model)}"
        };
        if (!string.IsNullOrWhiteSpace(normalized.ReasoningEffort))
        {
            section.Add($"  reasoningEffort: {YamlString(normalized.ReasoningEffort)}");
        }

        UpsertSection(GetSettingsPath(instance), "agent-default-model", section);
        return Task.CompletedTask;
    }

    private static string RemoveStructuralIndent(string line, int braceDepth, bool trimTrailingComma)
    {
        var indentation = line.Length - line.TrimStart().Length;
        var remove = Math.Min(indentation, braceDepth * 2);
        var normalized = line[remove..].TrimEnd();
        if (trimTrailingComma && normalized.EndsWith(",", StringComparison.Ordinal))
        {
            normalized = normalized[..^1].TrimEnd();
        }

        return normalized;
    }

    private static List<string> ReadNestedSection(IReadOnlyList<string> section, string parent, string child)
    {
        var parentStart = FindIndentedSectionStart(section, parent, 2);
        if (parentStart < 0) return new List<string>();
        var parentEnd = FindIndentedSectionEnd(section, parentStart, 2);
        var childStart = FindProviderStart(section, parentStart + 1, parentEnd, child);
        if (childStart < 0) return new List<string>();
        var childEnd = FindProviderEnd(section, childStart, parentEnd);
        return section.Skip(childStart + 1).Take(childEnd - childStart - 1).ToList();
    }

    private static IReadOnlyList<string> FindProviderNames(IReadOnlyList<string> section)
    {
        var parentStart = FindIndentedSectionStart(section, "providers", 2);
        if (parentStart < 0) return Array.Empty<string>();
        var parentEnd = FindIndentedSectionEnd(section, parentStart, 2);
        var result = new List<string>();
        for (var index = parentStart + 1; index < parentEnd; index++)
        {
            if (!section[index].StartsWith("    ", StringComparison.Ordinal)
                || section[index].StartsWith("     ", StringComparison.Ordinal)) continue;
            var name = section[index][4..].Split(':', 2)[0].Trim();
            if (name.Length > 0) result.Add(name.Trim('\'', '"'));
        }

        return result;
    }

    private static string? ReadScalar(IReadOnlyList<string> lines, string key)
    {
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            var value = trimmed[(key.Length + 1)..].Trim().TrimEnd(',').Trim();
            return ParseYamlScalar(value);
        }

        return null;
    }

    private static IReadOnlyList<string> ReadModels(IReadOnlyList<string> lines)
    {
        var modelsStart = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].TrimStart().StartsWith("models:", StringComparison.Ordinal))
            {
                modelsStart = index;
                break;
            }
        }

        if (modelsStart < 0) return Array.Empty<string>();
        var inlineModels = InlineModelId.Matches(lines[modelsStart])
            .Cast<Match>()
            .Select(match => ParseYamlScalar(match.Groups["value"].Value))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (inlineModels.Length > 0)
        {
            return inlineModels;
        }

        var baseIndent = lines[modelsStart].Length - lines[modelsStart].TrimStart().Length;
        var result = new List<string>();
        for (var index = modelsStart + 1; index < lines.Count; index++)
        {
            var line = lines[index];
            var indent = line.Length - line.TrimStart().Length;
            if (line.Trim().Length > 0 && indent <= baseIndent) break;
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("- id:", StringComparison.Ordinal)) continue;
            result.Add(trimmed[5..].Trim().Trim('"', '\''));
        }

        return result;
    }

    private static string ParseYamlScalar(string value)
    {
        var trimmed = value.Trim().TrimEnd(',').Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(trimmed) ?? string.Empty;
            }
            catch (System.Text.Json.JsonException)
            {
                return trimmed[1..^1];
            }
        }

        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            return trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return trimmed;
    }

    private static string NormalizeKey(string value, string label)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0 || normalized.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException($"{label}只能使用字母、数字、- 或 _。", nameof(value));
        }

        return normalized;
    }

    private static string? NormalizeEnvironmentName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > 128
            || normalized.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("API Key 环境变量名只能使用字母、数字和下划线，且不超过 128 个字符。", nameof(value));
        }

        return normalized;
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > 2048
            || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Base URL 必须是 HTTP(S) 地址，且不超过 2048 个字符。", nameof(value));
        }

        return normalized;
    }
}
