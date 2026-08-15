using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using DshLauncher.Models;
using DshLauncher.Services;
using Forms = System.Windows.Forms;

namespace DshLauncher;

public partial class ExtensionWindow : UserControl
{
    private readonly ManagerInstance _instance;
    private readonly ExtensionService _service;
    private readonly Func<NodeRuntimeInfo?> _nodeRuntime;
    private readonly bool _agentOnly;
    private readonly MarketplaceService? _marketplaceService;
    private readonly ObservableCollection<MarketplaceItem> MarketplaceItems = new();
    private IReadOnlyList<MarketplaceItem> _marketplaceSnapshot = Array.Empty<MarketplaceItem>();
    private Dictionary<string, ExtensionEntry> _installedPlugins = new(StringComparer.OrdinalIgnoreCase);
    private bool _marketplaceCanMutate;
    private bool _isMarketplaceLoading;
    private bool _controlLoaded;
    private CancellationTokenSource? _marketplaceCancellation;

    public ExtensionWindow(
        ManagerInstance instance,
        ExtensionService service,
        Func<NodeRuntimeInfo?> nodeRuntime,
        bool agentOnly = false,
        MarketplaceService? marketplaceService = null)
    {
        _instance = instance;
        _service = service;
        _nodeRuntime = nodeRuntime;
        _agentOnly = agentOnly;
        _marketplaceService = marketplaceService;
        InitializeComponent();
        CurrentInstanceNameText.Text = instance.Name;
        CurrentInstanceDetailsText.Text = $"{instance.KindText} · {instance.RootPath}\nDSH_HOME：{instance.DshHome}";
        if (_agentOnly)
        {
            MarketplacePanel.Visibility = Visibility.Collapsed;
            Grid.SetColumnSpan(InstalledPanel, 3);
            InstallPluginButton.Visibility = Visibility.Collapsed;
            AddMcpButton.Visibility = Visibility.Collapsed;
            EnableButton.Visibility = Visibility.Collapsed;
            DisableButton.Visibility = Visibility.Collapsed;
            UpdateButton.Visibility = Visibility.Collapsed;
            HintText.Text = "修改前请停止实例。Skill 会被 DSh 的 filesystem provider 发现，Agent Preset 和 Workflow 会在下次启动时生效。";
        }
        else
        {
            ImportSkillButton.Visibility = Visibility.Collapsed;
            ImportPresetButton.Visibility = Visibility.Collapsed;
        }
        MarketplaceList.ItemsSource = MarketplaceItems;
    }

    private ObservableCollection<ExtensionEntry> Entries { get; } = new();

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        _controlLoaded = true;
        if (!_agentOnly)
        {
            await LoadCachedMarketplaceAsync();
        }

        await RefreshAsync();
        if (!_agentOnly)
        {
            _ = RefreshMarketplaceAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            var selectedId = (ExtensionList.SelectedItem as ExtensionEntry)?.Id;
            var entries = await _service.ListAsync(_instance);
            entries = (_agentOnly
                    ? entries.Where(entry => entry.Kind is ExtensionKind.Skill or ExtensionKind.Preset or ExtensionKind.Workflow)
                    : entries.Where(entry => entry.Kind is ExtensionKind.Plugin or ExtensionKind.Mcp))
                .ToArray();
            Entries.Clear();
            foreach (var entry in entries) Entries.Add(entry);
            ExtensionList.ItemsSource = Entries;
            if (selectedId is not null)
            {
                ExtensionList.SelectedItem = Entries.FirstOrDefault(entry => entry.Id == selectedId);
            }
            StatusText.Text = _agentOnly
                ? $"已读取 {Entries.Count} 个 Skill / Agent Preset / Workflow。"
                : $"已读取 {Entries.Count} 个 Plugin / MCP。";
            UpdateSelection();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void MarketplaceSearch_Click(object sender, RoutedEventArgs e) => await RefreshMarketplaceAsync();

    private async void MarketplaceRefresh_Click(object sender, RoutedEventArgs e) => await RefreshMarketplaceAsync();

    private async void MarketplaceSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await RefreshMarketplaceAsync();
    }

    private void MarketplaceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_controlLoaded)
        {
            RenderMarketplaceItems();
        }
    }

    private async Task LoadCachedMarketplaceAsync()
    {
        if (_marketplaceService is null)
        {
            return;
        }

        try
        {
            var cached = _marketplaceService.ReadCached(_instance, MarketplaceSearchBox.Text);
            if (cached is null)
            {
                MarketplaceStatusText.Text = "还没有本地缓存；正在后台读取插件目录。";
                return;
            }

            await SetMarketplaceSnapshotAsync(cached, fromCache: true);
        }
        catch (Exception ex)
        {
            MarketplaceStatusText.Text = $"读取插件市场缓存失败：{ex.Message}";
        }
    }

    private async Task RefreshMarketplaceAsync()
    {
        if (_marketplaceService is null || _isMarketplaceLoading)
        {
            return;
        }

        _isMarketplaceLoading = true;
        _marketplaceCancellation?.Cancel();
        _marketplaceCancellation?.Dispose();
        _marketplaceCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        MarketplaceStatusText.Text = _marketplaceSnapshot.Count == 0
            ? "正在读取插件目录，请稍候…"
            : "正在后台更新目录，当前先显示本地缓存。";

        try
        {
            var result = await _marketplaceService.SearchAsync(
                _instance,
                MarketplaceSearchBox.Text,
                _marketplaceCancellation.Token);
            await SetMarketplaceSnapshotAsync(result, fromCache: false, _marketplaceCancellation.Token);
            MarketplaceStatusText.Text = result.Warnings.Count == 0
                ? "目录已更新。列表中的插件在真正安装前还会再次检查。"
                : $"目录已更新，但有 {result.Warnings.Count} 个来源暂时不可用；仍显示其他来源的结果。";
        }
        catch (OperationCanceledException) when (_marketplaceCancellation?.IsCancellationRequested == true)
        {
            MarketplaceStatusText.Text = "读取插件目录超时或已取消，请稍后重试。";
        }
        catch (Exception ex)
        {
            MarketplaceStatusText.Text = $"读取插件目录失败：{ex.Message}";
        }
        finally
        {
            _isMarketplaceLoading = false;
        }
    }

    private async Task SetMarketplaceSnapshotAsync(
        MarketplaceSearchResult result,
        bool fromCache,
        CancellationToken cancellationToken = default)
    {
        _marketplaceSnapshot = result.Items;
        var installed = await _service.ListAsync(_instance, cancellationToken);
        _installedPlugins = installed
            .Where(entry => entry.Kind == ExtensionKind.Plugin)
            .ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        _marketplaceCanMutate = _instance.RuntimeOwnership != InstanceRuntimeOwnership.Attached
            && _instance.RuntimeStatus != InstanceRuntimeStatus.Running;
        RenderMarketplaceItems();
        MarketplaceSummaryText.Text = $"找到 {_marketplaceSnapshot.Count} 个候选插件 · 已检查 {result.SourcesChecked} 个来源"
            + (fromCache ? " · 本地缓存" : string.Empty);
        if (fromCache)
        {
            MarketplaceStatusText.Text = result.Warnings.FirstOrDefault()
                ?? "先显示本地缓存，在线目录会在后台更新。";
        }
    }

    private void RenderMarketplaceItems()
    {
        if (_marketplaceService is null)
        {
            return;
        }

        var items = MarketplaceService.FilterAndSort(
            _marketplaceSnapshot,
            sourceKind: GetSelectedSourceKind(),
            sortOrder: GetSelectedSortOrder());
        MarketplaceItems.Clear();
        foreach (var item in items)
        {
            var lookupName = item.PackageName ?? item.Name;
            var isInstalled = _installedPlugins.TryGetValue(lookupName, out var installedEntry);
            MarketplaceItems.Add(item with
            {
                IsInstalled = isInstalled,
                IsManaged = isInstalled && installedEntry!.Managed,
                CanMutate = _marketplaceCanMutate
            });
        }
    }

    private MarketplaceSourceKind? GetSelectedSourceKind()
    {
        var tag = (MarketplaceSourceBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<MarketplaceSourceKind>(tag, out var value) ? value : null;
    }

    private MarketplaceSortOrder GetSelectedSortOrder()
    {
        var tag = (MarketplaceSortBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<MarketplaceSortOrder>(tag, out var value)
            ? value
            : MarketplaceSortOrder.Relevance;
    }

    private async void MarketplaceAction_Click(object sender, RoutedEventArgs e)
    {
        if (_marketplaceService is null || (sender as FrameworkElement)?.DataContext is not MarketplaceItem item)
        {
            return;
        }

        try
        {
            EnsureMarketplaceMutationAllowed();
            var verification = await _marketplaceService.VerifyAsync(item);
            if (verification.Status == MarketplaceVerificationStatus.Rejected)
            {
                MarketplaceStatusText.Text = verification.Message;
                System.Windows.MessageBox.Show(Window.GetWindow(this), verification.Message, "插件不能安装", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var snapshot = _marketplaceService.CreatePluginSnapshot(_instance);
            var packageSpec = verification.InstallSpec ?? item.InstallSpec;
            string output;
            if (item.IsInstalled)
            {
                if (!item.IsManaged)
                {
                    throw new InvalidOperationException("当前 Plugin 不是 Launcher 安装的，不能从市场更新。请在 DSh 自己的工具中管理它。");
                }

                output = await _service.UpdatePluginAsync(
                    _instance,
                    verification.PackageName ?? item.PackageName ?? packageSpec,
                    _nodeRuntime());
                StatusText.Text = $"Plugin 更新完成。实例下次启动时加载；备份：{snapshot}";
            }
            else
            {
                output = await _service.InstallPluginAsync(_instance, packageSpec, _nodeRuntime());
                StatusText.Text = $"Plugin 安装完成。实例下次启动时加载；备份：{snapshot}";
            }

            MarketplaceStatusText.Text = string.IsNullOrWhiteSpace(output)
                ? "操作完成。"
                : $"操作完成：{Tail(output)}";
            await RefreshMarketplaceAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void MarketplaceRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_marketplaceService is null || (sender as FrameworkElement)?.DataContext is not MarketplaceItem item)
        {
            return;
        }

        if (!item.IsInstalled || !item.IsManaged)
        {
            return;
        }

        if (System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                $"确定从当前实例卸载“{item.Name}”？实例需要停止，操作前会保存当前 Plugin 配置。",
                "确认卸载",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            EnsureMarketplaceMutationAllowed();
            var snapshot = _marketplaceService.CreatePluginSnapshot(_instance);
            var output = await _service.RemovePluginAsync(
                _instance,
                item.PackageName ?? item.InstallSpec,
                _nodeRuntime());
            StatusText.Text = $"Plugin 卸载完成。实例下次启动时生效；备份：{snapshot}";
            MarketplaceStatusText.Text = string.IsNullOrWhiteSpace(output)
                ? "卸载完成。"
                : $"卸载完成：{Tail(output)}";
            await RefreshMarketplaceAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void MarketplaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarketplaceList.SelectedItem is MarketplaceItem item)
        {
            MarketplaceStatusText.Text = $"{item.VerificationText}：{item.VerificationMessage}";
        }
    }

    private void EnsureMarketplaceMutationAllowed()
    {
        if (_instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            throw new InvalidOperationException("当前实例连接的是外部 DSh 服务，Launcher 不会修改它的 Plugin。请先使用 Launcher 管理的实例。");
        }

        if (_instance.RuntimeStatus == InstanceRuntimeStatus.Running)
        {
            throw new InvalidOperationException("请先停止实例，再安装、更新或卸载 Plugin。");
        }
    }

    private static string Tail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 600 ? trimmed : trimmed[^600..];
    }

    private async void InstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        var source = TextPromptWindow.Show(Window.GetWindow(this), "安装 Plugin", "输入 npm 包名、Git 仓库或本地路径：");
        if (string.IsNullOrWhiteSpace(source)) return;
        try
        {
            var output = await _service.InstallPluginAsync(_instance, source, _nodeRuntime());
            StatusText.Text = string.IsNullOrWhiteSpace(output) ? "Plugin 安装完成。" : $"Plugin 安装完成：{output}";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ImportSkill_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择包含 SKILL.md 的 Skill 目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            var entry = await _service.ImportSkillAsync(_instance, dialog.SelectedPath);
            StatusText.Text = $"Skill 已导入：{entry.Name}。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void AddMcp_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Show(Window.GetWindow(this), "添加 MCP", "输入 serverName（仅字母、数字、-、_）：");
        if (string.IsNullOrWhiteSpace(name)) return;
        var transport = TextPromptWindow.Show(Window.GetWindow(this), "添加 MCP", "输入 transport：stdio 或 streamable-http", "stdio");
        if (string.IsNullOrWhiteSpace(transport)) return;
        var commandOrUrl = TextPromptWindow.Show(Window.GetWindow(this), "添加 MCP", transport == "stdio" ? "输入 MCP command：" : "输入 MCP URL：");
        if (string.IsNullOrWhiteSpace(commandOrUrl)) return;
        var arguments = Array.Empty<string>();
        string? workingDirectory = null;
        string? url = null;
        if (transport == "stdio")
        {
            var rawArguments = TextPromptWindow.Show(Window.GetWindow(this), "添加 MCP", "输入参数（用 | 分隔，可留空）：") ?? string.Empty;
            arguments = rawArguments.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            workingDirectory = TextPromptWindow.Show(Window.GetWindow(this), "添加 MCP", "输入工作目录（可留空）：");
        }
        else
        {
            url = commandOrUrl;
        }

        try
        {
            await _service.AddMcpAsync(
                _instance,
                new McpServerDefinition(name, transport, transport == "stdio" ? commandOrUrl : string.Empty, arguments, url, new Dictionary<string, string>(), workingDirectory),
                _nodeRuntime());
            StatusText.Text = $"MCP 已添加：{name}。下次启动实例时加载。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ImportPreset_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择包含 agent.cordis.yml 的 Agent Preset 目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            var entry = await _service.ImportPresetAsync(_instance, dialog.SelectedPath);
            StatusText.Text = $"Agent Preset 已导入：{entry.Name}。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Enable_Click(object sender, RoutedEventArgs e) => await ToggleSelectedAsync(true);

    private async void Disable_Click(object sender, RoutedEventArgs e) => await ToggleSelectedAsync(false);

    private async Task ToggleSelectedAsync(bool enabled)
    {
        if (ExtensionList.SelectedItem is not ExtensionEntry entry) return;
        try
        {
            if (entry.Kind == ExtensionKind.Plugin)
            {
                await _service.SetPluginEnabledAsync(_instance, entry, enabled);
            }
            else if (entry.Kind == ExtensionKind.Mcp)
            {
                await _service.SetMcpEnabledAsync(_instance, entry.Name, enabled);
            }
            else
            {
                throw new InvalidOperationException("当前条目不支持独立启用/禁用。");
            }

            StatusText.Text = $"已{(enabled ? "启用" : "禁用")}：{entry.Name}。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (ExtensionList.SelectedItem is not ExtensionEntry entry || entry.Kind != ExtensionKind.Plugin || !entry.Managed) return;
        try
        {
            var output = await _service.UpdatePluginAsync(_instance, entry.Name, _nodeRuntime());
            StatusText.Text = string.IsNullOrWhiteSpace(output) ? "Plugin 更新完成。" : $"Plugin 更新完成：{output}";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ExtensionList.SelectedItem is not ExtensionEntry entry) return;
        if (System.Windows.MessageBox.Show(Window.GetWindow(this), $"确定删除“{entry.Name}”？该操作只针对当前实例。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            switch (entry.Kind)
            {
                case ExtensionKind.Plugin when entry.Managed:
                    await _service.RemovePluginAsync(_instance, entry.Name, _nodeRuntime());
                    break;
                case ExtensionKind.Skill when entry.Managed:
                    await _service.RemoveSkillAsync(_instance, entry);
                    break;
                case ExtensionKind.Preset when entry.Managed:
                    await _service.RemovePresetAsync(_instance, entry);
                    break;
                case ExtensionKind.Mcp:
                    await _service.RemoveMcpAsync(_instance, entry.Name);
                    break;
                default:
                    throw new InvalidOperationException("内置条目不能删除。");
            }

            StatusText.Text = $"已删除：{entry.Name}。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ExtensionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (ExtensionList.SelectedItem is not ExtensionEntry entry)
        {
            SelectedName.Text = "未选择条目";
            SelectedDetails.Text = string.Empty;
            return;
        }

        SelectedName.Text = entry.Name;
        SelectedDetails.Text = $"类型：{entry.Kind}\n状态：{(entry.Enabled ? "已启用" : "已禁用")}\n来源：{entry.Location}\n{entry.Description}";
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, "扩展操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
