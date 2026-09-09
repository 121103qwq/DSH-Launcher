using System.Security.Cryptography;
using System.Text;

namespace DshLauncher.Models;

/// <summary>
/// 实例级环境变量（存放在该实例的 version-settings.json，启动 dsh 时注入进程环境）。
///
/// 约定：
/// - DSH_HOME / DSH_AGENTS_HOME / PATH 为保留项，用户不能覆盖（Launcher 自己管理）；
/// - 名称非法、数量/长度超限的条目在读取与保存时都会被丢弃（防御手改文件）；
/// - 含 KEY/TOKEN/SECRET/PASSWORD/CREDENTIAL/AUTH 的值视为敏感值，落盘前用
///   DPAPI（CurrentUser）加密并加 <c>dpapi:</c> 前缀，内存中始终是明文。
/// </summary>
public static class DshEnvironmentVariables
{
    public const int MaximumCount = 64;
    public const int MaximumNameLength = 128;
    public const int MaximumValueLength = 4096;
    public const string ProtectedPrefix = "dpapi:";

    private static readonly string[] ReservedNames = { "DSH_HOME", "DSH_AGENTS_HOME", "PATH" };

    private static readonly string[] SensitiveMarkers =
    {
        "KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "AUTH"
    };

    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("DSH Launcher instance environment v1"));

    public static bool IsReserved(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && ReservedNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsValidName(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed)
            && trimmed.Length <= MaximumNameLength
            && trimmed[0] != '='
            && !trimmed.Contains('=')
            && !trimmed.Contains('\0')
            && !IsReserved(trimmed);
    }

    public static bool IsSensitive(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && SensitiveMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static bool IsProtectedValue(string? value) =>
        value is not null && value.StartsWith(ProtectedPrefix, StringComparison.Ordinal);

    /// <summary>清理用户/手改文件带来的非法条目，返回被丢弃的名称。</summary>
    public static IReadOnlyList<string> Sanitize(IDictionary<string, string>? source)
    {
        var rejected = new List<string>();
        if (source is null)
        {
            return rejected;
        }

        foreach (var pair in source.ToArray())
        {
            if (source.Count > MaximumCount)
            {
                rejected.Add(pair.Key ?? string.Empty);
                source.Remove(pair.Key!);
                continue;
            }

            var name = pair.Key?.Trim();
            if (!IsValidName(name)
                || pair.Value is null
                || pair.Value.Length > MaximumValueLength
                || pair.Value.Contains('\0'))
            {
                rejected.Add(pair.Key ?? string.Empty);
                source.Remove(pair.Key!);
                continue;
            }

            if (!string.Equals(name, pair.Key, StringComparison.Ordinal))
            {
                source.Remove(pair.Key!);
                source[name!] = pair.Value;
            }
        }

        return rejected;
    }

    public static bool TryProtect(string value, out string protectedValue)
    {
        protectedValue = string.Empty;
        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                Entropy,
                DataProtectionScope.CurrentUser);
            protectedValue = ProtectedPrefix + Convert.ToBase64String(cipher);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public static bool TryUnprotect(string value, out string plain)
    {
        plain = string.Empty;
        if (!IsProtectedValue(value))
        {
            return false;
        }

        try
        {
            var cipher = Convert.FromBase64String(value[ProtectedPrefix.Length..]);
            plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException
            or PlatformNotSupportedException
            or FormatException
            or ArgumentException)
        {
            return false;
        }
    }
}
