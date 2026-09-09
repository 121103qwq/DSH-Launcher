namespace DshLauncher.Services;

/// <summary>
/// 插件安装/更新/卸载命令失败：把 pnpm 报错墙替换成"原因 + 已重试 + 残留清理"
/// 三段式提示，同时保留原始输出与结构化字段，供 UI 与失败报告使用。
/// </summary>
public sealed class PluginCommandFailedException : InvalidOperationException
{
    public PluginCommandFailedException(
        string action,
        string actionText,
        string packageSpec,
        int exitCode,
        string rawOutput,
        PnpmFailure? failure,
        IReadOnlyList<string> attempts,
        PluginRollbackResult rollback)
        : base(BuildMessage(actionText, exitCode, failure, attempts, rollback, rawOutput))
    {
        Action = action;
        ActionText = actionText;
        PackageSpec = packageSpec;
        ExitCode = exitCode;
        RawOutput = rawOutput;
        Failure = failure;
        Attempts = attempts;
        Rollback = rollback;
    }

    public string Action { get; }

    public string ActionText { get; }

    public string PackageSpec { get; }

    public int ExitCode { get; }

    public string RawOutput { get; }

    public PnpmFailure? Failure { get; }

    public IReadOnlyList<string> Attempts { get; }

    public PluginRollbackResult Rollback { get; }

    internal static string BuildMessage(
        string actionText,
        int exitCode,
        PnpmFailure? failure,
        IReadOnlyList<string> attempts,
        PluginRollbackResult rollback,
        string rawOutput)
    {
        var lines = new List<string> { $"Plugin {actionText}失败（退出码 {exitCode}）。" };
        lines.Add(failure is null
            ? "【原因】未识别的 pnpm 错误，请查看下方原始输出或导出诊断包。"
            : $"【原因】{failure.Message}");
        if (attempts.Count > 1)
        {
            lines.Add($"【自动重试】已尝试：{string.Join(" → ", attempts)}（仍失败）。");
        }

        if (rollback.Changed || !string.IsNullOrWhiteSpace(rollback.Message))
        {
            lines.Add($"【残留清理】{rollback.Message}");
        }

        if (!string.IsNullOrWhiteSpace(rawOutput))
        {
            lines.Add("原始输出：");
            lines.Add(rawOutput);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
