using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace DshLauncher;

public partial class ChatWindow : Window
{
    private const string DeepSeekWindowAppUserModelId = "DSHLauncher.DeepSeekWindow";
    private static readonly object OpenWindowsGate = new();
    private static readonly Dictionary<string, WeakReference<ChatWindow>> OpenWindows = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly string _address;
    private readonly string? _conversationId;
    private bool _conversationSelectionApplied;
    private bool _openWindowRegistered;
    private readonly TaskCompletionSource<bool> _navigationReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public ChatWindow(string address, string? conversationId = null)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Chat 地址必须是有效的 HTTP(S) URL。", nameof(address));
        }

        _address = parsed.ToString();
        _conversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId.Trim();
        InitializeComponent();
        WindowSizeHelper.FitInitialSize(this);
    }

    public static event EventHandler? OpenChatWindowsChanged;

    public static bool TryGetOpenChat(string? address, out ChatWindow? chat)
    {
        chat = null;
        if (!TryGetWindowKey(address, out var key))
        {
            return false;
        }

        lock (OpenWindowsGate)
        {
            if (!OpenWindows.TryGetValue(key, out var reference)
                || !reference.TryGetTarget(out var candidate)
                || !candidate.IsVisible
                || candidate.Dispatcher.HasShutdownStarted)
            {
                OpenWindows.Remove(key);
                return false;
            }

            chat = candidate;
            return true;
        }
    }

    public static async Task<bool> TrySyncOpenChatAsync(
        string? address,
        CancellationToken cancellationToken = default)
    {
        return TryGetOpenChat(address, out var chat)
            && await chat!.SyncThemeAsync(cancellationToken);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TaskbarWindowIdentity.TrySetAppUserModelId(
            new WindowInteropHelper(this).Handle,
            DeepSeekWindowAppUserModelId);
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterOpenWindow();
        base.OnClosed(e);
    }

    public async Task<bool> SyncThemeAsync(CancellationToken cancellationToken = default)
    {
        if (!await WaitForNavigationAsync(cancellationToken)
            || Browser.CoreWebView2 is null)
        {
            return false;
        }

        try
        {
            Browser.CoreWebView2.Reload();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.NavigationCompleted += Browser_NavigationCompleted;
            Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
            Browser.CoreWebView2.Navigate(_address);
        }
        catch (Exception ex)
        {
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

    private void Window_OnContentRendered(object? sender, EventArgs e) => RegisterOpenWindow();

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

    private void RegisterOpenWindow()
    {
        if (_openWindowRegistered || !TryGetWindowKey(_address, out var key))
        {
            return;
        }

        lock (OpenWindowsGate)
        {
            OpenWindows[key] = new WeakReference<ChatWindow>(this);
        }

        _openWindowRegistered = true;
        OpenChatWindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UnregisterOpenWindow()
    {
        _openWindowRegistered = false;
        if (!TryGetWindowKey(_address, out var key))
        {
            return;
        }

        var removed = false;
        lock (OpenWindowsGate)
        {
            if (OpenWindows.TryGetValue(key, out var reference)
                && reference.TryGetTarget(out var candidate)
                && ReferenceEquals(candidate, this))
            {
                OpenWindows.Remove(key);
                removed = true;
            }
        }

        if (removed)
        {
            OpenChatWindowsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool TryGetWindowKey(string? address, out string key)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            key = string.Empty;
            return false;
        }

        key = parsed.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return !string.IsNullOrWhiteSpace(key);
    }
}
