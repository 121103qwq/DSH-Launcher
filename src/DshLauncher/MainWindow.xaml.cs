using System.Diagnostics;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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
    private const int WindowMessageNonClientHitTest = 0x0084;
    private const int HitTestClient = 1;
    private const int HitTestLeft = 10;
    private const int HitTestRight = 11;
    private const int HitTestTop = 12;
    private const int HitTestTopLeft = 13;
    private const int HitTestTopRight = 14;
    private const int HitTestBottom = 15;
    private const int HitTestBottomLeft = 16;
    private const int HitTestBottomRight = 17;
    private const double ResizeHitBorder = 5;
#if DEBUG
    private const string TestHideNodeVariable = "DSH_LAUNCHER_TEST_HIDE_NODE";
    private const string TestHideDshVariable = "DSH_LAUNCHER_TEST_HIDE_DSH";
#endif
    private readonly NodeRuntimeDetector _nodeDetector = new();
    private readonly DshRuntimeDetector _dshDetector = new();
    private readonly SourceProjectInspector _sourceInspector = new();
    private readonly InstanceRegistry _instanceRegistry = new();
    private readonly DshInstanceRunner _instanceRunner = new();
    private readonly ExtensionService _extensionService;
    private readonly MarketplaceService _marketplaceService;
    private readonly SkillMarketService _skillMarketService;
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
    private readonly Services.LifecycleBusyGuard _lifecycleGuard = new();
    private bool _isLifecycleInProgress => _lifecycleGuard.IsBusy;
    private bool _isDshInstallInProgress;
    private bool _isRuntimePrepareInProgress;
    private bool _isProviderDetectionInProgress;
    private bool _blockWindowCloseForMsi;
    private bool _instancesLoadedSuccessfully;
    private bool _firstRunSetupPromptShown;
    private Action? _runtimePanelUpdateStatus;
    private HwndSource? _windowSource;

    public MainWindow()
    {
        _extensionService = new(id => _instanceRunner.IsRunning(id));
        _marketplaceService = new();
        _skillMarketService = new(_extensionService);
        _versionPackageService = new(_instanceRegistry);
        _conversationService = new(isRunning: id => _instanceRunner.IsRunning(id));
        _conversationSyncService = new(_versionSettingsService, id => _instanceRunner.IsRunning(id));
        _modelService = new(id => _instanceRunner.IsRunning(id));
        _modelProviderSyncService = new(
            _versionSettingsService,
            _modelService,
            _providerStateService,
            id => _instanceRunner.IsRunning(id));
        // 超时的 msiexec 在后台运行期间阻止关闭 Launcher：不跨进程持久化标记，
        // 用“无法优雅关闭”保证 Launcher 重开后不会出现第二次 Node MSI 与残留安装重叠。
        _nodeInstaller.LingeringInstallerCompleted += OnLingeringInstallerCompleted;
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
            : ProviderCards.Count == 0
                ? "未检测到实例 settings.yaml 中的 llm Provider 配置"
                : $"{ProviderCards.Count} 个 Provider · 仅调用只读模型列表接口";

    public bool CanRefreshProviders => !_isProviderDetectionInProgress && SelectedInstance is not null;

    public string SelectedInstanceName => SelectedInstance?.Name ?? "尚未创建版本";

    public string SelectedInstanceSummary => SelectedInstance is null
        ? "按首次运行引导准备环境并创建第一个版本。"
        : $"{SelectedInstance.KindText} · {SelectedInstance.RootPath}";

    public string SelectedInstanceStatus => SelectedInstance?.StatusText ?? "未选择";

    public bool CanStartInstance => SelectedInstance is null
        ? CanStartFirstVersionSetupCore(
            _isLifecycleInProgress,
            _isRuntimePrepareInProgress,
            _isNodeDetectionInProgress,
            Instances.Count,
            _instancesLoadedSuccessfully)
        : CanStartInstanceCore(
            _isLifecycleInProgress,
            _isRuntimePrepareInProgress,
            _isNodeDetectionInProgress,
            hasSelection: true,
            _instanceRunner.IsRunning(SelectedInstance.Id)
                && string.IsNullOrWhiteSpace(SelectedInstance.WebUrl));

    internal static bool CanStartFirstVersionSetupCore(
        bool lifecycleInProgress,
        bool runtimePrepareInProgress,
        bool nodeDetectionInProgress,
        int instanceCount,
        bool instancesLoadedSuccessfully) =>
        !lifecycleInProgress
        && !runtimePrepareInProgress
        && !nodeDetectionInProgress
        && instancesLoadedSuccessfully
        && instanceCount == 0;

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

    public string StartInstanceButtonText => SelectedInstance is null
        ? "准备首个版本"
        : _instanceRunner.IsRunning(SelectedInstance.Id)
            ? "打开实例"
            : "启动实例";

    public bool CanStopInstance => CanStopInstanceCore(
        _isLifecycleInProgress,
        _isRuntimePrepareInProgress,
        SelectedInstance is not null
            && _instanceRunner.IsManaged(SelectedInstance.Id));

    public bool CanRestartInstance => CanStopInstance;

    internal static bool CanStopInstanceCore(
        bool lifecycleInProgress,
        bool runtimePrepareInProgress,
        bool isManaged) =>
        !lifecycleInProgress
        && !runtimePrepareInProgress
        && isManaged;

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
        try
        {
            await RefreshDshAsync();
            await LoadInstancesAsync();
            SwitchSection("启动");
            await RefreshNodeAsync();
            await RefreshProvidersAsync(SelectedInstance);
            await PromptFirstRunSetupIfNeededAsync();
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowNotice($"初始化失败：{ex.Message}");
        }
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
            _nodeRuntime = IsRuntimeHiddenForBootstrapTest(TestRuntimeKind.Node)
                ? NodeRuntimeInfo.Missing("隔离测试模式：已对当前 Launcher 进程隐藏本机 Node.js。")
                : await _nodeDetector.DetectAsync(preferredNodePath, _windowCancellation.Token);
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
            _dshRuntime = IsRuntimeHiddenForBootstrapTest(TestRuntimeKind.Dsh)
                ? DshRuntimeInfo.Missing("隔离测试模式：已对当前 Launcher 进程隐藏本机 DeepSeek Harness。")
                : await _dshDetector.DetectAsync(
                    GetConfiguredDshInstallDirectory(),
                    _windowCancellation.Token);
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
        // 注意：被动检测不触发重绑定。实例 root 位于临时不可用的卷（可移动盘/
        // 网络盘断开）时按“失效”持久化改写注册，会在卷恢复后丢失原 runtime 选择；
        // 重绑定只发生在用户确认的准备/修复流程（PrepareRuntimeAsync）。
    }

    private string? GetConfiguredDshInstallDirectory() =>
        _versionSettingsService.ReadLauncherSettings().DshInstallDirectory;

    private enum TestRuntimeKind
    {
        Node,
        Dsh
    }

    private static bool IsRuntimeHiddenForBootstrapTest(TestRuntimeKind runtime)
    {
#if DEBUG
        var variable = runtime == TestRuntimeKind.Node
            ? TestHideNodeVariable
            : TestHideDshVariable;
        return string.Equals(
            Environment.GetEnvironmentVariable(variable),
            "1",
            StringComparison.Ordinal);
#else
        return false;
#endif
    }

    private static void RevealRuntimeAfterBootstrap(TestRuntimeKind runtime)
    {
#if DEBUG
        Environment.SetEnvironmentVariable(
            runtime == TestRuntimeKind.Node ? TestHideNodeVariable : TestHideDshVariable,
            null);
#endif
    }

    internal static bool IsPreferredDshRuntimeReady(
        DshRuntimeInfo runtime,
        string? preferredInstallDirectory) =>
        runtime.IsAvailable
        && (string.IsNullOrWhiteSpace(preferredInstallDirectory)
            || DshRuntimeDetector.IsExecutableInInstallDirectory(
                runtime.ExecutablePath,
                preferredInstallDirectory));

    private async Task LoadInstancesAsync()
    {
        _instancesLoadedSuccessfully = false;
        try
        {
            Instances.Clear();
            foreach (var storedInstance in _instanceRegistry.Load())
            {
                var instance = storedInstance;
                if (storedInstance.RuntimeStatus == InstanceRuntimeStatus.Running
                    && await _instanceRunner.TryAdoptRunningProcessAsync(storedInstance, _windowCancellation.Token))
                {
                    // 上次 Launcher 异常退出遗留的受管实例：按记录的 PID/端口收编回
                    // Managed，Stop/Restart/删除保持可用；只读 Attached 只留给真正
                    // 由外部启动的服务。
                    instance = storedInstance with
                    {
                        RuntimeOwnership = InstanceRuntimeOwnership.Managed,
                        LastError = null
                    };
                }
                else if (storedInstance.RuntimeStatus == InstanceRuntimeStatus.Running
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
            _instancesLoadedSuccessfully = true;
            OnPropertyChanged(nameof(CanStartInstance));
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
        // 取消源字段可能被并发刷新或 OnClosed 释放；入口代码位于 try 之外，
        // 必须自防 ObjectDisposedException，否则会穿透 async void 调用方导致崩溃。
        var previous = _providerRefreshCancellation;
        _providerRefreshCancellation = null;
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        previous?.Dispose();

        CancellationTokenSource cancellation;
        try
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        }
        catch (ObjectDisposedException)
        {
            // 窗口已进入关闭流程。
            return;
        }

        _providerRefreshCancellation = cancellation;
        _isProviderDetectionInProgress = instance is not null;
        ProviderCards.Clear();
        OnPropertyChanged(nameof(NoProvidersVisibility));
        OnPropertyChanged(nameof(ProviderCardsVisibility));
        OnPropertyChanged(nameof(ProviderSummaryText));
        OnPropertyChanged(nameof(CanRefreshProviders));
        UpdateProviderSectionVisibility();

        if (instance is null)
        {
            _providerRefreshCancellation = null;
            cancellation.Dispose();
            _isProviderDetectionInProgress = false;
            OnPropertyChanged(nameof(ProviderSummaryText));
            OnPropertyChanged(nameof(CanRefreshProviders));
            UpdateProviderSectionVisibility();
            return;
        }

        try
        {
            await SynchronizeModelProvidersAsync(instance, cancellation.Token);
            var states = _providerStateService.Read(instance);
            var providers = _modelService.Read(instance);
            // 启动页只显示实际配置过的 Provider：settings.yaml 没有 llm 段时不
            // 显示占位默认卡，全新实例显示空状态而不是假列表。
            foreach (var provider in providers.Where(static item => item.Configured))
            {
                var isEnabled = !states.TryGetValue(provider.Provider, out var storedEnabled) || storedEnabled;
                ProviderCards.Add(new ProviderCardViewModel(provider, isEnabled));
            }

            OnPropertyChanged(nameof(NoProvidersVisibility));
            OnPropertyChanged(nameof(ProviderCardsVisibility));
            OnPropertyChanged(nameof(ProviderSummaryText));
            UpdateProviderSectionVisibility();

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
                UpdateProviderSectionVisibility();
            }

            cancellation.Dispose();
        }
    }

    private void UpdateProviderSectionVisibility()
    {
        // 经 DSh 登录页连接的 Provider 不写入 settings.yaml 的 llm 段，真实实例
        // 通常没有可展示的 Provider；此时整块隐藏，不长期显示"未检测到"。
        var visible = _isProviderDetectionInProgress || ProviderCards.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProviderHeaderGrid.Visibility = visible;
        ProviderListGrid.Visibility = visible;
        ProviderSeparator.Visibility = visible;
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

    private void SwitchContextInstance(ManagerInstance target, string section)
    {
        SelectedInstance = target;
        SwitchSection(section);
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
                ScrollMouseWheel(viewer, e.Delta);
                e.Handled = true;
                return;
            }

            current = GetParentObject(current);
        }

        if (CanScroll(MainScrollViewer, e.Delta))
        {
            ScrollMouseWheel(MainScrollViewer, e.Delta);
            e.Handled = true;
        }
    }

    private static void ScrollMouseWheel(ScrollViewer viewer, int delta)
    {
        if (viewer.CanContentScroll)
        {
            var lines = Math.Max(1, Math.Abs(delta) / Mouse.MouseWheelDeltaForOneLine);
            for (var index = 0; index < lines; index++)
            {
                if (delta > 0)
                {
                    viewer.LineUp();
                }
                else
                {
                    viewer.LineDown();
                }
            }

            return;
        }

        viewer.ScrollToVerticalOffset(viewer.VerticalOffset - delta / 3.0);
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
                    "扩展" => new ExtensionWindow(
                        instance,
                        _extensionService,
                        () => _nodeRuntime,
                        marketplaceService: _marketplaceService,
                        instances: Instances.ToArray(),
                        selectInstance: candidate => SwitchContextInstance(candidate, section)),
                    "Agent" => new ExtensionWindow(
                        instance,
                        _extensionService,
                        () => _nodeRuntime,
                        agentOnly: true,
                        marketplaceService: _marketplaceService,
                        instances: Instances.ToArray(),
                        selectInstance: candidate => SwitchContextInstance(candidate, section),
                        skillMarketService: _skillMarketService),
                    _ => new ConversationWindow(
                        instance,
                        _conversationService,
                        entry => OpenConversationAsync(instance, entry),
                        () => SynchronizeConversationsAsync(instance),
                        relativePath => PropagateConversationDeletionAsync(instance, relativePath),
                        instances: Instances.ToArray())
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
                    _ = SynchronizeModelProvidersAsync(current, notifyNoConfiguration: true);
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
            MaxWidth = 980
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

        var dshInstallLabel = new TextBlock
        {
            Text = "DSh 安装位置",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var dshInstallRow = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        dshInstallRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dshInstallRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dshInstallRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dshInstallBox = new System.Windows.Controls.TextBox
        {
            Height = 38,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Text = GetConfiguredDshInstallDirectory() ?? string.Empty,
            ToolTip = "留空时使用当前 Node.js 的 npm 全局默认位置"
        };
        var browseDshInstallButton = new System.Windows.Controls.Button
        {
            Content = "选择文件夹",
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(8, 0, 0, 0)
        };
        var saveDshInstallButton = new System.Windows.Controls.Button
        {
            Content = "保存位置",
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(browseDshInstallButton, 1);
        Grid.SetColumn(saveDshInstallButton, 2);
        dshInstallRow.Children.Add(dshInstallBox);
        dshInstallRow.Children.Add(browseDshInstallButton);
        dshInstallRow.Children.Add(saveDshInstallButton);
        panel.Children.Add(dshInstallLabel);
        panel.Children.Add(dshInstallRow);
        panel.Children.Add(new TextBlock
        {
            Text = "指定后，Launcher 会把 @deepseek-ai/dsh 安装到这个目录；留空则沿用 npm 全局默认位置。实例的 Plugin、Skill、Provider、设置和对话仍保存在各自独立的 DSH_HOME。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

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
            var preferredDshDirectory = GetConfiguredDshInstallDirectory();
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
            var ready = IsPreferredDshRuntimeReady(_dshRuntime, preferredDshDirectory)
                && IsGlobalRuntimeReady(_nodeRuntime, _dshRuntime.NodeEngine);
            prepareButton.Content = string.IsNullOrWhiteSpace(preferredDshDirectory)
                ? "准备运行环境（官方源）"
                : "安装到所选位置（官方源）";
            prepareMirrorButton.Content = string.IsNullOrWhiteSpace(preferredDshDirectory)
                ? "准备运行环境（国内镜像）"
                : "安装到所选位置（国内镜像）";
            prepareButton.IsEnabled = !ready && !_isRuntimePrepareInProgress && !_isNodeDetectionInProgress;
            prepareMirrorButton.IsEnabled = prepareButton.IsEnabled;
            prepareButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            prepareMirrorButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        }

        async Task<bool> SaveDshInstallLocationAsync(bool showNotice)
        {
            try
            {
                var normalized = DshInstallService.NormalizeInstallDirectory(dshInstallBox.Text);
                var launcherSettings = _versionSettingsService.ReadLauncherSettings();
                launcherSettings.DshInstallDirectory = normalized;
                _versionSettingsService.SaveLauncherSettings(launcherSettings);
                dshInstallBox.Text = normalized ?? string.Empty;
                await RefreshDshAsync();
                UpdateStatus();
                if (showNotice)
                {
                    ShowNotice(normalized is null
                        ? "DSh 安装位置已恢复为 npm 全局默认位置。"
                        : $"DSh 安装位置已保存：{normalized}。现有运行时不会被自动移动；下次准备或修复时安装到这里。");
                }

                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                ShowNotice($"无法保存 DSh 安装位置：{ex.Message}");
                return false;
            }
        }

        refreshButton.Click += async (_, _) =>
        {
            await RefreshDshAsync();
            await RefreshNodeAsync();
            UpdateStatus();
        };
        browseDshInstallButton.Click += async (_, _) =>
        {
            var selected = PickFolder(
                "选择 DeepSeek Harness 安装位置",
                dshInstallBox.Text,
                showNewFolderButton: true);
            if (selected is null)
            {
                return;
            }

            dshInstallBox.Text = selected;
            await SaveDshInstallLocationAsync(showNotice: true);
        };
        saveDshInstallButton.Click += async (_, _) =>
            await SaveDshInstallLocationAsync(showNotice: true);
        prepareButton.Click += async (_, _) =>
        {
            if (await SaveDshInstallLocationAsync(showNotice: false))
            {
                await PrepareRuntimeFromSettingsAsync("Node.js 官方源", NodeInstallService.OfficialDistBase, DshInstallService.OfficialRegistry);
            }
        };
        prepareMirrorButton.Click += async (_, _) =>
        {
            if (await SaveDshInstallLocationAsync(showNotice: false))
            {
                await PrepareRuntimeFromSettingsAsync("npmmirror 国内镜像", NodeInstallService.MirrorDistBase, DshInstallService.ChinaRegistry);
            }
        };

        _runtimePanelUpdateStatus = UpdateStatus;
        UpdateStatus();

        AddVersionSyncSection(panel);
        return panel;
    }

    private void AddVersionSyncSection(StackPanel panel)
    {
        panel.Children.Add(new TextBlock
        {
            Text = "版本数据同步",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 32, 0, 0)
        });

        var globalCardContent = new StackPanel();
        var globalCard = new Border
        {
            Background = (WpfBrush)FindResource("CardBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 14, 0, 0),
            Child = globalCardContent
        };
        var syncAll = new System.Windows.Controls.CheckBox
        {
            Content = "和所有版本配置同步",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            IsChecked = _versionSettingsService.ReadLauncherSettings().SyncAllConfiguration
        };
        globalCardContent.Children.Add(syncAll);
        globalCardContent.Children.Add(new TextBlock
        {
            Text = "开启后，所有版本按全量策略同步对话与通用配置；模型 Provider 仍由每个版本下面的独立开关控制。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
        panel.Children.Add(globalCard);

        void HandleSyncAllChanged()
        {
            var enabled = syncAll.IsChecked == true;
            var settings = _versionSettingsService.ReadLauncherSettings();
            settings.SyncAllConfiguration = enabled;
            _versionSettingsService.SaveLauncherSettings(settings);
            if (SelectedInstance is { } current)
            {
                _ = SynchronizeConversationsAsync(current);
            }

            ShowNotice(enabled
                ? "已开启全局“和所有版本配置同步”，将按全量策略同步所有已停止版本。"
                : "已关闭全局“和所有版本配置同步”，各版本恢复使用自己的同步设置。");
        }

        syncAll.Checked += (_, _) => HandleSyncAllChanged();
        syncAll.Unchecked += (_, _) => HandleSyncAllChanged();

        var versionCardContent = new StackPanel();
        var versionCard = new Border
        {
            Background = (WpfBrush)FindResource("CardBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 14, 0, 0),
            Child = versionCardContent
        };
        versionCardContent.Children.Add(new TextBlock
        {
            Text = "单独调节版本配置",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        versionCardContent.Children.Add(new TextBlock
        {
            Text = "先选择版本，再设置它的对话同步范围和模型 Provider 同步。这里与“版本设置 → 配置”使用同一份数据。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 14)
        });

        var versionRow = new Grid();
        versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var versionLabel = new TextBlock
        {
            Text = "版本",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 14, 0)
        };
        var versionBox = new System.Windows.Controls.ComboBox
        {
            Height = 38,
            DisplayMemberPath = "Name",
            ItemsSource = Instances,
            SelectedItem = SelectedInstance ?? Instances.FirstOrDefault()
        };
        Grid.SetColumn(versionBox, 1);
        versionRow.Children.Add(versionLabel);
        versionRow.Children.Add(versionBox);
        versionCardContent.Children.Add(versionRow);

        var versionSyncAll = new System.Windows.Controls.CheckBox
        {
            Content = "和所有版本配置同步",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 0)
        };
        versionCardContent.Children.Add(versionSyncAll);
        versionCardContent.Children.Add(new TextBlock
        {
            Text = "该版本打开此项后，下面的对话文件选项会变灰，并按全量策略与其它版本同步。模型同步仍可单独开关。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });

        var versionOptions = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        versionOptions.Children.Add(new TextBlock { Text = "对话文件同步", FontWeight = FontWeights.SemiBold });
        var independentRadio = new System.Windows.Controls.RadioButton
        {
            Content = "该版本完全独立",
            GroupName = "SettingsConversationSync",
            Margin = new Thickness(0, 10, 0, 0)
        };
        var workspaceRadio = new System.Windows.Controls.RadioButton
        {
            Content = "按工作区同步",
            GroupName = "SettingsConversationSync",
            Margin = new Thickness(0, 8, 0, 0)
        };
        var versionWorkspaceBox = new System.Windows.Controls.ComboBox
        {
            IsEditable = true,
            Height = 36,
            Margin = new Thickness(24, 7, 0, 0),
            ToolTip = "选择已有工作区，或输入新的工作区名称"
        };
        var allRadio = new System.Windows.Controls.RadioButton
        {
            Content = "全量同步（所有工作区和所有版本）",
            GroupName = "SettingsConversationSync",
            Margin = new Thickness(0, 10, 0, 0)
        };
        var syncProviders = new System.Windows.Controls.CheckBox
        {
            Content = "所有版本自动同步模型",
            Margin = new Thickness(0, 18, 0, 0)
        };
        versionOptions.Children.Add(independentRadio);
        versionOptions.Children.Add(workspaceRadio);
        versionOptions.Children.Add(versionWorkspaceBox);
        versionOptions.Children.Add(allRadio);
        versionCardContent.Children.Add(versionOptions);
        versionCardContent.Children.Add(syncProviders);
        versionCardContent.Children.Add(new TextBlock
        {
            Text = "模型同步不受上面对话文件同步范围影响。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 5, 0, 0)
        });
        var saveVersionButton = new System.Windows.Controls.Button
        {
            Content = "保存此版本配置",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 18, 0, 0)
        };
        versionCardContent.Children.Add(saveVersionButton);
        var versionStatus = new TextBlock
        {
            Foreground = (WpfBrush)FindResource("BlueBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };
        versionCardContent.Children.Add(versionStatus);
        panel.Children.Add(versionCard);

        var workspaceCardContent = new StackPanel();
        var workspaceCard = new Border
        {
            Background = (WpfBrush)FindResource("CardBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 14, 0, 0),
            Child = workspaceCardContent
        };
        workspaceCardContent.Children.Add(new TextBlock
        {
            Text = "工作区同步管理",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        workspaceCardContent.Children.Add(new TextBlock
        {
            Text = "重命名会同时更新成员版本；删除只解除同步关系，并把成员版本改为完全独立，不会删除对话。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 14)
        });
        var managedWorkspaceBox = new System.Windows.Controls.ComboBox
        {
            Height = 38,
            ToolTip = "选择要管理的工作区"
        };
        workspaceCardContent.Children.Add(managedWorkspaceBox);
        var workspaceMembersText = new TextBlock
        {
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0)
        };
        workspaceCardContent.Children.Add(workspaceMembersText);

        var workspaceNameBox = new System.Windows.Controls.TextBox
        {
            Height = 36,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
            ToolTip = "输入新工作区名称，或修改当前工作区名称"
        };
        workspaceCardContent.Children.Add(workspaceNameBox);
        var workspaceButtons = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        var addWorkspaceButton = new System.Windows.Controls.Button
        {
            Content = "添加工作区",
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var renameWorkspaceButton = new System.Windows.Controls.Button
        {
            Content = "重命名",
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var deleteWorkspaceButton = new System.Windows.Controls.Button
        {
            Content = "删除工作区",
            Padding = new Thickness(12, 7, 12, 7)
        };
        workspaceButtons.Children.Add(addWorkspaceButton);
        workspaceButtons.Children.Add(renameWorkspaceButton);
        workspaceButtons.Children.Add(deleteWorkspaceButton);
        workspaceCardContent.Children.Add(workspaceButtons);
        panel.Children.Add(workspaceCard);

        var loadingVersion = false;

        void UpdateVersionOptionState()
        {
            var hasVersion = versionBox.SelectedItem is ManagerInstance;
            versionOptions.IsEnabled = hasVersion && versionSyncAll.IsChecked != true;
            versionWorkspaceBox.IsEnabled = versionOptions.IsEnabled && workspaceRadio.IsChecked == true;
            syncProviders.IsEnabled = hasVersion;
            saveVersionButton.IsEnabled = hasVersion;
        }

        void LoadSelectedVersion()
        {
            loadingVersion = true;
            try
            {
                if (versionBox.SelectedItem is not ManagerInstance instance)
                {
                    versionSyncAll.IsChecked = false;
                    independentRadio.IsChecked = true;
                    syncProviders.IsChecked = true;
                    versionWorkspaceBox.ItemsSource = Array.Empty<string>();
                    versionWorkspaceBox.Text = string.Empty;
                    versionStatus.Text = "当前没有可设置的版本。";
                    return;
                }

                var settings = _versionSettingsService.Read(instance);
                versionSyncAll.IsChecked = settings.SyncAllConfiguration;
                independentRadio.IsChecked = settings.ConversationSyncMode == ConversationSyncMode.Independent;
                workspaceRadio.IsChecked = settings.ConversationSyncMode == ConversationSyncMode.Workspace;
                allRadio.IsChecked = settings.ConversationSyncMode == ConversationSyncMode.All;
                syncProviders.IsChecked = settings.SyncModelProviders;
                versionWorkspaceBox.ItemsSource = _versionSettingsService.GetWorkspaceNames(Instances);
                versionWorkspaceBox.Text = settings.ConversationWorkspace ?? string.Empty;
                versionStatus.Text = $"正在编辑：{instance.Name}";
            }
            catch (Exception ex)
            {
                versionStatus.Text = $"读取版本配置失败：{ex.Message}";
            }
            finally
            {
                loadingVersion = false;
                UpdateVersionOptionState();
            }
        }

        void UpdateWorkspaceDetails()
        {
            var selected = managedWorkspaceBox.SelectedItem as string;
            renameWorkspaceButton.IsEnabled = !string.IsNullOrWhiteSpace(selected);
            deleteWorkspaceButton.IsEnabled = renameWorkspaceButton.IsEnabled;
            if (string.IsNullOrWhiteSpace(selected))
            {
                workspaceMembersText.Text = "暂无工作区。可在下方输入名称后添加。";
                return;
            }

            workspaceNameBox.Text = selected;
            var members = new List<string>();
            foreach (var instance in Instances)
            {
                try
                {
                    if (string.Equals(
                            _versionSettingsService.Read(instance).ConversationWorkspace,
                            selected,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        members.Add(instance.Name);
                    }
                }
                catch
                {
                    // 单个版本设置损坏时，仍允许管理其它正常版本的工作区。
                }
            }

            workspaceMembersText.Text = members.Count == 0
                ? "当前没有版本使用这个工作区。"
                : $"包含版本：{string.Join("、", members)}";
        }

        void RefreshWorkspaceChoices(string? preferredWorkspace = null)
        {
            try
            {
                var previous = preferredWorkspace ?? managedWorkspaceBox.SelectedItem as string;
                var names = _versionSettingsService.GetWorkspaceNames(Instances);
                managedWorkspaceBox.ItemsSource = names;
                managedWorkspaceBox.SelectedItem = names.FirstOrDefault(name =>
                    string.Equals(name, previous, StringComparison.OrdinalIgnoreCase));
                if (managedWorkspaceBox.SelectedItem is null && names.Count > 0)
                {
                    managedWorkspaceBox.SelectedIndex = 0;
                }

                var currentText = versionWorkspaceBox.Text;
                versionWorkspaceBox.ItemsSource = names;
                versionWorkspaceBox.Text = currentText;
                UpdateWorkspaceDetails();
            }
            catch (Exception ex)
            {
                workspaceMembersText.Text = $"读取工作区失败：{ex.Message}";
            }
        }

        void AddWorkspace()
        {
            try
            {
                var name = _versionSettingsService.AddLauncherWorkspace(workspaceNameBox.Text);
                RefreshWorkspaceChoices(name);
                ShowNotice($"已添加工作区：{name}。");
            }
            catch (ArgumentException ex)
            {
                ShowNotice(ex.Message);
            }
        }

        versionBox.SelectionChanged += (_, _) => LoadSelectedVersion();
        versionSyncAll.Checked += (_, _) => { if (!loadingVersion) UpdateVersionOptionState(); };
        versionSyncAll.Unchecked += (_, _) => { if (!loadingVersion) UpdateVersionOptionState(); };
        workspaceRadio.Checked += (_, _) => { if (!loadingVersion) UpdateVersionOptionState(); };
        independentRadio.Checked += (_, _) => { if (!loadingVersion) UpdateVersionOptionState(); };
        allRadio.Checked += (_, _) => { if (!loadingVersion) UpdateVersionOptionState(); };
        saveVersionButton.Click += (_, _) =>
        {
            if (versionBox.SelectedItem is not ManagerInstance instance)
            {
                versionStatus.Text = "请先选择版本。";
                return;
            }

            try
            {
                var current = _versionSettingsService.Read(instance);
                string? workspace = null;
                if (workspaceRadio.IsChecked == true)
                {
                    workspace = _versionSettingsService.AddLauncherWorkspace(versionWorkspaceBox.Text);
                }

                var updated = new VersionSettingsData
                {
                    SyncAllConfiguration = versionSyncAll.IsChecked == true,
                    ConversationSyncMode = workspaceRadio.IsChecked == true
                        ? ConversationSyncMode.Workspace
                        : allRadio.IsChecked == true
                            ? ConversationSyncMode.All
                            : ConversationSyncMode.Independent,
                    ConversationWorkspace = workspace,
                    SyncModelProviders = syncProviders.IsChecked == true,
                    WindowTitle = current.WindowTitle,
                    NodeExecutablePath = current.NodeExecutablePath
                };
                _versionSettingsService.Save(instance, updated);
                RefreshWorkspaceChoices(workspace);
                versionStatus.Text = $"已保存 {instance.Name} 的同步配置。";
                _ = SynchronizeModelProvidersAsync(instance, notifyNoConfiguration: true);
                _ = SynchronizeConversationsAsync(instance);
            }
            catch (Exception ex)
            {
                versionStatus.Text = $"保存失败：{ex.Message}";
            }
        };

        managedWorkspaceBox.SelectionChanged += (_, _) => UpdateWorkspaceDetails();
        addWorkspaceButton.Click += (_, _) => AddWorkspace();
        renameWorkspaceButton.Click += (_, _) =>
        {
            if (managedWorkspaceBox.SelectedItem is not string current)
            {
                return;
            }

            try
            {
                var updated = workspaceNameBox.Text.Trim();
                var affected = _versionSettingsService.RenameLauncherWorkspace(Instances, current, updated);
                RefreshWorkspaceChoices(updated);
                LoadSelectedVersion();
                ShowNotice($"工作区已重命名为“{updated}”，已更新 {affected} 个版本。 ");
            }
            catch (Exception ex)
            {
                ShowNotice($"重命名工作区失败：{ex.Message}");
            }
        };
        deleteWorkspaceButton.Click += (_, _) =>
        {
            if (managedWorkspaceBox.SelectedItem is not string current
                || System.Windows.MessageBox.Show(
                    this,
                    $"确定删除工作区“{current}”？成员版本会改为完全独立，对话文件不会被删除。",
                    "删除工作区",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var affected = _versionSettingsService.DeleteLauncherWorkspace(Instances, current);
                workspaceNameBox.Clear();
                RefreshWorkspaceChoices();
                LoadSelectedVersion();
                ShowNotice($"已删除工作区“{current}”，{affected} 个版本已改为完全独立。 ");
            }
            catch (Exception ex)
            {
                ShowNotice($"删除工作区失败：{ex.Message}");
            }
        };
        workspaceNameBox.KeyDown += (_, keyArgs) =>
        {
            if (keyArgs.Key == System.Windows.Input.Key.Enter)
            {
                AddWorkspace();
                keyArgs.Handled = true;
            }
        };

        LoadSelectedVersion();
        RefreshWorkspaceChoices();
    }

    /// <summary>
    /// 设置/诊断页的运行环境准备入口：与 Start/Stop/Restart/对话自动启动共用
    /// 同一个 LifecycleBusyGuard——准备进行中不能执行实例生命周期操作，
    /// 实例操作进行中也不能开始准备，避免安装与启停重叠。
    /// </summary>
    private async Task PrepareRuntimeFromSettingsAsync(string sourceName, string nodeDistBase, string npmRegistry)
    {
        if (!TryBeginLifecycleOperation())
        {
            ShowNotice("当前有实例操作正在进行，请稍候再准备运行环境。");
            return;
        }

        try
        {
            // 设置/诊断页管理的是全局运行环境，不传实例目标；Source 专属的
            // 精简准备只发生在启动实例流程里。
            await PrepareRuntimeAsync(sourceName, nodeDistBase, npmRegistry, null);
        }
        finally
        {
            EndLifecycleOperation();
        }

        _runtimePanelUpdateStatus?.Invoke();
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
            var dshInstallDirectory = GetConfiguredDshInstallDirectory();
            var progress = new Progress<NodeDownloadProgress>(progressWindow.SetDownloadProgress);
            var nodeWasInstalled = false;

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

                RevealRuntimeAfterBootstrap(TestRuntimeKind.Node);
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

                nodeWasInstalled = true;
            }

            // MSI 只更新系统环境，当前 Launcher 进程的 PATH 仍是旧值；
            // 把新检测到的 Node 目录补到进程 PATH，DSh 检测/启动才能解析 node。
            EnsureNodeDirectoryOnPath(_nodeRuntime.ExecutablePath);

            progressWindow.SetInstallPhase(false);
            SetRuntimeInstallPhase(false);

            if (nodeWasInstalled && !_dshRuntime.IsAvailable)
            {
                // 启动时缺 Node 会让已有的 dsh shim 无法执行、DSh 检测误报缺失；
                // Node 就绪后先重新检测 DSh 再决定是否安装，避免无谓重装
                // （npm 失败时还会把本来已可用的环境误报为准备失败）。
                await RefreshDshAsync();
            }

            var shouldInstallDsh = DshInstallService.ShouldInstallGlobalDSh(
                    _dshRuntime.IsAvailable,
                    target?.Kind)
                || (target?.Kind is null or InstanceKind.Installed
                    && !string.IsNullOrWhiteSpace(dshInstallDirectory)
                    && !DshRuntimeDetector.IsExecutableInInstallDirectory(
                        _dshRuntime.ExecutablePath,
                        dshInstallDirectory));
            if (shouldInstallDsh)
            {
                var usingMirrorRegistry = string.Equals(npmRegistry, DshInstallService.ChinaRegistry, StringComparison.OrdinalIgnoreCase);
                progressWindow.SetIndeterminate(true);
                progressWindow.SetStatus(usingMirrorRegistry
                    ? "正在通过 npmmirror 国内镜像安装 DeepSeek Harness…"
                    : "正在通过 npm 官方源安装 DeepSeek Harness…");
                var dshResult = await _dshInstaller.InstallAsync(
                    _nodeRuntime,
                    npmRegistry,
                    dshInstallDirectory,
                    cancellation.Token);
                if (!dshResult.IsSuccess)
                {
                    progressWindow.SetStatus(dshResult.Error ?? "DSh 安装失败。");
                    System.Windows.MessageBox.Show(this, dshResult.Error ?? "DSh 安装失败。", "准备运行环境", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                RevealRuntimeAfterBootstrap(TestRuntimeKind.Dsh);
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

            // 重绑定只针对本次用户确认修复的目标实例：Source 只需 Node，不能
            // 顺手改写其它（例如位于临时断开卷上的）stale Installed 注册。
            if (target is { Kind: InstanceKind.Installed })
            {
                var staleTarget = ResolveInstanceById(Instances, target.Id);
                var reboundTarget = staleTarget is null
                    ? null
                    : InstanceRuntimeRebinder.RebindInstalledInstance(staleTarget, _dshRuntime);
                if (reboundTarget is not null)
                {
                    UpdateInstance(reboundTarget);
                }
            }

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
            // 无论成功、失败、取消还是超时，先恢复进度窗口的可关闭状态；
            // 主窗口的关闭保护在残留 msiexec 仍存活时保持生效，等它真正
            // 退出后由 OnLingeringInstallerCompleted 解除，期间不能通过
            // 关闭再重开 Launcher 来并发第二次 Node 安装。
            progressWindow.SetInstallPhase(false);
            if (!_nodeInstaller.HasLingeringInstaller)
            {
                SetRuntimeInstallPhase(false);
            }

            progressWindow.Close();
            _isRuntimePrepareInProgress = false;
            OnPropertyChanged(nameof(CanStartInstance));
            OnPropertyChanged(nameof(CanInstallDsh));
            OnPropertyChanged(nameof(DshInstallButtonText));
        }
    }

    private void OnLingeringInstallerCompleted()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnLingeringInstallerCompleted);
            return;
        }

        // 残留安装进程退出后解除关闭保护；若新一轮准备已经开始，则由该
        // 流程自己的安装阶段/finally 管理保护状态，这里不做任何改动。
        if (_isRuntimePrepareInProgress)
        {
            return;
        }

        SetRuntimeInstallPhase(false);
        ShowNotice("后台的 Windows Installer 已结束，Launcher 恢复正常关闭。");
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
        if (SelectedInstance is null)
        {
            await RunFirstVersionSetupAsync();
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

        if (!TryBeginLifecycleOperation())
        {
            return;
        }

        try
        {
            // 串行化 guard 必须先于 runtime 准备占用：准备会打开非模态窗口等待
            // 下载/安装，期间 Stop/Restart/对话自动启动都不能并发进入。
            if (!await EnsureRuntimeReadyAsync(selected))
            {
                return;
            }

            // runtime 准备窗口是非模态的，期间用户可能切换 SelectedInstance 或删除目标；
            // 必须按最初点击的实例 ID 重新解析（重绑定后取最新状态），目标不存在则中止。
            var resolvedStartTarget = ResolveInstanceById(Instances, selected.Id);
            if (resolvedStartTarget is null)
            {
                ShowNotice("目标实例已被删除，无法继续启动。");
                return;
            }

            selected = resolvedStartTarget;

            await StartPreparedInstanceAndOpenAsync(selected);
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
            EndLifecycleOperation();
        }
    }

    private async void StopInstance_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var selected = SelectedInstance;
        if (selected.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            ShowNotice("当前实例连接的是外部 DSh 服务，Launcher 不会停止该进程。");
            return;
        }

        if (!TryBeginLifecycleOperation())
        {
            return;
        }

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
            EndLifecycleOperation();
        }
    }

    private async void RestartInstance_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var selected = SelectedInstance;
        if (selected.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            ShowNotice("当前实例连接的是外部 DSh 服务，Launcher 不会重启该进程。");
            return;
        }

        if (!TryBeginLifecycleOperation())
        {
            return;
        }

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

            // 停止完成后与 Start 一致：先做 runtime readiness（运行期间 Node 或
            // package root 可能已被删除，此时应进入一键修复而不是直接启动失败），
            // 准备结束后仍按最初目标实例 ID 重新解析，目标被删除则中止。
            if (!await EnsureRuntimeReadyAsync(selected))
            {
                return;
            }

            var resolvedRestartTarget = ResolveInstanceById(Instances, selected.Id);
            if (resolvedRestartTarget is null)
            {
                ShowNotice("目标实例已被删除，无法继续重启。");
                return;
            }

            selected = resolvedRestartTarget;

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
            EndLifecycleOperation();
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
            var installDirectory = GetConfiguredDshInstallDirectory();
            var result = await _dshInstaller.InstallAsync(
                _nodeRuntime,
                registry,
                installDirectory,
                _windowCancellation.Token);
            if (!result.IsSuccess)
            {
                ShowNotice(result.Error ?? "DSh 安装失败。");
                return;
            }

            RevealRuntimeAfterBootstrap(TestRuntimeKind.Dsh);
            await RefreshDshAsync();
            ShowNotice(installDirectory is null
                ? $"DSh 安装/更新完成：{_dshRuntime.VersionText}。可以重新检测并注册实例。"
                : $"DSh 安装/更新完成：{_dshRuntime.VersionText} · {installDirectory}。");
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
        await SynchronizeModelProvidersAsync(instance, notifyNoConfiguration: true);
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

    /// <summary>
    /// 生命周期操作（Start/Stop/Restart/对话自动启动）在 handler 入口即占用
    /// 串行化 guard，一直持有到 runtime 准备 + 实际启停 + 状态更新全部结束；
    /// 只有占用者在自己的 finally 里释放，不存在交叉清写 busy 标志的路径。
    /// </summary>
    private bool TryBeginLifecycleOperation()
    {
        if (!_lifecycleGuard.TryBegin())
        {
            return false;
        }

        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(CanStopInstance));
        OnPropertyChanged(nameof(CanRestartInstance));
        OnPropertyChanged(nameof(InstanceEndpointText));
        return true;
    }

    private void EndLifecycleOperation()
    {
        _lifecycleGuard.End();
        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(CanStopInstance));
        OnPropertyChanged(nameof(CanRestartInstance));
        OnPropertyChanged(nameof(InstanceEndpointText));
    }

    private static string? PickFolder(
        string description,
        string? initialPath = null,
        bool showNewFolderButton = false)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = showNewFolderButton,
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
            ShowNotice(_nodeInstaller.HasLingeringInstaller
                ? "Windows Installer 仍在后台完成 Node.js 安装，结束后才能关闭 Launcher。"
                : "Node.js 系统安装正在进行，请等待安装完成后再关闭 Launcher。");
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowProcedure);
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message != WindowMessageNonClientHitTest
            || WindowState != WindowState.Normal
            || ResizeMode != ResizeMode.CanResize)
        {
            return IntPtr.Zero;
        }

        var packedPoint = longParameter.ToInt64();
        var screenPoint = new System.Windows.Point(
            unchecked((short)(packedPoint & 0xFFFF)),
            unchecked((short)((packedPoint >> 16) & 0xFFFF)));
        var clientPoint = PointFromScreen(screenPoint);
        var result = GetResizeHitTest(
            ActualWidth,
            ActualHeight,
            clientPoint.X,
            clientPoint.Y,
            ResizeHitBorder);
        if (result == HitTestClient)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(result);
    }

    internal static int GetResizeHitTest(
        double width,
        double height,
        double x,
        double y,
        double border)
    {
        if (width <= 0 || height <= 0 || border <= 0)
        {
            return HitTestClient;
        }

        var left = x < border;
        var right = x >= width - border;
        var top = y < border;
        var bottom = y >= height - border;

        if (top && left) return HitTestTopLeft;
        if (top && right) return HitTestTopRight;
        if (bottom && left) return HitTestBottomLeft;
        if (bottom && right) return HitTestBottomRight;
        if (left) return HitTestLeft;
        if (right) return HitTestRight;
        if (top) return HitTestTop;
        if (bottom) return HitTestBottom;
        return HitTestClient;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (WindowState != WindowState.Normal
            || ResizeMode != ResizeMode.CanResize
            || sender is not Thumb { Tag: string direction })
        {
            return;
        }

        if (direction.Contains("Left", StringComparison.Ordinal))
        {
            var horizontalChange = Math.Min(e.HorizontalChange, ActualWidth - MinWidth);
            Left += horizontalChange;
            Width = Math.Max(MinWidth, ActualWidth - horizontalChange);
        }
        else if (direction.Contains("Right", StringComparison.Ordinal))
        {
            Width = Math.Max(MinWidth, ActualWidth + e.HorizontalChange);
        }

        if (direction.Contains("Top", StringComparison.Ordinal))
        {
            var verticalChange = Math.Min(e.VerticalChange, ActualHeight - MinHeight);
            Top += verticalChange;
            Height = Math.Max(MinHeight, ActualHeight - verticalChange);
        }
        else if (direction.Contains("Bottom", StringComparison.Ordinal))
        {
            Height = Math.Max(MinHeight, ActualHeight + e.VerticalChange);
        }
    }

    private async Task PromptFirstRunSetupIfNeededAsync()
    {
        if (!ShouldPromptFirstRunSetup(
                Instances.Count,
                _instancesLoadedSuccessfully,
                _firstRunSetupPromptShown))
        {
            return;
        }

        _firstRunSetupPromptShown = true;
        await RunFirstVersionSetupAsync();
    }

    internal static bool ShouldPromptFirstRunSetup(
        int instanceCount,
        bool instancesLoadedSuccessfully,
        bool promptAlreadyShown) =>
        instancesLoadedSuccessfully
        && instanceCount == 0
        && !promptAlreadyShown;

    internal static string BuildDefaultFirstVersionName(string? dshVersion)
    {
        var normalized = dshVersion?.Trim().TrimStart('v', 'V');
        return string.IsNullOrWhiteSpace(normalized)
            ? "DSh 默认版本"
            : $"DSh {normalized}";
    }

    private async Task RunFirstVersionSetupAsync()
    {
        if (!_instancesLoadedSuccessfully)
        {
            ShowNotice("实例注册信息尚未读取完成，请稍候再试。");
            return;
        }

        if (Instances.Count > 0)
        {
            SelectedInstance ??= Instances[0];
            return;
        }

        var configuredDirectory = GetConfiguredDshInstallDirectory();
        var runtimeReady = IsPreferredDshRuntimeReady(_dshRuntime, configuredDirectory)
            && IsGlobalRuntimeReady(_nodeRuntime, _dshRuntime.NodeEngine);
        var choice = FirstRunSetupWindow.Show(
            this,
            _nodeRuntime.IsAvailable ? $"{_nodeRuntime.VersionText} · {NodeStatusText}" : "未安装",
            _dshRuntime.IsAvailable ? _dshRuntime.VersionText : "未安装",
            configuredDirectory,
            runtimeReady);
        if (choice is null)
        {
            ShowNotice("已暂缓首次运行配置；可点击“准备首个版本”重新打开引导。");
            return;
        }

        if (!TryBeginLifecycleOperation())
        {
            return;
        }

        ManagerInstance? created = null;
        try
        {
            var launcherSettings = _versionSettingsService.ReadLauncherSettings();
            launcherSettings.DshInstallDirectory = choice.DshInstallDirectory;
            _versionSettingsService.SaveLauncherSettings(launcherSettings);
            await RefreshDshAsync();

            var sourceName = choice.Source == FirstRunDownloadSource.ChinaMirror
                ? "npmmirror 国内镜像"
                : "Node.js 官方源";
            var nodeDistBase = choice.Source == FirstRunDownloadSource.ChinaMirror
                ? NodeInstallService.MirrorDistBase
                : NodeInstallService.OfficialDistBase;
            var npmRegistry = choice.Source == FirstRunDownloadSource.ChinaMirror
                ? DshInstallService.ChinaRegistry
                : DshInstallService.OfficialRegistry;
            if (!await PrepareRuntimeAsync(sourceName, nodeDistBase, npmRegistry, null))
            {
                return;
            }

            if (Instances.Count > 0)
            {
                SelectedInstance ??= Instances[0];
                return;
            }

            var template = GetVersionTemplate();
            if (template is null)
            {
                ShowNotice("运行环境已准备，但没有解析到可用于创建版本的 DSh 运行目录。");
                return;
            }

            created = await Task.Run(() => _versionPackageService.CreateCleanVersion(
                template,
                BuildDefaultFirstVersionName(_dshRuntime.Version)));
            AddCreatedVersion(created);
            ShowNotice($"首个版本已创建：{created.Name}。正在启动…");
            await StartPreparedInstanceAndOpenAsync(created);
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (created is not null)
            {
                UpdateInstanceStatus(created, InstanceRuntimeStatus.Error, ex.Message);
            }

            ShowNotice($"首次运行配置失败：{ex.Message}");
        }
        finally
        {
            EndLifecycleOperation();
        }
    }

    private async Task<bool> StartPreparedInstanceAndOpenAsync(ManagerInstance selected)
    {
        var result = await StartManagedInstanceAsync(selected);
        if (result is null)
        {
            return false;
        }

        if (!result.IsSuccess || result.ProcessId is null || result.Port is null || result.WebUrl is null)
        {
            ShowNotice(result.Error ?? "DSh 启动失败。");
            return false;
        }

        OpenChatWindow(selected.Id, result.WebUrl);
        ShowNotice($"实例已启动：{selected.Name}，运行地址 {result.WebUrl}。健康检查已通过。");
        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(WindowProcedure);
        _windowSource = null;
        try
        {
            _providerRefreshCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

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
        CancellationToken cancellationToken = default,
        bool notifyNoConfiguration = false)
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

            if (notifyNoConfiguration && result.NoConfigurationSource && result.CopiedVersions == 0)
            {
                ShowNotice("Provider 同步已开启，但这些版本的 settings.yaml 中都没有 llm Provider 配置，无可同步内容；经 DSh 登录页连接的 Provider 不写入该文件。");
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

        if (!TryBeginLifecycleOperation())
        {
            ShowNotice("实例正在执行启动或停止操作，请稍候再打开对话。");
            return false;
        }

        try
        {
            // 对话触发的自动启动同样先做运行环境准备，缺 Node/DSh 时提供一键准备；
            // 与 Start/Stop/Restart 共享同一个串行化 guard，准备等待期间不能并发进入其它生命周期操作。
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
            EndLifecycleOperation();
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
