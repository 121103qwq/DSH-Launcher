using System.Text;
using System.Text.Json;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Stores Launcher-only provider enable flags without changing DSh's settings schema.
/// </summary>
public sealed class ProviderStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string GetStatePath(ManagerInstance instance) =>
        Path.Combine(instance.DshHome, ".dsh-launcher", "providers.json");

    public bool IsEnabled(ManagerInstance instance, string provider)
    {
        var states = Read(instance);
        return !states.TryGetValue(provider, out var enabled) || enabled;
    }

    public IReadOnlyDictionary<string, bool> Read(ManagerInstance instance)
    {
        var path = GetStatePath(instance);
        if (!File.Exists(path))
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, bool>>(
                File.ReadAllText(path, Encoding.UTF8),
                JsonOptions);
            return stored is null
                ? new Dictionary<string, bool>(StringComparer.Ordinal)
                : new Dictionary<string, bool>(stored, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Provider 状态文件格式无效：{path}", ex);
        }
    }

    public void SetEnabled(ManagerInstance instance, string provider, bool enabled)
    {
        var normalized = NormalizeProvider(provider);
        var states = Read(instance).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (enabled)
        {
            states.Remove(normalized);
        }
        else
        {
            states[normalized] = false;
        }

        var path = GetStatePath(instance);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Provider 状态文件没有父目录。");
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(states, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    private static string NormalizeProvider(string provider)
    {
        var normalized = provider.Trim();
        if (normalized.Length == 0
            || normalized.Length > 128
            || normalized.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("Provider 名称格式无效。", nameof(provider));
        }

        return normalized;
    }
}
