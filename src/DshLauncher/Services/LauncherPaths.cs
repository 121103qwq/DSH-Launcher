using System.IO;

namespace DshLauncher.Services;

public sealed class LauncherPaths
{
#if DEBUG
    private const string TestRootVariable = "DSH_LAUNCHER_TEST_ROOT";
#endif

    public LauncherPaths(string? rootDirectory = null, string? executableDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? GetDefaultRoot());
        ExecutableDirectory = Path.GetFullPath(executableDirectory ?? AppContext.BaseDirectory);
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

    private static string GetDefaultRoot()
    {
#if DEBUG
        var testRoot = Environment.GetEnvironmentVariable(TestRootVariable);
        if (!string.IsNullOrWhiteSpace(testRoot))
        {
            return testRoot;
        }
#endif

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(documents, "DeepSeek", "launcher");
    }
}
