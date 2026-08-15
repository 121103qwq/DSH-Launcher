using System.Text;
using System.Text.Json;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class SourceProjectInspector
{
    public SourceProjectInfo Inspect(string rootPath)
    {
        var normalizedRoot = NormalizeRoot(rootPath);
        if (normalizedRoot is null)
        {
            return Invalid(rootPath, "Source 目录不存在。");
        }

        var packagePath = Path.Combine(normalizedRoot, "package.json");
        if (!File.Exists(packagePath))
        {
            return Invalid(normalizedRoot, "Source 目录缺少 package.json。");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packagePath, Encoding.UTF8));
            var root = document.RootElement;
            var name = ReadString(root, "name");
            var version = ReadString(root, "version");
            var cliPackagePath = Path.Combine(normalizedRoot, "apps", "cli", "package.json");
            var nodeEngine = ReadNodeEngine(root) ?? ReadNodeEngine(cliPackagePath);
            var packageManagerSpec = ReadString(root, "packageManager");
            var (packageManager, packageManagerVersion) = ResolvePackageManager(packageManagerSpec, normalizedRoot);
            var hasBuildScript = HasBuildScript(root);
            var cliPackageName = ReadPackageName(cliPackagePath);
            var hasCliEntrypoint = File.Exists(cliPackagePath)
                || File.Exists(Path.Combine(normalizedRoot, "apps", "cli", "src", "bin.ts"))
                || TryFindBuiltCliEntrypoint(normalizedRoot) is not null;
            var builtCliEntrypoint = TryFindBuiltCliEntrypoint(normalizedRoot);
            var isDshSource = string.Equals(name, "deepseek-harness", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "@deepseek-ai/deepseek-harness", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cliPackageName, "@deepseek-ai/dsh", StringComparison.Ordinal);

            return new SourceProjectInfo(
                true,
                isDshSource,
                normalizedRoot,
                name,
                version,
                packageManager,
                packageManagerVersion,
                hasBuildScript,
                Directory.Exists(Path.Combine(normalizedRoot, "node_modules")),
                hasCliEntrypoint,
                null,
                builtCliEntrypoint,
                nodeEngine);
        }
        catch (JsonException ex)
        {
            return Invalid(normalizedRoot, $"package.json 格式无效：{ex.Message}");
        }
        catch (IOException ex)
        {
            return Invalid(normalizedRoot, $"读取 Source 目录失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Invalid(normalizedRoot, $"没有权限读取 Source 目录：{ex.Message}");
        }
    }

    private static SourceProjectInfo Invalid(string rootPath, string error) =>
        new(false, false, rootPath ?? string.Empty, null, null, null, null, false, false, false, error);

    public static string? TryFindBuiltCliEntrypoint(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var normalizedRoot = Path.GetFullPath(rootPath.Trim());
        var candidates = new[]
        {
            Path.Combine(normalizedRoot, "apps", "cli", "lib", "bin.js"),
            Path.Combine(normalizedRoot, "apps", "cli", "dist", "bin.js"),
            Path.Combine(normalizedRoot, "apps", "cli", "build", "bin.js"),
            Path.Combine(normalizedRoot, "apps", "cli", "lib", "bin.mjs"),
            Path.Combine(normalizedRoot, "apps", "cli", "dist", "bin.mjs")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? TryReadNodeEngine(string rootPath)
    {
        var normalizedRoot = NormalizeRoot(rootPath);
        if (normalizedRoot is null)
        {
            return null;
        }

        try
        {
            var rootPackagePath = Path.Combine(normalizedRoot, "package.json");
            using var rootDocument = JsonDocument.Parse(File.ReadAllText(rootPackagePath, Encoding.UTF8));
            var rootEngine = ReadNodeEngine(rootDocument.RootElement);
            if (!string.IsNullOrWhiteSpace(rootEngine))
            {
                return rootEngine;
            }

            var cliPackagePath = Path.Combine(normalizedRoot, "apps", "cli", "package.json");
            if (!File.Exists(cliPackagePath))
            {
                return null;
            }

            using var cliDocument = JsonDocument.Parse(File.ReadAllText(cliPackagePath, Encoding.UTF8));
            return ReadNodeEngine(cliDocument.RootElement);
        }
        catch (JsonException)
        {
            return null;
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

    private static string? NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var normalized = Path.GetFullPath(rootPath.Trim());
        return Directory.Exists(normalized)
            ? normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : null;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? ReadNodeEngine(JsonElement root)
    {
        return root.TryGetProperty("engines", out var engines)
            && engines.ValueKind == JsonValueKind.Object
            && engines.TryGetProperty("node", out var node)
            && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
    }

    private static string? ReadNodeEngine(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packagePath, Encoding.UTF8));
            return ReadNodeEngine(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
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

    private static string? ReadPackageName(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packagePath, Encoding.UTF8));
            return ReadString(document.RootElement, "name");
        }
        catch (JsonException)
        {
            return null;
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

    private static bool HasBuildScript(JsonElement root)
    {
        return root.TryGetProperty("scripts", out var scripts)
            && scripts.ValueKind == JsonValueKind.Object
            && scripts.TryGetProperty("build", out var build)
            && build.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(build.GetString());
    }

    private static (string Name, string? Version) ResolvePackageManager(string? specification, string root)
    {
        if (!string.IsNullOrWhiteSpace(specification))
        {
            var separator = specification.IndexOf('@', 1);
            return separator > 0
                ? (specification[..separator], specification[(separator + 1)..])
                : (specification, null);
        }

        if (File.Exists(Path.Combine(root, "pnpm-lock.yaml")))
        {
            return ("pnpm", null);
        }

        if (File.Exists(Path.Combine(root, "yarn.lock")))
        {
            return ("yarn", null);
        }

        if (File.Exists(Path.Combine(root, "bun.lockb")) || File.Exists(Path.Combine(root, "bun.lock")))
        {
            return ("bun", null);
        }

        return ("npm", null);
    }
}
