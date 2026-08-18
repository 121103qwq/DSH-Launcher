using System.IO.Compression;
using System.IO;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed record PluginFailureReport(
    string ArchivePath,
    string InstanceName,
    string Operation,
    string PackageSpec,
    bool RollbackSucceeded,
    string RollbackMessage);

/// <summary>
/// Creates a local, post-rollback report that the current DSh instance can inspect.
/// This is intentionally not the shareable .dshpack format: the selected diagnostic
/// files are copied verbatim, including the official credentials file, because the
/// user explicitly requested an unredacted local handoff.
/// </summary>
public sealed class PluginFailureReportService
{
    private static readonly string[] DiagnosticFiles =
    {
        "settings.yaml",
        ".credentials.yaml",
        ".dsh-launcher/version-settings.json",
        ".dsh-launcher/providers.json",
        ".dsh-launcher/mcp.json",
        "profiles/web/package.json",
        "profiles/web/pnpm-lock.yaml",
        "profiles/web/package-lock.json",
        "profiles/web/yarn.lock",
        "profiles/web/pnpm-workspace.yaml",
        "profiles/web/cordis.yml",
        "profiles/web/cordis.patch.yml"
    };

    public PluginFailureReport Create(
        ManagerInstance instance,
        string operation,
        string packageSpec,
        Exception error,
        bool rollbackSucceeded,
        string rollbackMessage,
        string? snapshotPath)
    {
        var reportsDirectory = Path.Combine(instance.DshHome, ".dsh-launcher", "reports");
        EnsureDirectoryIsSafe(reportsDirectory);
        Directory.CreateDirectory(reportsDirectory);

        var archivePath = Path.Combine(
            reportsDirectory,
            $"plugin-failure-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.zip");

        var included = new List<ReportFile>();
        var missing = new List<string>();
        var errorText = BuildErrorText(
            instance,
            operation,
            packageSpec,
            error,
            rollbackSucceeded,
            rollbackMessage,
            snapshotPath);

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            AddTextEntry(archive, "error.txt", errorText);
            foreach (var relativePath in DiagnosticFiles)
            {
                var sourcePath = Path.Combine(
                    instance.DshHome,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(sourcePath) || IsReparsePoint(sourcePath))
                {
                    missing.Add(relativePath);
                    continue;
                }

                var entryName = $"files/{relativePath.Replace('\\', '/')}";
                AddFileEntry(archive, sourcePath, entryName);
                included.Add(new ReportFile(relativePath, new FileInfo(sourcePath).Length));
            }

            var manifest = new
            {
                format = "dsh-launcher-plugin-failure-v1",
                createdAt = DateTimeOffset.UtcNow,
                instanceId = instance.Id,
                instanceName = instance.Name,
                dshHome = instance.DshHome,
                operation,
                packageSpec,
                rollbackSucceeded,
                rollbackMessage,
                snapshotPath,
                includesCredentials = included.Any(file =>
                    string.Equals(file.RelativePath, ".credentials.yaml", StringComparison.OrdinalIgnoreCase)),
                includedFiles = included,
                missingFiles = missing,
                excludedByDesign = new[] { "sessions", "conversation files", "node_modules", "runtime processes" }
            };
            AddTextEntry(
                archive,
                "manifest.json",
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        return new PluginFailureReport(
            archivePath,
            instance.Name,
            operation,
            packageSpec,
            rollbackSucceeded,
            rollbackMessage);
    }

    private static string BuildErrorText(
        ManagerInstance instance,
        string operation,
        string packageSpec,
        Exception error,
        bool rollbackSucceeded,
        string rollbackMessage,
        string? snapshotPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("DSH Launcher Plugin failure report");
        builder.AppendLine($"Created (UTC): {DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"Instance: {instance.Name}");
        builder.AppendLine($"Instance ID: {instance.Id}");
        builder.AppendLine($"DSH_HOME: {instance.DshHome}");
        builder.AppendLine($"Operation: {operation}");
        builder.AppendLine($"Package: {packageSpec}");
        builder.AppendLine($"Rollback succeeded: {rollbackSucceeded}");
        builder.AppendLine($"Rollback result: {rollbackMessage}");
        builder.AppendLine($"Snapshot: {snapshotPath ?? "(none)"}");
        builder.AppendLine();
        builder.AppendLine("The following error is intentionally unredacted for local DSh diagnosis:");
        builder.AppendLine(error.ToString());
        return builder.ToString();
    }

    private static void AddFileEntry(ZipArchive archive, string sourcePath, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var target = entry.Open();
        source.CopyTo(target);
    }

    private static void AddTextEntry(ZipArchive archive, string entryName, string text)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void EnsureDirectoryIsSafe(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = new DirectoryInfo(fullPath);
        while (current is not null && current.Parent is not null)
        {
            if (current.Exists && IsReparsePoint(current.FullName))
            {
                throw new IOException($"诊断报告目录不能位于重解析点：{current.FullName}");
            }

            current = current.Parent;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record ReportFile(string RelativePath, long Length);
}
