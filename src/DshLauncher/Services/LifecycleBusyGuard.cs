namespace DshLauncher.Services;

/// <summary>
/// Start/Stop/Restart/对话自动启动共享的生命周期串行化 guard：同一时刻最多
/// 一个操作持有，且只有持有者调用 End 释放。runtime 准备会在持有期间打开
/// 非模态窗口等待，其它生命周期入口在此期间必须被拒绝，避免多个 async
/// handler 交叉改写同一个 busy 标志。所有调用都发生在 UI 线程。
/// </summary>
internal sealed class LifecycleBusyGuard
{
    private bool _busy;

    public bool IsBusy => _busy;

    /// <summary>尝试占用；已有人持有时返回 false，调用方应直接放弃本次操作。</summary>
    public bool TryBegin()
    {
        if (_busy)
        {
            return false;
        }

        _busy = true;
        return true;
    }

    /// <summary>释放当前持有者占用的 guard；未持有时调用为空操作。</summary>
    public void End()
    {
        _busy = false;
    }
}
