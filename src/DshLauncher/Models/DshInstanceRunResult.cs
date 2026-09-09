using DshLauncher.Services;

namespace DshLauncher.Models;

public sealed record DshInstanceRunResult(
    bool IsSuccess,
    int? ProcessId,
    int? Port,
    string? WebUrl,
    string? AuthenticatedWebUrl,
    string? Error,
    bool SafeMode = false,
    bool ZeroPollution = true,
    IReadOnlyList<StartupEvidence>? Evidence = null)
{
    public static DshInstanceRunResult Success(
        int processId,
        int port,
        string webUrl,
        string? authenticatedWebUrl = null,
        bool safeMode = false,
        bool zeroPollution = true,
        IReadOnlyList<StartupEvidence>? evidence = null) =>
        new(true, processId, port, webUrl, authenticatedWebUrl, null, safeMode, zeroPollution, evidence);

    public static DshInstanceRunResult Failure(
        string error,
        bool safeMode = false,
        bool zeroPollution = true,
        IReadOnlyList<StartupEvidence>? evidence = null) =>
        new(false, null, null, null, null, error, safeMode, zeroPollution, evidence);
}
