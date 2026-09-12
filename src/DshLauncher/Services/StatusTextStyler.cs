namespace DshLauncher.Services;

/// <summary>
/// 状态行三态分色（变更集 136 定的规范、变更集 145 提取为共享工具）。
/// 规范：空/加载/信息 = <c>MutedBrush</c>，警告（部分失败、需要用户先做某事）= <c>WarningTextBrush</c>，
/// 错误（失败且需要重试）= <c>DangerTextBrush</c>；失败文案必须自带下一步指引。
/// 原实现（<c>ExtensionWindow.SetStatusText</c>）保持不变，改为委托到此处，避免多份拷贝各自漂移。
/// </summary>
internal static class StatusTextStyler
{
    public static void Set(System.Windows.Controls.TextBlock block, string text, bool isError = false, bool isWarning = false)
    {
        block.Text = text;
        block.Foreground = isError
            ? UiBrush.Get("DangerTextBrush")
            : isWarning
                ? UiBrush.Get("WarningTextBrush")
                : UiBrush.Get("MutedBrush");
    }
}
