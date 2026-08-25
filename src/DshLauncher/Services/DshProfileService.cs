using System.IO;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

public static class DshProfileService
{
    public const string DefaultProfileName = "web";
    private const string WebBundlePackage = "@deepseek-ai/dsh-web-app";

    public static IReadOnlyList<string> ListProfiles(ManagerInstance instance)
    {
        var root = Path.Combine(instance.DshHome, "profiles");
        if (!Directory.Exists(root))
        {
            return new[] { DefaultProfileName };
        }

        RejectReparsePoint(root, "Profile 根目录");
        var profiles = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var name = Path.GetFileName(directory);
            if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                || !TryNormalizeName(name, out var normalized))
            {
                continue;
            }

            if (File.Exists(Path.Combine(directory, "package.json"))
                || File.Exists(Path.Combine(directory, "cordis.patch.yml"))
                || File.Exists(Path.Combine(directory, "cordis.yml")))
            {
                profiles.Add(normalized);
            }
        }

        if (profiles.Count == 0)
        {
            profiles.Add(DefaultProfileName);
        }

        return profiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => string.Equals(name, DefaultProfileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsWebProfile(ManagerInstance instance, string profileName)
    {
        var manifest = GetManifestPath(instance, profileName);
        if (!File.Exists(manifest) || IsReparsePoint(manifest))
        {
            return string.Equals(profileName, DefaultProfileName, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            if (root.TryGetProperty("dependencies", out var dependencies)
                && dependencies.ValueKind == JsonValueKind.Object
                && dependencies.TryGetProperty(WebBundlePackage, out _))
            {
                return true;
            }

            return root.TryGetProperty("dsh", out var dsh)
                && dsh.ValueKind == JsonValueKind.Object
                && dsh.TryGetProperty("profile", out var profile)
                && profile.ValueKind == JsonValueKind.Object
                && profile.TryGetProperty("bundles", out var bundles)
                && bundles.ValueKind == JsonValueKind.Array
                && bundles.EnumerateArray().Any(bundle =>
                    bundle.ValueKind == JsonValueKind.String
                    && string.Equals(bundle.GetString(), WebBundlePackage, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string GetProfileDirectory(ManagerInstance instance, string profileName) =>
        Path.Combine(instance.DshHome, "profiles", NormalizeName(profileName));

    public static string GetManifestPath(ManagerInstance instance, string profileName) =>
        Path.Combine(GetProfileDirectory(instance, profileName), "package.json");

    public static string NormalizeName(string? value)
    {
        if (!TryNormalizeName(value, out var normalized))
        {
            throw new InvalidDataException("Profile 名称格式无效。 ");
        }

        return normalized;
    }

    public static bool TryNormalizeName(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 64
            && normalized is not ("." or "..")
            && normalized.All(character => char.IsLetterOrDigit(character)
                || character is '.' or '_' or '-');
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void RejectReparsePoint(string path, string label)
    {
        if (IsReparsePoint(path))
        {
            throw new IOException($"{label}不能是重解析点。 ");
        }
    }
}
