using System.Windows;
using System.Windows.Controls;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher;

public partial class ModPackMarketView : System.Windows.Controls.UserControl
{
    private readonly ModPackMarketService _service;
    private readonly Func<ModPackMarketEntry, CancellationToken, Task<string>> _install;
    private readonly CancellationToken _windowToken;
    private IReadOnlyList<ModPackMarketEntry> _entries = Array.Empty<ModPackMarketEntry>();
    private CancellationTokenSource? _pageCancellation;
    private bool _busy;

    public ModPackMarketView(
        ModPackMarketService service,
        Func<ModPackMarketEntry, CancellationToken, Task<string>> install,
        CancellationToken windowToken = default)
    {
        _service = service;
        _install = install;
        _windowToken = windowToken;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_pageCancellation is not null) return;
            _pageCancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowToken);
            SetBusy(false);
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            var cancellation = _pageCancellation;
            _pageCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
        };
    }

    private bool IsCurrent(CancellationTokenSource cancellation) =>
        ReferenceEquals(_pageCancellation, cancellation) && !cancellation.IsCancellationRequested;

    private async Task RefreshAsync()
    {
        if (_busy || _pageCancellation is not { } cancellation) return;
        SetBusy(true);
        StatusText.Text = "正在获取社区整合包列表…";
        try
        {
            var entries = await _service.ReadCatalogAsync(cancellation.Token);
            if (!IsCurrent(cancellation)) return;
            _entries = entries;
            ApplyFilter();
        }
        catch (OperationCanceledException) when (!IsCurrent(cancellation))
        {
        }
        catch (Exception ex)
        {
            if (IsCurrent(cancellation)) StatusText.Text = $"读取整合包列表失败：{ex.Message}。可以刷新重试。";
        }
        finally
        {
            if (IsCurrent(cancellation)) SetBusy(false);
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var selected = PackList.SelectedItem as ModPackMarketEntry;
        var filtered = _entries.Where(entry => query.Length == 0
            || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        PackList.ItemsSource = filtered;
        PackList.SelectedItem = filtered.FirstOrDefault(entry => entry == selected);
        StatusText.Text = filtered.Length == 0
            ? "没有匹配的整合包。可修改搜索条件或刷新列表。"
            : $"显示 {filtered.Length} / {_entries.Count} 个整合包。社区内容不代表 Launcher 官方推荐。";
        UpdateSelection();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        PackList.IsEnabled = !busy;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var entry = PackList.SelectedItem as ModPackMarketEntry;
        InstallButton.IsEnabled = !_busy && entry?.IsInstallable == true;
        SelectionText.Text = entry is null ? "请选择一个整合包。"
            : !entry.IsInstallable ? $"此整合包暂不可安装：{entry.UnavailableReason}"
            : $"{entry.Description}\n下载地址：{entry.DownloadUrl}";
        SelectionText.ToolTip = SelectionText.Text;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PackList is not null) ApplyFilter();
    }

    private void PackList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || PackList.SelectedItem is not ModPackMarketEntry { IsInstallable: true } entry
            || _pageCancellation is not { } cancellation) return;
        SetBusy(true);
        StatusText.Text = $"正在准备安装 {entry.Name}…";
        try
        {
            var message = await _install(entry, cancellation.Token);
            if (IsCurrent(cancellation)) StatusText.Text = message;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(cancellation)) StatusText.Text = "已取消安装。";
        }
        catch (Exception ex)
        {
            if (IsCurrent(cancellation)) StatusText.Text = $"安装失败：{ex.Message}";
        }
        finally
        {
            if (IsCurrent(cancellation)) SetBusy(false);
        }
    }
}
