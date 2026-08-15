namespace DshLauncher.Models;

public sealed record NodeDownloadProgress(
    long BytesDownloaded,
    long? TotalBytes,
    double? Percent)
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

public sealed record NodeInstallResult(
    bool IsSuccess,
    string? NodeExecutablePath,
    string? Version,
    string? Error,
    int ExitCode = -1,
    string? Output = null)
{
    public static NodeInstallResult Success(string nodeExecutablePath, string version) =>
        new(true, nodeExecutablePath, version, null, 0, null);

    public static NodeInstallResult Failure(string error, int exitCode = -1, string? output = null) =>
        new(false, null, null, error, exitCode, output);
}
