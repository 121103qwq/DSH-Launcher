using System.IO;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Stores Launcher-wide Coding model policy. Workspace keys are the real
/// working directories carried by DSh session headers, not Launcher sync groups.
/// </summary>
public sealed class CodingModelPolicyService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly LauncherPaths _paths;

    public CodingModelPolicyService(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
    }

    public string StoragePath => _paths.CodingModelPoliciesPath;

    public CodingModelPolicyData Read()
    {
        if (!File.Exists(StoragePath))
        {
            return new CodingModelPolicyData();
        }

        try
        {
            var data = JsonSerializer.Deserialize<CodingModelPolicyData>(
                File.ReadAllText(StoragePath, Encoding.UTF8),
                JsonOptions) ?? new CodingModelPolicyData();
            return Normalize(data);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException)
        {
            throw new InvalidDataException($"全局 Coding 模型规则格式无效：{StoragePath}", ex);
        }
    }

    public void SetGlobalDefault(CodingModelSelection selection)
    {
        var data = Read();
        data.GlobalDefault = NormalizeSelection(selection);
        Write(data);
    }

    public void SetWorkspaceSelection(string workingDirectory, CodingModelSelection? selection)
    {
        var normalizedDirectory = NormalizeWorkingDirectory(workingDirectory);
        var data = Read();
        data.DshWorkspaces.RemoveAll(item =>
            string.Equals(item.WorkingDirectory, normalizedDirectory, StringComparison.OrdinalIgnoreCase));
        if (selection is not null)
        {
            data.DshWorkspaces.Add(new CodingWorkspaceModelPolicy(
                normalizedDirectory,
                NormalizeSelection(selection)));
        }

        Write(data);
    }

    public void SetSessionSelection(
        string instanceId,
        string sessionId,
        CodingModelSelection? selection)
    {
        var normalizedInstanceId = NormalizeIdentifier(instanceId, "实例 ID");
        var normalizedSessionId = NormalizeIdentifier(sessionId, "会话 ID");
        var data = Read();
        data.Sessions.RemoveAll(item =>
            string.Equals(item.InstanceId, normalizedInstanceId, StringComparison.Ordinal)
            && string.Equals(item.SessionId, normalizedSessionId, StringComparison.Ordinal));
        if (selection is not null)
        {
            data.Sessions.Add(new CodingSessionModelPolicy(
                normalizedInstanceId,
                normalizedSessionId,
                NormalizeSelection(selection)));
        }

        Write(data);
    }

    public CodingModelSelection? Resolve(
        string instanceId,
        string sessionId,
        string? workingDirectory)
    {
        var data = Read();
        var session = data.Sessions.FirstOrDefault(item =>
            string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal)
            && string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
        if (session is not null)
        {
            return session.Selection;
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            string normalizedDirectory;
            try
            {
                normalizedDirectory = NormalizeWorkingDirectory(workingDirectory);
            }
            catch (ArgumentException)
            {
                normalizedDirectory = workingDirectory.Trim();
            }

            var workspace = data.DshWorkspaces.FirstOrDefault(item =>
                string.Equals(item.WorkingDirectory, normalizedDirectory, StringComparison.OrdinalIgnoreCase));
            if (workspace is not null)
            {
                return workspace.Selection;
            }
        }

        return data.GlobalDefault;
    }

    public CodingModelSelection? ReadWorkspaceSelection(string workingDirectory)
    {
        var normalized = NormalizeWorkingDirectory(workingDirectory);
        return Read().DshWorkspaces.FirstOrDefault(item =>
            string.Equals(item.WorkingDirectory, normalized, StringComparison.OrdinalIgnoreCase))?.Selection;
    }

    public CodingModelSelection? ReadSessionSelection(string instanceId, string sessionId) =>
        Read().Sessions.FirstOrDefault(item =>
            string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal)
            && string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))?.Selection;

    private void Write(CodingModelPolicyData data)
    {
        var normalized = Normalize(data);
        var directory = Path.GetDirectoryName(StoragePath)
            ?? throw new InvalidOperationException("全局 Coding 模型规则没有父目录。");
        Directory.CreateDirectory(directory);
        var temporary = $"{StoragePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(normalized, JsonOptions),
                new UTF8Encoding(false));
            File.Move(temporary, StoragePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static CodingModelPolicyData Normalize(CodingModelPolicyData data)
    {
        data.DshWorkspaces ??= new List<CodingWorkspaceModelPolicy>();
        data.Sessions ??= new List<CodingSessionModelPolicy>();
        data.GlobalDefault = data.GlobalDefault is null
            ? null
            : NormalizeSelection(data.GlobalDefault);
        data.DshWorkspaces = data.DshWorkspaces
            .Select(item => new CodingWorkspaceModelPolicy(
                NormalizeWorkingDirectory(item.WorkingDirectory),
                NormalizeSelection(item.Selection)))
            .GroupBy(item => item.WorkingDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.WorkingDirectory, StringComparer.OrdinalIgnoreCase)
            .ToList();
        data.Sessions = data.Sessions
            .Select(item => new CodingSessionModelPolicy(
                NormalizeIdentifier(item.InstanceId, "实例 ID"),
                NormalizeIdentifier(item.SessionId, "会话 ID"),
                NormalizeSelection(item.Selection)))
            .GroupBy(item => $"{item.InstanceId}\n{item.SessionId}", StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(item => item.InstanceId, StringComparer.Ordinal)
            .ThenBy(item => item.SessionId, StringComparer.Ordinal)
            .ToList();
        return data;
    }

    internal static CodingModelSelection NormalizeSelection(CodingModelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new CodingModelSelection(
            NormalizeIdentifier(selection.Provider, "Provider"),
            NormalizeIdentifier(selection.Model, "模型"),
            string.IsNullOrWhiteSpace(selection.ReasoningEffort)
                ? null
                : NormalizeIdentifier(selection.ReasoningEffort, "思考强度"));
    }

    internal static string NormalizeWorkingDirectory(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("DSh 工作区不能为空。", nameof(workingDirectory));
        }

        try
        {
            var fullPath = Path.GetFullPath(workingDirectory.Trim());
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrWhiteSpace(root)
                && string.Equals(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"DSh 工作区路径无效：{ex.Message}", nameof(workingDirectory));
        }
    }

    private static string NormalizeIdentifier(string value, string label)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > 256 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException($"{label}格式无效。", label);
        }

        return normalized;
    }
}
