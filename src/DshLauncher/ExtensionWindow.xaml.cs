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
    private readonly IReadOnlyList<ManagerInstance>? _instances;
    private readonly Action<ManagerInstance>? _selectInstance;
    private readonly SkillMarketService? _skillMarketService;
    private bool _instanceSelectorReady;
    private readonly DshMarketThemeService _themeService = new();
    private IReadOnlyList<SkillMarketItem> _skillMarketSnapshot = Array.Empty<SkillMarketItem>();
    private bool _isSkillMarketLoading;
    private bool _isSkillMarketMutating;
    private IReadOnlyList<MarketplaceItem> _marketplaceSnapshot = Array.Empty<MarketplaceItem>();
    private IReadOnlyList<ExtensionEntry> _installedPlugins = Array.Empty<ExtensionEntry>();
    private bool _marketplaceCanMutate;
    private bool _isMarketplaceLoading;
    private bool _isMarketplaceMutating;
    private bool _controlLoaded;
    private DshMarketThemeState _themeState = DshMarketThemeState.Unavailable("尚未检测当前实例的 dsh-market。 ");
    private CancellationTokenSource? _marketplaceCancellation;
    private CancellationTokenSource? _searchDebounceCancellation;

    public ExtensionWindow(
        ManagerInstance instance,
        ExtensionService service,
        Func<NodeRuntimeInfo?> nodeRuntime,
        bool agentOnly = false,
        MarketplaceService? marketplaceService = null,
        IReadOnlyList<ManagerInstance>? instances = null,
        Action<ManagerInstance>? selectInstance = null,
        SkillMarketService? skillMarketService = null)
    {
        _instance = instance;
        _service = service;
        _nodeRuntime = nodeRuntime;
        _agentOnly = agentOnly;
        _marketplaceService = marketplaceService;
        _instances = instances;
        _selectInstance = selectInstance;
        _skillMarketService = skillMarketService;
        InitializeComponent();
        CurrentInstanceNameText.Text = instance.Name;
        CurrentInstanceDetailsText.Text = $"{instance.KindText} · {instance.RootPath}\nDSH_HOME：{instance.DshHome}";
        if (_instances is { Count: > 1 } && _selectInstance is not null)
        {
            InstanceSelectorBox.ItemsSource = _instances;
            InstanceSelectorBox.SelectedItem = _instances.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, instance.Id, StringComparison.Ordinal));
            InstanceSelectorBox.Visibility = Visibility.Visible;
            _instanceSelectorReady = true;
        }

        if (_agentOnly)
        {
            if (_skillMarketService is not null)
            {
                SkillMarketPanel.Visibility = Visibility.Visible;
                SetupSkillMarket();
            }
            else
            {
                Grid.SetColumnSpan(InstalledPanel, 3);
            }

            MarketplacePanel.Visibility = Visibility.Collapsed;
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
    }

    private void InstanceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_instanceSelectorReady
            || InstanceSelectorBox.SelectedItem is not ManagerInstance target
            || string.Equals(target.Id, _instance.Id, StringComparison.Ordinal))
        {
            return;
        }

        _selectInstance?.Invoke(target);
    }

    private void SetupSkillMarket()
    {
        var cached = _skillMarketService!.ReadCached();
        if (cached.Count > 0)
        {
            _skillMarketSnapshot = cached;
            RenderSkillMarketItems(cached);
            if (cached.Any(item => item.ValidationVersion != SkillMarketService.CurrentValidationVersion))
            {
                _ = RefreshSkillMarketAsync();
            }
        }
        else
        {
            _ = RefreshSkillMarketAsync();
        }
    }

    private void RenderSkillMarketItems(IReadOnlyList<SkillMarketItem> items)
    {
        var query = SkillMarketSearchBox.Text.Trim();
        var category = (SkillMarketCategoryList.SelectedItem as ListBoxItem)?.Tag?.ToString() ?? string.Empty;
        var instanceStopped = _instance.RuntimeStatus != InstanceRuntimeStatus.Running
            && _instance.RuntimeOwnership == InstanceRuntimeOwnership.None;
        var rendered = items
            .Where(item => string.IsNullOrWhiteSpace(category)
                || string.Equals(item.Category, category, StringComparison.Ordinal))
            .Where(item => string.IsNullOrWhiteSpace(query)
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Repository.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (item.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(item => new SkillMarketItemViewModel(item, instanceStopped))
            .ToArray();
        SkillMarketList.ItemsSource = rendered;
        SkillMarketStatusText.Text = items.Count == 0
            ? "目录为空；点击“刷新目录”从 GitHub 搜索。"
            : $"显示 {rendered.Length} / {items.Count} 个 Skill · 安装要求实例已停止";
    }

    private async Task RefreshSkillMarketAsync()
    {
        if (_skillMarketService is null || _isSkillMarketLoading)
        {
            return;
        }

        _isSkillMarketLoading = true;
        SkillMarketRefreshButton.IsEnabled = false;
        SkillMarketStatusText.Text = "正在从 GitHub 搜索并校验 SKILL.md…";
        try
        {
            var progress = new Progress<SkillMarketRefreshProgress>(state =>
            {
                if (!_isSkillMarketLoading)
                {
                    return;
                }

                _skillMarketSnapshot = state.Items;
                RenderSkillMarketItems(state.Items);
                SkillMarketStatusText.Text = state.Total == 0
                    ? $"{state.Stage}…"
                    : $"{state.Stage}：{state.Completed} / {state.Total}";
            });
            var items = await _skillMarketService.SearchAsync(progress: progress);
            _skillMarketSnapshot = items;
            RenderSkillMarketItems(items);
        }
        catch (Exception ex)
        {
            SkillMarketStatusText.Text = $"刷新 Skill 目录失败：{ex.Message}";
        }
        finally
        {
            _isSkillMarketLoading = false;
            SkillMarketRefreshButton.IsEnabled = true;
        }
    }

    private void SkillMarketRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshSkillMarketAsync();

    private void SkillMarketSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_skillMarketSnapshot.Count > 0)
        {
            RenderSkillMarketItems(_skillMarketSnapshot);
        }
    }

    private void SkillMarketCategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_skillMarketSnapshot.Count > 0)
        {
            RenderSkillMarketItems(_skillMarketSnapshot);
        }
    }

    private async void SkillInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_isSkillMarketMutating
            || (sender as FrameworkElement)?.DataContext is not SkillMarketItemViewModel viewModel)
        {
            return;
        }

        _isSkillMarketMutating = true;
        SkillMarketStatusText.Text = $"正在下载并安装 {viewModel.Item.Repository}…";
        try
        {
            var installedName = await Task.Run(() =>
                _skillMarketService!.InstallAsync(_instance, viewModel.Item));
            SkillMarketStatusText.Text = $"已安装 Skill：{installedName}。";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SkillMarketStatusText.Text = $"安装 Skill 失败：{ex.Message}";
        }
        finally
        {
            _isSkillMarketMutating = false;
        }
    }

    private sealed class SkillMarketItemViewModel
    {
        public SkillMarketItemViewModel(SkillMarketItem item, bool instanceStopped)
        {
            Item = item;
            CanInstall = item.Verified && instanceStopped;
        }

        public SkillMarketItem Item { get; }
        public string Name => Item.Name;
        public string Repository => Item.Repository;
        public string? Description => Item.Description;
        public string StarsText => $"{Item.Category} · ★ {Item.Stars} · {(Item.UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "时间未知")}";
        public string StatusText => Item.Verified
            ? "SKILL.md 已校验"
            : Item.ValidationVersion == SkillMarketService.CurrentValidationVersion
                ? "SKILL.md 未通过格式校验"
                : "校验暂未完成，可刷新重试";
        public bool CanInstall { get; }
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        _controlLoaded = true;
        if (!_agentOnly)
        {
            // Show the cached catalog first; only go online when there is no
            // cache yet (first run) or when the user explicitly refreshes.
            var hasCache = await LoadCachedMarketplaceAsync();
            if (!hasCache)
            {
                _ = RefreshMarketplaceAsync();
            }
        }

        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            var selectedId = (ExtensionList.SelectedItem as ExtensionEntry)?.Id;
            var entries = await Task.Run(async () => await _service.ListAsync(_instance));
            var rendered = (_agentOnly
                    ? entries.Where(entry => entry.Kind is ExtensionKind.Skill or ExtensionKind.Preset or ExtensionKind.Workflow)
                    : entries.Where(entry => entry.Kind is ExtensionKind.Plugin or ExtensionKind.Mcp))
                .ToList();
            // 整批替换 ItemsSource，避免逐条 Add 触发多次布局。
            ExtensionList.ItemsSource = rendered;
            if (selectedId is not null)
            {
                ExtensionList.SelectedItem = rendered.FirstOrDefault(entry => entry.Id == selectedId);
            }
            StatusText.Text = _agentOnly
                ? $"已读取 {rendered.Count} 个 Skill / Agent Preset / Workflow。"
                : $"已读取 {rendered.Count} 个 Plugin / MCP。";
            UpdateSelection();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void MarketplaceRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMarketplaceMutating)
        {
            await RefreshMarketplaceAsync();
        }
    }

    private async void MarketplaceSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_controlLoaded)
        {
            return;
        }

        _searchDebounceCancellation?.Cancel();
        _searchDebounceCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _searchDebounceCancellation = cancellation;
        try
        {
            await Task.Delay(180, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                RenderMarketplaceItems();
            }
        }
        catch (OperationCanceledException)
        {
            // A new keystroke superseded this local filter.
        }
    }

    private void MarketplaceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_controlLoaded)
        {
            RenderMarketplaceItems();
        }
    }

    private async Task<bool> LoadCachedMarketplaceAsync()
    {
        if (_marketplaceService is null)
        {
            return false;
        }

        try
        {
            // 缓存可达 MB 级 JSON；解析放到后台线程，打开页面不阻塞 UI。
            var cached = await Task.Run(() => _marketplaceService.ReadCached(_instance));
            if (cached is null)
            {
                MarketplaceStatusText.Text = "还没有本地缓存；点击“刷新目录”可从在线来源读取插件目录。";
                return false;
            }

            await SetMarketplaceSnapshotAsync(cached, fromCache: true);
            return true;
        }
        catch (Exception ex)
        {
            MarketplaceStatusText.Text = $"读取插件市场缓存失败：{ex.Message}";
            return false;
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
                query: null,
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
        var (installed, themeState) = await Task.Run(async () =>
        {
            var scanned = await _service.ListAsync(_instance, cancellationToken);
            var theme = await _themeService.ReadAsync(_instance, cancellationToken);
            return (scanned, theme);
        }, cancellationToken);
        _installedPlugins = installed
            .Where(entry => entry.Kind == ExtensionKind.Plugin)
            .ToArray();
        _themeState = themeState;
        _marketplaceCanMutate = !_isMarketplaceMutating
            && _instance.RuntimeOwnership != InstanceRuntimeOwnership.Attached
            && _instance.RuntimeStatus != InstanceRuntimeStatus.Running;
        RenderMarketplaceItems();
        MarketplaceSummaryText.Text = $"找到 {_marketplaceSnapshot.Count} 个候选插件 · 已检查 {result.SourcesChecked} 个来源"
            + (fromCache ? " · 本地缓存" : string.Empty);
        if (fromCache)
        {
            MarketplaceStatusText.Text = result.Warnings.FirstOrDefault()
                ?? "当前显示本地缓存；需要在线更新时请点击“刷新目录”。";
        }
    }

    private int _marketplaceRenderVersion;

    private void RenderMarketplaceItems()
    {
        if (_marketplaceService is null)
        {
            return;
        }

        // 筛选、排序和逐条投影全部放到后台线程（目录可达千余条，切分类/搜索
        // 时在 UI 线程同步重算会明显卡顿）；UI 线程只做一次 ItemsSource 替换。
        // 渲染版本号防止慢的旧结果覆盖新的选择。
        var renderVersion = ++_marketplaceRenderVersion;
        var snapshot = _marketplaceSnapshot;
        var query = MarketplaceSearchBox.Text;
        var sourceKind = GetSelectedSourceKind();
        var sortOrder = GetSelectedSortOrder();
        var category = GetSelectedCategory();
        var installedPlugins = _installedPlugins;
        var canMutate = _marketplaceCanMutate;
        var themeState = _themeState;
        var instanceRunning = _instance.RuntimeStatus == InstanceRuntimeStatus.Running;
        var instanceAttached = _instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached;
        var mutating = _isMarketplaceMutating;

        _ = Task.Run(() =>
        {
            var rendered = BuildMarketplaceItems(
                snapshot,
                query,
                sourceKind,
                sortOrder,
                category,
                installedPlugins,
                canMutate,
                themeState,
                instanceRunning,
                instanceAttached,
                mutating);
            Dispatcher.BeginInvoke(() =>
            {
                if (renderVersion != _marketplaceRenderVersion)
                {
                    return;
                }

                MarketplaceList.ItemsSource = rendered;
                MarketplaceSummaryText.Text = rendered.Count == snapshot.Count
                    ? $"找到 {snapshot.Count} 个候选插件"
                    : $"显示 {rendered.Count} / {snapshot.Count} 个候选插件";
            });
        });
    }

    internal static List<MarketplaceItem> BuildMarketplaceItems(
        IReadOnlyList<MarketplaceItem> snapshot,
        string? query,
        MarketplaceSourceKind? sourceKind,
        MarketplaceSortOrder sortOrder,
        string? category,
        IReadOnlyList<ExtensionEntry> installedPlugins,
        bool canMutate,
        DshMarketThemeState themeState,
        bool instanceRunning,
        bool instanceAttached,
        bool mutating)
    {
        var items = MarketplaceService.FilterAndSort(
            snapshot,
            query: query,
            sourceKind: sourceKind,
            sortOrder: sortOrder,
            category: category);
        var rendered = new List<MarketplaceItem>(items.Count);
        foreach (var item in items)
        {
            var installedEntry = MarketplaceService.FindInstalledPlugin(item, installedPlugins);
            var isInstalled = installedEntry is not null;
            var isTheme = string.Equals(
                MarketplaceService.NormalizeCategory(item.Category),
                "主题",
                StringComparison.OrdinalIgnoreCase);
            var themePackageName = installedEntry?.Name;
            var themeMarketAvailable = isTheme
                && themePackageName is not null
                && themeState.IsAvailable
                && themeState.InstalledNames.Contains(themePackageName);
            var themeCanApply = themeMarketAvailable
                && installedEntry!.Managed
                && instanceRunning
                && !instanceAttached
                && !mutating;
            rendered.Add(item with
            {
                IsInstalled = isInstalled,
                IsManaged = isInstalled && installedEntry!.Managed,
                InstalledVersion = installedEntry?.Version,
                UpdateStatus = isInstalled
                    ? MarketplaceService.GetUpdateStatus(item.Version, installedEntry?.Version)
                    : MarketplaceUpdateStatus.Unknown,
                CanMutate = canMutate,
                IsTheme = isTheme,
                ThemeMarketAvailable = themeMarketAvailable,
                ThemeCanApply = themeCanApply,
                ThemePackageName = themePackageName,
                ThemeStatusText = isTheme
                    ? GetThemeStatusText(isInstalled, themeMarketAvailable, themePackageName, themeState, instanceRunning, instanceAttached)
                    : null
            });
        }

        return rendered;
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

    private string? GetSelectedCategory()
    {
        var tag = (MarketplaceCategoryList.SelectedItem as ListBoxItem)?.Tag as string;
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private static string GetThemeStatusText(
        bool isInstalled,
        bool themeMarketAvailable,
        string? packageName,
        DshMarketThemeState themeState,
        bool instanceRunning,
        bool instanceAttached)
    {
        if (!isInstalled)
        {
            return "主题资源 · 安装后可通过 dsh-market 应用";
        }

        if (instanceAttached)
        {
            return "当前连接的是外部实例，Launcher 不会修改其主题";
        }

        if (!instanceRunning)
        {
            return "启动当前实例后可检测 dsh-market 并应用主题";
        }

        if (!themeState.IsAvailable)
        {
            return "未检测到 dsh-market；当前只能管理主题 Plugin";
        }

        if (!themeMarketAvailable || string.IsNullOrWhiteSpace(packageName))
        {
            return "dsh-market 未将该安装包识别为主题资源";
        }

        return themeState.LiveNames.Contains(packageName)
            ? "dsh-market 已热加载该主题"
            : "dsh-market 可应用该主题";
    }

    private async void MarketplaceAction_Click(object sender, RoutedEventArgs e)
    {
        if (_isMarketplaceMutating
            || _marketplaceService is null
            || (sender as FrameworkElement)?.DataContext is not MarketplaceItem item)
        {
            return;
        }

        try
        {
            EnsureMarketplaceMutationAllowed();
            BeginMarketplaceMutation(item.IsInstalled ? "正在准备更新 Plugin…" : "正在检查 Plugin…");
            var verification = await _marketplaceService.VerifyAsync(item);
            if (verification.Status == MarketplaceVerificationStatus.Rejected)
            {
                MarketplaceStatusText.Text = verification.Message;
                System.Windows.MessageBox.Show(Window.GetWindow(this), verification.Message, "插件不能安装", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var snapshot = _marketplaceService.CreatePluginSnapshot(_instance);
            var installedEntry = item.IsInstalled
                ? MarketplaceService.FindInstalledPlugin(item, _installedPlugins)
                : null;
            var packageSpec = verification.InstallSpec ?? item.InstallSpec;
            string output;
            try
            {
                if (item.IsInstalled)
                {
                    if (!item.IsManaged)
                    {
                        throw new InvalidOperationException("当前 Plugin 不是 Launcher 安装的，不能从市场更新。请在 DSh 自己的工具中管理它。");
                    }

                    SetMarketplaceMutationText("正在更新 Plugin…");
                    output = await _service.UpdatePluginAsync(
                        _instance,
                        installedEntry?.Name ?? verification.PackageName ?? item.PackageName ?? packageSpec,
                        _nodeRuntime());
                    StatusText.Text = $"Plugin 更新完成。实例下次启动时加载；备份：{snapshot}";
                }
                else
                {
                    SetMarketplaceMutationText("正在安装 Plugin…");
                    output = await _service.InstallPluginAsync(_instance, packageSpec, _nodeRuntime());
                    StatusText.Text = $"Plugin 安装完成。实例下次启动时加载；备份：{snapshot}";
                }
            }
            catch (Exception ex)
            {
                throw CreatePluginRollbackException(snapshot, ex);
            }

            MarketplaceStatusText.Text = string.IsNullOrWhiteSpace(output)
                ? "操作完成。"
                : $"操作完成：{Tail(output)}";
            SetMarketplaceMutationText("操作完成，正在刷新实例内容和插件市场…");
            await RefreshAsync();
            await RefreshMarketplaceAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            EndMarketplaceMutation();
        }
    }

    private async void MarketplaceRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_isMarketplaceMutating
            || _marketplaceService is null
            || (sender as FrameworkElement)?.DataContext is not MarketplaceItem item)
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
            BeginMarketplaceMutation("正在卸载 Plugin…");
            var snapshot = _marketplaceService.CreatePluginSnapshot(_instance);
            var installedEntry = MarketplaceService.FindInstalledPlugin(item, _installedPlugins);
            string output;
            try
            {
                output = await _service.RemovePluginAsync(
                    _instance,
                    installedEntry?.Name ?? item.PackageName ?? item.InstallSpec,
                    _nodeRuntime());
            }
            catch (Exception ex)
            {
                throw CreatePluginRollbackException(snapshot, ex);
            }
            StatusText.Text = $"Plugin 卸载完成。实例下次启动时生效；备份：{snapshot}";
            MarketplaceStatusText.Text = string.IsNullOrWhiteSpace(output)
                ? "卸载完成。"
                : $"卸载完成：{Tail(output)}";
            SetMarketplaceMutationText("卸载完成，正在刷新实例内容和插件市场…");
            await RefreshAsync();
            await RefreshMarketplaceAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            EndMarketplaceMutation();
        }
    }

    private void MarketplaceThemePreview_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MarketplaceItem item || !item.IsTheme)
        {
            return;
        }

        var message = $"{item.Name}\n\n{item.Description}\n\n来源：{item.SourceText}\n分类：主题\n状态：{item.VersionStatusText}\n{item.ThemeStatusText}";
        System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            message,
            "主题预览",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void MarketplaceThemeApply_Click(object sender, RoutedEventArgs e)
    {
        if (_isMarketplaceMutating
            || (sender as FrameworkElement)?.DataContext is not MarketplaceItem item
            || !item.IsTheme
            || !item.ThemeCanApply
            || string.IsNullOrWhiteSpace(item.ThemePackageName))
        {
            return;
        }

        if (System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                $"应用主题“{item.Name}”？dsh-market 会停用当前其它主题并即时切换。",
                "应用主题",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (_instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
            {
                throw new InvalidOperationException("当前实例连接的是外部 DSh 服务，Launcher 不会修改外部实例主题。 ");
            }

            BeginMarketplaceMutation("正在通过 dsh-market 应用主题…");
            var result = await _themeService.ApplyAsync(_instance, item.ThemePackageName);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? "dsh-market 应用主题失败。 ");
            }

            _themeState = _themeState with { LiveNames = result.LiveNames };
            MarketplaceStatusText.Text = $"主题已交给 dsh-market 应用：{item.Name}。";
            RenderMarketplaceItems();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            EndMarketplaceMutation();
        }
    }

    private void BeginMarketplaceMutation(string message)
    {
        _isMarketplaceMutating = true;
        MarketplaceRefreshButton.IsEnabled = false;
        MarketplaceProgressPanel.Visibility = Visibility.Visible;
        MarketplaceProgressText.Text = message;
        RenderMarketplaceItems();
    }

    private void SetMarketplaceMutationText(string message)
    {
        MarketplaceProgressText.Text = message;
        MarketplaceStatusText.Text = message;
    }

    private void EndMarketplaceMutation()
    {
        _isMarketplaceMutating = false;
        MarketplaceRefreshButton.IsEnabled = true;
        MarketplaceProgressPanel.Visibility = Visibility.Collapsed;
        _marketplaceCanMutate = _instance.RuntimeOwnership != InstanceRuntimeOwnership.Attached
            && _instance.RuntimeStatus != InstanceRuntimeStatus.Running;
        RenderMarketplaceItems();
    }

    private Exception CreatePluginRollbackException(string snapshot, Exception original)
    {
        try
        {
            if (_marketplaceService?.RestorePluginSnapshot(_instance, snapshot) == true)
            {
                return new InvalidOperationException(
                    $"{original.Message}\n已恢复操作前的 web profile 配置。",
                    original);
            }
        }
        catch (Exception rollbackError)
        {
            return new InvalidOperationException(
                $"{original.Message}\n自动恢复 web profile 失败：{rollbackError.Message}",
                original);
        }

        return new InvalidOperationException(
            $"{original.Message}\n没有可用的 web profile 备份，未能自动恢复。",
            original);
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
        var protectedBuiltIn = entry.Kind == ExtensionKind.Plugin
            && ExtensionService.IsProtectedBuiltInPlugin(entry.Name);
        if (protectedBuiltIn)
        {
            EnableButton.IsEnabled = false;
            DisableButton.IsEnabled = false;
            UpdateButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
            HintText.Text = "这是 DSh 默认 Plugin，由运行时管理，Launcher 不允许启用、禁用、更新或删除。";
            return;
        }

        EnableButton.IsEnabled = true;
        DisableButton.IsEnabled = true;
        UpdateButton.IsEnabled = true;
        RemoveButton.IsEnabled = true;
        HintText.Text = "修改前请停止实例。Plugin 和 MCP 会在下次启动时生效。";
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, "扩展操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
