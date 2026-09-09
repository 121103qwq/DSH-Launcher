using System.IO;

namespace DshLauncher.Services;

public sealed class LauncherPaths
{
#if DEBUG
    private const string TestRootVariable = "DSH_LAUNCHER_TEST_ROOT";
#endif
    private const string DataRootOverrideVariable = "DSH_LAUNCHER_DATA_ROOT";

    /// <summary>便携模式标记目录名：exe 旁存在该目录时，数据根改到 exe 同目录。</summary>
    public const string PortableDataDirectoryName = "launcher-data";

    public LauncherPaths(string? rootDirectory = null, string? executableDirectory = null)
    {
        ExecutableDirectory = Path.GetFullPath(executableDirectory ?? AppContext.BaseDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory ?? GetDefaultRoot(ExecutableDirectory));
    }

    public string RootDirectory { get; }

    /// <summary>Launcher 可执行文件所在目录（默认安装位置以此为基准，便于做成便携版）。</summary>
    public string ExecutableDirectory { get; }

    public string InstancesFilePath => Path.Combine(RootDirectory, "instances.json");

    public string InstancesDirectory => Path.Combine(RootDirectory, "instances");

    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

    public string MarketplaceCatalogPath => Path.Combine(RootDirectory, "marketplace.json");

    public string MarketplaceSourcesPath => Path.Combine(RootDirectory, "marketplace-sources.json");

    public string MarketplaceCachePath => Path.Combine(RootDirectory, "marketplace-cache.json");

    public string RuntimeCachePath => Path.Combine(RootDirectory, "runtime-cache.json");

    /// <summary>默认安装位置：exe 同目录下的 run_time（便携优先，不依赖用户文档目录）。</summary>
    public string PortableRuntimeDirectory => Path.Combine(ExecutableDirectory, "run_time");

    /// <summary>旧默认安装位置（&lt;数据根&gt;\runtime\dsh），exe 同目录不可写时兜底。</summary>
    public string ManagedDshRuntimeDirectory => Path.Combine(RootDirectory, "runtime", "dsh");

    /// <summary>便携版 Node.js 的安装目录（免管理员，不写系统 PATH）。</summary>
    public string PortableNodeDirectory => Path.Combine(RootDirectory, "node");

    public string VersionSettingsPath => Path.Combine(RootDirectory, "version-settings.json");

    public string GetInstanceDshHome(string instanceId) =>
        Path.Combine(InstancesDirectory, instanceId, "dsh-home");

    public string GetInstanceBackupDirectory(string instanceId) =>
        Path.Combine(BackupsDirectory, instanceId);

    public string GetVersionSnapshotDirectory(string instanceId) =>
        Path.Combine(GetInstanceBackupDirectory(instanceId), "snapshots");

    private static string GetDefaultRoot(string executableDirectory)
    {
#if DEBUG
        var testRoot = Environment.GetEnvironmentVariable(TestRootVariable);
        if (!string.IsNullOrWhiteSpace(testRoot))
        {
            return testRoot;
        }
#endif

        // 1) 环境变量显式覆盖（自动化/高级用法）。
        var overrideRoot = Environment.GetEnvironmentVariable(DataRootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            try
            {
                return Path.GetFullPath(overrideRoot.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                LauncherLog.Warn("DSH_LAUNCHER_DATA_ROOT 不是有效路径，已忽略。", ErrorCodes.E1011,
                    new { value = overrideRoot, error = ex.Message });
            }
        }

        // 2) 便携模式：exe 旁存在 launcher-data 目录（用户手动创建即启用，整个文件夹可拷走）。
        var portableRoot = Path.Combine(executableDirectory, PortableDataDirectoryName);
        if (Directory.Exists(portableRoot))
        {
            if (IsWritable(portableRoot))
            {
                return portableRoot;
            }

            LauncherLog.Warn("便携数据目录不可写，已回退到用户文档下的默认数据根。", ErrorCodes.E1011,
                new { path = portableRoot });
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(documents, "DeepSeek", "launcher");
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".dsh-write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
