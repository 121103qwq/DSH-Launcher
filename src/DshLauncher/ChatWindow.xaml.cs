using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

using DshLauncher.Services;

namespace DshLauncher;

/// <summary>页面层探针结果：Ok=页面可用；Failed=导航失败/根节点缺失/命中错误签名；Unknown=探针异常（绝不判死）。</summary>
public enum PageProbeStatus
{
    Ok,
    Failed,
    Unknown
}

public sealed record PageProbeResult(PageProbeStatus Status, string? Summary);

public partial class ChatWindow : Window
{
    private const string DeepSeekWindowAppUserModelId = "DSHLauncher.DeepSeekWindow";
    private readonly string _address;
    private readonly string? _conversationId;
    private readonly Action<string>? _pageFailureReporter;
    private bool _conversationSelectionApplied;
    private readonly TaskCompletionSource<bool> _navigationReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public ChatWindow(
        string address,
        string? conversationId = null,
        Action<string>? pageFailureReporter = null)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Chat 地址必须是有效的 HTTP(S) URL。", nameof(address));
        }

        _address = parsed.ToString();
        _conversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId.Trim();
        _pageFailureReporter = pageFailureReporter;
        InitializeComponent();
        WindowSizeHelper.FitInitialSize(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TaskbarWindowIdentity.TrySetAppUserModelId(
            new WindowInteropHelper(this).Handle,
            DeepSeekWindowAppUserModelId);
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // exe 同目录不可写（如装在 Program Files）时回退到 %LocalAppData%，
            // 否则 Desktop 窗口会直接失败。
            var userDataFolder = Services.WebView2DataFolder.ResolveForCurrentProcess();
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.NavigationCompleted += Browser_NavigationCompleted;
            Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
            Browser.CoreWebView2.Navigate(_address);
        }
        catch (Exception ex)
        {
            _pageFailureReporter?.Invoke($"WebView2 环境创建失败：{ex.Message}");
            System.Windows.MessageBox.Show(
                this,
                $"DeepSeek 窗口无法加载 WebView2。\n\n{ex.Message}\n\nLauncher 和 DSh 实例仍会保持运行。",
                "DeepSeek 启动诊断",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Close();
        }
    }

    private async void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            Title = $"DeepSeek - 连接失败 ({e.WebErrorStatus})";
            _pageFailureReporter?.Invoke($"页面加载失败：{e.WebErrorStatus}");
            _navigationReady.TrySetResult(false);
            return;
        }

        Title = "DeepSeek";
        if (_conversationId is null || _conversationSelectionApplied || Browser.CoreWebView2 is null)
        {
            _navigationReady.TrySetResult(true);
            return;
        }

        _conversationSelectionApplied = true;
        try
        {
            var sessionId = JsonSerializer.Serialize(_conversationId);
            var script = $"localStorage.setItem('dsh.sessions.current', JSON.stringify({{sessionId:{sessionId}}})); location.reload();";
            await Browser.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch
        {
            // A session preselection is best-effort; the running Chat remains usable.
        }

        _navigationReady.TrySetResult(true);
    }

    /// <summary>
    /// 页面层探针（启动健康四层证据之一）：导航完成后检查 DOM 根节点与错误签名。
    /// 探针自身异常一律返回 Unknown（绝不判死）。
    /// </summary>
    public async Task<PageProbeResult> ProbePageAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        // Chat 窗口 Show() 后 WebView2 是异步初始化的：先等它就绪（最多 5 秒），
        // 否则会误报 Unknown。
        var initDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (Browser.CoreWebView2 is null && DateTimeOffset.UtcNow < initDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken);
        }

        if (Browser.CoreWebView2 is null)
        {
            return new PageProbeResult(PageProbeStatus.Unknown, "WebView2 未初始化");
        }

        try
        {
            var readyTask = _navigationReady.Task;
            var completed = await Task.WhenAny(readyTask, Task.Delay(timeout, cancellationToken));
            if (completed != readyTask)
            {
                return new PageProbeResult(PageProbeStatus.Failed, "页面导航超时");
            }

            if (!await readyTask)
            {
                return new PageProbeResult(PageProbeStatus.Failed, "页面导航失败");
            }

            const string script = """
                (() => {
                  const body = document.body;
                  const text = body ? (body.innerText || '') : '';
                  const root = document.querySelector('#root, #app, [data-dsh-root], .dsh-app');
                  return JSON.stringify({ root: !!root, text: text.slice(0, 300) });
                })()
                """;
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync(script);
            // ExecuteScriptAsync 返回的是 JSON 编码的字符串：外层解一次得到探针的 JSON 文本。
            var payload = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return new PageProbeResult(PageProbeStatus.Unknown, "探针返回空结果");
            }

            using var document = JsonDocument.Parse(payload);
            var hasRoot = document.RootElement.TryGetProperty("root", out var rootElement)
                && rootElement.ValueKind == JsonValueKind.True;
            var text = document.RootElement.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Empty;
            foreach (var signature in new[]
                     {
                         "failed to load",
                         "cannot get",
                         "plugin tree failed",
                         "internal server error"
                     })
            {
                if (text.Contains(signature, StringComparison.OrdinalIgnoreCase))
                {
                    return new PageProbeResult(
                        PageProbeStatus.Failed,
                        PageErrorText.DescribeProbeFailure(text));
                }
            }

            if (!hasRoot && text.Trim().Length == 0)
            {
                return new PageProbeResult(PageProbeStatus.Failed, "页面没有渲染出应用根节点");
            }

            return new PageProbeResult(
                PageProbeStatus.Ok,
                hasRoot ? "应用根节点已渲染" : "页面已渲染（未识别根节点选择器）");
        }
        catch (OperationCanceledException)
        {
            return new PageProbeResult(PageProbeStatus.Unknown, "探针被取消");
        }
        catch (Exception ex)
        {
            // 探针异常只记证据，不判死。
            return new PageProbeResult(PageProbeStatus.Unknown, $"探针异常：{ex.Message}");
        }
    }

    public async Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)
            || !await WaitForNavigationAsync(cancellationToken)
            || Browser.CoreWebView2 is null)
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await Browser.CoreWebView2.ExecuteScriptAsync(BuildSendMessageScript(message));
                if (bool.TryParse(result, out var sent) && sent)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                // The page can still be replacing its composer after the first navigation.
            }

            await Task.Delay(350, cancellationToken);
        }

        return false;
    }

    private async Task<bool> WaitForNavigationAsync(CancellationToken cancellationToken)
    {
        if (_navigationReady.Task.IsCompleted)
        {
            return await _navigationReady.Task;
        }

        try
        {
            return await _navigationReady.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static string BuildSendMessageScript(string message)
    {
        var serializedMessage = JsonSerializer.Serialize(message);
        return $$"""
            (() => {
              const text = {{serializedMessage}};
              const visible = (element) => {
                const style = window.getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return !element.disabled
                  && style.display !== 'none'
                  && style.visibility !== 'hidden'
                  && rect.width > 0
                  && rect.height > 0;
              };
              const inputs = Array.from(document.querySelectorAll(
                'textarea, [contenteditable="true"], [role="textbox"]'))
                .filter(visible)
                .sort((left, right) =>
                  right.getBoundingClientRect().width - left.getBoundingClientRect().width);
              const input = inputs[0];
              if (!input) {
                return false;
              }

              input.focus();
              if (input instanceof HTMLTextAreaElement) {
                const setter = Object.getOwnPropertyDescriptor(
                  HTMLTextAreaElement.prototype, 'value')?.set;
                if (setter) setter.call(input, text);
                else input.value = text;
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
              } else {
                input.textContent = text;
                input.dispatchEvent(new InputEvent('input', {
                  bubbles: true,
                  inputType: 'insertText',
                  data: text
                }));
              }

              const buttons = Array.from(document.querySelectorAll('button, [role="button"]'))
                .filter(visible);
              const sendButton = buttons.find((button) => {
                const hint = [
                  button.getAttribute('aria-label'),
                  button.getAttribute('title'),
                  button.getAttribute('data-testid'),
                  button.textContent
                ].filter(Boolean).join(' ');
                return /send|发送|提交|发送消息/i.test(hint);
              });
              if (sendButton) {
                sendButton.click();
                return true;
              }

              input.dispatchEvent(new KeyboardEvent('keydown', {
                key: 'Enter',
                code: 'Enter',
                keyCode: 13,
                which: 13,
                bubbles: true
              }));
              return true;
            })()
            """;
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch
        {
            // A blocked external browser must not close the Chat or Launcher window.
        }
    }
}
