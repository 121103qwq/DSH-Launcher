namespace DshLauncher.Models;

public sealed record SourceProjectInfo(
    bool IsValid,
    bool IsDshSource,
    string RootPath,
    string? Name,
    string? Version,
    string? PackageManager,
    string? PackageManagerVersion,
    bool HasBuildScript,
    bool DependenciesPresent,
    bool HasCliEntrypoint,
    string? Error,
    string? BuiltCliEntrypoint = null)
{
    public string StatusText => !IsValid
        ? "无法识别"
        : !IsDshSource
            ? "不是 DSh 源码"
            : !DependenciesPresent
                ? "需要安装依赖"
                : !HasBuildScript
                    ? "缺少 build 脚本"
                    : BuiltCliEntrypoint is null ? "可构建" : "已构建";

    public string BuildCommand => PackageManager switch
    {
        "pnpm" => "pnpm run build",
        "yarn" => "yarn build",
        "bun" => "bun run build",
        _ => "npm run build"
    };
}
