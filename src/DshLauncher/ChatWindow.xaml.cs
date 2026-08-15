using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DshLauncher;

public partial class ChatWindow : Window
{
    private readonly string _address;
    private readonly string? _conversationId;
    private bool _conversationSelectionApplied;

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
            return;
        }

        Title = "DeepSeek";
        if (_conversationId is null || _conversationSelectionApplied || Browser.CoreWebView2 is null)
        {
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
