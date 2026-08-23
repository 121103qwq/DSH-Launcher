namespace DshLauncher.Models;

public enum DshInstallProgressPhase
{
    ResolvingPackage,
    DownloadingPackage,
    InstallingDependencies
}

public sealed record DshInstallProgress(
    DshInstallProgressPhase Phase,
    long BytesDownloaded = 0,
    long? TotalBytes = null,
    double? Percent = null)
{
    public string PercentText => Percent is { } percent
        ? $"{percent:F1}%"
        : "未知进度";

    public string BytesText => TotalBytes is { } total
        ? $"{FormatBytes(BytesDownloaded)} / {FormatBytes(total)}"
        : FormatBytes(BytesDownloaded);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        return $"{bytes / 1024.0:F0} KB";
    }
}

public sealed record DshInstallResult(
    bool IsSuccess,
    int ExitCode,
    string? Output,
    string? Error)
{
    public static DshInstallResult Success(string? output) =>
        new(true, 0, output, null);

    public static DshInstallResult Failure(string error, int exitCode = -1, string? output = null) =>
        new(false, exitCode, output, error);
}
