using System.IO;

namespace DshLauncher.Services;

public sealed class LauncherPaths
{
#if DEBUG
    private const string TestRootVariable = "DSH_LAUNCHER_TEST_ROOT";
#endif

    public LauncherPaths(string? rootDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? GetDefaultRoot());
    }

    public string RootDirectory { get; }

    public string InstancesFilePath => Path.Combine(RootDirectory, "instances.json");

    public string InstancesDirectory => Path.Combine(RootDirectory, "instances");

    public string BackupsDirectory => Path.Combine(RootDirectory, "backups");

    public string MarketplaceCatalogPath => Path.Combine(RootDirectory, "marketplace.json");

    public string MarketplaceSourcesPath => Path.Combine(RootDirectory, "marketplace-sources.json");

    public string MarketplaceCachePath => Path.Combine(RootDirectory, "marketplace-cache.json");

    public string RuntimeCachePath => Path.Combine(RootDirectory, "runtime-cache.json");

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public string TaskHistoryPath => Path.Combine(RootDirectory, "task-history.json");

    public string ManagedDshRuntimeDirectory => Path.Combine(RootDirectory, "runtime", "dsh");

    public string VersionSettingsPath => Path.Combine(RootDirectory, "version-settings.json");

    public string CodingModelPoliciesPath => Path.Combine(RootDirectory, "coding-model-policies.json");

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
