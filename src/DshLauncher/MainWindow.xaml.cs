using System.Diagnostics;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using DshLauncher.Models;
using DshLauncher.Services;
using Forms = System.Windows.Forms;

namespace DshLauncher;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly NodeRuntimeDetector _nodeDetector = new();
    private readonly DshRuntimeDetector _dshDetector = new();
    private readonly SourceProjectInspector _sourceInspector = new();
    private readonly InstanceRegistry _instanceRegistry = new();
    private readonly DshInstanceRunner _instanceRunner = new();
    private readonly ExtensionService _extensionService;
    private readonly MarketplaceService _marketplaceService;
    private readonly VersionPackageService _versionPackageService;
    private readonly VersionSettingsService _versionSettingsService = new();
    private readonly ConversationService _conversationService;
    private readonly ConversationSyncService _conversationSyncService;
    private readonly ModelService _modelService;
    private readonly ModelProviderSyncService _modelProviderSyncService;
    private readonly ProviderStateService _providerStateService = new();
    private readonly ProviderDiagnosticService _providerDiagnosticService = new();
    private readonly DshInstallService _dshInstaller = new();
    private readonly NodeInstallService _nodeInstaller = new();
    private readonly SourceBuildService _sourceBuilder = new();
    private readonly CancellationTokenSource _windowCancellation = new();
    private CancellationTokenSource? _providerRefreshCancellation;
    private NodeRuntimeInfo _nodeRuntime = NodeRuntimeInfo.Missing();
    private DshRuntimeInfo _dshRuntime = DshRuntimeInfo.Missing();
    private readonly Dictionary<string, ChatWindow> _chatWindows = new(StringComparer.Ordinal);
    private ManagerInstance? _selectedInstance;
    private bool _isNodeDetectionInProgress;
    private bool _isLifecycleInProgress;
    private bool _isDshInstallInProgress;
    private bool _isRuntimePrepareInProgress;
    private bool _isProviderDetectionInProgress;
    private bool _blockWindowCloseForMsi;
    private Action? _runtimePanelUpdateStatus;

    public MainWindow()
    {
        _extensionService = new(id => _instanceRunner.IsRunning(id));
        _marketplaceService = new();
        _versionPackageService = new(_instanceRegistry);
        _conversationService = new(isRunning: id => _instanceRunner.IsRunning(id));
        _conversationSyncService = new(_versionSettingsService, id => _instanceRunner.IsRunning(id));
        _modelService = new(id => _instanceRunner.IsRunning(id));
        _modelProviderSyncService = new(
            _versionSettingsService,
            _modelService,
            _providerStateService,
            id => _instanceRunner.IsRunning(id));
        InitializeComponent();
        DataContext = this;
    }

    public string PageTitle { get; private set; } = "启动";

    public string PageSubtitle { get; private set; } = "管理 DeepSeek Harness 实例与运行环境";

    public string PageNotice { get; private set; } = string.Empty;

    public Visibility PageNoticeVisibility { get; private set; } = Visibility.Collapsed;

    public ObservableCollection<ManagerInstance> Instances { get; } = new();

    public ObservableCollection<ManagerInstance> RunningInstances { get; } = new();

    public ObservableCollection<ProviderCardViewModel> ProviderCards { get; } = new();

    public ManagerInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (ReferenceEquals(_selectedInstance, value))
            {
                return;
            }

            _selectedInstance = value;
            ApplySelectedVersionSettings(_selectedInstance);
            OnPropertyChanged(nameof(SelectedInstance));
            OnPropertyChanged(nameof(SelectedInstanceName));
            OnPropertyChanged(nameof(SelectedInstanceSummary));
            OnPropertyChanged(nameof(SelectedInstanceStatus));
            OnPropertyChanged(nameof(InstanceEndpointText));
            OnPropertyChanged(nameof(CanStartInstance));
            OnPropertyChanged(nameof(StartInstanceButtonText));
            OnPropertyChanged(nameof(CanStopInstance));
            OnPropertyChanged(nameof(CanRestartInstance));
            OnPropertyChanged(nameof(NodeStatusText));
            OnPropertyChanged(nameof(NodeStatusBrush));
            OnPropertyChanged(nameof(NodeVersionText));
            OnPropertyChanged(nameof(NodePathText));
            OnPropertyChanged(nameof(ProviderSummaryText));
            if (IsLoaded)
            {
                _ = RefreshProvidersAsync(_selectedInstance);
                _ = RefreshNodeAsync();
            }
        }
    }

    public string InstanceCountText => $"{Instances.Count} 个实例";

    public string RunningInstanceCountText => $"{RunningInstances.Count} 个运行中";

    public Visibility NoInstancesVisibility => Instances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InstancesVisibility => Instances.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NoRunningInstancesVisibility => RunningInstances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RunningInstancesVisibility => RunningInstances.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NoProvidersVisibility => ProviderCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ProviderCardsVisibility => ProviderCards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string ProviderSummaryText => SelectedInstance is null
        ? "选择实例后检测 Provider 的连接、模型列表和思考能力"
        : _isProviderDetectionInProgress
            ? $"正在检测 {SelectedInstance.Name} 的 Provider…"
            : $"{ProviderCards.Count} 个 Provider · 仅调用只读模型列表接口";

    public bool CanRefreshProviders => !_isProviderDetectionInProgress && SelectedInstance is not null;

    public string SelectedInstanceName => SelectedInstance?.Name ?? "等待实例注册";

    public string SelectedInstanceSummary => SelectedInstance is null
        ? "先注册一个 DSh 实例，再从这里启动。"
        : $"{SelectedInstance.KindText} · {SelectedInstance.RootPath}";

    public string SelectedInstanceStatus => SelectedInstance?.StatusText ?? "未选择";

    public bool CanStartInstance => CanStartInstanceCore(
        _isLifecycleInProgress,
        _isRuntimePrepareInProgress,
        _isNodeDetectionInProgress,
        SelectedInstance is not null,
        SelectedInstance is not null
            && _instanceRunner.IsRunning(SelectedInstance.Id)
            && string.IsNullOrWhiteSpace(SelectedInstance.WebUrl));

    internal static bool CanStartInstanceCore(
        bool lifecycleInProgress,
        bool runtimePrepareInProgress,
        bool nodeDetectionInProgress,
        bool hasSelection,
        bool runningWithoutWebUrl) =>
        !lifecycleInProgress
        && !runtimePrepareInProgress
        && !nodeDetectionInProgress
        && hasSelection
        && !runningWithoutWebUrl;

    public string StartInstanceButtonText => SelectedInstance is not null
        && _instanceRunner.IsRunning(SelectedInstance.Id)
        ? "打开实例"
        : "启动实例";

    public bool CanStopInstance => !_isLifecycleInProgress
        && SelectedInstance is not null
        && _instanceRunner.IsManaged(SelectedInstance.Id);

    public bool CanRestartInstance => CanStopInstance;

    public string InstanceEndpointText => SelectedInstance?.WebUrl
        ?? (SelectedInstance?.RuntimeStatus == InstanceRuntimeStatus.Running ? "正在检查运行地址…" : "尚未启动");

    public bool CanInstallDsh => !_isDshInstallInProgress
        && !_isNodeDetectionInProgress
        && _nodeRuntime.IsAvailable
        && (_dshRuntime.NodeEngine is null
            || _nodeRuntime.GetCompatibility(_dshRuntime.NodeEngine) == NodeRuntimeCompatibility.Compatible);

    public string DshInstallButtonText => _isDshInstallInProgress
        ? "安装中…"
        : _dshRuntime.IsAvailable ? "安装/更新 DSh" : "安装 DSh";

    public bool CanRefreshNode => !_isNodeDetectionInProgress;

    public string NodeStatusText => _isNodeDetectionInProgress
        ? "检测中…"
        : GetSelectedNodeCompatibility() switch
        {
            NodeRuntimeCompatibility.Missing => "Missing",
            NodeRuntimeCompatibility.Compatible => "Compatible",
            NodeRuntimeCompatibility.Incompatible => "Incompatible",
            _ => "Unknown"
        };

    public System.Windows.Media.Brush NodeStatusBrush => _isNodeDetectionInProgress
        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 129, 150))
        : GetSelectedNodeCompatibility() switch
        {
            NodeRuntimeCompatibility.Compatible => new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 135, 90)),
            NodeRuntimeCompatibility.Incompatible => new SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 75, 55)),
            NodeRuntimeCompatibility.Missing => new SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 105, 30)),
            _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 129, 150))
        };

    public string NodeVersionText => _isNodeDetectionInProgress
        ? "请稍候"
        : !_nodeRuntime.IsAvailable
            ? "需要安装 Node.js"
            : $"{_nodeRuntime.VersionText} · {GetNodeRequirementText()}";

    public string NodePathText => _isNodeDetectionInProgress
        ? "正在检查 PATH 和 Windows 常见安装位置…"
        : _nodeRuntime.IsAvailable
            ? (_nodeRuntime.ExecutablePath ?? "已找到 node.exe，但路径不可用")
            : _nodeRuntime.Error ?? "未找到 PATH 中的 node.exe，也没有发现常见安装位置";

    public string DshStatusText => _dshRuntime.IsAvailable ? "可用" : "未安装";

    public System.Windows.Media.Brush DshStatusBrush => _dshRuntime.IsAvailable
        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 135, 90))
        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 105, 30));

    public string DshVersionText => _dshRuntime.IsAvailable
        ? $"{_dshRuntime.VersionText} · {(_dshRuntime.PackageRoot is null ? "路径未解析" : "已找到安装包")}"
        : "实例注册后由对应运行环境启动";

    private NodeRuntimeCompatibility GetSelectedNodeCompatibility()
    {
        if (!_nodeRuntime.IsAvailable)
        {
            return NodeRuntimeCompatibility.Missing;
        }

        return _nodeRuntime.GetCompatibility(GetNodeEngineRequirement(SelectedInstance));
    }

    private string GetNodeRequirementText()
    {
        var requirement = GetNodeEngineRequirement(SelectedInstance);
        return string.IsNullOrWhiteSpace(requirement)
            ? "未声明 engines.node"
            : $"要求 {requirement}";
    }

    private string? GetNodeEngineRequirement(ManagerInstance? instance) =>
        DshRuntimeDetector.ResolveNodeEngine(instance, _dshRuntime.NodeEngine);

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshDshAsync();
        await LoadInstancesAsync();
        SwitchSection("启动");
        await RefreshNodeAsync();
        await RefreshProvidersAsync(SelectedInstance);
    }

    private async void RefreshNode_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDshAsync();
        var runtime = await RefreshNodeAsync();
        if (runtime is null)
        {
            return;
        }

        ShowNotice(runtime.IsAvailable
            ? $"运行环境检测完成：Node.js {runtime.VersionText}（{NodeStatusText}），DSh {_dshRuntime.VersionText}。"
            : "运行环境检测完成：当前没有找到可用的 node.exe。Launcher 本身仍可继续运行。");
    }

    private async Task<NodeRuntimeInfo?> RefreshNodeAsync()
    {
        if (_isNodeDetectionInProgress)
        {
            return null;
        }

        _isNodeDetectionInProgress = true;
        OnPropertyChanged(nameof(CanRefreshNode));
        OnPropertyChanged(nameof(NodeStatusText));
        OnPropertyChanged(nameof(NodeStatusBrush));
        OnPropertyChanged(nameof(NodeVersionText));
        OnPropertyChanged(nameof(NodePathText));
        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(StartInstanceButtonText));

        try
        {
            var preferredNodePath = GetPreferredNodePath();
            _nodeRuntime = await _nodeDetector.DetectAsync(preferredNodePath, _windowCancellation.Token);
            OnPropertyChanged(nameof(NodeStatusBrush));
            OnPropertyChanged(nameof(NodeStatusText));
            OnPropertyChanged(nameof(NodeVersionText));
            OnPropertyChanged(nameof(NodePathText));
            OnPropertyChanged(nameof(CanStartInstance));
            OnPropertyChanged(nameof(StartInstanceButtonText));
            OnPropertyChanged(nameof(CanInstallDsh));
            return _nodeRuntime;
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _nodeRuntime = NodeRuntimeInfo.Missing($"Node.js 检测失败：{ex.Message}");
            OnPropertyChanged(nameof(NodeStatusBrush));
            OnPropertyChanged(nameof(NodeStatusText));
            OnPropertyChanged(nameof(NodeVersionText));
            OnPropertyChanged(nameof(NodePathText));
            OnPropertyChanged(nameof(CanStartInstance));
            OnPropertyChanged(nameof(CanInstallDsh));
            ShowNotice(_nodeRuntime.Error ?? "Node.js 检测失败。");
            return _nodeRuntime;
        }
        finally
        {
            _isNodeDetectionInProgress = false;
            OnPropertyChanged(nameof(CanRefreshNode));
            OnPropertyChanged(nameof(NodeStatusText));
            OnPropertyChanged(nameof(NodeStatusBrush));
            OnPropertyChanged(nameof(NodeVersionText));
            OnPropertyChanged(nameof(NodePathText));
            OnPropertyChanged(nameof(CanStartInstance));
            OnPropertyChanged(nameof(StartInstanceButtonText));
            OnPropertyChanged(nameof(CanInstallDsh));
            _runtimePanelUpdateStatus?.Invoke();
        }
    }

    private async Task RefreshDshAsync()
    {
        try
        {
            _dshRuntime = await _dshDetector.DetectAsync(_windowCancellation.Token);
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _dshRuntime = DshRuntimeInfo.Missing($"DSh 检测失败：{ex.Message}");
        }

        OnPropertyChanged(nameof(DshStatusText));
        OnPropertyChanged(nameof(DshStatusBrush));
        OnPropertyChanged(nameof(DshVersionText));
        OnPropertyChanged(nameof(NodeStatusText));
        OnPropertyChanged(nameof(NodeStatusBrush));
        OnPropertyChanged(nameof(NodeVersionText));
        OnPropertyChanged(nameof(CanInstallDsh));
        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(DshInstallButtonText));
        RebindInstalledInstancesToDetectedDSh();
    }

    private async Task LoadInstancesAsync()
    {
        try
        {
            Instances.Clear();
            foreach (var storedInstance in _instanceRegistry.Load())
            {
                var instance = storedInstance;
                if (storedInstance.RuntimeStatus == InstanceRuntimeStatus.Running
                    && await _instanceRunner.TryAttachAsync(storedInstance, _windowCancellation.Token))
                {
                    instance = storedInstance with
                    {
                        RuntimeOwnership = InstanceRuntimeOwnership.Attached,
                        LastError = null
                    };
                }
                else if (storedInstance.RuntimeStatus == InstanceRuntimeStatus.Running)
                {
                    instance = storedInstance with
                    {
                        RuntimeStatus = InstanceRuntimeStatus.Stopped,
                        RuntimeOwnership = InstanceRuntimeOwnership.None,
                        ProcessId = null,
                        Port = null,
                        WebUrl = null
                    };
                }

                Instances.Add(instance);
                if (instance != storedInstance)
                {
                    _instanceRegistry.Update(instance);
                }
            }

            SelectedInstance = Instances.FirstOrDefault();
            OnPropertyChanged(nameof(InstanceCountText));
            OnPropertyChanged(nameof(NoInstancesVisibility));
            OnPropertyChanged(nameof(InstancesVisibility));
            OnPropertyChanged(nameof(CanStartInstance));
            OnPropertyChanged(nameof(CanStopInstance));
            OnPropertyChanged(nameof(CanRestartInstance));
            RefreshRunningInstances();
            OnPropertyChanged(nameof(ProviderSummaryText));
            OnPropertyChanged(nameof(CanRefreshProviders));
            RebindInstalledInstancesToDetectedDSh();
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowNotice($"读取实例注册文件失败：{ex.Message}");
        }
    }

    private async Task RefreshProvidersAsync(ManagerInstance? instance)
    {
        _providerRefreshCancellation?.Cancel();
        _providerRefreshCancellation?.Dispose();

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        _providerRefreshCancellation = cancellation;
        _isProviderDetectionInProgress = instance is not null;
        ProviderCards.Clear();
        OnPropertyChanged(nameof(NoProvidersVisibility));
        OnPropertyChanged(nameof(ProviderCardsVisibility));
        OnPropertyChanged(nameof(ProviderSummaryText));
        OnPropertyChanged(nameof(CanRefreshProviders));

        if (instance is null)
        {
            _providerRefreshCancellation = null;
            cancellation.Dispose();
            _isProviderDetectionInProgress = false;
            OnPropertyChanged(nameof(ProviderSummaryText));
            OnPropertyChanged(nameof(CanRefreshProviders));
            return;
        }

        try
        {
            await SynchronizeModelProvidersAsync(instance, cancellation.Token);
            var states = _providerStateService.Read(instance);
            var providers = _modelService.Read(instance);
            foreach (var provider in providers)
            {
                var isEnabled = !states.TryGetValue(provider.Provider, out var storedEnabled) || storedEnabled;
                ProviderCards.Add(new ProviderCardViewModel(provider, isEnabled));
            }

            OnPropertyChanged(nameof(NoProvidersVisibility));
            OnPropertyChanged(nameof(ProviderCardsVisibility));
            OnPropertyChanged(nameof(ProviderSummaryText));

            var checks = ProviderCards.Select(async card =>
                (Card: card, Result: await _providerDiagnosticService.CheckAsync(card.Provider, cancellation.Token)));
            var results = await Task.WhenAll(checks);
            foreach (var result in results)
            {
                result.Card.SetDiagnostic(result.Result);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowNotice($"Provider 检测失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_providerRefreshCancellation, cancellation))
            {
                _providerRefreshCancellation = null;
                _isProviderDetectionInProgress = false;
                OnPropertyChanged(nameof(ProviderSummaryText));
                OnPropertyChanged(nameof(CanRefreshProviders));
            }

            cancellation.Dispose();
        }
    }

    private async void RefreshProviders_Click(object sender, RoutedEventArgs e)
    {
        await RefreshProvidersAsync(SelectedInstance);
    }

    private void ProviderToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is null || (sender as FrameworkElement)?.DataContext is not ProviderCardViewModel card)
        {
            return;
        }

        try
        {
            var enabled = !card.IsEnabled;
            _providerStateService.SetEnabled(SelectedInstance, card.ProviderKey, enabled);
            card.SetEnabled(enabled);
            if (!_instanceRunner.IsRunning(SelectedInstance.Id))
            {
                _ = SynchronizeModelProvidersAsync(SelectedInstance);
            }
            ShowNotice($"Provider“{card.DisplayName}”已{(enabled ? "启用" : "禁用")}。该状态由 Launcher 保存，不会改写 DSh 的 settings.yaml。 ");
        }
        catch (Exception ex)
        {
            ShowNotice($"保存 Provider 状态失败：{ex.Message}");
        }
    }

    private void ProviderIssue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProviderCardViewModel card)
        {
            return;
        }

        System.Windows.MessageBox.Show(
            this,
            card.IssueDetails,
            $"{card.DisplayName} Provider 问题",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void RefreshRunningInstances()
    {
        RunningInstances.Clear();
        foreach (var instance in Instances.Where(instance => _instanceRunner.IsRunning(instance.Id)))
        {
            RunningInstances.Add(instance);
        }

        OnPropertyChanged(nameof(RunningInstanceCountText));
        OnPropertyChanged(nameof(NoRunningInstancesVisibility));
        OnPropertyChanged(nameof(RunningInstancesVisibility));
    }

    private void RunningInstances_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(RunningInstancesList, source) is not ListBoxItem item
            || item.DataContext is not ManagerInstance instance)
        {
            return;
        }

        SelectedInstance = instance;
        if (!_instanceRunner.IsRunning(instance.Id)
            && instance.RuntimeOwnership != InstanceRuntimeOwnership.Attached)
        {
            ShowNotice($"实例“{instance.Name}”当前没有运行。请先启动实例。 ");
            e.Handled = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(instance.WebUrl))
        {
            ShowNotice($"实例“{instance.Name}”正在运行，但没有可用的 Web 地址。 ");
            e.Handled = true;
            return;
        }

        if (!TryFocusChatWindow(instance.Id))
        {
            OpenChatWindow(instance.Id, instance.WebUrl);
        }

        ShowNotice($"已打开实例：{instance.Name}。 ");
        e.Handled = true;
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string section)
        {
            return;
        }

        SwitchSection(section);
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null)
        {
            if (current is ScrollViewer viewer && CanScroll(viewer, e.Delta))
            {
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 3.0);
                e.Handled = true;
                return;
            }

            current = GetParentObject(current);
        }

        if (CanScroll(MainScrollViewer, e.Delta))
        {
            MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }

    private static bool CanScroll(ScrollViewer viewer, int delta)
    {
        if (viewer.ScrollableHeight <= 0)
        {
            return false;
        }

        return delta > 0
            ? viewer.VerticalOffset > 0
            : viewer.VerticalOffset < viewer.ScrollableHeight;
    }

    private static DependencyObject? GetParentObject(DependencyObject child)
    {
        try
        {
            var visualParent = System.Windows.Media.VisualTreeHelper.GetParent(child);
            if (visualParent is not null)
            {
                return visualParent;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // ContentElement parents are resolved through the logical tree below.
        }

        return LogicalTreeHelper.GetParent(child);
    }

    private void SwitchSection(string section)
    {
        if (section == "实例")
        {
            section = "启动";
        }

        SetNavigationSelection(section);
        PageNoticeVisibility = Visibility.Collapsed;

        if (section == "启动")
        {
            PageTitle = "启动";
            PageSubtitle = "启动实例并查看正在运行的 DeepSeek Harness";
            ShowMainDashboard();
            _ = RefreshProvidersAsync(SelectedInstance);
        }
        else if (section is "扩展" or "Agent" or "对话")
        {
            if (SelectedInstance is not { } instance)
            {
                PageTitle = section;
                PageSubtitle = "请先在“启动”工作区注册并选择一个 DSh 实例";
                ShowMainDashboard();
                ShowNotice($"请先注册并选择一个 DSh 实例，再打开“{section}”。");
            }
            else
            {
                PageTitle = section;
                PageSubtitle = section switch
                {
                    "扩展" => "管理当前实例的 Plugin 与 MCP",
                    "Agent" => "管理当前实例的 Skill、Agent Preset 与 Workflow",
                    _ => "管理当前实例的 session.jsonl / .zstd 对话文件"
                };

                object page = section switch
                {
                    "扩展" => new ExtensionWindow(instance, _extensionService, () => _nodeRuntime, marketplaceService: _marketplaceService),
                    "Agent" => new ExtensionWindow(instance, _extensionService, () => _nodeRuntime, agentOnly: true, marketplaceService: _marketplaceService),
                    _ => new ConversationWindow(
                        instance,
                        _conversationService,
                        entry => OpenConversationAsync(instance, entry),
                        () => SynchronizeConversationsAsync(instance),
                        relativePath => PropagateConversationDeletionAsync(instance, relativePath))
                };
                ShowEmbeddedPage(page);
            }
        }
        else
        {
            PageTitle = section;
            PageSubtitle = "DSH Launcher Core 设置与诊断";
            ShowEmbeddedPage(CreateSettingsPage());
        }

        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(PageNoticeVisibility));
    }

    private void ShowMainDashboard()
    {
        EmbeddedPageHost.Content = null;
        EmbeddedPageHost.Visibility = Visibility.Collapsed;
        MainDashboardGrid.Visibility = Visibility.Visible;
        ProviderSummaryCard.Visibility = Visibility.Visible;
    }

    private void ShowEmbeddedPage(object page)
    {
        MainDashboardGrid.Visibility = Visibility.Collapsed;
        ProviderSummaryCard.Visibility = Visibility.Collapsed;
        EmbeddedPageHost.Content = page;
        EmbeddedPageHost.Visibility = Visibility.Visible;
    }

    private void VersionControl_Click(object sender, RoutedEventArgs e) => ShowVersionControl();

    private void VersionSettings_Click(object sender, RoutedEventArgs e) => ShowVersionSettings();

    private void ShowVersionControl()
    {
        PageTitle = "版本控制";
        PageSubtitle = "按版本选择、复制版本或导入整合包；每个版本使用独立 DSH_HOME";
        ShowEmbeddedPage(new VersionControlWindow(
            Instances,
            SelectedInstance,
            _versionPackageService,
            GetVersionTemplate,
            AddCreatedVersion,
            RemoveDeletedVersion,
            version => SelectedInstance = version));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
    }

    public void ShowVersionSettings()
    {
        PageTitle = SelectedInstance is { } instance
            ? $"版本设置 - {instance.Name}"
            : "版本设置";
        PageSubtitle = SelectedInstance is { } selected
            ? $"当前实例：{selected.Name} · 管理个性化、配置、插件和分享导出"
            : "按 PCL2 的版本设置方式管理个性化、配置、插件和分享导出";
        ShowEmbeddedPage(new VersionSettingsWindow(
            SelectedInstance,
            Instances,
            _versionSettingsService,
            _extensionService,
            () => _nodeRuntime,
            _versionPackageService,
            () =>
            {
                ApplySelectedVersionSettings(SelectedInstance);
                _ = RefreshNodeAsync();
                if (SelectedInstance is { } current)
                {
                    _ = SynchronizeModelProvidersAsync(current);
                    _ = SynchronizeConversationsAsync(current);
                }
            }));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
    }

    private string? GetPreferredNodePath()
    {
        if (SelectedInstance is null)
        {
            return null;
        }

        try
        {
            return _versionSettingsService.Read(SelectedInstance).NodeExecutablePath;
        }
        catch
        {
            return null;
        }
    }

    private void ApplySelectedVersionSettings(ManagerInstance? instance)
    {
        if (instance is null)
        {
            Title = "DSH Launcher";
            return;
        }

        try
        {
            var title = _versionSettingsService.Read(instance).WindowTitle;
            Title = string.IsNullOrWhiteSpace(title) ? "DSH Launcher" : title;
        }
        catch
        {
            Title = "DSH Launcher";
        }
    }

    private ManagerInstance? GetVersionTemplate()
    {
        if (SelectedInstance is not null)
        {
            return SelectedInstance;
        }

        if (_dshRuntime.PackageRoot is not { } packageRoot
            || !Directory.Exists(packageRoot))
        {
            return null;
        }

        return new ManagerInstance(
            "runtime-template",
            $"DSh {_dshRuntime.Version ?? "installed"}",
            packageRoot,
            InstanceKind.Installed,
            string.Empty,
            _dshRuntime.ExecutablePath,
            _dshRuntime.Version,
            _dshRuntime.ExecutablePath is null ? InstanceRuntimeStatus.Unknown : InstanceRuntimeStatus.Ready,
            "npm",
            null,
            DateTimeOffset.UtcNow);
    }

    private void AddCreatedVersion(ManagerInstance created)
    {
        if (!Instances.Any(instance => string.Equals(instance.Id, created.Id, StringComparison.Ordinal)))
        {
            Instances.Add(created);
        }

        SelectedInstance = created;
        OnPropertyChanged(nameof(InstanceCountText));
        OnPropertyChanged(nameof(NoInstancesVisibility));
        OnPropertyChanged(nameof(InstancesVisibility));
        RefreshRunningInstances();
    }

    private void RemoveDeletedVersion(ManagerInstance deleted)
    {
        var wasSelected = SelectedInstance is not null
            && string.Equals(SelectedInstance.Id, deleted.Id, StringComparison.Ordinal);
        var removed = Instances.FirstOrDefault(instance =>
            string.Equals(instance.Id, deleted.Id, StringComparison.Ordinal));
        if (removed is not null)
        {
            Instances.Remove(removed);
        }
        if (wasSelected)
        {
            SelectedInstance = Instances.FirstOrDefault();
        }

        OnPropertyChanged(nameof(InstanceCountText));
        OnPropertyChanged(nameof(NoInstancesVisibility));
        OnPropertyChanged(nameof(InstancesVisibility));
        RefreshRunningInstances();
    }

    private FrameworkElement CreateSettingsPage()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 720
        };
        panel.Children.Add(new TextBlock
        {
            Text = "运行环境检测",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });

        var nodeStatus = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var nodeDetail = new TextBlock
        {
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var dshStatus = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var dshDetail = new TextBlock
        {
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(nodeStatus);
        panel.Children.Add(nodeDetail);
        panel.Children.Add(dshStatus);
        panel.Children.Add(dshDetail);

        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        var refreshButton = new System.Windows.Controls.Button
        {
            Content = "重新检测",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        var prepareButton = new System.Windows.Controls.Button
        {
            Content = "准备运行环境（官方源）",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var prepareMirrorButton = new System.Windows.Controls.Button
        {
            Content = "准备运行环境（国内镜像）",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var hint = new TextBlock
        {
            Text = "准备运行环境会下载 Node.js 官方安装程序并按系统授权安装，再通过 npm 安装 @deepseek-ai/dsh；Launcher 启动时不会自动下载或安装任何内容。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        buttons.Children.Add(refreshButton);
        buttons.Children.Add(prepareButton);
        buttons.Children.Add(prepareMirrorButton);
        panel.Children.Add(buttons);
        panel.Children.Add(hint);

        void UpdateStatus()
        {
            nodeStatus.Text = $"Node.js：{NodeStatusText}";
            nodeDetail.Text = _nodeRuntime.IsAvailable
                ? $"{_nodeRuntime.VersionText} · {(_nodeRuntime.ExecutablePath ?? "路径未知")}"
                : _nodeRuntime.Error ?? "未安装";
            dshStatus.Text = $"DeepSeek Harness：{DshStatusText}";
            dshDetail.Text = _dshRuntime.IsAvailable
                ? $"{_dshRuntime.VersionText} · {(_dshRuntime.ExecutablePath ?? "路径未知")}"
                : "未安装";
            // 设置页按全局环境判定就绪：DSh 声明的 engines.node 与现有 Node
            // 不兼容时保持“未就绪”，让状态和不兼容提示可见，而不是隐藏准备按钮。
            var ready = _dshRuntime.IsAvailable && IsGlobalRuntimeReady(_nodeRuntime, _dshRuntime.NodeEngine);
            prepareButton.IsEnabled = !ready && !_isRuntimePrepareInProgress && !_isNodeDetectionInProgress;
            prepareMirrorButton.IsEnabled = prepareButton.IsEnabled;
            prepareButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            prepareMirrorButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        }

        refreshButton.Click += async (_, _) =>
        {
            await RefreshDshAsync();
            await RefreshNodeAsync();
            UpdateStatus();
        };
        prepareButton.Click += async (_, _) =>
        {
            // 设置/诊断页管理的是全局运行环境，不传实例目标；Source 专属的
            // 精简准备只发生在启动实例流程里。
            await PrepareRuntimeAsync("Node.js 官方源", NodeInstallService.OfficialDistBase, DshInstallService.OfficialRegistry, null);
            UpdateStatus();
        };
        prepareMirrorButton.Click += async (_, _) =>
        {
            await PrepareRuntimeAsync("npmmirror 国内镜像", NodeInstallService.MirrorDistBase, DshInstallService.ChinaRegistry, null);
            UpdateStatus();
        };

        _runtimePanelUpdateStatus = UpdateStatus;
        UpdateStatus();
        return panel;
    }

    private async Task<bool> PrepareRuntimeAsync(string sourceName, string nodeDistBase, string? npmRegistry, ManagerInstance? target)
    {
        if (_isRuntimePrepareInProgress)
        {
            return false;
        }

        if (_isNodeDetectionInProgress)
        {
            ShowNotice("Node.js 检测进行中，请稍候再准备运行环境。");
            return false;
        }

        var prepareNodeEngine = GetNodeEngineRequirement(target);
        if (_nodeRuntime.IsAvailable
            && !string.IsNullOrWhiteSpace(prepareNodeEngine)
            && _nodeRuntime.GetCompatibility(prepareNodeEngine) != NodeRuntimeCompatibility.Compatible)
        {
            System.Windows.MessageBox.Show(this,
                $"当前 Node.js {_nodeRuntime.VersionText} 与 DeepSeek Harness 要求（{prepareNodeEngine}）不兼容。\n\nLauncher 不会自动卸载现有 Node.js。请安装兼容版本后重试。",
                "运行环境不兼容",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        _isRuntimePrepareInProgress = true;
        OnPropertyChanged(nameof(CanStartInstance));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        var progressWindow = new RuntimeProgressWindow(this, cancellation);
        progressWindow.Show();
        try
        {
            var progress = new Progress<NodeDownloadProgress>(progressWindow.SetDownloadProgress);

            if (!_nodeRuntime.IsAvailable)
            {
                progressWindow.SetIndeterminate(false);
                progressWindow.SetStatus($"正在通过 {sourceName} 解析 Node.js 版本并下载安装程序…");
                var nodeResult = await _nodeInstaller.InstallAsync(
                    nodeDistBase,
                    progress,
                    onInstallStarted: () =>
                    {
                        progressWindow.SetInstallPhase(true);
                        SetRuntimeInstallPhase(true);
                    },
                    cancellation.Token,
                    prepareNodeEngine);
                if (!nodeResult.IsSuccess)
                {
                    progressWindow.SetStatus(nodeResult.Error ?? "Node.js 安装失败。");
                    System.Windows.MessageBox.Show(this, nodeResult.Error ?? "Node.js 安装失败。", "准备运行环境", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                progressWindow.SetStatus("Node.js 安装完成，正在重新检测…");
                for (var attempt = 0; attempt < 5 && !_nodeRuntime.IsAvailable; attempt++)
                {
                    await RefreshNodeAsync();
                    if (!_nodeRuntime.IsAvailable)
                    {
                        await Task.Delay(1000, cancellation.Token);
                    }
                }

                if (!_nodeRuntime.IsAvailable)
                {
                    progressWindow.SetStatus("Node.js 安装后仍未被检测到，请确认安装路径后重新检测。");
                    System.Windows.MessageBox.Show(this, "Node.js 安装后仍未被检测到，请确认安装路径后重新检测。", "准备运行环境", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            // MSI 只更新系统环境，当前 Launcher 进程的 PATH 仍是旧值；
            // 把新检测到的 Node 目录补到进程 PATH，DSh 检测/启动才能解析 node。
            EnsureNodeDirectoryOnPath(_nodeRuntime.ExecutablePath);

            progressWindow.SetInstallPhase(false);
            SetRuntimeInstallPhase(false);

            if (DshInstallService.ShouldInstallGlobalDSh(_dshRuntime.IsAvailable, target?.Kind))
            {
                progressWindow.SetIndeterminate(true);
                progressWindow.SetStatus(npmRegistry is null
                    ? "正在通过 npm 官方源安装 DeepSeek Harness…"
                    : "正在通过 npmmirror 国内镜像安装 DeepSeek Harness…");
                var dshResult = await _dshInstaller.InstallAsync(_nodeRuntime, npmRegistry, cancellation.Token);
                if (!dshResult.IsSuccess)
                {
                    progressWindow.SetStatus(dshResult.Error ?? "DSh 安装失败。");
                    System.Windows.MessageBox.Show(this, dshResult.Error ?? "DSh 安装失败。", "准备运行环境", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                progressWindow.SetStatus("DSh 安装完成，正在重新检测…");
                for (var attempt = 0; attempt < 5 && !_dshRuntime.IsAvailable; attempt++)
                {
                    await RefreshDshAsync();
                    if (!_dshRuntime.IsAvailable)
                    {
                        await Task.Delay(1000, cancellation.Token);
                    }
                }

                if (!_dshRuntime.IsAvailable)
                {
                    progressWindow.SetStatus("DSh 安装完成但未检测到可用的 dsh 命令，请重新检测或检查 npm 安装结果。");
                    System.Windows.MessageBox.Show(this, "DSh 安装完成但未检测到可用的 dsh 命令，请重新检测或检查 npm 安装结果。", "准备运行环境", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            // DSh 刚被安装/更新，或目标实例原绑定的运行目录已失效：把失效的
            // Installed 实例重绑定到当前检测到的 DSh，准备流程才能自愈“入口在、
            // 目录没了”的实例；无失效实例时这里是空操作。
            RebindInstalledInstancesToDetectedDSh();

            // 新检测到的 DSh metadata 可能声明与现有 Node 不兼容的 engines.node，
            // 成功提示前必须按最新要求复查，不能沿用准备开始前的结论。
            var finalRequirement = GetNodeEngineRequirement(target);
            if (_nodeRuntime.GetCompatibility(finalRequirement) != NodeRuntimeCompatibility.Compatible)
            {
                var message = $"Node.js {_nodeRuntime.VersionText} 与当前 DSh 要求（{finalRequirement ?? "未声明"}）不兼容。\n\n"
                    + "Launcher 不会自动卸载现有 Node.js。请安装满足要求的兼容版本后重试。";
                progressWindow.SetStatus(message);
                System.Windows.MessageBox.Show(this, message, "运行环境不兼容", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            progressWindow.SetStatus("运行环境已就绪。");
            ShowNotice(target?.Kind == InstanceKind.Source
                ? $"运行环境已准备完成：Node.js {_nodeRuntime.VersionText}。"
                : $"运行环境已准备完成：Node.js {_nodeRuntime.VersionText}，DSh {_dshRuntime.VersionText}。");
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowNotice("运行环境准备已取消。");
            return false;
        }
        catch (Exception ex)
        {
            ShowNotice($"准备运行环境失败：{ex.Message}");
            return false;
        }
        finally
        {
            // 无论成功、失败、取消还是超时，先恢复进度窗口与主窗口的可关闭状态，
            // 避免 MSI 安装阶段的关闭保护把窗口卡在屏幕上。
            progressWindow.SetInstallPhase(false);
            SetRuntimeInstallPhase(false);
            progressWindow.Close();
            _isRuntimePrepareInProgress = false;
            OnPropertyChanged(nameof(CanStartInstance));
            OnPropertyChanged(nameof(CanInstallDsh));
            OnPropertyChanged(nameof(DshInstallButtonText));
        }
    }

    internal static ManagerInstance? ResolveInstanceById(
        IEnumerable<ManagerInstance> instances,
        string instanceId) =>
        instances.FirstOrDefault(item => string.Equals(item.Id, instanceId, StringComparison.Ordinal));

    internal static bool IsRuntimeReadyAfterPreparation(
        NodeRuntimeInfo nodeRuntime,
        string? nodeEngineRequirement,
        ManagerInstance target)
    {
        if (!nodeRuntime.IsAvailable
            || nodeRuntime.GetCompatibility(nodeEngineRequirement) != NodeRuntimeCompatibility.Compatible)
        {
            return false;
        }

        // 入口 shim 仍在但 package 目录已删除的实例同样无法启动，
        // 必须一并视为运行目录失效（否则会被 Runner 的 RootPath 检查拒绝）。
        return target.Kind != InstanceKind.Installed
            || ((!string.IsNullOrWhiteSpace(target.DshExecutablePath) && File.Exists(target.DshExecutablePath))
                && DshRuntimeDetector.TryResolvePackageRoot(target.RootPath) is not null);
    }

    internal static bool IsGlobalRuntimeReady(NodeRuntimeInfo nodeRuntime, string? dshNodeEngine) =>
        nodeRuntime.IsAvailable
        && (string.IsNullOrWhiteSpace(dshNodeEngine)
            || nodeRuntime.GetCompatibility(dshNodeEngine) == NodeRuntimeCompatibility.Compatible);

    private void SetRuntimeInstallPhase(bool installing)
    {
        _blockWindowCloseForMsi = installing;
    }

    private static void EnsureNodeDirectoryOnPath(string? nodeExecutablePath)
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var updated = BuildPathWithNodeDirectory(nodeExecutablePath, currentPath);
        if (!string.Equals(currentPath, updated, StringComparison.Ordinal))
        {
            Environment.SetEnvironmentVariable("PATH", updated);
        }
    }

    internal static string BuildPathWithNodeDirectory(string? nodeExecutablePath, string currentPath)
    {
        if (string.IsNullOrWhiteSpace(nodeExecutablePath))
        {
            return currentPath;
        }

        var nodeDirectory = Path.GetDirectoryName(Path.GetFullPath(nodeExecutablePath));
        if (string.IsNullOrWhiteSpace(nodeDirectory))
        {
            return currentPath;
        }

        var entries = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static entry => entry.Trim().Trim('"'))
            .Where(static entry => entry.Length > 0)
            .ToList();
        if (entries.Contains(nodeDirectory, StringComparer.OrdinalIgnoreCase))
        {
            return currentPath;
        }

        return nodeDirectory + Path.PathSeparator + string.Join(Path.PathSeparator, entries);
    }

    private async Task<bool> EnsureRuntimeReadyAsync(ManagerInstance instance)
    {
        var requirement = GetNodeEngineRequirement(instance);
        var missing = new List<string>();
        var needsManualNode = false;

        if (!_nodeRuntime.IsAvailable)
        {
            missing.Add("Node.js 未安装（可一键准备）");
        }
        else if (_nodeRuntime.GetCompatibility(requirement) != NodeRuntimeCompatibility.Compatible)
        {
            missing.Add($"Node.js 版本不兼容（当前 {_nodeRuntime.VersionText}，要求 {(string.IsNullOrWhiteSpace(requirement) ? "未声明" : requirement)}）");
            needsManualNode = true;
        }

        if (instance.Kind == InstanceKind.Installed
            && ((string.IsNullOrWhiteSpace(instance.DshExecutablePath) || !File.Exists(instance.DshExecutablePath))
                || DshRuntimeDetector.TryResolvePackageRoot(instance.RootPath) is null))
        {
            missing.Add("DeepSeek Harness 入口或运行目录缺失（可一键修复）");
        }

        if (missing.Count == 0)
        {
            return true;
        }

        if (needsManualNode)
        {
            var message = "当前 Node.js 版本与 DeepSeek Harness 要求不兼容。\n\n"
                + string.Join("\n", missing.Select(item => "• " + item))
                + $"\n\nLauncher 不会自动卸载现有 Node.js。请安装满足要求（{requirement}）的兼容版本后重试。";
            System.Windows.MessageBox.Show(this, message, "运行环境不兼容", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var confirm = System.Windows.MessageBox.Show(
            this,
            "运行环境缺失：\n\n" + string.Join("\n", missing.Select(item => "• " + item))
            + "\n\n是否现在准备运行环境？准备完成后将自动继续启动实例。",
            "准备运行环境",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return false;
        }

        if (!await PrepareRuntimeAsync("Node.js 官方源", NodeInstallService.OfficialDistBase, DshInstallService.OfficialRegistry, instance))
        {
            return false;
        }

        // 重新安装/重绑定后按最初目标 ID 重新解析实例，并重新读取其 engines.node；
        // 不能用准备开始前缓存的 requirement 判断最终兼容性。
        var current = ResolveInstanceById(Instances, instance.Id);
        if (current is null)
        {
            ShowNotice("目标实例已被删除，无法继续启动。");
            return false;
        }

        var currentRequirement = GetNodeEngineRequirement(current);
        if (!IsRuntimeReadyAfterPreparation(_nodeRuntime, currentRequirement, current))
        {
            ShowNotice("运行环境仍未就绪，请重新检测或手动安装后重试。");
            return false;
        }

        return true;
    }
    private void RebindInstalledInstancesToDetectedDSh()
    {
        foreach (var instance in Instances.Where(static item => item.Kind == InstanceKind.Installed).ToArray())
        {
            var rebound = InstanceRuntimeRebinder.RebindInstalledInstance(instance, _dshRuntime);
            if (rebound is not null)
            {
                UpdateInstance(rebound);
            }
        }
    }

    private void SetNavigationSelection(string section)
    {
        var buttons = new[]
        {
            NavigationHome,
            NavigationExtensions,
            NavigationAgent,
            NavigationConversations,
            NavigationSettings
        };
        foreach (var button in buttons)
        {
            button.Background = WpfBrushes.Transparent;
            button.Foreground = WpfBrushes.White;
        }

        var selected = buttons.FirstOrDefault(button => string.Equals(button.Tag as string, section, StringComparison.Ordinal));
        if (selected is not null)
        {
            selected.Background = WpfBrushes.White;
            selected.Foreground = (WpfBrush)FindResource("BlueBrush");
        }
    }

    private void AddInstance_Click(object sender, RoutedEventArgs e)
    {
        var selectedDirectory = PickFolder("选择已安装 DSh 的目录", _dshRuntime.PackageRoot);
        if (selectedDirectory is null)
        {
            return;
        }

        var packageRoot = DshRuntimeDetector.TryResolvePackageRoot(selectedDirectory);
        if (packageRoot is null && _dshRuntime.ExecutablePath is not null)
        {
            packageRoot = DshRuntimeDetector.TryFindPackageRoot(_dshRuntime.ExecutablePath);
        }

        if (packageRoot is null)
        {
            ShowNotice("没有在所选目录找到 @deepseek-ai/dsh 的 package.json。请选择 DSh 安装目录或其上级运行目录。");
            return;
        }

        try
        {
            var executablePath = DshRuntimeDetector.FindExecutableForPackageRoot(packageRoot)
                ?? (_dshRuntime.PackageRoot is not null && string.Equals(_dshRuntime.PackageRoot, packageRoot, StringComparison.OrdinalIgnoreCase)
                    ? _dshRuntime.ExecutablePath
                    : null);
            var packageVersion = DshRuntimeDetector.TryReadPackageVersion(packageRoot) ?? _dshRuntime.Version;
            var instance = _instanceRegistry.Register(
                "DSh " + (packageVersion ?? "installed"),
                packageRoot,
                InstanceKind.Installed,
                executablePath,
                packageVersion,
                "npm");
            Instances.Add(instance);
            SelectedInstance = instance;
            OnPropertyChanged(nameof(InstanceCountText));
            OnPropertyChanged(nameof(NoInstancesVisibility));
            OnPropertyChanged(nameof(InstancesVisibility));
            RefreshRunningInstances();
            ShowNotice($"已注册 installed 实例：{instance.Name}。实例 DSH_HOME 已隔离到 {instance.DshHome}。");
        }
        catch (Exception ex)
        {
            ShowNotice($"注册 installed 实例失败：{ex.Message}");
        }
    }

    private void ImportSource_Click(object sender, RoutedEventArgs e)
    {
        var selectedDirectory = PickFolder("选择 DeepSeek Harness Source 项目");
        if (selectedDirectory is null)
        {
            return;
        }

        var project = _sourceInspector.Inspect(selectedDirectory);
        if (!project.IsValid || !project.IsDshSource)
        {
            ShowNotice(project.Error ?? $"Source 项目识别失败：{project.StatusText}。");
            return;
        }

        try
        {
            var instance = _instanceRegistry.Register(
                project.Name ?? new DirectoryInfo(project.RootPath).Name,
                project.RootPath,
                InstanceKind.Source,
                packageManager: project.PackageManager);
            Instances.Add(instance);
            SelectedInstance = instance;
            OnPropertyChanged(nameof(InstanceCountText));
            OnPropertyChanged(nameof(NoInstancesVisibility));
            OnPropertyChanged(nameof(InstancesVisibility));
            RefreshRunningInstances();
            ShowNotice($"已注册 Source 实例：{instance.Name}。包管理器：{project.PackageManager}；状态：{project.StatusText}。");
        }
        catch (Exception ex)
        {
            ShowNotice($"注册 Source 实例失败：{ex.Message}");
        }
    }

    private async void StartInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_isLifecycleInProgress || SelectedInstance is null)
        {
            return;
        }

        var selected = SelectedInstance;
        if (_instanceRunner.IsRunning(selected.Id)
            || selected.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            if (!string.IsNullOrWhiteSpace(selected.WebUrl))
            {
                if (!TryFocusChatWindow(selected.Id))
                {
                    OpenChatWindow(selected.Id, selected.WebUrl);
                }

                ShowNotice($"实例仍在运行，已重新打开：{selected.Name}。关闭窗口不会停止实例。 ");
            }
            else
            {
                ShowNotice("实例仍在运行，但没有可用的 Web 地址，暂时不能重新打开窗口。 ");
            }
            return;
        }

        if (!await EnsureRuntimeReadyAsync(selected))
        {
            return;
        }

        // runtime 准备窗口是非模态的，期间用户可能切换 SelectedInstance 或删除目标；
        // 必须按最初点击的实例 ID 重新解析（重绑定后取最新状态），目标不存在则中止。
        selected = ResolveInstanceById(Instances, selected.Id);
        if (selected is null)
        {
            ShowNotice("目标实例已被删除，无法继续启动。");
            return;
        }
        SetLifecycleBusy(true);
        try
        {
            var result = await StartManagedInstanceAsync(selected);
            if (result is null)
            {
                return;
            }

            if (!result.IsSuccess || result.ProcessId is null || result.Port is null || result.WebUrl is null)
            {
                ShowNotice(result.Error ?? "DSh 启动失败。");
                return;
            }

            OpenChatWindow(selected.Id, result.WebUrl);
            ShowNotice($"实例已启动：{selected.Name}，运行地址 {result.WebUrl}。健康检查已通过。");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
            // Window close cancels startup and the runner cleans up its process tree.
        }
        catch (Exception ex)
        {
            UpdateInstanceStatus(selected, InstanceRuntimeStatus.Error, ex.Message);
            ShowNotice($"启动 DSh 失败：{ex.Message}");
        }
        finally
        {
            SetLifecycleBusy(false);
        }
    }

    private async void StopInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_isLifecycleInProgress || SelectedInstance is null)
        {
            return;
        }

        var selected = SelectedInstance;
        if (selected.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            ShowNotice("当前实例连接的是外部 DSh 服务，Launcher 不会停止该进程。");
            return;
        }

        SetLifecycleBusy(true);
        try
        {
            var result = await _instanceRunner.StopAsync(selected.Id, _windowCancellation.Token);
            if (!result.IsSuccess)
            {
                UpdateInstanceStatus(selected, InstanceRuntimeStatus.Error, result.Error);
                ShowNotice(result.Error ?? "停止 DSh 失败。");
                return;
            }

            CloseChatWindow(selected.Id);
            UpdateInstance(selected with
            {
                RuntimeStatus = InstanceRuntimeStatus.Stopped,
                RuntimeOwnership = InstanceRuntimeOwnership.None,
                ProcessId = null,
                Port = null,
                WebUrl = null,
                LastError = null
            });
            await SynchronizeConversationsAsync(selected with
            {
                RuntimeStatus = InstanceRuntimeStatus.Stopped,
                RuntimeOwnership = InstanceRuntimeOwnership.None,
                ProcessId = null,
                Port = null,
                WebUrl = null,
                LastError = null
            });
            ShowNotice($"实例已停止：{selected.Name}。");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowNotice($"停止 DSh 失败：{ex.Message}");
        }
        finally
        {
            SetLifecycleBusy(false);
        }
    }

    private async void RestartInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_isLifecycleInProgress || SelectedInstance is null)
        {
            return;
        }

        var selected = SelectedInstance;
        if (selected.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            ShowNotice("当前实例连接的是外部 DSh 服务，Launcher 不会重启该进程。");
            return;
        }

        SetLifecycleBusy(true);
        try
        {
            if (_instanceRunner.IsRunning(selected.Id))
            {
                var stopped = await _instanceRunner.StopAsync(selected.Id, _windowCancellation.Token);
                if (!stopped.IsSuccess)
                {
                    UpdateInstanceStatus(selected, InstanceRuntimeStatus.Error, stopped.Error);
                    ShowNotice(stopped.Error ?? "重启前停止失败。");
                    return;
                }

                CloseChatWindow(selected.Id);
                selected = selected with
                {
                    RuntimeStatus = InstanceRuntimeStatus.Stopped,
                    RuntimeOwnership = InstanceRuntimeOwnership.None,
                    ProcessId = null,
                    Port = null,
                    WebUrl = null,
                    LastError = null
                };
                UpdateInstance(selected);
                await SynchronizeConversationsAsync(selected);
            }

            var result = await StartManagedInstanceAsync(selected);
            if (result is null)
            {
                return;
            }
            if (!result.IsSuccess || result.ProcessId is null || result.Port is null || result.WebUrl is null)
            {
                UpdateInstanceStatus(selected, InstanceRuntimeStatus.Error, result.Error);
                ShowNotice(result.Error ?? "DSh 重启失败。");
                return;
            }

            UpdateInstance(selected with
            {
                RuntimeStatus = InstanceRuntimeStatus.Running,
                RuntimeOwnership = InstanceRuntimeOwnership.Managed,
                ProcessId = result.ProcessId,
                Port = result.Port,
                WebUrl = result.WebUrl,
                LastError = null
            });
            OpenChatWindow(selected.Id, result.WebUrl);
            ShowNotice($"实例已重启：{selected.Name}，运行地址 {result.WebUrl}。");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            UpdateInstanceStatus(selected, InstanceRuntimeStatus.Error, ex.Message);
            ShowNotice($"重启 DSh 失败：{ex.Message}");
        }
        finally
        {
            SetLifecycleBusy(false);
        }
    }

    private void InstallNode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://nodejs.org/en/download",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowNotice($"无法打开 Node.js 官方安装页：{ex.Message}");
        }
    }

    private void InstallNodeMirror_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://npmmirror.com/mirrors/node/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowNotice($"无法打开 Node.js 国内镜像页：{ex.Message}");
        }
    }

    private async void InstallDsh_Click(object sender, RoutedEventArgs e)
    {
        await InstallDshAsync(DshInstallService.OfficialRegistry, "npm 官方源");
    }

    private async void InstallDshMirror_Click(object sender, RoutedEventArgs e)
    {
        await InstallDshAsync(DshInstallService.ChinaRegistry, "npmmirror 国内镜像");
    }

    private async Task InstallDshAsync(string registry, string sourceName)
    {
        if (_isDshInstallInProgress)
        {
            return;
        }

        if (!_nodeRuntime.IsAvailable)
        {
            ShowNotice("当前没有可用的 Node.js。请先安装 Node.js，再执行 DSh 安装。");
            InstallNode_Click(this, new RoutedEventArgs());
            return;
        }

        _isDshInstallInProgress = true;
        OnPropertyChanged(nameof(CanInstallDsh));
        OnPropertyChanged(nameof(DshInstallButtonText));
        ShowNotice($"正在使用当前 Node.js 通过 {sourceName} 执行 npm install --global @deepseek-ai/dsh，请稍候…");

        try
        {
            var result = await _dshInstaller.InstallAsync(_nodeRuntime, registry, _windowCancellation.Token);
            if (!result.IsSuccess)
            {
                ShowNotice(result.Error ?? "DSh 安装失败。");
                return;
            }

            await RefreshDshAsync();
            ShowNotice($"DSh 安装/更新完成：{_dshRuntime.VersionText}。可以重新检测并注册实例。");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowNotice($"DSh 安装失败：{ex.Message}");
        }
        finally
        {
            _isDshInstallInProgress = false;
            OnPropertyChanged(nameof(CanInstallDsh));
            OnPropertyChanged(nameof(DshInstallButtonText));
        }
    }

    private void UpdateInstanceStatus(ManagerInstance original, InstanceRuntimeStatus status, string? error)
    {
        try
        {
            UpdateInstance(original with
            {
                RuntimeStatus = status,
                RuntimeOwnership = status == InstanceRuntimeStatus.Running
                    ? original.RuntimeOwnership
                    : InstanceRuntimeOwnership.None,
                ProcessId = status == InstanceRuntimeStatus.Running ? original.ProcessId : null,
                Port = status == InstanceRuntimeStatus.Running ? original.Port : null,
                WebUrl = status == InstanceRuntimeStatus.Running ? original.WebUrl : null,
                LastError = error
            });
        }
        catch (Exception updateException)
        {
            ShowNotice($"实例状态保存失败：{updateException.Message}");
        }
    }

    private async Task<bool> PrepareSourceAsync(ManagerInstance instance)
    {
        if (instance.Kind != InstanceKind.Source)
        {
            return true;
        }

        var project = _sourceInspector.Inspect(instance.RootPath);
        var result = await _sourceBuilder.PrepareAsync(project, _nodeRuntime, _windowCancellation.Token);
        if (result.IsSuccess)
        {
            ShowNotice($"Source 依赖和构建已完成：{Path.GetFileName(result.EntrypointPath!)}。正在启动实例…");
            return true;
        }

        UpdateInstanceStatus(instance, InstanceRuntimeStatus.Error, result.Error);
        var outputSuffix = string.IsNullOrWhiteSpace(result.Output)
            ? string.Empty
            : $" 输出：{result.Output}";
        ShowNotice((result.Error ?? "Source 准备失败。") + outputSuffix);
        return false;
    }

    private async Task<DshInstanceRunResult?> StartManagedInstanceAsync(ManagerInstance instance)
    {
        await SynchronizeModelProvidersAsync(instance);
        await SynchronizeConversationsAsync(instance);
        if (!await PrepareSourceAsync(instance))
        {
            return null;
        }

        var result = await _instanceRunner.StartAsync(
            instance,
            instance.Kind == InstanceKind.Source ? _nodeRuntime : null,
            _windowCancellation.Token);
        if (!result.IsSuccess || result.ProcessId is null || result.Port is null || result.WebUrl is null)
        {
            UpdateInstanceStatus(instance, InstanceRuntimeStatus.Error, result.Error);
            return result;
        }

        UpdateInstance(instance with
        {
            RuntimeStatus = InstanceRuntimeStatus.Running,
            RuntimeOwnership = InstanceRuntimeOwnership.Managed,
            ProcessId = result.ProcessId,
            Port = result.Port,
            WebUrl = result.WebUrl,
            LastError = null
        });
        return result;
    }

    private void UpdateInstance(ManagerInstance updated)
    {
        _instanceRegistry.Update(updated);
        var wasSelected = string.Equals(SelectedInstance?.Id, updated.Id, StringComparison.Ordinal);
        var index = Instances.ToList().FindIndex(instance => string.Equals(instance.Id, updated.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            Instances[index] = updated;
        }

        if (wasSelected)
        {
            SelectedInstance = updated;
        }

        OnPropertyChanged(nameof(SelectedInstanceStatus));
        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(StartInstanceButtonText));
        OnPropertyChanged(nameof(CanStopInstance));
        OnPropertyChanged(nameof(CanRestartInstance));
        OnPropertyChanged(nameof(InstanceEndpointText));
        OnPropertyChanged(nameof(NodeStatusText));
        OnPropertyChanged(nameof(NodeStatusBrush));
        OnPropertyChanged(nameof(NodeVersionText));
        RefreshRunningInstances();
    }

    private void SetLifecycleBusy(bool isBusy)
    {
        _isLifecycleInProgress = isBusy;
        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(CanStopInstance));
        OnPropertyChanged(nameof(CanRestartInstance));
        OnPropertyChanged(nameof(InstanceEndpointText));
    }

    private static string? PickFolder(string description, string? initialPath = null)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = !string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)
                ? initialPath
                : string.Empty
        };

        return dialog.ShowDialog() == Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed
            && !IsInsideButton(e.OriginalSource as DependencyObject))
        {
            DragMove();
        }
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Button)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void Brand_Click(object sender, RoutedEventArgs e)
    {
        SwitchSection("启动");
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_blockWindowCloseForMsi)
        {
            e.Cancel = true;
            ShowNotice("Node.js 系统安装正在进行，请等待安装完成后再关闭 Launcher。");
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _providerRefreshCancellation?.Cancel();
        _providerRefreshCancellation?.Dispose();
        _providerRefreshCancellation = null;
        _windowCancellation.Cancel();
        CloseAllChatWindows();
        try
        {
            _instanceRunner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Window shutdown must not be blocked by a failed child-process cleanup.
        }
        _providerDiagnosticService.Dispose();
        _windowCancellation.Dispose();
        base.OnClosed(e);
    }

    private void OpenChatWindow(string instanceId, string address, string? conversationId = null)
    {
        CloseChatWindow(instanceId);
        try
        {
            var chat = new ChatWindow(address, conversationId);
            _chatWindows[instanceId] = chat;
            chat.Closed += (_, _) =>
            {
                if (_chatWindows.TryGetValue(instanceId, out var current)
                    && ReferenceEquals(current, chat))
                {
                    _chatWindows.Remove(instanceId);
                }
            };
            chat.Show();
        }
        catch (Exception ex)
        {
            ShowNotice($"Chat 窗口无法打开：{ex.Message}。Launcher 和实例仍保持运行。");
        }
    }

    private bool TryFocusChatWindow(string instanceId)
    {
        if (!_chatWindows.TryGetValue(instanceId, out var chat) || !chat.IsVisible)
        {
            return false;
        }

        if (chat.WindowState == WindowState.Minimized)
        {
            chat.WindowState = WindowState.Normal;
        }

        chat.Activate();
        return true;
    }

    private void CloseChatWindow(string instanceId)
    {
        if (!_chatWindows.Remove(instanceId, out var chat))
        {
            return;
        }

        try
        {
            chat.Close();
        }
        catch
        {
            // A closing Chat window must not prevent Launcher shutdown or DSh cleanup.
        }
    }

    private void CloseAllChatWindows()
    {
        var chats = _chatWindows.Values.ToArray();
        _chatWindows.Clear();
        foreach (var chat in chats)
        {
            try
            {
                chat.Close();
            }
            catch
            {
                // A closing Chat window must not prevent Launcher shutdown.
            }
        }
    }

    private async Task SynchronizeConversationsAsync(ManagerInstance focus)
    {
        try
        {
            var versions = Instances.ToArray();
            var result = await Task.Run(() =>
                _conversationSyncService.Synchronize(focus, versions));
            if (result.CopiedFiles > 0)
            {
                ShowNotice($"已按版本对话策略同步 {result.CopiedFiles} 个会话文件。 ");
            }

            if (result.HasErrors)
            {
                ShowNotice($"对话同步完成，但有 {result.Errors.Count} 个文件未处理：{result.Errors[0]}");
            }
        }
        catch (Exception ex)
        {
            ShowNotice($"对话同步失败：{ex.Message}");
        }
    }

    private async Task SynchronizeModelProvidersAsync(
        ManagerInstance focus,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var versions = Instances.ToArray();
            var result = await Task.Run(
                () => _modelProviderSyncService.Synchronize(focus, versions),
                cancellationToken);
            if (result.CopiedVersions > 0)
            {
                ShowNotice($"已按版本设置同步模型 Provider 到 {result.CopiedVersions} 个版本。 ");
            }

            if (result.HasErrors)
            {
                ShowNotice($"模型 Provider 同步完成，但有 {result.Errors.Count} 个版本未处理：{result.Errors[0]}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowNotice($"模型 Provider 同步失败：{ex.Message}");
        }
    }

    private async Task PropagateConversationDeletionAsync(ManagerInstance focus, string relativePath)
    {
        try
        {
            var versions = Instances.ToArray();
            var result = await Task.Run(() =>
                _conversationSyncService.PropagateDeletion(focus, relativePath, versions));
            if (result.HasErrors)
            {
                ShowNotice($"会话已从当前版本删除，但有 {result.Errors.Count} 个版本未同步删除：{result.Errors[0]}");
            }
        }
        catch (Exception ex)
        {
            ShowNotice($"同步删除会话失败：{ex.Message}");
        }
    }

    private async Task<bool> OpenConversationAsync(ManagerInstance owner, ConversationEntry entry)
    {
        var instance = Instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, owner.Id, StringComparison.Ordinal)) ?? owner;
        if (instance is null
            || entry.SessionId is null
            || string.IsNullOrWhiteSpace(instance.Id))
        {
            return false;
        }

        SelectedInstance = instance;
        if (_instanceRunner.IsRunning(instance.Id)
            || instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            if (string.IsNullOrWhiteSpace(instance.WebUrl))
            {
                return false;
            }

            OpenChatWindow(instance.Id, instance.WebUrl, entry.SessionId);
            return true;
        }

        if (_isLifecycleInProgress)
        {
            ShowNotice("实例正在执行启动或停止操作，请稍候再打开对话。");
            return false;
        }

        // 对话触发的自动启动同样先做运行环境准备，缺 Node/DSh 时提供一键准备。
        if (!await EnsureRuntimeReadyAsync(instance))
        {
            return false;
        }

        var resolvedTarget = ResolveInstanceById(Instances, instance.Id);
        if (resolvedTarget is null)
        {
            ShowNotice("目标实例已被删除，无法打开对话。");
            return false;
        }

        instance = resolvedTarget;
        SetLifecycleBusy(true);
        try
        {
            var result = await StartManagedInstanceAsync(instance);
            if (result is null)
            {
                return false;
            }

            if (!result.IsSuccess || result.WebUrl is null)
            {
                ShowNotice(result.Error ?? "无法启动实例，暂时不能打开对话。");
                return false;
            }

            OpenChatWindow(instance.Id, result.WebUrl, entry.SessionId);
            ShowNotice($"实例已启动，正在打开对话：{entry.SessionId}。 ");
            return true;
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            UpdateInstanceStatus(instance, InstanceRuntimeStatus.Error, ex.Message);
            ShowNotice($"启动实例后打开对话失败：{ex.Message}");
            return false;
        }
        finally
        {
            SetLifecycleBusy(false);
        }
    }

    private void ShowNotice(string message)
    {
        PageNotice = message;
        PageNoticeVisibility = Visibility.Visible;
        OnPropertyChanged(nameof(PageNotice));
        OnPropertyChanged(nameof(PageNoticeVisibility));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
