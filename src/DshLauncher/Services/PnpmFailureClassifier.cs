using System.Text.RegularExpressions;

namespace DshLauncher.Services;

/// <summary>已知的 pnpm 失败模式（借鉴 MarcoG-h/DSH-Launcher 的 pnpm-compat.ts 清单）。</summary>
public enum PnpmFailureCode
{
    AddingToRoot,
    NotAWorkspace,
    HoistPatternDiff,
    UnexpectedStore,
    MinimumReleaseAge,
    IgnoredBuilds,
    GitPrepareNotAllowed,
    GitNetwork,
    LlamaBinary,
    Fetch404,
    TransientNetwork,
    FetchTimeout,
    StoreCorrupt,
    MissingGit,
    FileLocked,
    UnsupportedEngine,
    PnpmMissing,
    OutdatedLockfile,
    NoMatchingVersion
}

/// <summary>一次可识别的 pnpm 失败：面向用户的可操作说明 + 是否值得自动重试一次。</summary>
public sealed record PnpmFailure(PnpmFailureCode Code, string Message, bool RetryOnce);

/// <summary>
/// 把 <c>dsh plugin</c>（内部走 pnpm）的失败输出映射成可操作的中文提示。
/// 设计来自 MarcoG-h/DSH-Launcher 的 pnpm-compat.ts：dsh 只透传 pnpm 的报错墙，
/// 用户看不出"是网络抖了、还是插件要求构建、还是 profile 里留了幽灵依赖"。
/// 未识别时返回 null，调用方原样展示输出。
/// </summary>
public static class PnpmFailureClassifier
{
    private static readonly Regex TransientNetwork = new(
        "ERR_PNPM_FETCH_5\\d\\d|ERR_PNPM_META_FETCH_FAIL|FetchError|ECONNRESET|ETIMEDOUT|EAI_AGAIN|ENETUNREACH|socket hang up|network timeout",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FetchTimeout = new(
        "operation was aborted due to timeout|TimeoutError|error \\(23\\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GitNetwork = new(
        "fatal: unable to access|fatal: could not read from remote repository|fatal: unable to look up|fatal: could not resolve host|could not resolve host: github|failed to connect to (?:codeload\\.)?github\\.com|github\\.com port \\d+: connection refused|the remote end hung up unexpectedly|early eof|rpc failed",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LlamaBinary = new(
        "failed to (?:download|find|get).*?llama|llama.*?failed to (?:download|find)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Fetch404Package = new(
        "GET\\s+\\S*/([^/\\s]+):",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UnexpectedStoreLinked = new(
        "currently linked from the store at \"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UnexpectedStoreWanted = new(
        "wants to use the store at \"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GitHostedSpec = new(
        "^(?:git\\+|github:|git@|git://|https?://github\\.com/)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ScopedBareName = new(
        "^(?:@[^/]+/)?[^/@\\s\\\\:]+$",
        RegexOptions.CultureInvariant);

    /// <summary>该 spec 是否走 git/GitHub 直装（决定是否注入镜像重写）。</summary>
    public static bool IsGitHostedSpec(string? spec) =>
        !string.IsNullOrWhiteSpace(spec) && GitHostedSpec.IsMatch(spec.Trim());

    /// <summary>
    /// 从安装 spec 推断包名候选（npm 裸名、github:owner/repo → repo 与 @owner/repo、
    /// 本地路径取目录名），用于失败后只清理"本次新增"的残留。
    /// </summary>
    public static IReadOnlyList<string> ExtractPackageNameCandidates(string? spec)
    {
        var value = spec?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        if (ScopedBareName.IsMatch(value))
        {
            return new[] { value };
        }

        var stripped = value;
        if (stripped.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            stripped = stripped[4..];
        }

        if (stripped.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            stripped = stripped["github:".Length..];
        }

        var hashIndex = stripped.IndexOf('#');
        if (hashIndex >= 0)
        {
            stripped = stripped[..hashIndex];
        }

        if (stripped.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            stripped = stripped[..^4];
        }

        var match = Regex.Match(stripped, "github\\.com/([^/]+)/([^/?#]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(stripped, "^([^/]+)/([^/?#]+)$");
        }
        if (match.Success)
        {
            var repo = match.Groups[2].Value;
            var owner = match.Groups[1].Value;
            return repo.StartsWith('@') ? new[] { repo } : new[] { repo, $"@{owner}/{repo}" };
        }

        var segment = Regex.Match(stripped, "[/\\\\]([^/\\\\?#]+)[/\\\\]?$");
        return segment.Success ? new[] { segment.Groups[1].Value } : Array.Empty<string>();
    }

    /// <summary>瞬时网络类失败（值得且只值得自动重试一次）。</summary>
    public static bool IsTransient(string? output) =>
        !string.IsNullOrWhiteSpace(output) && TransientNetwork.IsMatch(output);

    /// <summary>识别失败模式；未识别返回 null。</summary>
    public static PnpmFailure? Classify(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        if (output.Contains("ERR_PNPM_PUBLIC_HOIST_PATTERN_DIFF", StringComparison.Ordinal))
        {
            return new PnpmFailure(
                PnpmFailureCode.HoistPatternDiff,
                "这个 profile 的 node_modules 由不同大版本的 pnpm 创建，与当前 pnpm 的默认布局不兼容。"
                + "已自动重试一次；若仍失败，请用「兼容性安装」模式重新安装（会以 copy 方式重建依赖）。",
                RetryOnce: true);
        }

        if (output.Contains("ERR_PNPM_UNEXPECTED_STORE", StringComparison.Ordinal))
        {
            var linked = UnexpectedStoreLinked.Match(output).Groups[1].Value;
            var wanted = UnexpectedStoreWanted.Match(output).Groups[1].Value;
            var detail = linked.Length > 0 && wanted.Length > 0
                ? $"\n  node_modules 链接到：{linked}\n  当前 pnpm 想用：{wanted}"
                : string.Empty;
            return new PnpmFailure(
                PnpmFailureCode.UnexpectedStore,
                "这个 profile 的 node_modules 链接到的 pnpm store 与当前 pnpm 默认使用的不是同一个，"
                + "pnpm 因此拒绝所有安装与卸载。" + detail
                + "\n请先停止实例，再到 profile 目录执行一次 pnpm install --store-dir <上面第一个路径> 重新链接。",
                RetryOnce: false);
        }

        if (output.Contains("ERR_PNPM_ADDING_TO_ROOT", StringComparison.Ordinal))
        {
            return new PnpmFailure(
                PnpmFailureCode.AddingToRoot,
                "pnpm 拒绝在 workspace 根目录安装（缺少 -w）。这是 Launcher/DSh 的兼容问题，"
                + "请导出诊断包反馈；临时可先停止实例后在 profile 目录手动执行 dsh plugin add。",
                RetryOnce: false);
        }

        if (output.Contains("--workspace-root may only be used inside a workspace", StringComparison.Ordinal))
        {
            return new PnpmFailure(
                PnpmFailureCode.NotAWorkspace,
                "profile 目录不是 pnpm workspace，却传入了 -w。这是 Launcher/DSh 的兼容问题，"
                + "请导出诊断包反馈；临时可先停止实例后在 profile 目录手动执行 dsh plugin add。",
                RetryOnce: false);
        }

        if (output.Contains("ERR_PNPM_MINIMUM_RELEASE_AGE_VIOLATION", StringComparison.Ordinal)
            || output.Contains("ERR_PNPM_NO_MATURE_MATCHING_VERSION", StringComparison.Ordinal))
        {
            return new PnpmFailure(
                PnpmFailureCode.MinimumReleaseAge,
                "profile 里有刚发布不久的版本，pnpm 的安全等待期检查因此拒绝了本次改动（即使改的是别的插件）。"
                + "已自动重试一次；若仍失败请过几小时再试，或把该版本加入 profile 的 pnpm-workspace.yaml 的 minimumReleaseAgeExclude。",
                RetryOnce: true);
        }

        if (output.Contains("ERR_PNPM_IGNORED_BUILDS", StringComparison.Ordinal))
        {
            return new PnpmFailure(
                PnpmFailureCode.IgnoredBuilds,
                "有依赖需要执行构建脚本，被 pnpm 默认拦截。请确认插件允许构建的包名后重试"
                + "（Launcher 安装时会传 --allow-build；也可在安装对话框里勾选「允许构建」）。",
                RetryOnce: false);
        }

        if (output.Contains("ERR_PNPM_GIT_DEP_PREPARE_NOT_ALLOWED", StringComparison.Ordinal))
        {
            return new PnpmFailure(
                PnpmFailureCode.GitPrepareNotAllowed,
                "这个 git 插件需要在安装时执行构建脚本，被 pnpm 默认拦截。允许构建后重试即可。",
                RetryOnce: false);
        }

        if (GitNetwork.IsMatch(output))
        {
            return new PnpmFailure(
                PnpmFailureCode.GitNetwork,
                "拉取 GitHub 仓库失败（无法连上 GitHub）。已自动重试并依次尝试国内镜像；"
                + "若仍失败，说明当前网络到 GitHub 不通，请稍后再试或改用 npm 包名安装。",
                RetryOnce: false);
        }

        if (LlamaBinary.IsMatch(output))
        {
            return new PnpmFailure(
                PnpmFailureCode.LlamaBinary,
                "下载模型引擎二进制失败（该插件依赖的本地模型引擎从 GitHub Releases 拉取），"
                + "已自动重试一次；若仍失败通常是网络到 GitHub 不通，请稍后再试。",
                RetryOnce: true);
        }

        if (output.Contains("ERR_PNPM_FETCH_404", StringComparison.Ordinal))
        {
            var raw = Fetch404Package.Match(output).Groups[1].Value;
            var package = raw.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase).Replace("%2f", "/", StringComparison.Ordinal);
            var suffix = package.Length == 0 ? string.Empty : $"（{package}）";
            return new PnpmFailure(
                PnpmFailureCode.Fetch404,
                $"有一个依赖在 registry 上不存在{suffix}，pnpm 因此拒绝任何安装操作。"
                + "它可能是之前失败安装残留在 profile package.json 里的幽灵依赖，也可能是需要登录的私有包；"
                + "已自动重试一次，若仍失败请检查 profile 的 package.json 依赖。",
                RetryOnce: true);
        }

        if (IsTransient(output))
        {
            return new PnpmFailure(
                PnpmFailureCode.TransientNetwork,
                "拉取依赖时网络临时失败（不一定是正在装的插件——安装会重放整个依赖树，"
                + "任何一个既有依赖抖动都会中断）。已自动重试一次；若仍失败请稍后再试。",
                RetryOnce: true);
        }

        if (FetchTimeout.IsMatch(output))
        {
            return new PnpmFailure(
                PnpmFailureCode.FetchTimeout,
                "下载超时：插件的安装包较大（GitHub 源会拉整个仓库）或网络较慢，"
                + "pnpm 默认的单次请求超时不够用。已自动重试一次；若仍失败请稍后再试或改用国内镜像。",
                RetryOnce: true);
        }

        if (output.Contains("ERR_PNPM_TARBALL_INTEGRITY", StringComparison.Ordinal)
            || output.Contains("ERR_PNPM_BAD_TARBALL_SIZE", StringComparison.Ordinal)
            || output.Contains("Integrity check failed", StringComparison.OrdinalIgnoreCase))
        {
            return new PnpmFailure(
                PnpmFailureCode.StoreCorrupt,
                "pnpm 内容寻址存储里的包校验失败（下载不完整或存储损坏）。"
                + "请先执行 pnpm store prune 清理存储后重试；若反复出现请检查磁盘与杀毒软件。",
                RetryOnce: false);
        }

        if (output.Contains("ERR_PNPM_NO_GIT_BIN", StringComparison.Ordinal)
            || output.Contains("git is not installed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("'git' is not recognized", StringComparison.OrdinalIgnoreCase))
        {
            return new PnpmFailure(
                PnpmFailureCode.MissingGit,
                "该插件从 GitHub 直装，需要 git，但系统没有可用的 git。"
                + "请安装 git 后重试，或改用该插件的 npm 包名安装。",
                RetryOnce: false);
        }

        if (output.Contains("ERR_PNPM_UNSUPPORTED_ENGINE", StringComparison.Ordinal)
            || output.Contains("Unsupported engine", StringComparison.OrdinalIgnoreCase))
        {
            return new PnpmFailure(
                PnpmFailureCode.UnsupportedEngine,
                "插件要求的 Node.js 版本与当前环境不兼容。请在「设置 → 准备运行环境」里安装便携版 Node.js（≥22.19）后重试。",
                RetryOnce: false);
        }

        if (output.Contains("ERR_PNPM_OUTDATED_LOCKFILE", StringComparison.Ordinal)
            || output.Contains("ERR_PNPM_LOCKFILE_MISSING_DEPENDENCY", StringComparison.Ordinal))
        {
            return new PnpmFailure(
                PnpmFailureCode.OutdatedLockfile,
                "profile 的锁文件与 package.json 不一致，pnpm 拒绝安装。"
                + "请用「兼容性安装」模式重试，或恢复操作前的备份后重试。",
                RetryOnce: false);
        }

        if (output.Contains("ERR_PNPM_NO_MATCHING_VERSION", StringComparison.Ordinal)
            || output.Contains("ERR_PNPM_NO_VERSIONS", StringComparison.Ordinal)
            || output.Contains("No matching version found", StringComparison.OrdinalIgnoreCase))
        {
            return new PnpmFailure(
                PnpmFailureCode.NoMatchingVersion,
                "registry 上没有满足版本范围的版本（包名或版本写错，或国内镜像还没同步到该版本）。"
                + "请核对包名/版本，或稍后重试。",
                RetryOnce: false);
        }

        if (ContainsFileLock(output))
        {
            return new PnpmFailure(
                PnpmFailureCode.FileLocked,
                "node_modules 里的文件被占用（实例可能仍在运行，或杀毒/同步软件锁定）。"
                + "请先停止实例，必要时关闭占用该目录的程序后重试。",
                RetryOnce: false);
        }

        if (output.Contains("pnpm not found on PATH", StringComparison.OrdinalIgnoreCase)
            || output.Contains("pnpm: command not found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("'pnpm' is not recognized", StringComparison.OrdinalIgnoreCase))
        {
            return new PnpmFailure(
                PnpmFailureCode.PnpmMissing,
                "找不到 pnpm：请先在「设置 → 准备运行环境」里准备运行环境（会自动带上 pnpm/Corepack）后重试。",
                RetryOnce: false);
        }

        return null;
    }

    private static bool ContainsFileLock(string output) =>
        (output.Contains("EPERM", StringComparison.Ordinal) || output.Contains("EBUSY", StringComparison.Ordinal))
        && (output.Contains("unlink", StringComparison.OrdinalIgnoreCase)
            || output.Contains("rename", StringComparison.OrdinalIgnoreCase)
            || output.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase));
}
