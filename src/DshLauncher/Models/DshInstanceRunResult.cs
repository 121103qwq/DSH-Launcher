namespace DshLauncher.Models;

public sealed record DshInstanceRunResult(
    bool IsSuccess,
    int? ProcessId,
    int? Port,
    string? WebUrl,
    string? Error,
    DateTimeOffset? ProcessStartedAt)
{
    public static DshInstanceRunResult Success(
        int processId,
        int port,
        string webUrl,
        DateTimeOffset? processStartedAt = null) =>
        new(true, processId, port, webUrl, null, processStartedAt);

    public static DshInstanceRunResult Failure(string error) =>
        new(false, null, null, null, error, null);
}
