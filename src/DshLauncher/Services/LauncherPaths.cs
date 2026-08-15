using System.IO;

namespace DshLauncher.Services;

public sealed class LauncherPaths
{
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

    public string VersionSettingsPath => Path.Combine(RootDirectory, "version-settings.json");

    public string GetInstanceDshHome(string instanceId) =>
        Path.Combine(InstancesDirectory, instanceId, "dsh-home");

    public string GetInstanceBackupDirectory(string instanceId) =>
        Path.Combine(BackupsDirectory, instanceId);

    private static string GetDefaultRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(documents, "DeepSeek", "launcher");
    }
}
