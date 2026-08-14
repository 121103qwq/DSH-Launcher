namespace DshLauncher.Models;

public sealed record DshInstanceRunResult(
    bool IsSuccess,
    int? ProcessId,
    int? Port,
    string? WebUrl,
    string? Error)
{
    public static DshInstanceRunResult Success(int processId, int port, string webUrl) =>
        new(true, processId, port, webUrl, null);

    public static DshInstanceRunResult Failure(string error) =>
        new(false, null, null, null, error);
}
