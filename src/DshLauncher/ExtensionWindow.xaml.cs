using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;
using WpfListBox = System.Windows.Controls.ListBox;
using DshLauncher.Models;
using DshLauncher.Services;
using Forms = System.Windows.Forms;

namespace DshLauncher;

public partial class ExtensionWindow : UserControl
{
    private const string FeaturedCategoryKey = "__featured__";
    private ManagerInstance _instance;
    private readonly ExtensionService _service;
    private readonly Func<NodeRuntimeInfo?> _nodeRuntime;
    private readonly Func<PluginInstallMode> _pluginInstallMode;
    private readonly bool _agentOnly;
    private readonly MarketplaceService? _marketplaceService;
    private readonly Func<ManagerInstance, CancellationToken, Task<bool>>? _stopInstanceForPluginRetry;
    private readonly Func<ManagerInstance, string, Task<bool>>? _handoffPluginFailure;
    private readonly PluginFailureReportService _failureReportService = new();
    private readonly SkillMarketService? _skillMarketService;
    private readonly DshMarketThemeService _themeService = new();
    private readonly VersionSettingsService? _versionSettingsService;
    private readonly VersionSnapshotService? _versionSnapshotService;
    private readonly Action? _openPluginMatrix;
    private IReadOnlyList<SkillMarketItem> _skillMarketSnapshot = Array.Empty<SkillMarketItem>();
    private bool _isSkillMarketLoading;
    private bool _isSkillMarketMutating;
    private int _lastSkillProgressItemCount = -1;
    private IReadOnlyList<string> _skillMarketWarnings = Array.Empty<string>();
    private DateTimeOffset _lastSkillProgressRenderAt = DateTimeOffset.MinValue;
    private IReadOnlyList<MarketplaceItem> _marketplaceSnapshot = Array.Empty<MarketplaceItem>();
    private IReadOnlyList<ExtensionEntry> _installedPlugins = Array.Empty<ExtensionEntry>();
    private IReadOnlyList<ExtensionEntry> _installedSkills = Array.Empty<ExtensionEntry>();
    private bool _marketplaceCanMutate;
    private bool _isMarketplaceLoading;
    private bool _isMarketplaceMutating;
    private bool _controlLoaded;
    private DshMarketThemeState _themeState = DshMarketThemeState.Unavailable("尚未检测当前实例的 dsh-market。 ");
    private CancellationTokenSource? _marketplaceCancellation;
    private CancellationTokenSource? _searchDebounceCancellation;
    private CancellationTokenSource? _skillSearchDebounceCancellation;
    private readonly Dictionary<string, double> _marketplaceScrollOffsets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _skillMarketScrollOffsets = new(StringComparer.Ordinal);
    private string _activeMarketplaceCategoryKey = string.Empty;
    private string _activeSkillMarketCategoryKey = string.Empty;
    private bool _useDshMarketHotReload = true;
    private readonly UiStateStore _uiStateStore;
    private readonly DispatcherTimer _uiStateSaveTimer;
    private IReadOnlyDictionary<string, PluginUpdateInfo> _pluginUpdateInfos =
        new Dictionary<string, PluginUpdateInfo>(StringComparer.OrdinalIgnoreCase);

    public ExtensionWindow(
        ManagerInstance instance,
        ExtensionService service,
        Func<NodeRuntimeInfo?> nodeRuntime,
        bool agentOnly = false,
        MarketplaceService? marketplaceService = null,
        SkillMarketService? skillMarketService = null,
        Func<PluginInstallMode>? pluginInstallMode = null,
        Func<ManagerInstance, CancellationToken, Task<bool>>? stopInstanceForPluginRetry = null,
        Func<ManagerInstance, string, Task<bool>>? handoffPluginFailure = null,
        VersionSettingsService? versionSettingsService = null,
        VersionSnapshotService? versionSnapshotService = null,
        Action? openPluginMatrix = null,
        UiStateStore? uiStateStore = null)
    {
        _instance = instance;
        _service = service;
        _nodeRuntime = nodeRuntime;
        _pluginInstallMode = pluginInstallMode ?? (() => PluginInstallMode.Fast);
        _agentOnly = agentOnly;
        _marketplaceService = marketplaceService;
        _stopInstanceForPluginRetry = stopInstanceForPluginRetry;
        _handoffPluginFailure = handoffPluginFailure;
        _skillMarketService = skillMarketService;
        _versionSettingsService = versionSettingsService;
        _versionSnapshotService = versionSnapshotService;
        _openPluginMatrix = openPluginMatrix;
        // 默认用真实数据根；测试/冒烟可注入临时目录，避免污染用户 ui-state.json。
        _uiStateStore = uiStateStore ?? new UiStateStore();
        InitializeComponent();
        _uiStateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiStateSaveTimer.Tick += (_, _) =>
        {
            _uiStateSaveTimer.Stop();
            SaveMarketplaceUiState();
        };
        Unloaded += (_, _) =>
        {
            _uiStateSaveTimer.Stop();
            SaveMarketplaceUiState();
        };
        _useDshMarketHotReload = _versionSettingsService?.Read(instance).UseDshMarketHotReload ?? true;
        DshMarketHotReloadCheckBox.IsChecked = _useDshMarketHotReload;
        MarketplaceCategoryList.Visibility = _agentOnly ? Visibility.Collapsed : Visibility.Visible;
        SkillMarketCategoryList.Visibility = _agentOnly ? Visibility.Visible : Visibility.Collapsed;
        CurrentInstanceNameText.Text = instance.Name;
        CurrentInstanceMetaText.Text = string.Join(
            " · ",
            new[] { instance.DshVersionText, instance.KindText }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        // 路径太长会挤占左栏：链接化（点击复制，悬停出悬浮卡片，不占界面布局）。
        CurrentInstanceRootPathLink.Content = "目录：" + PathDisplay.Tail(instance.RootPath);
        CurrentInstanceRootPathLink.Tag = instance.RootPath;
        CurrentInstanceRootPathLink.ToolTip = CreatePathCard("实例目录", instance.RootPath);
        CurrentInstanceDshHomeLink.Content = "DSH_HOME：" + PathDisplay.Tail(instance.DshHome);
        CurrentInstanceDshHomeLink.Tag = instance.DshHome;
        CurrentInstanceDshHomeLink.ToolTip = CreatePathCard("DSH_HOME", instance.DshHome);

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
            CheckUpdatesButton.Visibility = Visibility.Collapsed;
            UpdateAllButton.Visibility = Visibility.Collapsed;
            DoctorButton.Visibility = Visibility.Collapsed;
            AddMcpButton.Visibility = Visibility.Collapsed;
            DshMarketHotReloadCheckBox.Visibility = Visibility.Collapsed;
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

    private void SetupSkillMarket()
    {
        _ = SetupSkillMarketAsync();
    }

    private async Task SetupSkillMarketAsync()
    {
        var cached = await Task.Run(() => _skillMarketService!.ReadCached());
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

    private void RenderSkillMarketItems(
        IReadOnlyList<SkillMarketItem> items,
        string? restoreCategoryKey = null)
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
            .Select(item => new SkillMarketItemViewModel(
                item,
                instanceStopped,
                IsSkillInstalled(item, _installedSkills)))
            .ToArray();
        SkillMarketList.ItemsSource = rendered;
        SkillMarketStatusText.Text = items.Count == 0
            ? "目录为空；点击“刷新目录”从 GitHub 搜索。"
            : $"显示 {rendered.Length} / {items.Count} 个 Skill · 安装要求实例已停止";
        if (restoreCategoryKey is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RestoreScrollOffset(
                SkillMarketList,
                _skillMarketScrollOffsets,
                restoreCategoryKey)));
        }
    }

    private async Task RefreshSkillMarketAsync()
    {
        if (_skillMarketService is null || _isSkillMarketLoading)
        {
            return;
        }

        _isSkillMarketLoading = true;
        _lastSkillProgressItemCount = -1;
        _lastSkillProgressRenderAt = DateTimeOffset.MinValue;
        _skillMarketWarnings = Array.Empty<string>();
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
                _skillMarketWarnings = state.Warnings ?? Array.Empty<string>();
                var now = DateTimeOffset.UtcNow;
                var shouldRender = state.Items.Count != _lastSkillProgressItemCount
                    && (now - _lastSkillProgressRenderAt >= TimeSpan.FromMilliseconds(150)
                        || state.Completed >= state.Total);
                if (shouldRender)
                {
                    RenderSkillMarketItems(state.Items);
                    _lastSkillProgressItemCount = state.Items.Count;
                    _lastSkillProgressRenderAt = now;
                }

                var progressText = state.Total == 0
                    ? $"{state.Stage}…"
                    : $"{state.Stage}：{state.Completed} / {state.Total}";
                SkillMarketStatusText.Text = _skillMarketWarnings.Count > 0
                    ? $"{progressText}\n⚠ {_skillMarketWarnings[^1]}"
                    : progressText;
            });
            var items = await _skillMarketService.SearchAsync(progress: progress);
            _skillMarketSnapshot = items;
            RenderSkillMarketItems(items);
            if (_skillMarketWarnings.Count > 0)
            {
                // 刷新结束保留失败提示，避免“目录为空”误导（GitHub 限流等）。
                SkillMarketStatusText.Text = $"⚠ {_skillMarketWarnings[^1]}";
            }
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

    private async void SkillMarketSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _skillSearchDebounceCancellation?.Cancel();
        _skillSearchDebounceCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _skillSearchDebounceCancellation = cancellation;
        try
        {
            await Task.Delay(180, cancellation.Token);
            if (!cancellation.IsCancellationRequested && _skillMarketSnapshot.Count > 0)
            {
                RenderSkillMarketItems(_skillMarketSnapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // A new keystroke superseded this local filter.
        }
    }

    private void SkillMarketCategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var nextCategoryKey = GetSelectedSkillCategoryKey();
        SaveScrollOffset(SkillMarketList, _skillMarketScrollOffsets, _activeSkillMarketCategoryKey);
        _activeSkillMarketCategoryKey = nextCategoryKey;
        if (_skillMarketSnapshot.Count > 0)
        {
            RenderSkillMarketItems(_skillMarketSnapshot, nextCategoryKey);
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
        using var operationCancellation = new CancellationTokenSource();
        var progressWindow = new PluginProgressWindow(
            Window.GetWindow(this),
            operationCancellation,
            $"安装 Skill · {viewModel.Item.Name}",
            "正在连接 GitHub 下载 Skill…");
        progressWindow.Show();
        progressWindow.SetIndeterminate("正在连接 GitHub 下载 Skill…");
        try
        {
            var progress = new Progress<SkillInstallProgress>(update =>
                progressWindow.SetDownloadProgress(update, viewModel.Item.Name));
            var installedName = await Task.Run(() =>
                _skillMarketService!.InstallAsync(
                    _instance,
                    viewModel.Item,
                    progress,
                    operationCancellation.Token));
            SkillMarketStatusText.Text = $"已安装 Skill：{installedName}。";
            progressWindow.SetIndeterminate("Skill 已导入，正在刷新当前实例…");
            await RefreshAsync();
            progressWindow.Complete($"Skill 已安装：{installedName}。");
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            const string message = "Skill 安装已取消。";
            SkillMarketStatusText.Text = message;
            progressWindow.Canceled(message);
        }
        catch (Exception ex)
        {
            SkillMarketStatusText.Text = $"安装 Skill 失败：{ex.Message}";
            progressWindow.Fail(ex.Message);
        }
        finally
        {
            _isSkillMarketMutating = false;
        }
    }

    private sealed class SkillMarketItemViewModel
    {
        public SkillMarketItemViewModel(SkillMarketItem item, bool instanceStopped, bool isInstalled)
        {
            Item = item;
            IsInstalled = isInstalled;
            CanInstall = item.Verified && instanceStopped && !isInstalled;
        }

        public SkillMarketItem Item { get; }
        public string Name => Item.Name;
        public string Repository => Item.Repository;
        public string? Description => Item.Description;
        public string StarsText => $"{Item.Category} · ★ {Item.Stars} · {(Item.UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "时间未知")}";
        public string StatusText => IsInstalled
            ? "已安装到当前实例"
            : Item.Verified
            ? "SKILL.md 已校验"
            : Item.ValidationVersion == SkillMarketService.CurrentValidationVersion
                ? "SKILL.md 未通过格式校验"
                : "校验暂未完成，可刷新重试";
        public string ActionText => IsInstalled ? "已安装" : "安装";
        public bool IsInstalled { get; }
        public bool CanInstall { get; }
    }

    internal static bool IsSkillInstalled(
        SkillMarketItem item,
        IEnumerable<ExtensionEntry> installedSkills) =>
        installedSkills.Any(entry => entry.Kind == ExtensionKind.Skill
            && entry.Managed
            && string.Equals(entry.Name, item.Name, StringComparison.OrdinalIgnoreCase));

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        // 先恢复上次的市场 UI 状态（搜索/分类/来源/排序/滚动），再置 _controlLoaded
        // 以抑制恢复过程中的渲染与重复保存。
        RestoreMarketplaceUiState();
        _controlLoaded = true;
        _activeMarketplaceCategoryKey = GetSelectedCategoryKey();
        _activeSkillMarketCategoryKey = GetSelectedSkillCategoryKey();
        MarketplaceList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(MarketplaceList_ScrollChanged));
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

    private void PathLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string text || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText.Text = "已复制路径：" + text;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>悬浮卡片：独立浮层显示完整路径，不占原界面布局。</summary>
    private System.Windows.Controls.ToolTip CreatePathCard(string title, string fullPath)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        });
        panel.Children.Add(new TextBlock
        {
            Text = fullPath,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Margin = new Thickness(0, 4, 0, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "点击即可复制",
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 8, 0, 0)
        });
        return new System.Windows.Controls.ToolTip { Style = (Style)FindResource("PathCardToolTip"), Content = panel };
    }

    /// <summary>插件详情卡片：类型/状态/完整来源/描述，避免在左栏里堆文字。</summary>
    private System.Windows.Controls.ToolTip CreatePluginDetailCard(ExtensionEntry entry)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"类型：{entry.Kind}　状态：{(entry.Enabled ? "已启用" : "已禁用")}",
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 4, 0, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = entry.Location,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Margin = new Thickness(0, 6, 0, 0)
        });
        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            panel.Children.Add(new TextBlock
            {
                Text = entry.Description,
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "点击路径即可复制",
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 8, 0, 0)
        });
        return new System.Windows.Controls.ToolTip { Style = (Style)FindResource("PathCardToolTip"), Content = panel };
    }

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
            if (_agentOnly)
            {
                _installedSkills = entries
                    .Where(entry => entry.Kind == ExtensionKind.Skill && entry.Managed)
                    .ToArray();
                if (_skillMarketSnapshot.Count > 0)
                {
                    RenderSkillMarketItems(_skillMarketSnapshot);
                }
            }
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
                ScheduleUiStateSave();
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
            if (ReferenceEquals(sender, MarketplaceCategoryList))
            {
                var nextCategoryKey = GetSelectedCategoryKey();
                SaveScrollOffset(MarketplaceList, _marketplaceScrollOffsets, _activeMarketplaceCategoryKey);
                _activeMarketplaceCategoryKey = nextCategoryKey;
                RenderMarketplaceItems(nextCategoryKey);
            }
            else
            {
                RenderMarketplaceItems();
            }

            ScheduleUiStateSave();
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
        // 刷新总预算：社区目录大文件（约 2MB）在慢网络需 50s 左右，
        // 放宽到 90s（各来源并行，实际等待约等于最慢来源）。
        _marketplaceCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        MarketplaceStatusText.Text = _marketplaceSnapshot.Count == 0
            ? "正在读取插件目录，请稍候…"
            : "正在后台更新目录，当前先显示本地缓存。";

        try
        {
            // 分批到达：每个来源完成即把合并结果先显示出来（保留滚动位置），
            // 剩余来源继续在后台更新；完成后走完整刷新（重扫已安装/主题状态）。
            var progress = new Progress<MarketplaceRefreshProgress>(state =>
            {
                if (state.Items.Count == _marketplaceSnapshot.Count)
                {
                    return;
                }

                _marketplaceSnapshot = state.Items;
                MarketplaceStatusText.Text = $"后台更新中：已合并 {state.Items.Count} 条候选插件…";
                RenderMarketplaceItems();
            });
            var result = await _marketplaceService.SearchAsync(
                _instance,
                query: null,
                _marketplaceCancellation.Token,
                progress: progress);
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
            var theme = _useDshMarketHotReload
                ? await _themeService.ReadAsync(_instance, cancellationToken)
                : DshMarketThemeState.Unavailable("当前实例已关闭 dsh-market 热加载。 ");
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
    private const int MarketplacePageSize = 150;
    private int _marketplaceVisibleCount = MarketplacePageSize;
    private string _marketplaceFilterKey = string.Empty;

    private void RenderMarketplaceItems(string? restoreCategoryKey = null)
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
        var featuredOnly = string.Equals(category, FeaturedCategoryKey, StringComparison.Ordinal);
        if (featuredOnly)
        {
            category = null;
        }
        var filterKey = $"{query}|{sourceKind}|{sortOrder}|{category}|{featuredOnly}";
        if (!string.Equals(filterKey, _marketplaceFilterKey, StringComparison.Ordinal))
        {
            // 筛选条件变化：批量加载回到底部一次；否则保留已展开的条数。
            _marketplaceFilterKey = filterKey;
            _marketplaceVisibleCount = MarketplacePageSize;
        }

        var visibleCount = _marketplaceVisibleCount;
        // 记录当前位置：ItemSource 替换会重置滚动偏移，保证任何重渲染“原地”。
        var currentOffset = FindScrollViewer(MarketplaceList)?.VerticalOffset ?? 0;
        if (currentOffset <= 0
            && _marketplaceScrollOffsets.TryGetValue(_activeMarketplaceCategoryKey, out var savedOffset))
        {
            // 首次打开（或重渲染到顶部）时恢复上次保存的滚动位置。
            currentOffset = savedOffset;
        }
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
                featuredOnly,
                installedPlugins,
                canMutate,
                themeState,
                instanceRunning,
                instanceAttached,
                mutating);
            // 批量加载：只暴露前 N 条，滚动到底部附近时再扩大。
            var visible = visibleCount < rendered.Count
                ? rendered.Take(visibleCount).ToList()
                : rendered;
            Dispatcher.BeginInvoke(() =>
            {
                if (renderVersion != _marketplaceRenderVersion)
                {
                    return;
                }

                MarketplaceList.ItemsSource = visible;
                MarketplaceSummaryText.Text = visible.Count == snapshot.Count
                    ? $"找到 {snapshot.Count} 个候选插件"
                    : $"显示 {visible.Count} / {snapshot.Count} 个候选插件（滚动到底部加载更多）";
                if (restoreCategoryKey is not null)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RestoreScrollOffset(
                        MarketplaceList,
                        _marketplaceScrollOffsets,
                        restoreCategoryKey)));
                }
                else
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                        FindScrollViewer(MarketplaceList)?.ScrollToVerticalOffset(currentOffset)));
                }
            });
        });
    }

    private void MarketplaceList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        ScheduleUiStateSave();
        // 到达底部附近时加载下一批（增量加载，CanContentScroll 下单位为“条目”）。
        var viewer = FindScrollViewer(MarketplaceList);
        if (viewer is null || viewer.ExtentHeight <= viewer.ViewportHeight)
        {
            return;
        }

        var remaining = viewer.ExtentHeight - (viewer.VerticalOffset + viewer.ViewportHeight);
        if (remaining <= Math.Max(6, viewer.ViewportHeight * 1.5)
            && _marketplaceVisibleCount < _marketplaceSnapshot.Count)
        {
            _marketplaceVisibleCount = Math.Min(
                _marketplaceSnapshot.Count,
                _marketplaceVisibleCount + MarketplacePageSize);
            RenderMarketplaceItems();
        }
    }

    internal static List<MarketplaceItem> BuildMarketplaceItems(
        IReadOnlyList<MarketplaceItem> snapshot,
        string? query,
        MarketplaceSourceKind? sourceKind,
        MarketplaceSortOrder sortOrder,
        string? category,
        bool featuredOnly,
        IReadOnlyList<ExtensionEntry> installedPlugins,
        bool canMutate,
        DshMarketThemeState themeState,
        bool instanceRunning,
        bool instanceAttached,
        bool mutating)
    {
        var items = MarketplaceService.FilterAndSortMerged(
            snapshot,
            query: query,
            sourceKind: sourceKind,
            sortOrder: sortOrder,
            category: category);
        if (featuredOnly)
        {
            items = items.Where(IsFeaturedMarketplaceItem).ToList();
        }
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
                CanInstallOrUpdate = !instanceAttached && !mutating,
                IsTheme = isTheme,
                ThemeMarketAvailable = themeMarketAvailable,
                ThemeCanApply = themeCanApply,
                ThemePackageName = themePackageName,
                DeveloperAvatarUrl = MarketplaceService.GetDeveloperAvatarUrl(item),
                IsHotLoadAction = instanceRunning && !instanceAttached && !isInstalled,
                ThemeStatusText = isTheme
                    ? GetThemeStatusText(isInstalled, themeMarketAvailable, themePackageName, themeState, instanceRunning, instanceAttached)
                    : null
            });
        }

        return rendered;
    }

    internal static bool IsFeaturedMarketplaceItem(MarketplaceItem item) =>
        item.SourceKind == MarketplaceSourceKind.CommunityCatalog
        || item.MergedSourceKinds?.Contains(MarketplaceSourceKind.CommunityCatalog) == true;

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
        var tag = GetSelectedCategoryKey();
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private string GetSelectedCategoryKey() =>
        (MarketplaceCategoryList.SelectedItem as ListBoxItem)?.Tag as string ?? string.Empty;

    private string GetSelectedSkillCategoryKey() =>
        (SkillMarketCategoryList.SelectedItem as ListBoxItem)?.Tag?.ToString() ?? string.Empty;

    private static void SaveScrollOffset(
        WpfListBox list,
        IDictionary<string, double> offsets,
        string categoryKey)
    {
        if (string.IsNullOrEmpty(categoryKey))
        {
            return;
        }

        var viewer = FindScrollViewer(list);
        if (viewer is not null)
        {
            offsets[categoryKey] = viewer.VerticalOffset;
        }
    }

    private static void RestoreScrollOffset(
        WpfListBox list,
        IReadOnlyDictionary<string, double> offsets,
        string categoryKey)
    {
        var viewer = FindScrollViewer(list);
        if (viewer is null)
        {
            return;
        }

        viewer.ScrollToVerticalOffset(offsets.TryGetValue(categoryKey, out var offset) ? offset : 0);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, index));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
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

        using var operationCancellation = new CancellationTokenSource();
        PluginProgressWindow? progressWindow = null;
        try
        {
            EnsureMarketplaceMutationAllowed(allowRunning: true);
            var useDshMarket = _instance.RuntimeStatus == InstanceRuntimeStatus.Running;
            var initialStatus = useDshMarket
                ? "正在检查当前实例的 dsh-market…"
                : item.IsInstalled ? "正在准备更新 Plugin…" : "正在检查 Plugin…";
            BeginMarketplaceMutation(initialStatus);
            progressWindow = new PluginProgressWindow(
                Window.GetWindow(this),
                operationCancellation,
                item.IsInstalled ? $"更新 Plugin · {item.Name}" : $"安装 Plugin · {item.Name}",
                initialStatus);
            progressWindow.Show();
            progressWindow.SetIndeterminate(initialStatus);
            if (useDshMarket)
            {
                if (!_useDshMarketHotReload)
                {
                    const string message = "当前实例已关闭 dsh-market 热加载。请先停止实例，再点击“安装”或使用“手动安装 Plugin”。";
                    MarketplaceStatusText.Text = message;
                    progressWindow.Fail(message);
                    System.Windows.MessageBox.Show(
                        Window.GetWindow(this),
                        message,
                        "无法热加载",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                _themeState = await _themeService.ReadAsync(_instance, operationCancellation.Token);
                if (!_themeState.IsAvailable)
                {
                    var message = $"当前实例没有可用的 dsh-market，运行中不能热加载。请先停止实例，再点击“安装”或使用“手动安装 Plugin”。\n\n{_themeState.Error}";
                    MarketplaceStatusText.Text = message;
                    progressWindow.Fail(message);
                    System.Windows.MessageBox.Show(
                        Window.GetWindow(this),
                        message,
                        "未检测到 dsh-market",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (!item.IsInstalled && string.IsNullOrWhiteSpace(item.DshMarketUrl))
                {
                    const string message = "该插件不在 dsh-market 目录中，无法在运行中热加载。请先停止实例，再点击“安装”进行普通安装。";
                    MarketplaceStatusText.Text = message;
                    progressWindow.Fail(message);
                    System.Windows.MessageBox.Show(
                        Window.GetWindow(this),
                        message,
                        "dsh-market 不支持该条目",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (item.IsInstalled && string.IsNullOrWhiteSpace(item.DshMarketUrl))
                {
                    const string message = "该插件不在 dsh-market 目录中，无法在运行中热加载更新。请先停止实例，再点击“更新”进行普通更新。";
                    MarketplaceStatusText.Text = message;
                    progressWindow.Fail(message);
                    System.Windows.MessageBox.Show(
                        Window.GetWindow(this),
                        message,
                        "dsh-market 不支持该条目",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
            }

            var verification = await _marketplaceService.VerifyAsync(item, operationCancellation.Token, _instance);
            if (verification.Status == MarketplaceVerificationStatus.Rejected)
            {
                MarketplaceStatusText.Text = verification.Message;
                progressWindow.Fail(verification.Message);
                return;
            }

            if (verification.Status == MarketplaceVerificationStatus.Incompatible)
            {
                var proceed = System.Windows.MessageBox.Show(
                    Window.GetWindow(this),
                    $"该插件与当前实例的 DSh 运行时不兼容：\n\n{verification.Message}\n\n继续{(item.IsInstalled ? "更新" : "安装")}后实例可能无法启动（可用安全模式或「逐插件定位」恢复）。仍要继续吗？",
                    "插件依赖不兼容",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes;
                if (!proceed)
                {
                    MarketplaceStatusText.Text = verification.Message;
                    progressWindow.Fail(verification.Message);
                    return;
                }
            }

            progressWindow.SetIndeterminate("Plugin 校验通过，正在保存当前配置…");
            var snapshot = _marketplaceService.CreatePluginSnapshot(_instance);
            var installMode = _pluginInstallMode();
            var installedEntry = item.IsInstalled
                ? MarketplaceService.FindInstalledPlugin(item, _installedPlugins)
                : null;
            var packageSpec = verification.InstallSpec ?? item.InstallSpec;
            if (item.IsInstalled && !item.IsManaged)
            {
                throw new InvalidOperationException("当前 Plugin 不是 Launcher 安装的，不能从市场更新。请在 DSh 自己的工具中管理它。");
            }

            async Task<string> ExecuteMutationAsync(PluginInstallMode mode)
            {
                var modeText = mode == PluginInstallMode.Fast ? "快速安装" : "兼容性安装";
                var commandText = item.IsInstalled
                    ? $"正在通过官方 DSh CLI 更新 Plugin（{modeText}）…"
                    : $"正在通过官方 DSh CLI 安装 Plugin（{modeText}）…";
                var cliProgress = new Progress<PluginCommandProgress>(update =>
                    progressWindow.SetPackageProgress(update, commandText));
                if (item.IsInstalled)
                {
                    SetMarketplaceMutationText("正在更新 Plugin…");
                    progressWindow.SetIndeterminate(commandText, "等待 CLI");
                    return await _service.UpdatePluginAsync(
                        _instance,
                        installedEntry?.Name ?? verification.PackageName ?? item.PackageName ?? packageSpec,
                        _nodeRuntime(),
                        mode,
                        operationCancellation.Token,
                        cliProgress);
                }

                SetMarketplaceMutationText("正在安装 Plugin…");
                progressWindow.SetIndeterminate(commandText, "等待 CLI");
                return string.IsNullOrWhiteSpace(verification.PackageName)
                    ? await _service.InstallPluginAsync(
                        _instance,
                        packageSpec,
                        _nodeRuntime(),
                        mode,
                        operationCancellation.Token,
                        cliProgress)
                    : await _service.InstallPluginAsync(
                        _instance,
                        packageSpec,
                        _nodeRuntime(),
                        verification.PackageName,
                        mode,
                        operationCancellation.Token,
                        cliProgress);
            }

            string output;
            var dshMarketHotLoaded = false;
            try
            {
                if (useDshMarket)
                {
                    SetMarketplaceMutationText(item.IsInstalled
                        ? "正在通过 dsh-market 更新 Plugin…"
                        : "正在通过 dsh-market 热加载 Plugin…");
                    progressWindow.SetIndeterminate(item.IsInstalled
                        ? "正在通过 dsh-market 更新 Plugin…"
                        : "正在通过 dsh-market 安装并热加载 Plugin…");
                    var result = item.IsInstalled
                        ? await _themeService.UpdatePluginAsync(
                            _instance,
                            installedEntry?.Name ?? verification.PackageName ?? item.PackageName ?? packageSpec,
                            operationCancellation.Token)
                        : await _themeService.InstallPluginAsync(
                            _instance,
                            item.DshMarketUrl!,
                            operationCancellation.Token);
                    if (!result.IsSuccess)
                    {
                        throw new InvalidOperationException(result.Error ?? "dsh-market Plugin 操作失败。");
                    }

                    output = result.Output;
                    dshMarketHotLoaded = result.IsHotLoaded;
                }
                else
                {
                    output = await ExecutePluginInstallWithFallbackAsync(
                        ExecuteMutationAsync,
                        installMode,
                        progressWindow,
                        operationCancellation.Token);
                }

                progressWindow.SetIndeterminate(item.IsInstalled
                    ? "Plugin 更新完成，正在整理结果…"
                    : "Plugin 安装完成，正在整理结果…");
                var activationText = useDshMarket
                    ? dshMarketHotLoaded
                        ? "dsh-market 已完成热加载；请刷新 DSh 页面"
                        : "dsh-market 已完成安装；该 Plugin 需要刷新页面或重启实例"
                    : "实例下次启动时加载";
                StatusText.Text = item.IsInstalled
                    ? $"Plugin 更新完成。{activationText}；备份：{snapshot}"
                    : $"Plugin 安装完成。{activationText}；备份：{snapshot}";
            }
            catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
            {
                var restored = _marketplaceService.RestorePluginSnapshot(_instance, snapshot);
                var cancellationMessage = restored
                    ? "Plugin 操作已取消，已恢复操作前配置。"
                    : "Plugin 操作已取消；没有可恢复的配置备份。";
                MarketplaceStatusText.Text = cancellationMessage;
                progressWindow.Canceled(cancellationMessage);
                return;
            }
            catch (Exception ex)
            {
                progressWindow.SetIndeterminate("Plugin 操作失败，正在回档并打包完整诊断报告…");
                var recovery = await RecoverPluginFailureAsync(
                    snapshot,
                    ex,
                    item.IsInstalled ? "update" : "install",
                    packageSpec);
                progressWindow.SetIndeterminate(recovery.HandoffSucceeded
                    ? "已回档，完整报告已发送给当前 DSh。"
                    : recovery.ReportPath is null
                        ? "已回档，但诊断报告未能生成。"
                        : "已回档，正在使用报告路径交给当前 DSh。 ");
                throw new InvalidOperationException(recovery.Summary, ex);
            }

            MarketplaceStatusText.Text = string.IsNullOrWhiteSpace(output)
                ? "操作完成。"
                : $"操作完成：{Tail(output)}";
            SetMarketplaceMutationText("操作完成，正在刷新实例内容和插件市场…");
            progressWindow.SetIndeterminate("Plugin 操作完成，正在刷新当前实例…");
            await RefreshAsync();
            progressWindow.SetIndeterminate("当前实例已刷新，正在刷新插件市场…");
            await RefreshMarketplaceAsync();
            progressWindow.Complete(item.IsInstalled
                ? "Plugin 更新完成。"
                : useDshMarket ? "Plugin 热加载完成。" : "Plugin 安装完成。");
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            const string cancellationMessage = "Plugin 操作已取消。";
            MarketplaceStatusText.Text = cancellationMessage;
            progressWindow?.Canceled(cancellationMessage);
        }
        catch (Exception ex)
        {
            if (progressWindow is null)
            {
                ShowError(ex);
            }
            else
            {
                MarketplaceStatusText.Text = ex.Message;
                progressWindow.Fail(ex.Message);
            }
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
                var recovery = await RecoverPluginFailureAsync(
                    snapshot,
                    ex,
                    "remove",
                    installedEntry?.Name ?? item.PackageName ?? item.InstallSpec);
                throw new InvalidOperationException(recovery.Summary, ex);
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

    private void MarketplaceSite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MarketplaceService.CommunitySiteZhUrl)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MarketplaceStatusText.Text = $"无法打开中文官网：{ex.Message}";
        }
    }

    private async void MarketplaceThemePreview_Click(object sender, RoutedEventArgs e)
    {
        if (_marketplaceService is null
            || (sender as FrameworkElement)?.DataContext is not MarketplaceItem item
            || !item.IsTheme)
        {
            return;
        }

        var previewWindow = new ThemePreviewWindow(Window.GetWindow(this), item);
        previewWindow.Show();
        try
        {
            var preview = await _marketplaceService.GetThemeReadmePreviewAsync(
                item,
                previewWindow.CancellationToken);
            if (previewWindow.IsVisible)
            {
                previewWindow.SetPreview(preview);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the preview cancels its network request.
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

    private async Task<PluginFailureRecovery> RecoverPluginFailureAsync(
        string snapshot,
        Exception original,
        string operation,
        string packageSpec)
    {
        var rollbackSucceeded = false;
        string rollbackMessage;
        try
        {
            rollbackSucceeded = _marketplaceService?.RestorePluginSnapshot(_instance, snapshot) == true;
            rollbackMessage = rollbackSucceeded
                ? "已恢复操作前的 web profile 配置。"
                : "没有可用的 web profile 备份，未能自动恢复。";
        }
        catch (Exception rollbackError)
        {
            rollbackMessage = $"自动恢复 web profile 失败：{rollbackError.Message}";
        }

        PluginFailureReport? report = null;
        string? reportError = null;
        try
        {
            report = _failureReportService.Create(
                _instance,
                operation,
                packageSpec,
                original,
                rollbackSucceeded,
                rollbackMessage,
                string.IsNullOrWhiteSpace(snapshot) ? null : snapshot);
        }
        catch (Exception ex)
        {
            reportError = ex.Message;
        }

        var handoffSucceeded = false;
        if (report is not null && _handoffPluginFailure is not null)
        {
            try
            {
                handoffSucceeded = await _handoffPluginFailure(
                    _instance,
                    BuildDshFailurePrompt(report, original, rollbackMessage));
            }
            catch (Exception ex)
            {
                reportError = string.IsNullOrWhiteSpace(reportError)
                    ? $"发送给当前 DSh 失败：{ex.Message}"
                    : $"{reportError}；发送给当前 DSh 失败：{ex.Message}";
            }
        }

        var summary = $"{original.Message}\n{rollbackMessage}";
        if (report is not null)
        {
            summary += $"\n完整诊断报告：{report.ArchivePath}";
            summary += handoffSucceeded
                ? "\n已把报告路径和错误上下文发送给当前 DSh，请让它读取报告后继续排查和安装。"
                : "\n当前 DSh 未能自动接收报告，请打开该实例后把报告路径交给它。";
        }
        else if (!string.IsNullOrWhiteSpace(reportError))
        {
            summary += $"\n诊断报告生成失败：{reportError}";
        }

        return new PluginFailureRecovery(summary, report?.ArchivePath, handoffSucceeded);
    }

    private async void DshMarketHotReloadCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_controlLoaded || _agentOnly || _versionSettingsService is null)
        {
            return;
        }

        _useDshMarketHotReload = DshMarketHotReloadCheckBox.IsChecked == true;
        try
        {
            var settings = _versionSettingsService.Read(_instance);
            settings.UseDshMarketHotReload = _useDshMarketHotReload;
            _versionSettingsService.Save(_instance, settings);
            _themeState = _useDshMarketHotReload
                ? await _themeService.ReadAsync(_instance)
                : DshMarketThemeState.Unavailable("当前实例已关闭 dsh-market 热加载。 ");
            MarketplaceStatusText.Text = _useDshMarketHotReload
                ? "已开启 dsh-market 热加载；应用主题前会创建自动存档。"
                : "已关闭 dsh-market 热加载；Plugin 仍可正常安装和管理。";
            RenderMarketplaceItems();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private static string BuildDshFailurePrompt(
        PluginFailureReport report,
        Exception original,
        string rollbackMessage)
    {
        return $"""
            Launcher 的 Plugin {report.Operation} 失败，Launcher 已先回档。

            实例：{report.InstanceName}
            Plugin：{report.PackageSpec}
            回档结果：{rollbackMessage}
            完整诊断报告压缩包：{report.ArchivePath}
            错误摘要：{Tail(original.ToString())}

            请在当前实例中读取并检查这个压缩包，定位安装失败的根因，修复必要配置后继续完成这次 Plugin 安装。不要删除 DSH_HOME、会话或工作区，不要重新初始化实例。报告按用户要求保留原始配置和凭据，不要把凭据复制到回复或转发到其它位置。
            """;
    }

    private sealed record PluginFailureRecovery(
        string Summary,
        string? ReportPath,
        bool HandoffSucceeded);

    private void MarketplaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarketplaceList.SelectedItem is MarketplaceItem item)
        {
            MarketplaceStatusText.Text = $"{item.VerificationText}：{item.VerificationMessage}";
        }
    }

    private void EnsureMarketplaceMutationAllowed(bool allowRunning = false)
    {
        if (_instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            throw new InvalidOperationException("当前实例连接的是外部 DSh 服务，Launcher 不会修改它的 Plugin。请先使用 Launcher 管理的实例。");
        }

        if (!allowRunning && _instance.RuntimeStatus == InstanceRuntimeStatus.Running)
        {
            throw new InvalidOperationException("请先停止实例，再卸载 Plugin。");
        }
    }

    private static string Tail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 600 ? trimmed : trimmed[^600..];
    }

    private async Task<string> ExecutePluginInstallWithFallbackAsync(
        Func<PluginInstallMode, Task<string>> execute,
        PluginInstallMode initialMode,
        PluginProgressWindow progressWindow,
        CancellationToken cancellationToken)
    {
        if (initialMode == PluginInstallMode.Fast)
        {
            try
            {
                return await execute(PluginInstallMode.Fast);
            }
            catch (Exception fastError) when (
                fastError is not OperationCanceledException
                && !cancellationToken.IsCancellationRequested)
            {
                progressWindow.SetIndeterminate("快速安装失败，正在自动尝试兼容性安装…");
            }
        }

        try
        {
            return await execute(PluginInstallMode.Compatibility);
        }
        catch (Exception compatibilityError) when (
            compatibilityError is not OperationCanceledException
            && !cancellationToken.IsCancellationRequested
            && _instance.RuntimeStatus == InstanceRuntimeStatus.Running)
        {
            progressWindow.SetIndeterminate("兼容性热安装仍然失败，等待确认是否停止实例后重试…");
            if (System.Windows.MessageBox.Show(
                    progressWindow,
                    $"热安装未成功。是否由 Launcher 停止当前实例，然后再使用兼容性安装重试？\n\n{Tail(compatibilityError.Message)}",
                    "热安装失败",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                throw;
            }

            if (_stopInstanceForPluginRetry is null)
            {
                throw new InvalidOperationException(
                    "当前页面不能自动停止实例。请手动停止实例后重新安装。",
                    compatibilityError);
            }

            progressWindow.SetIndeterminate("正在停止当前实例…");
            if (!await _stopInstanceForPluginRetry(_instance, cancellationToken))
            {
                throw new InvalidOperationException(
                    "实例未能停止，已取消关闭后安装。请检查实例状态后重试。",
                    compatibilityError);
            }

            _instance = _instance with
            {
                RuntimeStatus = InstanceRuntimeStatus.Stopped,
                RuntimeOwnership = InstanceRuntimeOwnership.None,
                ProcessId = null,
                Port = null,
                WebUrl = null,
                LastError = null
            };
            progressWindow.SetIndeterminate("实例已停止，正在使用兼容性安装重试…");
            return await execute(PluginInstallMode.Compatibility);
        }
    }

    private async void InstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        var source = TextPromptWindow.Show(Window.GetWindow(this), "安装 Plugin", "输入 npm 包名、Git 仓库或本地路径：");
        if (string.IsNullOrWhiteSpace(source)) return;
        using var operationCancellation = new CancellationTokenSource();
        var progressWindow = new PluginProgressWindow(
            Window.GetWindow(this),
            operationCancellation,
            "安装 Plugin",
            "正在准备 Plugin 安装…");
        progressWindow.Show();
        progressWindow.SetIndeterminate("正在准备 Plugin 安装…");
        var snapshot = string.Empty;
        var mutationStarted = false;
        try
        {
            EnsureMarketplaceMutationAllowed(allowRunning: true);
            // 安装前校验：普通 npm 包装进去只会变成“已安装（默认禁用）”，
            // 点“启用”还会把非插件写进 bundles，因此在改动 profile 前就拦住。
            progressWindow.SetIndeterminate("正在校验安装目标是否为 DSH 插件…");
            if (_marketplaceService is not null)
            {
                var verdict = await _marketplaceService.VerifyManualInstallAsync(
                    source,
                    operationCancellation.Token,
                    _instance);
                if (verdict.Status == MarketplaceVerificationStatus.Rejected)
                {
                    var proceed = System.Windows.MessageBox.Show(
                        Window.GetWindow(this),
                        $"这个目标不是可用的 DSH 插件：\n\n{verdict.Message}\n\n继续安装的话，它只会出现在「已安装（默认禁用）」里，DSh 不会加载它。仍要继续吗？",
                        "不是 DSH 插件",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes;
                    if (!proceed)
                    {
                        throw new InvalidOperationException(verdict.Message);
                    }
                }
                else if (verdict.Status == MarketplaceVerificationStatus.Incompatible)
                {
                    var proceed = System.Windows.MessageBox.Show(
                        Window.GetWindow(this),
                        $"该插件与当前实例的 DSh 运行时不兼容：\n\n{verdict.Message}\n\n安装后实例可能无法启动（可用安全模式或「逐插件定位」恢复）。仍要继续吗？",
                        "插件依赖不兼容",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes;
                    if (!proceed)
                    {
                        throw new InvalidOperationException(verdict.Message);
                    }
                }
                else if (verdict.Status == MarketplaceVerificationStatus.Unverified
                    && !string.IsNullOrWhiteSpace(verdict.Message))
                {
                    progressWindow.SetIndeterminate($"未能确认是否为 DSH 插件：{verdict.Message}（继续安装…）");
                }
            }

            snapshot = _marketplaceService?.CreatePluginSnapshot(_instance) ?? string.Empty;
            mutationStarted = true;
            var installMode = _pluginInstallMode();
            async Task<string> ExecuteMutationAsync(PluginInstallMode mode)
            {
                var installModeText = mode == PluginInstallMode.Fast ? "快速安装" : "兼容性安装";
                var commandText = $"正在通过官方 DSh CLI 安装 Plugin（{installModeText}）…";
                progressWindow.SetIndeterminate(commandText, "等待 CLI");
                var cliProgress = new Progress<PluginCommandProgress>(update =>
                    progressWindow.SetPackageProgress(update, commandText));
                return await _service.InstallPluginAsync(
                    _instance,
                    source,
                    _nodeRuntime(),
                    mode,
                    operationCancellation.Token,
                    cliProgress);
            }

            var output = await ExecutePluginInstallWithFallbackAsync(
                ExecuteMutationAsync,
                installMode,
                progressWindow,
                operationCancellation.Token);
            progressWindow.SetIndeterminate("Plugin 安装完成，正在整理结果…");
            var activationText = _instance.RuntimeStatus == InstanceRuntimeStatus.Running
                ? "已热安装；请刷新 DSh 页面，包含 host 改动时仍需重启实例。"
                : "实例下次启动时加载。";
            StatusText.Text = string.IsNullOrWhiteSpace(output)
                ? $"Plugin 安装完成。{activationText}"
                : $"Plugin 安装完成。{activationText} {output}";
            progressWindow.SetIndeterminate("Plugin 安装完成，正在刷新当前实例…");
            await RefreshAsync();
            progressWindow.Complete("Plugin 安装完成。");
        }
        catch (Exception ex)
        {
            if (!mutationStarted)
            {
                StatusText.Text = ex.Message;
                progressWindow.Fail(ex.Message);
                return;
            }

            progressWindow.SetIndeterminate("Plugin 安装失败，正在回档并打包完整诊断报告…");
            var recovery = await RecoverPluginFailureAsync(snapshot, ex, "install", source);
            progressWindow.SetIndeterminate(recovery.HandoffSucceeded
                ? "已回档，完整报告已发送给当前 DSh。"
                : recovery.ReportPath is null
                    ? "已回档，但诊断报告未能生成。"
                    : "已回档，正在使用报告路径交给当前 DSh。 ");
            StatusText.Text = recovery.Summary;
            progressWindow.Fail(recovery.Summary);
        }
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
            // 更新前兼容性预检：最新版本声明的核心 peerDependencies 与实例运行时不符时先警告。
            if (_marketplaceService is not null)
            {
                var verdict = await _marketplaceService.CheckPluginCompatibilityAsync(entry.Name, _instance);
                if (verdict.Status == MarketplaceVerificationStatus.Incompatible
                    && System.Windows.MessageBox.Show(
                        Window.GetWindow(this),
                        $"“{entry.Name}”的最新版本与当前实例的 DSh 运行时不兼容：\n\n{verdict.Message}\n\n更新后实例可能无法启动。仍要继续吗？",
                        "插件依赖不兼容",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    StatusText.Text = "已取消更新：插件依赖不兼容。";
                    return;
                }
            }

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
            SelectedMeta.Text = string.Empty;
            SelectedDescription.Text = string.Empty;
            SelectedDescription.ToolTip = null;
            SelectedLocationLink.Content = string.Empty;
            SelectedLocationLink.Tag = null;
            SelectedLocationLink.ToolTip = null;
            return;
        }

        SelectedName.Text = entry.Name;
        SelectedMeta.Text = $"类型：{entry.Kind}　状态：{(entry.Enabled ? "已启用" : "已禁用")}";
        SelectedDescription.Text = string.IsNullOrWhiteSpace(entry.Description) ? "没有描述" : entry.Description;
        SelectedDescription.ToolTip = string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description;
        SelectedLocationLink.Content = PathDisplay.Tail(entry.Location, 40);
        SelectedLocationLink.Tag = entry.Location;
        SelectedLocationLink.ToolTip = CreatePluginDetailCard(entry);
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
        if (entry.Kind == ExtensionKind.Plugin
            && _pluginUpdateInfos.TryGetValue(entry.Name, out var update)
            && update.HasUpdate)
        {
            HintText.Text += $" 可更新：{update.Current ?? "?"} → {update.Latest}（点“更新”或“全部更新”）。";
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_isMarketplaceMutating)
        {
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        StatusText.Text = "正在检查插件更新（registry latest）…";
        try
        {
            var updates = await _service.CheckPluginUpdatesAsync(_instance);
            _pluginUpdateInfos = updates.ToDictionary(info => info.Name, StringComparer.OrdinalIgnoreCase);
            var pending = updates.Where(info => info.HasUpdate).ToArray();
            UpdateAllButton.Visibility = pending.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = pending.Length == 0
                ? $"已检查 {updates.Count} 个插件：均为最新版本。"
                : $"发现 {pending.Length} 个可更新插件：" +
                  string.Join("、", pending.Take(6).Select(info => $"{info.Name} {info.Current}→{info.Latest}")) +
                  (pending.Length > 6 ? " 等" : string.Empty);
            UpdateSelection();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isMarketplaceMutating)
        {
            return;
        }

        var pending = _pluginUpdateInfos.Values.Where(info => info.HasUpdate).Select(info => info.Name).ToArray();
        if (pending.Length == 0)
        {
            StatusText.Text = "没有可更新的插件（先点“检查更新”）。";
            return;
        }

        if (_instance.RuntimeStatus == InstanceRuntimeStatus.Running
            && System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                $"当前实例正在运行。批量更新 {pending.Length} 个插件可能不会立即生效（需重启实例）。是否继续？",
                "批量更新插件",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        UpdateAllButton.IsEnabled = false;

        // 兼容性预检：不兼容的直接跳过（批量更新不适合逐个弹窗确认）。检查失败时保守放行。
        var skipped = new List<string>();
        if (_marketplaceService is not null && pending.Length > 0)
        {
            try
            {
                var compatible = new List<string>();
                foreach (var name in pending)
                {
                    var verdict = await _marketplaceService.CheckPluginCompatibilityAsync(name, _instance);
                    if (verdict.Status == MarketplaceVerificationStatus.Incompatible)
                    {
                        skipped.Add(name);
                    }
                    else
                    {
                        compatible.Add(name);
                    }
                }

                if (skipped.Count > 0)
                {
                    pending = compatible.ToArray();
                }

                if (pending.Length == 0)
                {
                    StatusText.Text = $"没有可安全更新的插件：{string.Join("、", skipped)} 与当前 DSh 运行时不兼容，已跳过。";
                    UpdateAllButton.IsEnabled = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                skipped.Clear();
                StatusText.Text = $"兼容性检查失败（{ex.Message}），继续批量更新。";
            }
        }

        var progress = new Progress<PluginBatchProgress>(item =>
        {
            StatusText.Text = $"正在更新（{item.Index}/{item.Total}）：{item.Name}" +
                              (item.Error is null ? string.Empty : $"（失败：{item.Error}）");
        });
        try
        {
            var summary = await _service.UpdatePluginsAsync(
                _instance,
                pending,
                _nodeRuntime(),
                _pluginInstallMode(),
                progress);
            StatusText.Text = skipped.Count > 0
                ? $"{summary}（已跳过不兼容：{string.Join("、", skipped)}）"
                : summary;
            await RefreshAsync();
            _pluginUpdateInfos = new Dictionary<string, PluginUpdateInfo>(StringComparer.OrdinalIgnoreCase);
            UpdateAllButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"批量更新失败：{ex.Message}";
        }
        finally
        {
            UpdateAllButton.IsEnabled = true;
        }
    }

    private void PluginMatrix_Click(object sender, RoutedEventArgs e) => _openPluginMatrix?.Invoke();

    private async void Doctor_Click(object sender, RoutedEventArgs e)
    {
        DoctorButton.IsEnabled = false;
        StatusText.Text = "正在做依赖自检…";
        try
        {
            var findings = await _service.RunDoctorAsync(_instance);
            if (findings.Count == 0)
            {
                StatusText.Text = "依赖自检通过：未发现核心包混入或 bundle 缺失。";
                System.Windows.MessageBox.Show(Window.GetWindow(this),
                    "依赖自检通过：未发现核心包混入或 bundle 缺失。", "依赖自检",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var errors = findings.Count(finding => finding.Level == "error");
            StatusText.Text = $"依赖自检：{findings.Count} 项发现（{errors} 项错误）。";
            System.Windows.MessageBox.Show(Window.GetWindow(this),
                string.Join("\n\n", findings.Select(finding =>
                    $"[{(finding.Level == "error" ? "错误" : "警告")}] {finding.Message}")),
                "依赖自检结果",
                MessageBoxButton.OK,
                errors > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"依赖自检失败：{ex.Message}";
        }
        finally
        {
            DoctorButton.IsEnabled = true;
        }
    }

    private void ScheduleUiStateSave()
    {
        if (!_controlLoaded)
        {
            return;
        }

        _uiStateSaveTimer.Stop();
        _uiStateSaveTimer.Start();
    }

    private void SaveMarketplaceUiState()
    {
        if (_agentOnly)
        {
            return;
        }

        try
        {
            SaveScrollOffset(MarketplaceList, _marketplaceScrollOffsets, _activeMarketplaceCategoryKey);
            _uiStateStore.SaveMarketplace(_instance.Id, new MarketplaceUiState
            {
                Search = MarketplaceSearchBox.Text,
                CategoryKey = GetSelectedCategoryKey(),
                SourceKey = (MarketplaceSourceBox.SelectedItem as ComboBoxItem)?.Tag as string,
                SortKey = (MarketplaceSortBox.SelectedItem as ComboBoxItem)?.Tag as string,
                ScrollOffsets = new Dictionary<string, double>(_marketplaceScrollOffsets, StringComparer.Ordinal)
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // UI 状态保存失败不影响使用。
        }
    }

    private void RestoreMarketplaceUiState()
    {
        if (_agentOnly)
        {
            return;
        }

        var state = _uiStateStore.GetMarketplace(_instance.Id);
        if (state is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(state.Search))
        {
            MarketplaceSearchBox.Text = state.Search;
        }

        SelectListBoxTag(MarketplaceCategoryList, state.CategoryKey);
        SelectComboTag(MarketplaceSourceBox, state.SourceKey);
        SelectComboTag(MarketplaceSortBox, state.SortKey);
        foreach (var pair in state.ScrollOffsets)
        {
            _marketplaceScrollOffsets[pair.Key] = pair.Value;
        }
    }

    private static void SelectListBoxTag(WpfListBox list, string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return;
        }

        foreach (var item in list.Items)
        {
            if (item is ListBoxItem listBoxItem
                && string.Equals(listBoxItem.Tag as string, tag, StringComparison.Ordinal))
            {
                list.SelectedItem = listBoxItem;
                return;
            }
        }
    }

    private static void SelectComboTag(System.Windows.Controls.ComboBox combo, string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return;
        }

        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem comboItem
                && string.Equals(comboItem.Tag as string, tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = comboItem;
                return;
            }
        }
    }

    private void MarketplaceTitle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MarketplaceItem item)
        {
            return;
        }

        var repositoryUrl = MarketplaceService.GetGitHubRepositoryUrl(item);
        if (repositoryUrl is null)
        {
            MarketplaceStatusText.Text = "这个目录条目没有提供 GitHub 仓库地址。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(repositoryUrl) { UseShellExecute = true });
            e.Handled = true;
        }
        catch (Exception ex)
        {
            MarketplaceStatusText.Text = $"无法打开 GitHub：{ex.Message}";
        }
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, "扩展操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
