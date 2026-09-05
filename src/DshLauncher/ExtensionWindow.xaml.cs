using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly ThemeIntegrationController _themeIntegration = new();
    private readonly VersionSettingsService? _versionSettingsService;
    private readonly VersionSnapshotService? _versionSnapshotService;
    private IReadOnlyList<SkillMarketItem> _skillMarketSnapshot = Array.Empty<SkillMarketItem>();
    private bool _isSkillMarketLoading;
    private bool _isSkillMarketMutating;
    private int _lastSkillProgressItemCount = -1;
    private DateTimeOffset _lastSkillProgressRenderAt = DateTimeOffset.MinValue;
    private IReadOnlyList<MarketplaceItem> _marketplaceSnapshot = Array.Empty<MarketplaceItem>();
    private IReadOnlyList<ExtensionEntry> _installedPlugins = Array.Empty<ExtensionEntry>();
    private IReadOnlyList<ExtensionEntry> _installedSkills = Array.Empty<ExtensionEntry>();
    private bool _marketplaceCanMutate;
    private bool _isMarketplaceLoading;
    private bool _isMarketplaceMutating;
    private bool _controlLoaded;
    private CancellationTokenSource? _marketplaceCancellation;
    private CancellationTokenSource? _searchDebounceCancellation;
    private CancellationTokenSource? _skillSearchDebounceCancellation;
    private CancellationTokenSource? _skillMarketCancellation;
    private Window? _agentLayoutOwner;
    private readonly Dictionary<string, double> _marketplaceScrollOffsets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _skillMarketScrollOffsets = new(StringComparer.Ordinal);
    private string _activeMarketplaceCategoryKey = string.Empty;
    private string _activeSkillMarketCategoryKey = string.Empty;
    private bool _profileSelectionReady;
    private bool _isUnloaded;
    private bool _chatWindowEventsAttached;
    private bool _themeIntegrationDisposed;

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
        VersionSnapshotService? versionSnapshotService = null)
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
        InitializeComponent();
        _themeIntegration.SetUseDshMarketHotReload(
            _versionSettingsService?.Read(instance).UseDshMarketHotReload ?? true);
        DshMarketHotReloadCheckBox.IsChecked = _themeIntegration.UseDshMarketHotReload;
        MarketplaceCategoryList.Visibility = _agentOnly ? Visibility.Collapsed : Visibility.Visible;
        SkillMarketCategoryList.Visibility = _agentOnly ? Visibility.Visible : Visibility.Collapsed;
        CurrentInstanceNameText.Text = instance.Name;
        CurrentInstanceDetailsText.Text = $"{instance.DshVersionText}\n{instance.KindText} · {instance.RootPath}\nDSH_HOME：{instance.DshHome}";

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
            DshMarketHotReloadCheckBox.Visibility = Visibility.Collapsed;
            ChatThemeSyncCheckBox.Visibility = Visibility.Collapsed;
            ChatThemeSyncButton.Visibility = Visibility.Collapsed;
            ChatThemeCapabilityText.Visibility = Visibility.Collapsed;
            EnableButton.Visibility = Visibility.Collapsed;
            DisableButton.Visibility = Visibility.Collapsed;
            UpdateButton.Visibility = Visibility.Collapsed;
            HintText.Text = "修改前请停止实例。Skill 会被 DSh 的 filesystem provider 发现，Agent Preset 和 Workflow 会在下次启动时生效。";
        }
        else
        {
            SetupProfileSelector();
            ImportSkillButton.Visibility = Visibility.Collapsed;
            ImportPresetButton.Visibility = Visibility.Collapsed;
        }
    }

    private void SetupProfileSelector()
    {
        var profiles = _service.ListProfiles(_instance).ToList();
        var configured = _service.GetActiveProfileName(_instance);
        var selected = profiles.FirstOrDefault(profile =>
                string.Equals(profile, configured, StringComparison.OrdinalIgnoreCase))
            ?? profiles.First();
        ProfileSelectorBox.ItemsSource = profiles;
        ProfileSelectorBox.SelectedItem = selected;
        ProfileSelectorPanel.Visibility = Visibility.Visible;
        if (!string.Equals(selected, configured, StringComparison.OrdinalIgnoreCase)
            && _versionSettingsService is not null)
        {
            var settings = _versionSettingsService.Read(_instance);
            settings.ActiveProfileName = selected;
            _versionSettingsService.Save(_instance, settings);
        }

        _profileSelectionReady = true;
    }

    private async void ProfileSelectorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_profileSelectionReady
            || _agentOnly
            || ProfileSelectorBox.SelectedItem is not string profileName)
        {
            return;
        }

        try
        {
            var profileGeneration = _themeIntegration.BeginProfileSelection(profileName);
            UpdateChatThemeControls();
            if (_versionSettingsService is not null)
            {
                var settings = _versionSettingsService.Read(_instance);
                settings.ActiveProfileName = profileName;
                _versionSettingsService.Save(_instance, settings);
            }

            StatusText.Text = $"已切换 Plugin 管理 Profile：{profileName}。";
            await RefreshAsync();
            if (_themeIntegration.IsCurrentProfile(profileGeneration)
                && string.Equals(profileName, "web", StringComparison.OrdinalIgnoreCase))
            {
                await _themeIntegration.ProbeChatCapabilityAsync(
                    _instance,
                    profileGeneration: profileGeneration);
            }

            if (_themeIntegration.IsCurrentProfile(profileGeneration))
            {
                if (_marketplaceSnapshot.Count > 0)
                {
                    RenderMarketplaceItems();
                }

                UpdateChatThemeControls();
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private string GetSelectedProfileName() =>
        ProfileSelectorBox.SelectedItem as string ?? _service.GetActiveProfileName(_instance);

    private void SetupSkillMarket()
    {
        _ = SetupSkillMarketAsync();
    }

    private async Task SetupSkillMarketAsync()
    {
        var cached = await Task.Run(() => _skillMarketService!.ReadCached());
        if (_isUnloaded)
        {
            return;
        }

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
        _skillMarketCancellation?.Cancel();
        _skillMarketCancellation?.Dispose();
        _skillMarketCancellation = new CancellationTokenSource();
        _lastSkillProgressItemCount = -1;
        _lastSkillProgressRenderAt = DateTimeOffset.MinValue;
        SkillMarketRefreshButton.IsEnabled = false;
        SkillMarketStatusText.Text = "正在从 GitHub 搜索并校验 SKILL.md…";
        try
        {
            var progress = new Progress<SkillMarketRefreshProgress>(state =>
            {
                if (!_isSkillMarketLoading || _isUnloaded)
                {
                    return;
                }

                _skillMarketSnapshot = state.Items;
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

                SkillMarketStatusText.Text = state.Total == 0
                    ? $"{state.Stage}…"
                    : $"{state.Stage}：{state.Completed} / {state.Total}";
            });
            var items = await _skillMarketService.SearchAsync(
                _skillMarketCancellation.Token,
                progress);
            if (_isUnloaded)
            {
                return;
            }

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
        catch (OperationCanceledException) when (_skillMarketCancellation?.IsCancellationRequested == true)
        {
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
        _isUnloaded = false;
        _controlLoaded = true;
        if (!_chatWindowEventsAttached)
        {
            ChatWindow.OpenChatWindowsChanged += ChatWindow_OpenChatWindowsChanged;
            _chatWindowEventsAttached = true;
        }
        UpdateChatThemeControls();
        _activeMarketplaceCategoryKey = GetSelectedCategoryKey();
        _activeSkillMarketCategoryKey = GetSelectedSkillCategoryKey();
        AttachAgentLayoutOwner();
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

    private void Window_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _controlLoaded = false;
        if (_chatWindowEventsAttached)
        {
            ChatWindow.OpenChatWindowsChanged -= ChatWindow_OpenChatWindowsChanged;
            _chatWindowEventsAttached = false;
        }
        _skillMarketCancellation?.Cancel();
        _skillMarketCancellation?.Dispose();
        _skillMarketCancellation = null;
        _marketplaceCancellation?.Cancel();
        _marketplaceCancellation?.Dispose();
        _marketplaceCancellation = null;
        _skillSearchDebounceCancellation?.Cancel();
        if (_agentLayoutOwner is not null)
        {
            _agentLayoutOwner.SizeChanged -= AgentLayoutOwner_SizeChanged;
            _agentLayoutOwner = null;
        }

        if (!_themeIntegrationDisposed)
        {
            _themeIntegration.Dispose();
            _themeIntegrationDisposed = true;
        }
    }

    private void AttachAgentLayoutOwner()
    {
        if (!_agentOnly)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        if (!ReferenceEquals(_agentLayoutOwner, owner))
        {
            if (_agentLayoutOwner is not null)
            {
                _agentLayoutOwner.SizeChanged -= AgentLayoutOwner_SizeChanged;
            }

            _agentLayoutOwner = owner;
            if (_agentLayoutOwner is not null)
            {
                _agentLayoutOwner.SizeChanged += AgentLayoutOwner_SizeChanged;
            }
        }

        UpdateAgentPanelHeights();
    }

    private void AgentLayoutOwner_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateAgentPanelHeights();

    private void UpdateAgentPanelHeights()
    {
        if (!_agentOnly)
        {
            return;
        }

        var windowHeight = _agentLayoutOwner?.ActualHeight > 0
            ? _agentLayoutOwner.ActualHeight
            : SystemParameters.WorkArea.Height;
        var rightHeight = Math.Clamp(windowHeight - 170, 500, 760);
        var leftHeight = Math.Clamp(rightHeight - 36, 464, 700);
        InstalledPanel.Height = leftHeight;
        SkillMarketPanel.Height = rightHeight;
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
            else
            {
                _installedPlugins = entries
                    .Where(entry => entry.Kind == ExtensionKind.Plugin)
                    .ToArray();
                UpdateMarketplaceUpdateAllButton();
                if (_marketplaceSnapshot.Count > 0)
                {
                    RenderMarketplaceItems();
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

    private void ChatWindow_OpenChatWindowsChanged(object? sender, EventArgs e)
    {
        if (!_isUnloaded && !_agentOnly)
        {
            UpdateChatThemeControls();
        }
    }

    private void UpdateChatThemeControls()
    {
        if (_agentOnly)
        {
            return;
        }

        var capability = _themeIntegration.ChatCapability;
        var isWebProfile = string.Equals(
            GetSelectedProfileName(),
            "web",
            StringComparison.OrdinalIgnoreCase);
        var hasOpenChat = isWebProfile
            && ChatWindow.TryGetOpenChat(_instance.WebUrl, out _);
        var reason = capability.Status switch
        {
            _ when !isWebProfile => "Chat 主题联动只作用于 web profile，当前 Profile 不支持。 ",
            ThemeCapabilityStatus.Supported when hasOpenChat => "已探测到上游 ui-theme.preference，Chat 主题联动可用。 ",
            ThemeCapabilityStatus.Supported => "已探测到上游 ui-theme.preference，但当前没有打开的 Chat 窗口。 ",
            _ => capability.Reason
        };
        var canSync = isWebProfile && capability.IsSupported && hasOpenChat;
        ChatThemeSyncCheckBox.IsEnabled = canSync;
        ChatThemeSyncButton.IsEnabled = canSync;
        ChatThemeSyncCheckBox.ToolTip = reason;
        ChatThemeSyncButton.ToolTip = reason;
        ChatThemeCapabilityText.Text = $"Chat 主题联动：{reason}";
    }

    private async void MarketplaceUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_marketplaceService is null || _isMarketplaceMutating)
        {
            return;
        }

        var updates = SelectAvailableMarketplaceUpdates(_marketplaceSnapshot, _installedPlugins);
        if (updates.Count == 0)
        {
            MarketplaceStatusText.Text = "当前实例没有可批量更新的 Plugin。";
            UpdateMarketplaceUpdateAllButton();
            return;
        }

        try
        {
            EnsureMarketplaceMutationAllowed();
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return;
        }

        if (System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                $"将串行更新当前 Profile 中的 {updates.Count} 个 Plugin。每个 Plugin 更新前都会单独保存快照，失败项会自动回档并继续处理下一项。",
                "确认全部更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var progressWindow = new PluginProgressWindow(
            Window.GetWindow(this),
            cancellation,
            "批量更新 Plugin",
            $"准备更新 {updates.Count} 个 Plugin…");
        progressWindow.Show();
        BeginMarketplaceMutation($"正在批量更新 {updates.Count} 个 Plugin…");

        var succeeded = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();
        try
        {
            for (var index = 0; index < updates.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var item = updates[index];
                var installedEntry = MarketplaceService.FindInstalledPlugin(item, _installedPlugins);
                if (installedEntry is null || !installedEntry.Managed)
                {
                    skipped.Add($"{item.Name}：已不在当前 Profile 中");
                    continue;
                }

                var ordinal = index + 1;
                var itemPrefix = $"[{ordinal}/{updates.Count}] {item.Name}";
                progressWindow.SetProgress(
                    index * 100d / updates.Count,
                    $"{itemPrefix}：正在重新校验…");
                SetMarketplaceMutationText($"{itemPrefix}：正在重新校验…");

                MarketplaceVerificationResult verification;
                try
                {
                    verification = await _marketplaceService.VerifyAsync(
                        item,
                        _instance.DetectedVersion,
                        cancellation.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    skipped.Add($"{item.Name}：校验失败，{Tail(ex.Message)}");
                    continue;
                }

                if (verification.Status == MarketplaceVerificationStatus.Rejected)
                {
                    skipped.Add($"{item.Name}：{verification.Message}");
                    continue;
                }

                if (verification.CompatibilityStatus == MarketplaceCompatibilityStatus.Incompatible)
                {
                    skipped.Add($"{item.Name}：与当前 DSh {_instance.DetectedVersion ?? "未知版本"} 不兼容");
                    continue;
                }

                var packageName = installedEntry.Name;
                var snapshot = string.Empty;
                try
                {
                    progressWindow.SetIndeterminate($"{itemPrefix}：正在保存当前配置…");
                    snapshot = _marketplaceService.CreatePluginSnapshot(
                        _instance,
                        GetSelectedProfileName());
                    var mode = _pluginInstallMode();
                    async Task<string> ExecuteUpdateAsync(PluginInstallMode selectedMode)
                    {
                        var modeText = selectedMode == PluginInstallMode.Fast ? "快速安装" : "兼容性安装";
                        var message = $"{itemPrefix}：正在通过官方 DSh CLI 更新（{modeText}）…";
                        SetMarketplaceMutationText(message);
                        var cliProgress = new Progress<PluginCommandProgress>(update =>
                            progressWindow.SetPackageProgress(update, message));
                        return await _service.UpdatePluginAsync(
                            _instance,
                            packageName,
                            _nodeRuntime(),
                            selectedMode,
                            cancellation.Token,
                            cliProgress);
                    }

                    _ = await ExecutePluginInstallWithFallbackAsync(
                        ExecuteUpdateAsync,
                        mode,
                        progressWindow,
                        cancellation.Token);
                    succeeded.Add(item.Name);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    if (!string.IsNullOrWhiteSpace(snapshot))
                    {
                        _marketplaceService.RestorePluginSnapshot(
                            _instance,
                            snapshot,
                            GetSelectedProfileName());
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    var detail = ex.Message;
                    if (!string.IsNullOrWhiteSpace(snapshot))
                    {
                        var recovery = await RecoverPluginFailureAsync(
                            snapshot,
                            ex,
                            "batch-update",
                            packageName);
                        detail = recovery.Summary;
                    }

                    failed.Add($"{item.Name}：{Tail(detail)}");
                }
                finally
                {
                    progressWindow.SetProgress(
                        ordinal * 100d / updates.Count,
                        $"已处理 {ordinal} / {updates.Count} 个 Plugin");
                }
            }

            await RefreshAsync();
            var summary = $"批量更新完成：成功 {succeeded.Count}，跳过 {skipped.Count}，失败 {failed.Count}。";
            var details = string.Join(
                Environment.NewLine,
                new[]
                {
                    succeeded.Count == 0 ? null : $"成功：{string.Join("、", succeeded)}",
                    skipped.Count == 0 ? null : $"跳过：{string.Join(Environment.NewLine + "  ", skipped)}",
                    failed.Count == 0 ? null : $"失败：{string.Join(Environment.NewLine + "  ", failed)}"
                }.Where(static value => value is not null));
            StatusText.Text = summary;
            MarketplaceStatusText.Text = summary;
            progressWindow.Complete(summary, details);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            const string message = "批量更新已取消；当前正在处理的 Plugin 已恢复操作前配置。";
            StatusText.Text = message;
            MarketplaceStatusText.Text = message;
            progressWindow.Canceled(message);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MarketplaceStatusText.Text = ex.Message;
            progressWindow.Fail(ex.Message);
        }
        finally
        {
            EndMarketplaceMutation();
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
        UpdateMarketplaceUpdateAllButton();
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
            UpdateMarketplaceUpdateAllButton();
        }
    }

    private async Task SetMarketplaceSnapshotAsync(
        MarketplaceSearchResult result,
        bool fromCache,
        CancellationToken cancellationToken = default)
    {
        _marketplaceSnapshot = result.Items;
        var selectedProfileName = GetSelectedProfileName();
        var isWebProfile = string.Equals(selectedProfileName, "web", StringComparison.OrdinalIgnoreCase);
        var useThemeHotReload = _themeIntegration.UseDshMarketHotReload && isWebProfile;
        var profileGeneration = _themeIntegration.ProfileGeneration;
        var installed = await Task.Run(
            () => _service.ListAsync(_instance, cancellationToken),
            cancellationToken);
        if (useThemeHotReload)
        {
            await _themeIntegration.ReadMarketAsync(_instance, cancellationToken);
        }
        else
        {
            _themeIntegration.MarkMarketUnavailable("dsh-market 主题热加载只作用于 web profile。 ");
        }

        if (isWebProfile)
        {
            await _themeIntegration.ProbeChatCapabilityAsync(
                _instance,
                cancellationToken,
                profileGeneration);
        }
        else
        {
            _themeIntegration.SetChatCapability(
                ThemeCapabilityProbeResult.Unsupported(
                    "Chat 主题联动只作用于 web profile，当前 Profile 不支持。 "),
                profileGeneration);
        }
        _installedPlugins = installed
            .Where(entry => entry.Kind == ExtensionKind.Plugin)
            .ToArray();
        _marketplaceCanMutate = !_isMarketplaceMutating
            && _instance.RuntimeOwnership != InstanceRuntimeOwnership.Attached
            && _instance.RuntimeStatus != InstanceRuntimeStatus.Running;
        UpdateMarketplaceUpdateAllButton();
        RenderMarketplaceItems();
        UpdateChatThemeControls();
        MarketplaceSummaryText.Text = $"找到 {_marketplaceSnapshot.Count} 个候选插件 · 已检查 {result.SourcesChecked} 个来源"
            + (fromCache ? " · 本地缓存" : string.Empty);
        if (fromCache)
        {
            MarketplaceStatusText.Text = result.Warnings.FirstOrDefault()
                ?? "当前显示本地缓存；需要在线更新时请点击“刷新目录”。";
        }
    }

    private int _marketplaceRenderVersion;

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
        var installedPlugins = _installedPlugins;
        var canMutate = _marketplaceCanMutate;
        var themeState = _themeIntegration.MarketState;
        var instanceRunning = _instance.RuntimeStatus == InstanceRuntimeStatus.Running
            && string.Equals(GetSelectedProfileName(), "web", StringComparison.OrdinalIgnoreCase);
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
                if (restoreCategoryKey is not null)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RestoreScrollOffset(
                        MarketplaceList,
                        _marketplaceScrollOffsets,
                        restoreCategoryKey)));
                }
            });
        });
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

    internal static IReadOnlyList<MarketplaceItem> SelectAvailableMarketplaceUpdates(
        IReadOnlyList<MarketplaceItem> snapshot,
        IReadOnlyList<ExtensionEntry> installedPlugins)
    {
        var updates = new Dictionary<string, MarketplaceItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot)
        {
            var installed = MarketplaceService.FindInstalledPlugin(item, installedPlugins);
            if (installed is null
                || !installed.Managed
                || MarketplaceService.GetUpdateStatus(item.Version, installed.Version)
                    != MarketplaceUpdateStatus.Available)
            {
                continue;
            }

            var candidate = item with
            {
                IsInstalled = true,
                IsManaged = true,
                InstalledVersion = installed.Version,
                UpdateStatus = MarketplaceUpdateStatus.Available
            };
            if (!updates.TryGetValue(installed.Name, out var existing)
                || MarketplaceService.GetUpdateStatus(candidate.Version, existing.Version)
                    == MarketplaceUpdateStatus.Available)
            {
                updates[installed.Name] = candidate;
            }
        }

        return updates.Values
            .OrderBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void UpdateMarketplaceUpdateAllButton()
    {
        if (_agentOnly || MarketplaceUpdateAllButton is null)
        {
            return;
        }

        var count = SelectAvailableMarketplaceUpdates(_marketplaceSnapshot, _installedPlugins).Count;
        MarketplaceUpdateAllButton.Content = count == 0 ? "全部更新" : $"全部更新 ({count})";
        MarketplaceUpdateAllButton.IsEnabled = count > 0
            && !_isMarketplaceLoading
            && !_isMarketplaceMutating
            && _instance.RuntimeOwnership != InstanceRuntimeOwnership.Attached
            && _instance.RuntimeStatus != InstanceRuntimeStatus.Running;
        MarketplaceUpdateAllButton.ToolTip = count == 0
            ? "当前 Profile 没有检测到可更新的 Plugin"
            : _instance.RuntimeStatus == InstanceRuntimeStatus.Running
                ? "批量更新前请先停止实例"
                : $"串行更新 {count} 个 Plugin；每项都会独立快照和失败回档";
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
                if (!_themeIntegration.UseDshMarketHotReload)
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

                var themeState = await _themeIntegration.ReadMarketAsync(
                    _instance,
                    operationCancellation.Token);
                if (!themeState.IsAvailable)
                {
                    var message = $"当前实例没有可用的 dsh-market，运行中不能热加载。请先停止实例，再点击“安装”或使用“手动安装 Plugin”。\n\n{themeState.Error}";
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
            }

            var verification = await _marketplaceService.VerifyAsync(
                item,
                _instance.DetectedVersion,
                operationCancellation.Token);
            if (verification.Status == MarketplaceVerificationStatus.Rejected)
            {
                MarketplaceStatusText.Text = verification.Message;
                progressWindow.Fail(verification.Message);
                return;
            }


            if (verification.CompatibilityStatus == MarketplaceCompatibilityStatus.Incompatible
                && System.Windows.MessageBox.Show(
                    Window.GetWindow(this),
                    verification.Message + "\n\n仍要强制尝试安装吗？",
                    "Plugin 兼容性警告",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                MarketplaceStatusText.Text = "已取消安装不兼容的 Plugin。";
                progressWindow.Canceled("用户取消了不兼容 Plugin 的强制安装。");
                return;
            }

            progressWindow.SetIndeterminate("Plugin 校验通过，正在保存当前配置…");
            var snapshot = _marketplaceService.CreatePluginSnapshot(_instance, GetSelectedProfileName());
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
                        ? await _themeIntegration.UpdatePluginAsync(
                            _instance,
                            installedEntry?.Name ?? verification.PackageName ?? item.PackageName ?? packageSpec,
                            operationCancellation.Token)
                        : await _themeIntegration.InstallPluginAsync(
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
                var restored = _marketplaceService.RestorePluginSnapshot(
                    _instance,
                    snapshot,
                    GetSelectedProfileName());
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
            var snapshot = _marketplaceService.CreatePluginSnapshot(_instance, GetSelectedProfileName());
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

            if (!_themeIntegration.UseDshMarketHotReload)
            {
                throw new InvalidOperationException("当前实例已关闭 dsh-market 热加载，请先在扩展页左侧开启。 ");
            }

            BeginMarketplaceMutation("正在通过 dsh-market 应用主题…");
            var snapshot = _versionSnapshotService?.CreateLivePluginSnapshot(
                _instance,
                $"dsh-market 应用主题：{item.Name}");
            var result = await _themeIntegration.ApplyThemeAsync(_instance, item.ThemePackageName);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? "dsh-market 应用主题失败。 ");
            }

            MarketplaceStatusText.Text = snapshot is null
                ? $"主题已交给 dsh-market 应用：{item.Name}。"
                : $"主题已交给 dsh-market 应用：{item.Name}；已创建自动存档。";
            if (string.Equals(GetSelectedProfileName(), "web", StringComparison.OrdinalIgnoreCase)
                && ChatThemeSyncCheckBox.IsChecked == true
                && _themeIntegration.ChatCapability.IsSupported)
            {
                var sync = await _themeIntegration.SyncChatThemeAsync(_instance.WebUrl);
                MarketplaceStatusText.Text += sync.IsSuccess
                    ? $"{sync.Reason}"
                    : $"Chat 主题未同步：{sync.Reason}";
            }
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
        MarketplaceUpdateAllButton.IsEnabled = false;
        ProfileSelectorBox.IsEnabled = false;
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
        ProfileSelectorBox.IsEnabled = true;
        MarketplaceProgressPanel.Visibility = Visibility.Collapsed;
        _marketplaceCanMutate = _instance.RuntimeOwnership != InstanceRuntimeOwnership.Attached
            && _instance.RuntimeStatus != InstanceRuntimeStatus.Running;
        UpdateMarketplaceUpdateAllButton();
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
            rollbackSucceeded = _marketplaceService?.RestorePluginSnapshot(
                _instance,
                snapshot,
                GetSelectedProfileName()) == true;
            rollbackMessage = rollbackSucceeded
                ? $"已恢复操作前的 {GetSelectedProfileName()} profile 配置。"
                : $"没有可用的 {GetSelectedProfileName()} profile 备份，未能自动恢复。";
        }
        catch (Exception rollbackError)
        {
            rollbackMessage = $"自动恢复 {GetSelectedProfileName()} profile 失败：{rollbackError.Message}";
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

        _themeIntegration.SetUseDshMarketHotReload(DshMarketHotReloadCheckBox.IsChecked == true);
        var profileGeneration = _themeIntegration.ProfileGeneration;
        try
        {
            var settings = _versionSettingsService.Read(_instance);
            settings.UseDshMarketHotReload = _themeIntegration.UseDshMarketHotReload;
            _versionSettingsService.Save(_instance, settings);
            if (_themeIntegration.UseDshMarketHotReload)
            {
                await _themeIntegration.ReadMarketAsync(_instance);
            }
            else
            {
                _themeIntegration.MarkMarketUnavailable("当前实例已关闭 dsh-market 热加载。 ");
            }

            if (string.Equals(GetSelectedProfileName(), "web", StringComparison.OrdinalIgnoreCase))
            {
                await _themeIntegration.ProbeChatCapabilityAsync(
                    _instance,
                    profileGeneration: profileGeneration);
            }
            else
            {
                _themeIntegration.SetChatCapability(
                    ThemeCapabilityProbeResult.Unsupported(
                        "Chat 主题联动只作用于 web profile，当前 Profile 不支持。 "),
                    profileGeneration);
            }
            if (!_themeIntegration.IsCurrentProfile(profileGeneration))
            {
                return;
            }
            MarketplaceStatusText.Text = _themeIntegration.UseDshMarketHotReload
                ? "已开启 dsh-market 热加载；应用主题前会创建自动存档。"
                : "已关闭 dsh-market 热加载；Plugin 仍可正常安装和管理。";
            UpdateChatThemeControls();
            RenderMarketplaceItems();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ChatThemeSyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (_agentOnly
            || !string.Equals(GetSelectedProfileName(), "web", StringComparison.OrdinalIgnoreCase)
            || !_themeIntegration.ChatCapability.IsSupported)
        {
            UpdateChatThemeControls();
            return;
        }

        var result = await _themeIntegration.SyncChatThemeAsync(_instance.WebUrl);
        MarketplaceStatusText.Text = result.IsSuccess
            ? result.Reason
            : $"Chat 主题未同步：{result.Reason}";
        UpdateChatThemeControls();
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

        if (allowRunning
            && _instance.RuntimeStatus == InstanceRuntimeStatus.Running
            && !string.Equals(GetSelectedProfileName(), "web", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("非 web Profile 的 Plugin 修改需要先停止实例。 ");
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
                ProcessStartedAt = null,
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
            snapshot = _marketplaceService?.CreatePluginSnapshot(
                _instance,
                GetSelectedProfileName()) ?? string.Empty;
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
            var snapshot = _marketplaceService?.CreatePluginSnapshot(
                _instance,
                GetSelectedProfileName()) ?? string.Empty;
            string output;
            try
            {
                output = await _service.UpdatePluginAsync(_instance, entry.Name, _nodeRuntime());
            }
            catch (Exception ex)
            {
                var recovery = await RecoverPluginFailureAsync(snapshot, ex, "update", entry.Name);
                throw new InvalidOperationException(recovery.Summary, ex);
            }

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
                {
                    var snapshot = _marketplaceService?.CreatePluginSnapshot(
                        _instance,
                        GetSelectedProfileName()) ?? string.Empty;
                    try
                    {
                        await _service.RemovePluginAsync(_instance, entry.Name, _nodeRuntime());
                    }
                    catch (Exception ex)
                    {
                        var recovery = await RecoverPluginFailureAsync(snapshot, ex, "remove", entry.Name);
                        throw new InvalidOperationException(recovery.Summary, ex);
                    }

                    break;
                }
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
