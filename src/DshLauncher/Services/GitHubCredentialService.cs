using System.Security.Cryptography;
using System.Text;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Stores the optional GitHub token with Windows DPAPI (CurrentUser). Environment
/// variables remain a zero-write fallback for portable or managed environments.
/// </summary>
public sealed class GitHubCredentialService
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("DSH Launcher GitHub token v1");

    private readonly VersionSettingsService _settingsService;

    public GitHubCredentialService(VersionSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public GitHubCredentialState ReadState()
    {
        var settings = _settingsService.ReadLauncherSettings();
        if (!string.IsNullOrWhiteSpace(settings.GitHubTokenCiphertext))
        {
            return new GitHubCredentialState(ReadProtected(settings.GitHubTokenCiphertext), "Launcher 设置");
        }

        var environmentToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var source = "GITHUB_TOKEN 环境变量";
        if (string.IsNullOrWhiteSpace(environmentToken))
        {
            environmentToken = Environment.GetEnvironmentVariable("GH_TOKEN");
            source = "GH_TOKEN 环境变量";
        }

        return string.IsNullOrWhiteSpace(environmentToken)
            ? new GitHubCredentialState(null, null)
            : new GitHubCredentialState(environmentToken.Trim(), source);
    }

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var plaintext = Encoding.UTF8.GetBytes(token.Trim());
        byte[]? encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            var settings = _settingsService.ReadLauncherSettings();
            settings.GitHubTokenCiphertext = Convert.ToBase64String(encrypted);
            _settingsService.SaveLauncherSettings(settings);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    public void Clear()
    {
        var settings = _settingsService.ReadLauncherSettings();
        if (string.IsNullOrWhiteSpace(settings.GitHubTokenCiphertext))
        {
            return;
        }

        settings.GitHubTokenCiphertext = null;
        _settingsService.SaveLauncherSettings(settings);
    }

    private static string ReadProtected(string ciphertext)
    {
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            encrypted = Convert.FromBase64String(ciphertext);
            plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var token = Encoding.UTF8.GetString(plaintext).Trim();
            if (token.Length == 0)
            {
                throw new InvalidDataException("已保存的 GitHub Token 为空。");
            }

            return token;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("已保存的 GitHub Token 数据格式无效。", ex);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("GitHub Token 无法由当前 Windows 用户解密。", ex);
        }
        finally
        {
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }
}

public sealed record GitHubCredentialState(string? Token, string? Source)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token);
}
