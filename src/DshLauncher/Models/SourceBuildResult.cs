namespace DshLauncher.Models;

public sealed record SourceBuildResult(
    bool IsSuccess,
    bool DependenciesInstalled,
    bool BuildExecuted,
    string? EntrypointPath,
    string? Error,
    string Output)
{
    public static SourceBuildResult Failure(
        string error,
        string output = "",
        bool dependenciesInstalled = false,
        bool buildExecuted = false) =>
        new(false, dependenciesInstalled, buildExecuted, null, error, output);

    public static SourceBuildResult Success(
        string entrypointPath,
        string output,
        bool dependenciesInstalled,
        bool buildExecuted) =>
        new(true, dependenciesInstalled, buildExecuted, entrypointPath, null, output);
}
