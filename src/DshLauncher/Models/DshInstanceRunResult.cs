namespace DshLauncher.Models;

public sealed record DshInstanceRunResult(
    bool IsSuccess,
    int? ProcessId,
    int? Port,
    string? WebUrl,
    string? AuthenticatedWebUrl,
    string? Error)
{
    public static DshInstanceRunResult Success(
        int processId,
        int port,
        string webUrl,
        string? authenticatedWebUrl = null) =>
        new(true, processId, port, webUrl, authenticatedWebUrl, null);

    public static DshInstanceRunResult Failure(string error) =>
        new(false, null, null, null, null, error);
}
