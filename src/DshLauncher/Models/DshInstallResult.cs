namespace DshLauncher.Models;

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
