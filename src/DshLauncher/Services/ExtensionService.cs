using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Reads and changes the parts of an instance that DSh actually consumes.
/// The service deliberately refuses mutating operations while the instance is
/// running: DSh watches some of these files, while plugin composition and
/// preset loading are not safe to change halfway through a process lifetime.
/// </summary>
public sealed class ExtensionService
{
    private const string ProfileName = "web";
    private const string McpPackage = "@deepseek-ai/dsh-mcp-client";
    private const string BuiltInBase = "@deepseek-ai/dsh-base";
    private const string BuiltInWeb = "@deepseek-ai/dsh-web-app";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static readonly Regex SafeServerName = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafePackageName = new("^(@[A-Za-z0-9._~-]+/)?[A-Za-z0-9._~-]+$", RegexOptions.CultureInvariant);
    private readonly Func<string, bool> _isRunning;
    private readonly SourceProjectInspector _sourceInspector;

    public ExtensionService(Func<string, bool>? isRunning = null, SourceProjectInspector? sourceInspector = null)
    {
        _isRunning = isRunning ?? (_ => false);
        _sourceInspector = sourceInspector ?? new SourceProjectInspector();
    }

    public string GetMcpMetadataPath(ManagerInstance instance) =>
        Path.Combine(instance.DshHome, ".dsh-launcher", "mcp.json");

    public string GetLauncherPatchPath(ManagerInstance instance) =>
        Path.Combine(instance.DshHome, "launcher.patch.yml");

    public async Task<IReadOnlyList<ExtensionEntry>> ListAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<ExtensionEntry>();
        await ListPluginsAsync(instance, result, cancellationToken);
        await ListSkillsAsync(instance, result, cancellationToken);
        foreach (var server in await ReadMcpAsync(instance, cancellationToken))
        {
            result.Add(new ExtensionEntry(
                $"mcp:{server.ServerName}",
                ExtensionKind.Mcp,
                server.ServerName,
                null,
                server.Transport == "stdio" ? server.Command : server.Url,
                GetMcpMetadataPath(instance),
                server.Enabled,
                true));
        }

        await ListPresetsAsync(instance, result, cancellationToken);
        // Workflow execution is supplied by the shipped standard preset. It is
        // shown as a real built-in capability instead of pretending that a
        // made-up workflow directory is consumed by DSh.
        result.Add(new ExtensionEntry(
            "workflow:standard",
            ExtensionKind.Workflow,
            "Workflow",
            null,
            "由 Agent Preset 的 workflow 工具提供",
            "内置 standard preset",
            true,
            false));

        return result
            .GroupBy(entry => $"{entry.Kind}:{entry.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> InstallPluginAsync(
        ManagerInstance instance,
        string packageSpec,
        NodeRuntimeInfo? nodeRuntime,
        CancellationToken cancellationToken = default)
    {
        return await RunPluginCommandAsync(instance, "add", packageSpec, nodeRuntime, cancellationToken);
    }

    public async Task<string> UpdatePluginAsync(
        ManagerInstance instance,
        string packageSpec,
        NodeRuntimeInfo? nodeRuntime,
        CancellationToken cancellationToken = default)
    {
        return await RunPluginCommandAsync(instance, "update", packageSpec, nodeRuntime, cancellationToken);
    }

    public async Task<string> RemovePluginAsync(
        ManagerInstance instance,
        string packageSpec,
        NodeRuntimeInfo? nodeRuntime,
        CancellationToken cancellationToken = default)
    {
        return await RunPluginCommandAsync(instance, "remove", packageSpec, nodeRuntime, cancellationToken);
    }

    public Task SetPluginEnabledAsync(
        ManagerInstance instance,
        ExtensionEntry entry,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Kind != ExtensionKind.Plugin || !entry.Managed)
        {
            throw new InvalidOperationException("只有实例自己安装的 Plugin 可以启用或禁用。内置 bundle 不可修改。");
        }

        if (entry.Name.Length > 214 || !SafePackageName.IsMatch(entry.Name))
        {
            throw new InvalidDataException("Plugin 包名不符合 npm 包名格式。");
        }

        var profilePath = GetProfileManifestPath(instance);
        if (!File.Exists(profilePath))
        {
            throw new FileNotFoundException("实例的 web profile 尚未初始化。", profilePath);
        }

        var root = ReadJsonObject(profilePath);
        var dependencies = GetOrCreateObject(root, "dependencies");
        if (!dependencies.ContainsKey(entry.Name))
        {
            throw new InvalidOperationException($"Plugin {entry.Name} 不在当前实例的依赖中。");
        }

        var bundles = GetOrCreateBundles(root);
        var index = FindStringIndex(bundles, entry.Name);
        if (enabled && index < 0)
        {
            bundles.Add(entry.Name);
        }
        else if (!enabled && index >= 0)
        {
            bundles.RemoveAt(index);
        }

        WriteJsonAtomically(profilePath, root);
        return Task.CompletedTask;
    }

    public async Task<ExtensionEntry> ImportSkillAsync(
        ManagerInstance instance,
        string sourcePath,
        string? requestedName = null,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        cancellationToken.ThrowIfCancellationRequested();
        var source = NormalizeExistingPath(sourcePath, "Skill 源路径");
        RejectReparsePoint(source, "Skill 源路径");
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new FileNotFoundException("Skill 源路径不存在。", source);
        }

        var sourceIsDirectory = Directory.Exists(source);
        var sourceName = requestedName?.Trim();
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = sourceIsDirectory
                ? new DirectoryInfo(source).Name
                : Path.GetFileNameWithoutExtension(source);
        }

        var safeName = SafeSegment(sourceName, "Skill 名称");
        var skillRoot = Path.Combine(instance.DshHome, "skills");
        Directory.CreateDirectory(skillRoot);
        RejectReparsePoint(skillRoot, "Skill 根目录");
        var target = Path.Combine(skillRoot, safeName);
        EnsurePathDoesNotEscape(target, skillRoot);
        EnsureSourceDoesNotContainTarget(source, target);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"实例中已经存在同名 Skill：{safeName}。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (sourceIsDirectory)
        {
            CopyDirectoryWithoutReparsePoints(source, target);
        }
        else
        {
            Directory.CreateDirectory(target);
            File.Copy(source, Path.Combine(target, "SKILL.md"), overwrite: false);
        }

        var metadata = ParseSkillFrontmatter(Path.Combine(target, "SKILL.md"));
        if (metadata is null)
        {
            DeleteDirectoryIfOwned(target, Path.Combine(instance.DshHome, "skills"));
            throw new InvalidDataException("Skill 必须包含可识别的 SKILL.md frontmatter（至少需要 name 和 description）。");
        }

        await Task.CompletedTask;
        return new ExtensionEntry(
            $"skill:{Path.GetFullPath(Path.Combine(target, "SKILL.md"))}",
            ExtensionKind.Skill,
            metadata.Name,
            null,
            metadata.Description,
            Path.Combine(target, "SKILL.md"),
            true,
            true);
    }

    public Task RemoveSkillAsync(
        ManagerInstance instance,
        ExtensionEntry entry,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Kind != ExtensionKind.Skill || !entry.Managed)
        {
            throw new InvalidOperationException("只能删除当前实例导入的 Skill。");
        }

        var root = Path.GetFullPath(Path.Combine(instance.DshHome, "skills"));
        var target = Path.GetFullPath(entry.Location);
        EnsurePathDoesNotEscape(target, root);
        RejectReparsePoint(target, "Skill 目录");
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            throw new FileNotFoundException("Skill 不存在。", target);
        }

        if (Directory.Exists(target))
        {
            DeleteDirectoryIfOwned(target, root);
        }
        else
        {
            File.Delete(target);
        }
        return Task.CompletedTask;
    }

    public async Task AddMcpAsync(
        ManagerInstance instance,
        McpServerDefinition definition,
        NodeRuntimeInfo? nodeRuntime,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        ValidateMcp(definition);
        var mcpPlugin = (await ListAsync(instance, cancellationToken))
            .FirstOrDefault(entry => entry.Kind == ExtensionKind.Plugin && string.Equals(entry.Name, McpPackage, StringComparison.OrdinalIgnoreCase));
        if (mcpPlugin is null)
        {
            await InstallPluginAsync(instance, McpPackage, nodeRuntime, cancellationToken);
        }
        else if (!mcpPlugin.Enabled)
        {
            await SetPluginEnabledAsync(instance, mcpPlugin, true, cancellationToken);
        }

        await AddMcpConfigurationAsync(instance, definition, cancellationToken);
    }

    public async Task AddMcpConfigurationAsync(
        ManagerInstance instance,
        McpServerDefinition definition,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        ValidateMcp(definition);
        var definitions = (await ReadMcpAsync(instance, cancellationToken)).ToList();
        if (definitions.Any(item => string.Equals(item.ServerName, definition.ServerName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException($"MCP serverName 已存在：{definition.ServerName}。");
        }

        definitions.Add(definition);
        await WriteMcpAsync(instance, definitions, cancellationToken);
    }

    public async Task RemoveMcpAsync(
        ManagerInstance instance,
        string serverName,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        if (!SafeServerName.IsMatch(serverName))
        {
            throw new ArgumentException("MCP serverName 格式无效。", nameof(serverName));
        }

        var definitions = (await ReadMcpAsync(instance, cancellationToken)).ToList();
        var removed = definitions.RemoveAll(item => string.Equals(item.ServerName, serverName, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            throw new FileNotFoundException("MCP server 不存在。", serverName);
        }

        await WriteMcpAsync(instance, definitions, cancellationToken);
    }

    public async Task SetMcpEnabledAsync(
        ManagerInstance instance,
        string serverName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        var definitions = (await ReadMcpAsync(instance, cancellationToken)).ToList();
        var index = definitions.FindIndex(item => string.Equals(item.ServerName, serverName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new FileNotFoundException("MCP server 不存在。", serverName);
        }

        definitions[index] = definitions[index] with { Enabled = enabled };
        await WriteMcpAsync(instance, definitions, cancellationToken);
    }

    public async Task<ExtensionEntry> ImportPresetAsync(
        ManagerInstance instance,
        string sourcePath,
        string? requestedName = null,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        cancellationToken.ThrowIfCancellationRequested();
        var source = NormalizeExistingPath(sourcePath, "Agent Preset 源路径");
        RejectReparsePoint(source, "Agent Preset 源路径");
        if (!Directory.Exists(source) || !File.Exists(Path.Combine(source, "agent.cordis.yml")))
        {
            throw new InvalidDataException("Agent Preset 必须是包含 agent.cordis.yml 的目录。");
        }

        var sourceName = string.IsNullOrWhiteSpace(requestedName)
            ? new DirectoryInfo(source).Name
            : requestedName.Trim();
        var safeName = SafeSegment(sourceName, "Agent Preset 名称");
        var root = Path.Combine(instance.DshHome, ".agent-presets");
        var target = Path.Combine(root, safeName);
        EnsurePathDoesNotEscape(target, root);
        EnsureSourceDoesNotContainTarget(source, target);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"实例中已经存在同名 Agent Preset：{safeName}。");
        }

        Directory.CreateDirectory(root);
        RejectReparsePoint(root, "Agent Preset 根目录");
        CopyDirectoryWithoutReparsePoints(source, target);
        await Task.CompletedTask;
        return new ExtensionEntry(
            $"preset:{safeName}",
            ExtensionKind.Preset,
            safeName,
            null,
            "用户导入的 Agent Preset",
            target,
            true,
            true);
    }

    public Task RemovePresetAsync(
        ManagerInstance instance,
        ExtensionEntry entry,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped(instance);
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Kind != ExtensionKind.Preset || !entry.Managed)
        {
            throw new InvalidOperationException("只能删除当前实例导入的 Agent Preset。");
        }

        var root = Path.GetFullPath(Path.Combine(instance.DshHome, ".agent-presets"));
        var target = Path.GetFullPath(entry.Location);
        EnsurePathDoesNotEscape(target, root);
        RejectReparsePoint(target, "Agent Preset 目录");
        DeleteDirectoryIfOwned(target, root);
        return Task.CompletedTask;
    }

    private Task ListPluginsAsync(
        ManagerInstance instance,
        ICollection<ExtensionEntry> result,
        CancellationToken cancellationToken)
    {
        var profilePath = GetProfileManifestPath(instance);
        if (!File.Exists(profilePath))
        {
            return Task.CompletedTask;
        }

        JsonObject root;
        try
        {
            root = ReadJsonObject(profilePath);
        }
        catch
        {
            return Task.CompletedTask;
        }

        var dependencies = root["dependencies"] as JsonObject;
        var bundles = GetBundles(root);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in bundles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageName = GetString(bundle);
            if (string.IsNullOrWhiteSpace(packageName) || !names.Add(packageName))
            {
                continue;
            }

            var manifest = TryReadPackageManifest(profilePath, packageName);
            var builtIn = string.Equals(packageName, BuiltInBase, StringComparison.OrdinalIgnoreCase)
                || string.Equals(packageName, BuiltInWeb, StringComparison.OrdinalIgnoreCase);
            result.Add(new ExtensionEntry(
                $"plugin:{packageName}",
                ExtensionKind.Plugin,
                packageName,
                GetString(dependencies, packageName) ?? GetString(manifest, "version"),
                GetString(manifest, "description"),
                profilePath,
                true,
                !builtIn));
        }

        if (dependencies is null)
        {
            return Task.CompletedTask;
        }

        foreach (var dependency in dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!names.Add(dependency.Key)
                || string.Equals(dependency.Key, BuiltInBase, StringComparison.OrdinalIgnoreCase)
                || string.Equals(dependency.Key, BuiltInWeb, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var manifest = TryReadPackageManifest(profilePath, dependency.Key);
            result.Add(new ExtensionEntry(
                $"plugin:{dependency.Key}",
                ExtensionKind.Plugin,
                dependency.Key,
                GetString(dependency.Value),
                GetString(manifest, "description"),
                profilePath,
                false,
                true));
        }

        return Task.CompletedTask;
    }

    private Task ListSkillsAsync(
        ManagerInstance instance,
        ICollection<ExtensionEntry> result,
        CancellationToken cancellationToken)
    {
        var roots = new[]
        {
            (Path.Combine(instance.DshHome, "skills"), true),
            (Path.Combine(instance.DshHome, ".agents", "skills"), false),
            (Path.Combine(instance.RootPath, ".dsh", "skills"), false),
            (Path.Combine(instance.RootPath, ".agents", "skills"), false)
        };

        foreach (var (root, managed) in roots)
        {
            if (!Directory.Exists(root) || IsReparsePoint(root))
            {
                continue;
            }

            foreach (var child in EnumerateEntries(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(child))
                {
                    continue;
                }

                var candidate = Directory.Exists(child)
                    ? Path.Combine(child, "SKILL.md")
                    : child;
                if (!candidate.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                {
                    continue;
                }

                var metadata = ParseSkillFrontmatter(candidate);
                if (metadata is null)
                {
                    continue;
                }

                result.Add(new ExtensionEntry(
                    $"skill:{candidate}",
                    ExtensionKind.Skill,
                    metadata.Name,
                    null,
                    metadata.Description,
                    candidate,
                    true,
                    managed));
            }
        }

        return Task.CompletedTask;
    }

    private static async Task ListPresetsAsync(
        ManagerInstance instance,
        ICollection<ExtensionEntry> result,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(instance.DshHome, ".agent-presets");
        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            return;
        }

        foreach (var directory in EnumerateEntries(root).Where(Directory.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(directory) || !File.Exists(Path.Combine(directory, "agent.cordis.yml")))
            {
                continue;
            }

            var name = new DirectoryInfo(directory).Name;
            result.Add(new ExtensionEntry(
                $"preset:{name}",
                ExtensionKind.Preset,
                name,
                null,
                "用户导入的 Agent Preset",
                directory,
                true,
                true));
        }

        await Task.CompletedTask;
    }

    private async Task<string> RunPluginCommandAsync(
        ManagerInstance instance,
        string action,
        string packageSpec,
        NodeRuntimeInfo? nodeRuntime,
        CancellationToken cancellationToken)
    {
        EnsureStopped(instance);
        ValidatePackageSpec(packageSpec);
        if (action is not ("add" or "update" or "remove"))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        var startInfo = CreatePluginStartInfo(instance, action, packageSpec, nodeRuntime);
        var output = await RunProcessAsync(startInfo, cancellationToken);
        if (output.ExitCode != 0)
        {
            throw new InvalidOperationException($"Plugin {action} 失败（退出码 {output.ExitCode}）。{Tail(output.Output)}");
        }

        return Tail(output.Output);
    }

    private ProcessStartInfo CreatePluginStartInfo(
        ManagerInstance instance,
        string action,
        string packageSpec,
        NodeRuntimeInfo? nodeRuntime)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = instance.RootPath,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (instance.Kind == InstanceKind.Source)
        {
            var project = _sourceInspector.Inspect(instance.RootPath);
            var entrypoint = project.BuiltCliEntrypoint
                ?? SourceProjectInspector.TryFindBuiltCliEntrypoint(instance.RootPath)
                ?? throw new InvalidOperationException("Source 尚未完成构建，无法管理 Plugin。");
            if (nodeRuntime is null || !nodeRuntime.IsAvailable || !nodeRuntime.IsCompatibleWithDshSource
                || string.IsNullOrWhiteSpace(nodeRuntime.ExecutablePath))
            {
                throw new InvalidOperationException("Source Plugin 管理需要满足 DSh 要求的 Node.js。");
            }

            startInfo.FileName = nodeRuntime.ExecutablePath;
            startInfo.ArgumentList.Add(entrypoint);
            startInfo.ArgumentList.Add("plugin");
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add(ProfileName);
            startInfo.ArgumentList.Add(action);
            startInfo.ArgumentList.Add(packageSpec);
        }
        else
        {
            var executable = instance.DshExecutablePath
                ?? throw new InvalidOperationException("实例没有 DSh 可执行入口。");
            if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                var commandLine = $"\"{executable}\" plugin --profile {ProfileName} {action} {QuoteCmdArgument(packageSpec)}";
                startInfo.Arguments = $"/d /s /c \"{commandLine}\"";
            }
            else
            {
                startInfo.FileName = executable;
                startInfo.ArgumentList.Add("plugin");
                startInfo.ArgumentList.Add("--profile");
                startInfo.ArgumentList.Add(ProfileName);
                startInfo.ArgumentList.Add(action);
                startInfo.ArgumentList.Add(packageSpec);
            }
        }

        SetInstanceEnvironment(startInfo, instance);
        return startInfo;
    }

    private async Task<IReadOnlyList<McpServerDefinition>> ReadMcpAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken)
    {
        var path = GetMcpMetadataPath(instance);
        if (!File.Exists(path))
        {
            return Array.Empty<McpServerDefinition>();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var stored = await JsonSerializer.DeserializeAsync<StoredMcpFile>(stream, JsonOptions, cancellationToken);
            return stored?.Servers?.Select(ToDefinition).Where(definition => definition is not null).Cast<McpServerDefinition>().ToArray()
                ?? Array.Empty<McpServerDefinition>();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"MCP 配置格式无效：{path}", ex);
        }
    }

    private async Task WriteMcpAsync(
        ManagerInstance instance,
        IReadOnlyList<McpServerDefinition> definitions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadataPath = GetMcpMetadataPath(instance);
        var metadataDirectory = Path.GetDirectoryName(metadataPath)!;
        Directory.CreateDirectory(metadataDirectory);
        var stored = new StoredMcpFile
        {
            Servers = definitions.Select(ToStored).ToList()
        };
        var json = JsonSerializer.Serialize(stored, JsonOptions);
        WriteTextAtomically(metadataPath, json + Environment.NewLine);
        WriteLauncherPatch(instance, definitions);
        await Task.CompletedTask;
    }

    private static void WriteLauncherPatch(ManagerInstance instance, IReadOnlyList<McpServerDefinition> definitions)
    {
        var enabled = definitions.Where(item => item.Enabled).ToArray();
        var builder = new StringBuilder();
        foreach (var definition in enabled)
        {
            builder.AppendLine($"- id: {YamlString($"launcher-mcp-{definition.ServerName}")}");
            builder.AppendLine($"  name: {YamlString(McpPackage)}");
            builder.AppendLine("  config:");
            builder.AppendLine($"    transport: {YamlString(definition.Transport)}");
            builder.AppendLine($"    serverName: {YamlString(definition.ServerName)}");
            if (definition.Transport == "stdio")
            {
                builder.AppendLine($"    command: {YamlString(definition.Command)}");
                builder.AppendLine($"    args: {JsonSerializer.Serialize(definition.Arguments)}");
                builder.AppendLine($"    cwd: {YamlString(definition.WorkingDirectory ?? string.Empty)}");
                builder.AppendLine($"    env: {JsonSerializer.Serialize(definition.Headers)}");
            }
            else
            {
                builder.AppendLine($"    url: {YamlString(definition.Url ?? string.Empty)}");
                builder.AppendLine($"    headers: {JsonSerializer.Serialize(definition.Headers)}");
            }
            builder.AppendLine("    failOnStartupError: false");
        }

        var patchPath = Path.Combine(instance.DshHome, "launcher.patch.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(patchPath)!);
        WriteTextAtomically(patchPath, builder.Length == 0 ? "[]\n" : builder.ToString());
    }

    private static void ValidateMcp(McpServerDefinition definition)
    {
        if (!SafeServerName.IsMatch(definition.ServerName))
        {
            throw new ArgumentException("MCP serverName 必须匹配 [A-Za-z0-9_-]{1,32}。", nameof(definition));
        }

        if (definition.Transport is not ("stdio" or "streamable-http"))
        {
            throw new ArgumentException("MCP transport 只能是 stdio 或 streamable-http。", nameof(definition));
        }

        if (definition.Transport == "stdio")
        {
            if (string.IsNullOrWhiteSpace(definition.Command) || ContainsControlCharacters(definition.Command))
            {
                throw new ArgumentException("stdio MCP command 不能为空或包含换行。", nameof(definition));
            }
        }
        else if (!Uri.TryCreate(definition.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("streamable-http MCP URL 必须是 HTTP(S) 地址。", nameof(definition));
        }

        if (definition.Arguments.Any(ContainsControlCharacters)
            || definition.Headers.Any(pair => ContainsControlCharacters(pair.Key) || ContainsControlCharacters(pair.Value)))
        {
            throw new ArgumentException("MCP 配置不能包含换行或控制字符。", nameof(definition));
        }

        ValidateMcpHeaderDictionary(definition.Headers);
        if (definition.Command.Length > 4096
            || definition.Arguments.Count > 128
            || definition.Arguments.Any(argument => argument.Length > 4096)
            || (definition.WorkingDirectory?.Length ?? 0) > 4096)
        {
            throw new ArgumentException("MCP command、参数或工作目录过长。", nameof(definition));
        }
    }

    private void EnsureStopped(ManagerInstance instance)
    {
        if (_isRunning(instance.Id))
        {
            throw new InvalidOperationException("实例正在运行，请先停止实例再修改 Plugin、Skill、MCP 或 Agent Preset。");
        }
    }

    private static string GetProfileManifestPath(ManagerInstance instance) =>
        Path.Combine(instance.DshHome, "profiles", ProfileName, "package.json");

    private static JsonObject ReadJsonObject(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
        return node ?? throw new InvalidDataException($"JSON 配置必须是对象：{path}");
    }

    private static JsonObject? ReadPackageManifest(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? TryReadPackageManifest(string profilePath, string packageName)
    {
        if (packageName.Length > 214 || !SafePackageName.IsMatch(packageName))
        {
            return null;
        }

        var packagePath = Path.Combine(
            Path.GetDirectoryName(profilePath)!,
            "node_modules",
            packageName,
            "package.json");
        return ReadPackageManifest(packagePath);
    }

    private static JsonObject GetOrCreateObject(JsonObject root, string name)
    {
        if (root[name] is JsonObject objectValue)
        {
            return objectValue;
        }

        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    private static JsonArray GetOrCreateBundles(JsonObject root)
    {
        var dsh = GetOrCreateObject(root, "dsh");
        var profile = GetOrCreateObject(dsh, "profile");
        if (profile["bundles"] is JsonArray bundles)
        {
            return bundles;
        }

        var created = new JsonArray();
        profile["bundles"] = created;
        return created;
    }

    private static JsonArray GetBundles(JsonObject root)
    {
        return root["dsh"] is JsonObject dsh
            && dsh["profile"] is JsonObject profile
            && profile["bundles"] is JsonArray bundles
            ? bundles
            : new JsonArray();
    }

    private static int FindStringIndex(JsonArray array, string value)
    {
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is JsonValue node
                && string.Equals(node.GetValue<string>(), value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string? GetString(JsonObject? objectValue, string name)
    {
        if (objectValue is null || objectValue[name] is not JsonValue value)
        {
            return null;
        }

        try
        {
            return value.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        try
        {
            return value.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static void WriteJsonAtomically(string path, JsonObject root)
    {
        WriteTextAtomically(path, root.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("配置文件没有父目录。");
        Directory.CreateDirectory(directory);
        RejectReparsePoint(directory, "配置文件目录");
        RejectReparsePoint(path, "配置文件");
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
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

    private static void SetInstanceEnvironment(ProcessStartInfo startInfo, ManagerInstance instance)
    {
        startInfo.Environment["DSH_HOME"] = instance.DshHome;
        // The DSh skill provider otherwise falls back to the user's global
        // .agents directory, which would leak Skills across instances.
        startInfo.Environment["DSH_AGENTS_HOME"] = Path.Combine(instance.DshHome, ".agents");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("外部命令无法启动。");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            KillProcessTree(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"外部命令超过 {CommandTimeout.TotalMinutes:0} 分钟。");
        }

        var output = $"{await standardOutput}\n{await standardError}";
        return new ProcessResult(process.ExitCode, output);
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // The original timeout/operation error is more useful than cleanup noise.
        }
    }

    private static string NormalizeExistingPath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{label}不能为空。", nameof(path));
        }

        return Path.GetFullPath(path.Trim());
    }

    private static void ValidatePackageSpec(string packageSpec)
    {
        if (string.IsNullOrWhiteSpace(packageSpec) || packageSpec.Length > 512 || ContainsControlCharacters(packageSpec))
        {
            throw new ArgumentException("Plugin 来源不能为空、不能超过 512 个字符，也不能包含换行。", nameof(packageSpec));
        }

        // Installed DSh is reached through a .cmd shim. Refusing cmd syntax
        // characters here prevents a package name from becoming a second shell
        // command when the shim is invoked.
        if (packageSpec.IndexOfAny(['&', '|', '<', '>', '^', '%', '"']) >= 0)
        {
            throw new ArgumentException("Plugin 来源包含 Windows 命令行保留字符。", nameof(packageSpec));
        }
    }

    private static string QuoteCmdArgument(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string SafeSegment(string? value, string label)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized is "." or "..")
        {
            throw new ArgumentException($"{label}不能为空。", nameof(value));
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        }

        var result = builder.ToString().Trim('.', ' ');
        if (result.Length == 0 || result.Length > 80)
        {
            throw new ArgumentException($"{label}不符合安全路径名称要求。", nameof(value));
        }

        return result;
    }

    private static void EnsurePathDoesNotEscape(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("目标路径不在实例管理目录内。");
        }
    }

    private static void RejectReparsePoint(string path, string label)
    {
        if (IsReparsePoint(path))
        {
            throw new IOException($"{label}不能是符号链接或重解析点。");
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> EnumerateEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static void CopyDirectoryWithoutReparsePoints(string source, string target)
    {
        RejectReparsePoint(source, "复制源");
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            RejectReparsePoint(entry, "复制源中的文件");
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyDirectoryWithoutReparsePoints(entry, destination);
            }
            else
            {
                File.Copy(entry, destination, overwrite: false);
            }
        }
    }

    private static void EnsureSourceDoesNotContainTarget(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        var normalizedSource = Path.GetFullPath(source)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(target);
        if (string.Equals(normalizedTarget, normalizedSource, StringComparison.OrdinalIgnoreCase)
            || normalizedTarget.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不能把包含目标目录的目录导入到它自己的子目录中。");
        }
    }

    private static void DeleteDirectoryIfOwned(string directory, string root)
    {
        EnsurePathDoesNotEscape(directory, root);
        if (string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不能删除实例生态根目录。");
        }

        Directory.Delete(directory, recursive: true);
    }

    private static SkillMetadata? ParseSkillFrontmatter(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
            var first = reader.ReadLine();
            if (!string.Equals(first?.Trim(), "---", StringComparison.Ordinal))
            {
                return null;
            }

            string? name = null;
            string? description = null;
            for (var count = 0; count < 128; count++)
            {
                var line = reader.ReadLine();
                if (line is null || line.Trim() == "---")
                {
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

            return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description)
                ? null
                : new SkillMetadata(name, description);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ValidateMcpHeaderDictionary(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count > 100)
        {
            throw new ArgumentException("MCP headers 数量过多。", nameof(headers));
        }

        if (headers.Any(pair => pair.Key.Length > 256 || pair.Value.Length > 4096))
        {
            throw new ArgumentException("MCP header 名称或值过长。", nameof(headers));
        }
    }

    private static bool ContainsControlCharacters(string value) => value.Any(character => char.IsControl(character));

    private static string YamlString(string value) => JsonSerializer.Serialize(value);

    private static string Tail(string value)
    {
        const int max = 5000;
        return value.Length <= max ? value.Trim() : value[^max..].Trim();
    }

    private static StoredMcpServer ToStored(McpServerDefinition definition) => new()
    {
        ServerName = definition.ServerName,
        Transport = definition.Transport,
        Command = definition.Command,
        Arguments = definition.Arguments.ToList(),
        Url = definition.Url,
        Headers = new Dictionary<string, string>(definition.Headers, StringComparer.Ordinal),
        WorkingDirectory = definition.WorkingDirectory,
        Enabled = definition.Enabled
    };

    private static McpServerDefinition? ToDefinition(StoredMcpServer? stored)
    {
        if (stored is null || string.IsNullOrWhiteSpace(stored.ServerName) || string.IsNullOrWhiteSpace(stored.Transport))
        {
            return null;
        }

        return new McpServerDefinition(
            stored.ServerName,
            stored.Transport,
            stored.Command ?? string.Empty,
            stored.Arguments ?? new List<string>(),
            stored.Url,
            stored.Headers ?? new Dictionary<string, string>(),
            stored.WorkingDirectory,
            stored.Enabled);
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed record SkillMetadata(string Name, string Description);

    private sealed class StoredMcpFile
    {
        public List<StoredMcpServer> Servers { get; set; } = new();
    }

    private sealed class StoredMcpServer
    {
        public string ServerName { get; set; } = string.Empty;
        public string Transport { get; set; } = "stdio";
        public string? Command { get; set; }
        public List<string>? Arguments { get; set; }
        public string? Url { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public string? WorkingDirectory { get; set; }
        public bool Enabled { get; set; } = true;
    }
}
