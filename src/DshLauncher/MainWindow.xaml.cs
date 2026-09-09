using System.Runtime.InteropServices;
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
using System.Windows.Media.Animation;
using System.Windows.Data;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using DshLauncher.Models;
using DshLauncher.Services;
using DshLauncher.Watchdog;
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
    private readonly DetectedRuntimeRegistrationService _detectedRuntimeRegistrationService;
    private readonly DshInstanceRunner _instanceRunner;
    private readonly ExtensionService _extensionService;
    private readonly MarketplaceService _marketplaceService;
    private readonly SkillMarketService _skillMarketService;
    private readonly VersionPackageService _versionPackageService;
    private readonly VersionSettingsService _versionSettingsService = new();
    private readonly VersionHealthService _versionHealthService;
    private readonly VersionSnapshotService _versionSnapshotService;
    private readonly ConversationService _conversationService;
    private readonly ConversationSyncService _conversationSyncService;
    private readonly ModelService _modelService;
    private readonly ModelProviderSyncService _modelProviderSyncService;
    private readonly ProviderStateService _providerStateService = new();
    private readonly DshInstallService _dshInstaller = new();
    private readonly NodeInstallService _nodeInstaller = new();
    private readonly PortableNodeService _portableNodeInstaller = new();
    private readonly SourceBuildService _sourceBuilder = new();
    private readonly CancellationTokenSource _windowCancellation = new();
    private NodeRuntimeInfo _nodeRuntime = NodeRuntimeInfo.Missing();
    private DshRuntimeInfo _dshRuntime = DshRuntimeInfo.Missing();
    private IReadOnlyList<DshRuntimeInfo> _detectedDshRuntimes = Array.Empty<DshRuntimeInfo>();
    private readonly Dictionary<string, ChatWindow> _chatWindows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recentInstanceIds = new(StringComparer.Ordinal);
    private ManagerInstance? _selectedInstance;
    private string _versionSettingsReturnSection = "启动";
    private bool _isNodeDetectionInProgress;
    private readonly Services.LifecycleBusyGuard _lifecycleGuard = new();
    private bool _isLifecycleInProgress => _lifecycleGuard.IsBusy;
    private bool _isDshInstallInProgress;
    private bool _isRuntimePrepareInProgress;
    private bool _isLoadingCachedInstances;
    private bool _blockWindowCloseForMsi;
    private bool _shutdownCleanupStarted;
    private bool _shutdownCleanupCompleted;
    private bool _instancesLoadedSuccessfully;
    private bool _firstRunSetupPromptShown;
    private bool _lastDshScanWasFromCache;
    private string _currentSection = "启动";
    private Action? _runtimePanelUpdateStatus;
    private HwndSource? _windowSource;
    private Services.TrayIconService? _trayIconService;
    private bool _shutdownFromTray;
    private readonly WatchdogRuntime _watchdog;
    private readonly Dictionary<string, DateTimeOffset> _lastReassertAt = new(StringComparer.Ordinal);
    private readonly WindowStateStore _windowStateStore = new();
    private readonly WebCacheVersionLedger _webCacheLedger = new();
    private readonly BalanceService _balanceService = new();
    private readonly BrowserGuard _browserGuard = new();
    private CancellationTokenSource? _balanceCancellation;
    private bool _windowStateRestored;

    public MainWindow()
    {
        RecentInstancesView = new ListCollectionView(Instances)
        {
            Filter = item => item is ManagerInstance instance && _recentInstanceIds.Contains(instance.Id)
        };
        RecentInstancesView.SortDescriptions.Add(new SortDescription(
            nameof(ManagerInstance.RecentSortAt),
            ListSortDirection.Descending));
        _versionSnapshotService = new(isRunning: id => _instanceRunner!.IsRunning(id));
        _extensionService = new(
            id => _instanceRunner!.IsRunning(id),
            snapshotService: _versionSnapshotService);
        _instanceRunner = new(
            extensionService: _extensionService,
            proxySettings: () => ProxySettings.From(_versionSettingsService.ReadLauncherSettings()));
        _watchdog = new WatchdogRuntime(ReadWatchdogProbeSeconds());
        _watchdog.GhostAdopted += OnGhostAdopted;
        _watchdog.InstanceStopped += OnInstanceStopped;
        _watchdog.OrphanDetected += OnOrphanDetected;
        _watchdog.ResourcesUpdated += OnResourcesUpdated;
        _marketplaceService = new();
        _skillMarketService = new(_extensionService);
        _versionPackageService = new(_instanceRegistry);
        _detectedRuntimeRegistrationService = new(_instanceRegistry);
        _conversationService = new(isRunning: id => _instanceRunner.IsRunning(id));
        _conversationSyncService = new(_versionSettingsService, id => _instanceRunner.IsRunning(id));
        _modelService = new(id => _instanceRunner.IsRunning(id));
        _versionHealthService = new(_versionSettingsService, _modelService);
        _modelProviderSyncService = new(
            _versionSettingsService,
            _modelService,
            _providerStateService,
            id => _instanceRunner.IsRunning(id));
        // 超时的 msiexec 在后台运行期间阻止关闭 Launcher：不跨进程持久化标记，
        // 用“无法优雅关闭”保证 Launcher 重开后不会出现第二次 Node MSI 与残留安装重叠。
        _nodeInstaller.LingeringInstallerCompleted += OnLingeringInstallerCompleted;
        InitializeComponent();
        WindowSizeHelper.FitInitialSize(this);
        _windowStateRestored = _windowStateStore.TryRestore(this);
        DataContext = this;
    }

    /// <summary>读取设置页配置的实例守护监控间隔（秒，越界回落默认 5）。</summary>
    private int ReadWatchdogProbeSeconds()
    {
        try
        {
            return _versionSettingsService.ReadLauncherSettings().WatchdogProbeSeconds;
        }
        catch
        {
            return 5;
        }
    }

    public string PageTitle { get; private set; } = "启动";

    public string PageSubtitle { get; private set; } = "管理 DeepSeek Harness 实例与运行环境";

    public string PageNotice { get; private set; } = string.Empty;

    public Visibility PageNoticeVisibility { get; private set; } = Visibility.Collapsed;

    public string PageNoticeDetail { get; private set; } = string.Empty;

    public Visibility PageNoticeDetailVisibility { get; private set; } = Visibility.Collapsed;

    /// <summary>余额卡片（仅在设置中显式启用后显示；凭据只在内存中读取）。</summary>
    public string BalanceText { get; private set; } = string.Empty;

    public string BalanceDetailText { get; private set; } = string.Empty;

    public Visibility BalanceVisibility { get; private set; } = Visibility.Collapsed;

    public WpfBrush BalanceForeground { get; private set; } =
        new SolidColorBrush(WpfColor.FromRgb(46, 166, 107));

    public ObservableCollection<ManagerInstance> Instances { get; } = new();

    public ListCollectionView RecentInstancesView { get; }

    public ObservableCollection<ManagerInstance> RunningInstances { get; } = new();

    public ManagerInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (ReferenceEquals(_selectedInstance, value))
            {
                return;
            }

            // WPF 在列表刷新 / 仪表盘收起 / 页面切换时会把 SelectedItem 瞬时写回 null：
            // 只要当前实例仍在注册列表里，就忽略这次清空并回写绑定（否则列表仍高亮、
            // 但“停止/重启”全灰，出现“实例被清空”的错觉）。真正删除实例后
            // Instances 已不再包含它，null 会被正常接受。
            if (value is null
                && _selectedInstance is not null
                && Instances.Any(candidate =>
                    string.Equals(candidate.Id, _selectedInstance.Id, StringComparison.Ordinal)))
            {
                OnPropertyChanged(nameof(SelectedInstance));
                return;
            }

            _selectedInstance = value;
            ApplySelectedVersionSettings(_selectedInstance);
            OnPropertyChanged(nameof(SelectedInstance));
            OnPropertyChanged(nameof(SelectedInstanceName));
            OnPropertyChanged(nameof(SelectedInstanceKindText));
            OnPropertyChanged(nameof(SelectedInstancePathVisibility));
            OnPropertyChanged(nameof(SelectedInstanceRootPath));
            OnPropertyChanged(nameof(SelectedInstanceRootPathText));
            OnPropertyChanged(nameof(SelectedInstanceDshHome));
            OnPropertyChanged(nameof(SelectedInstanceDshHomeText));
            OnPropertyChanged(nameof(SelectedInstanceStatus));
            OnPropertyChanged(nameof(SelectedInstanceStatusBrush));
            OnPropertyChanged(nameof(SelectedInstanceStatusBackgroundBrush));
            OnPropertyChanged(nameof(SelectedInstanceStatusTextBrush));
            OnPropertyChanged(nameof(InstanceEndpointText));
            OnPropertyChanged(nameof(SelectedInstanceResourceText));
            OnPropertyChanged(nameof(SelectedInstanceResourceVisibility));
            OnPropertyChanged(nameof(CanStartInstance));
            OnPropertyChanged(nameof(StartInstanceButtonText));
            OnPropertyChanged(nameof(CanStopInstance));
            OnPropertyChanged(nameof(CanRestartInstance));
            OnPropertyChanged(nameof(DesktopShellVisibility));
            OnPropertyChanged(nameof(LauncherStartVisibility));
            OnPropertyChanged(nameof(NodeStatusText));
            OnPropertyChanged(nameof(NodeStatusBrush));
            OnPropertyChanged(nameof(NodeVersionText));
            OnPropertyChanged(nameof(NodePathText));
            if (IsLoaded && !_isLoadingCachedInstances)
            {
                RefreshBalanceAsync();
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

    public string SelectedInstanceName => SelectedInstance?.Name ?? "尚未创建版本";

    public string SelectedInstanceKindText => SelectedInstance?.KindText ?? "尚未选择版本";

    public Visibility SelectedInstancePathVisibility =>
        SelectedInstance is null ? Visibility.Collapsed : Visibility.Visible;

    public string SelectedInstanceRootPath => SelectedInstance?.RootPath ?? string.Empty;

    public string SelectedInstanceRootPathText => SelectedInstance is null
        ? string.Empty
        : "目录：" + PathDisplay.Tail(SelectedInstance.RootPath);

    public string SelectedInstanceDshHome => SelectedInstance?.DshHome ?? string.Empty;

    public string SelectedInstanceDshHomeText => SelectedInstance is null
        ? string.Empty
        : "DSH_HOME：" + PathDisplay.Tail(SelectedInstance.DshHome);

    public string SelectedInstanceStatus => SelectedInstance?.StatusText ?? "未选择";

    public WpfBrush SelectedInstanceStatusBrush => SelectedInstance?.RuntimeStatus switch
    {
        InstanceRuntimeStatus.Running => new SolidColorBrush(WpfColor.FromRgb(46, 166, 107)),
        InstanceRuntimeStatus.Error => new SolidColorBrush(WpfColor.FromRgb(217, 74, 74)),
        _ => new SolidColorBrush(WpfColor.FromRgb(150, 163, 181))
    };

    /// <summary>状态胶囊底色（状态色 15% 透明度）。</summary>
    public WpfBrush SelectedInstanceStatusBackgroundBrush => SelectedInstance?.RuntimeStatus switch
    {
        InstanceRuntimeStatus.Running => new SolidColorBrush(WpfColor.FromArgb(52, 46, 166, 107)),
        InstanceRuntimeStatus.Error => new SolidColorBrush(WpfColor.FromArgb(52, 217, 74, 74)),
        _ => new SolidColorBrush(WpfColor.FromArgb(44, 150, 163, 181))
    };

    /// <summary>状态胶囊文字色（状态色深色调）。</summary>
    public WpfBrush SelectedInstanceStatusTextBrush => SelectedInstance?.RuntimeStatus switch
    {
        InstanceRuntimeStatus.Running => new SolidColorBrush(WpfColor.FromRgb(31, 122, 80)),
        InstanceRuntimeStatus.Error => new SolidColorBrush(WpfColor.FromRgb(180, 35, 24)),
        _ => new SolidColorBrush(WpfColor.FromRgb(96, 107, 122))
    };

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
        : IsCustomOpenBound
            ? "打开窗口"
            : _instanceRunner.IsRunning(SelectedInstance.Id)
                ? "打开实例"
                : GetSelectedOpenMode() == VersionOpenMode.Web
                    ? "Web 启动"
                    : "Desktop 启动";

    public bool CanStopInstance => CanStopInstanceCore(
        _isLifecycleInProgress,
        _isRuntimePrepareInProgress,
        SelectedInstance is not null
            && _instanceRunner.IsManaged(SelectedInstance.Id));

    public bool CanRestartInstance => CanStopInstance;

    public bool IsCustomOpenBound => SelectedInstance is not null
        && GetSelectedOpenMode() == VersionOpenMode.Custom;

    public bool IsExternalOpenBound => IsCustomOpenBound;

    public Visibility LauncherStartVisibility => IsExternalOpenBound
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility DesktopShellVisibility => SelectedInstance?.CanOpenDesktopShell == true
        && !IsExternalOpenBound
        ? Visibility.Visible
        : Visibility.Collapsed;

    private VersionOpenMode GetSelectedOpenMode()
    {
        if (SelectedInstance is null)
        {
            return VersionOpenMode.Desktop;
        }

        try
        {
            var settings = _versionSettingsService.Read(SelectedInstance);
            return settings.OpenMode ?? VersionOpenMode.Desktop;
        }
        catch
        {
            return VersionOpenMode.Desktop;
        }
    }

    internal static bool CanStopInstanceCore(
        bool lifecycleInProgress,
        bool runtimePrepareInProgress,
        bool isManaged) =>
        !lifecycleInProgress
        && !runtimePrepareInProgress
        && isManaged;

    public string InstanceEndpointText => SelectedInstance?.WebUrl
        ?? (SelectedInstance?.RuntimeStatus == InstanceRuntimeStatus.Running ? "正在检查运行地址…" : "尚未启动");

    /// <summary>选中实例的实时资源（CPU/内存/运行时长/进程数）；无数据时为空。</summary>
    public string SelectedInstanceResourceText =>
        SelectedInstance is { } instance && _watchdog.GetResource(instance.Id) is { } snapshot
            ? $"CPU {snapshot.CpuPercent:0.#}% · 内存 {FormatBytes(snapshot.WorkingSetBytes)} · 运行 {FormatDuration(snapshot.Uptime)} · {snapshot.ProcessCount} 个进程"
            : string.Empty;

    public Visibility SelectedInstanceResourceVisibility =>
        SelectedInstanceResourceText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B"
    };

    internal static string FormatDuration(TimeSpan duration) => duration switch
    {
        { TotalHours: >= 1 } => $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分",
        { TotalMinutes: >= 1 } => $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒",
        _ => $"{Math.Max(0, (int)duration.TotalSeconds)} 秒"
    };

    private void OnResourcesUpdated()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(SelectedInstanceResourceText));
            OnPropertyChanged(nameof(SelectedInstanceResourceVisibility));
        });
    }

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
        : GetCurrentRuntimeLaunchSpec()?.UsesPackagedNode == true
            ? "由封装应用内置，无需系统 Node.js"
        : !_nodeRuntime.IsAvailable
            ? "需要安装 Node.js"
            : $"{_nodeRuntime.VersionText} · {GetNodeRequirementText()}";

    public string NodePathText => _isNodeDetectionInProgress
        ? "正在检查 PATH、Windows 常见安装位置和 DeepSeek Desktop…"
        : GetCurrentRuntimeLaunchSpec() is { UsesPackagedNode: true } packagedRuntime
            ? packagedRuntime.NodeExecutablePath ?? packagedRuntime.HostPath
        : _nodeRuntime.IsAvailable
            ? (_nodeRuntime.ExecutablePath ?? "已找到 node.exe，但路径不可用")
            : _nodeRuntime.Error ?? "没有发现可用的 node.exe";

    public string DshStatusText => _dshRuntime.IsAvailable ? "可用" : "未安装";

    public System.Windows.Media.Brush DshStatusBrush => _dshRuntime.IsAvailable
        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 135, 90))
        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 105, 30));

    public string DshVersionText => _dshRuntime.IsAvailable
        ? $"{_dshRuntime.DisplayVersionText} · {(_dshRuntime.ExecutablePath is null ? "启动文件未解析" : "已找到启动文件")}"
        : "实例注册后由对应运行环境启动";

    private NodeRuntimeCompatibility GetSelectedNodeCompatibility()
    {
        if (GetCurrentRuntimeLaunchSpec() is { UsesPackagedNode: true } packagedRuntime
            && DshRuntimeCommandFactory.IsUsable(packagedRuntime))
        {
            return NodeRuntimeCompatibility.Compatible;
        }

        if (!_nodeRuntime.IsAvailable)
        {
            return NodeRuntimeCompatibility.Missing;
        }

        return _nodeRuntime.GetCompatibility(GetNodeEngineRequirement(SelectedInstance));
    }

    private DshRuntimeLaunchSpec? GetCurrentRuntimeLaunchSpec() =>
        SelectedInstance?.EffectiveDshLaunchSpec ?? _dshRuntime.EffectiveLaunchSpec;

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

    internal bool IsShutdownInProgress => _shutdownCleanupStarted;

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 尺寸/居中已由构造函数里的 WindowSizeHelper.FitInitialSize 完成：
            // SystemParameters.WorkArea 本身就是 DIP（125% 缩放下 1920x1020 物理
            // → 1536x816），这里再除一次 DpiScale 会让 Width 小于 MinWidth，
            // 原生窗口按偏小值创建、布局按 MinWidth 排 → 右侧/底部内容被裁切。

            LauncherLog.Info("窗口尺寸诊断", null, new
            {
                dpi = VisualTreeHelper.GetDpi(this).DpiScaleX,
                workArea = new { width = SystemParameters.WorkArea.Width, height = SystemParameters.WorkArea.Height },
                width = Width,
                height = Height,
                actual = new { width = ActualWidth, height = ActualHeight },
                min = new { width = MinWidth, height = MinHeight }
            });

            SwitchSection("启动");
            LoadCachedInstances();
            RefreshBalanceAsync();
            await Dispatcher.Yield(DispatcherPriority.Background);
            _watchdog.Start();
            await ReconcileCachedInstanceStatesAsync();
            await RefreshDshAsync();
            await RefreshNodeAsync();
            await PromptFirstRunSetupIfNeededAsync();
            if (_lastDshScanWasFromCache)
            {
                _ = RefreshDshInBackgroundAsync();
            }
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
        await RefreshDshAsync(forceRefresh: true);
        var runtime = await RefreshNodeAsync();
        if (runtime is null)
        {
            return;
        }

        ShowNotice(runtime.IsAvailable
            ? $"运行环境检测完成：Node.js {runtime.VersionText}（{NodeStatusText}），{_dshRuntime.DisplayVersionText}。"
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
                ? NodeRuntimeInfo.Missing("未安装")
                : await _nodeDetector.DetectAsync(
                    preferredNodePath,
                    GetNodeEngineRequirement(SelectedInstance),
                    _windowCancellation.Token);
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

    private async Task RefreshDshAsync(bool forceRefresh = false)
    {
        _lastDshScanWasFromCache = false;
        try
        {
            if (IsRuntimeHiddenForBootstrapTest(TestRuntimeKind.Dsh))
            {
                _detectedDshRuntimes = Array.Empty<DshRuntimeInfo>();
                _dshRuntime = DshRuntimeInfo.Missing("未安装");
            }
            else
            {
                var scan = await _dshDetector.ScanAsync(
                    GetConfiguredDshInstallDirectory(),
                    forceRefresh,
                    _windowCancellation.Token);
                _detectedDshRuntimes = scan.Runtimes;
                _dshRuntime = scan.PrimaryRuntime;
                _lastDshScanWasFromCache = scan.FromCache;
            }
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _detectedDshRuntimes = Array.Empty<DshRuntimeInfo>();
            _dshRuntime = DshRuntimeInfo.Missing($"DSh 检测失败：{ex.Message}");
        }

        if (_instancesLoadedSuccessfully)
        {
            try
            {
                await AutoImportDetectedDshRuntimesAsync();
            }
            catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
            {
                return;
            }
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

    private async Task RefreshDshInBackgroundAsync()
    {
        await RefreshDshAsync(forceRefresh: true);
        await RefreshNodeAsync();
    }

    private void PathLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string text || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            ShowNotice("已复制路径：" + text);
        }
        catch (Exception ex)
        {
            ShowNotice($"复制失败：{ex.Message}");
        }
    }

    private string GetConfiguredDshInstallDirectory() =>
        _versionSettingsService.ResolveDshInstallDirectory();

    private sealed record MoveSourceItem(string Label, string Directory);

    /// <summary>
    /// 当前选中实例可移动的运行时根目录：只允许 Launcher 数据根之下的运行时，
    /// 避免把系统 npm 全局安装搬走；<root>ersions\<ver>\dsh.cmd 归一到 <root>。
    /// </summary>
    private static string? ResolveMovableRuntimeDirectory(ManagerInstance? instance)
    {
        if (instance?.DshExecutablePath is not { Length: > 0 } executable)
        {
            return null;
        }

        string directory;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (directory.Length == 0)
        {
            return null;
        }

        var parent = Path.GetDirectoryName(directory);
        if (parent is not null
            && string.Equals(Path.GetFileName(parent), "versions", StringComparison.OrdinalIgnoreCase))
        {
            // <root>\versions\<ver>\dsh.cmd → <root>
            directory = Path.GetDirectoryName(parent) ?? directory;
        }

        return DshInstallMoveService.LooksLikeInstall(directory) && !IsSystemPackageDirectory(directory)
            ? directory
            : null;
    }

    /// <summary>系统包管理器目录（npm 全局前缀 / nodejs 安装目录）不允许搬动。</summary>
    private static bool IsSystemPackageDirectory(string directory)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs")
        })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (string.Equals(directory, normalized, StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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

    private void LoadCachedInstances()
    {
        _instancesLoadedSuccessfully = false;
        try
        {
            Instances.Clear();
            foreach (var storedInstance in _instanceRegistry.Load())
            {
                Instances.Add(storedInstance);
            }

            RefreshRecentInstances();
            _isLoadingCachedInstances = true;
            try
            {
                SelectedInstance = RecentInstancesView.Cast<ManagerInstance>().FirstOrDefault()
                    ?? Instances.FirstOrDefault();
            }
            finally
            {
                _isLoadingCachedInstances = false;
            }

            OnPropertyChanged(nameof(InstanceCountText));
            OnPropertyChanged(nameof(NoInstancesVisibility));
            OnPropertyChanged(nameof(InstancesVisibility));
            OnPropertyChanged(nameof(CanStartInstance));
            OnPropertyChanged(nameof(CanStopInstance));
            OnPropertyChanged(nameof(CanRestartInstance));
            RefreshRunningInstances();
            _instancesLoadedSuccessfully = true;
            OnPropertyChanged(nameof(CanStartInstance));
        }
        catch (Exception ex)
        {
            ShowNotice($"读取实例注册文件失败：{ex.Message}");
        }
    }

    // ---------- Watchdog 集成（进程内监控，事件驱动） ----------

    /// <summary>幽灵转正（market 自重启被识别）：由 Launcher 追加接管重启。</summary>
    private void OnGhostAdopted(WatchdogInstanceDto instance)
    {
        // 事件可能来自后台循环线程：转回 UI 线程处理。
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var current = ResolveInstanceById(Instances, instance.InstanceId);
                if (current is not null)
                {
                    _instanceRunner.TryDropExitedProcess(current.Id);
                    await ReassertManagedInstanceAsync(current);
                }
            }
            catch (Exception ex)
            {
                ShowNotice($"接管实例异常：{ex.Message}");
            }
        });
    }

    /// <summary>watchdog 判定实例停止（端口已释放）：把界面收敛为 Stopped。</summary>
    private void OnInstanceStopped(WatchdogInstanceDto instance)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var current = ResolveInstanceById(Instances, instance.InstanceId);
                if (current is not null
                    && current.RuntimeStatus == InstanceRuntimeStatus.Running
                    && !_instanceRunner.IsRunning(current.Id)
                    && !_instanceRunner.IsAttached(current.Id))
                {
                    UpdateInstance(current with
                    {
                        RuntimeStatus = InstanceRuntimeStatus.Stopped,
                        RuntimeOwnership = InstanceRuntimeOwnership.None,
                        ProcessId = null,
                        Port = null,
                        WebUrl = null,
                        AuthenticatedWebUrl = null,
                        LastError = null
                    });
                }
            }
            catch (Exception ex)
            {
                ShowNotice($"同步实例状态异常：{ex.Message}");
            }
        });
    }

    /// <summary>检测到未登记进程占用实例端口（残留/异常启动）：提示用户，不自动处置。</summary>
    private void OnOrphanDetected(WatchdogInstanceDto instance)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var current = ResolveInstanceById(Instances, instance.InstanceId);
            if (current is null)
            {
                return;
            }

            ShowNotice(
                $"检测到未受管进程占用实例 {current.Name} 的端口 {instance.Port}" +
                "（可能来自插件市场重启或上次异常退出残留）。" +
                "若该实例未被 Launcher 接管，可先「停止实例」清理，再重新启动。");
        });
    }

    /// <summary>
    /// 接管重启：检测到实例被非启动器方式（插件市场自重启）重建后，由 Launcher
    /// 追加一次标准重启——杀掉无主实例（连同 powershell/cmd 包装链，终端窗口随之
    /// 消失），用 Launcher 的 CreateNoWindow 方式重新拉起：完全受管、全程无窗口。
    /// </summary>
    private async Task ReassertManagedInstanceAsync(ManagerInstance instance)
    {
        if (instance is null
            || instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            return;
        }

        if (!TryBeginLifecycleOperation())
        {
            return;
        }

        // 防重入：同步循环每 5s 可能重复检测到"市场实例仍在台账"，接管重启只需一次。
        if (_lastReassertAt.TryGetValue(instance.Id, out var lastReassert)
            && DateTimeOffset.UtcNow - lastReassert < TimeSpan.FromMinutes(1))
        {
            EndLifecycleOperation();
            return;
        }

        try
        {
            // 1) 杀掉无主实例（市场重启的进程 + 包装链 + 桌宠残留），台账出账。
            try
            {
                _watchdog.Cleanup(instance.Id);
            }
            catch
            {
                // 清理失败不阻塞接管；后述启动前 Worker 仍会取得锁。
            }

            try
            {
                _watchdog.Unregister(instance.Id);
            }
            catch
            {
                // 台账注销失败由 watchdog 自身状态收敛兜底。
            }

            if (_instanceRunner.IsRunning(instance.Id))
            {
                await _instanceRunner.StopAsync(instance.Id, _windowCancellation.Token);
            }

            CloseChatWindow(instance.Id);

            // 2) Launcher 标准启动（CreateNoWindow）重新拉起。
            if (!await EnsureRuntimeReadyAsync(instance))
            {
                return;
            }

            var resolved = ResolveInstanceById(Instances, instance.Id) ?? instance;
            var openBrowser = GetSelectedOpenMode() == VersionOpenMode.Web;
            var result = await StartManagedInstanceAsync(resolved, openBrowser);
            if (result is null || !result.IsSuccess || result.ProcessId is null || result.WebUrl is null)
            {
                ShowStartFailure(result?.Error, "接管重启失败：实例已被插件市场重启，Launcher 重新拉起失败。");
                return;
            }

            _lastReassertAt[instance.Id] = DateTimeOffset.UtcNow;

            // 3) 开窗（Web 模式浏览器 / Desktop 模式 Chat）并提示。
            if (openBrowser)
            {
                OpenWebUrlInBrowser(result.AuthenticatedWebUrl ?? result.WebUrl);
            }
            else
            {
                OpenChatWindow(resolved.Id, result.AuthenticatedWebUrl ?? result.WebUrl);
            }

            ShowNotice(
                $"实例 {resolved.Name} 曾被插件市场重启：Launcher 已接管并整理，" +
                $"新运行地址 {result.WebUrl}（使用无窗口方式运行）。");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            UpdateInstanceStatus(instance, InstanceRuntimeStatus.Error, ex.ToString());
            ShowNotice($"接管实例重启失败：{ex.Message}");
        }
        finally
        {
            EndLifecycleOperation();
        }
    }

    /// <summary>停止成功后：注销台账 + 清理该实例残留（桌宠/市场 helper 等）。</summary>
    private void ReportInstanceStoppedAsync(string instanceId)
    {
        try
        {
            _watchdog.Unregister(instanceId);
            _watchdog.Cleanup(instanceId);
        }
        catch
        {
            // 清理失败不阻塞停止流程；残留由收尾清理兜底。
        }
    }

    /// <summary>启动成功后：登记到 Watchdog 台账。</summary>
    private void ReportInstanceStartedAsync(
        ManagerInstance instance,
        int processId,
        int port,
        string webUrl,
        string? authenticatedWebUrl)
    {
        try
        {
            _watchdog.Register(
                instance.Id,
                instance.Name,
                instance.DshHome,
                instance.RootPath,
                processId,
                port,
                webUrl,
                authenticatedWebUrl);
        }
        catch
        {
            // Watchdog 不可用时只失去检测能力；实例本身继续运行。
        }
    }

    private async Task ReconcileCachedInstanceStatesAsync()
    {
        // Watchdog 台账优先（进程内，始终可用）：launcher 崩溃/重启后，
        // 台账里记录的才是权威的真实 PID/端口（含市场自重启转正后的新进程）。
        var watchdogSnapshot = new Dictionary<string, WatchdogInstanceDto>(StringComparer.Ordinal);
        foreach (var entry in _watchdog.Snapshot())
        {
            watchdogSnapshot[entry.InstanceId] = entry;
        }

        foreach (var storedInstance in Instances
                     .Where(static instance => instance.RuntimeStatus == InstanceRuntimeStatus.Running)
                     .ToArray())
        {
            _windowCancellation.Token.ThrowIfCancellationRequested();
            var instance = storedInstance;
            if (watchdogSnapshot.TryGetValue(storedInstance.Id, out var tracked)
                && tracked.ProcessId > 0)
            {
                instance = storedInstance with
                {
                    ProcessId = tracked.ProcessId,
                    Port = tracked.Port,
                    WebUrl = tracked.WebUrl,
                    AuthenticatedWebUrl = tracked.AuthenticatedWebUrl
                };
            }

            if (await _instanceRunner.TryAdoptRunningProcessAsync(instance, _windowCancellation.Token))
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
            else if (await _instanceRunner.TryAttachAsync(storedInstance, _windowCancellation.Token))
            {
                instance = storedInstance with
                {
                    RuntimeOwnership = InstanceRuntimeOwnership.Attached,
                    LastError = null
                };
            }
            else
            {
                instance = storedInstance with
                {
                    RuntimeStatus = InstanceRuntimeStatus.Stopped,
                    RuntimeOwnership = InstanceRuntimeOwnership.None,
                    ProcessId = null,
                    Port = null,
                    WebUrl = null,
                    AuthenticatedWebUrl = null
                };
            }

            if (instance != storedInstance)
            {
                ReplaceInstanceInMemory(_instanceRegistry.Update(instance));
            }
        }

        RefreshRecentInstances();
        RefreshRunningInstances();
        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(CanStopInstance));
        OnPropertyChanged(nameof(CanRestartInstance));
    }

    private async Task<DetectedRuntimeRegistrationResult> AutoImportDetectedDshRuntimesAsync(
        IReadOnlyCollection<DshRuntimeInfo>? runtimes = null,
        bool refreshRegisteredRuntimeRoots = false,
        CancellationToken cancellationToken = default)
    {
        var effectiveCancellation = cancellationToken.CanBeCanceled
            ? cancellationToken
            : _windowCancellation.Token;
        var result = await _detectedRuntimeRegistrationService.ImportAsync(
            Instances.ToArray(),
            runtimes ?? _detectedDshRuntimes,
            refreshRegisteredRuntimeRoots,
            effectiveCancellation);
        foreach (var instance in result.AddedInstances)
        {
            Instances.Add(instance);
        }

        foreach (var instance in result.BackfilledInstances)
        {
            ReplaceInstanceInMemory(instance);
        }

        foreach (var instance in result.UpdatedInstances)
        {
            ReplaceInstanceInMemory(instance);
        }

        if (result.AddedInstances.Count > 0
            || result.UpdatedInstances.Count > 0
            || result.BackfilledInstances.Count > 0)
        {
            SelectedInstance ??= result.AddedInstances.FirstOrDefault()
                ?? result.UpdatedInstances.FirstOrDefault()
                ?? result.BackfilledInstances.FirstOrDefault();
            RefreshRecentInstances();
            OnPropertyChanged(nameof(InstanceCountText));
            OnPropertyChanged(nameof(NoInstancesVisibility));
            OnPropertyChanged(nameof(InstancesVisibility));
            OnPropertyChanged(nameof(CanStartInstance));
            RefreshRunningInstances();
        }

        if (result.AddedInstances.Count == 0
            && result.UpdatedInstances.Count == 0
            && result.BackfilledInstances.Count == 0
            && result.Errors.Count == 0)
        {
            return result;
        }

        var messages = new List<string>();
        if (result.AddedInstances.Count > 0)
        {
            var names = string.Join("、", result.AddedInstances.Take(3).Select(static instance => instance.Name));
            var suffix = result.AddedInstances.Count > 3 ? "等" : string.Empty;
            var dataText = result.ImportedInstances.Count == result.AddedInstances.Count
                ? "，并复制了原有配置、工作区、对话和凭据"
                : result.ImportedInstances.Count > 0
                    ? $"；其中 {result.ImportedInstances.Count} 个复制了原有 DSH_HOME，其余未找到旧数据"
                    : "；未找到可复制的旧 DSH_HOME，已使用新的独立目录";
            messages.Add($"已导入 {result.AddedInstances.Count} 个 DSh 实例：{names}{suffix}{dataText}。");
        }

        if (result.UpdatedInstances.Count > 0)
        {
            var names = string.Join("、", result.UpdatedInstances.Take(3).Select(static instance => instance.Name));
            var suffix = result.UpdatedInstances.Count > 3 ? "等" : string.Empty;
            messages.Add($"已覆盖更新 {result.UpdatedInstances.Count} 个同地址实例：{names}{suffix}，未创建重复版本。");
        }


        if (result.BackfilledInstances.Count > 0)
        {
            var names = string.Join("、", result.BackfilledInstances.Take(3).Select(static instance => instance.Name));
            var suffix = result.BackfilledInstances.Count > 3 ? "等" : string.Empty;
            messages.Add($"已为 {result.BackfilledInstances.Count} 个旧实例补入原有工作区、对话和缺少的凭据：{names}{suffix}。");
        }

        if (result.Errors.Count > 0)
        {
            messages.Add(string.Join("；", result.Errors));
        }

        ShowNotice(string.Join(" ", messages));
        return result;
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
        _trayIconService?.RefreshRunningItems();
    }

    internal static IReadOnlyList<ManagerInstance> SelectRecentInstances(
        IEnumerable<ManagerInstance> instances,
        int maximumCount = 3) =>
        instances
            .OrderByDescending(static instance => instance.RecentSortAt)
            .ThenBy(static instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maximumCount))
            .ToArray();

    private void RefreshRecentInstances()
    {
        _recentInstanceIds.Clear();
        foreach (var instance in SelectRecentInstances(Instances))
        {
            _recentInstanceIds.Add(instance.Id);
        }

        RecentInstancesView.Refresh();
        OnPropertyChanged(nameof(InstanceCountText));
    }

    private void MarkInstanceUsed(string instanceId)
    {
        var instance = Instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, instanceId, StringComparison.Ordinal));
        if (instance is null)
        {
            return;
        }

        UpdateInstance(instance with { LastUsedAt = DateTimeOffset.UtcNow });
    }

    private void ReplaceInstanceInMemory(ManagerInstance updated)
    {
        var index = Instances.ToList().FindIndex(instance =>
            string.Equals(instance.Id, updated.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        var wasSelected = string.Equals(SelectedInstance?.Id, updated.Id, StringComparison.Ordinal);
        Instances[index] = updated;
        if (wasSelected)
        {
            SelectedInstance = updated;
        }
    }

    private async void RunningInstances_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(RunningInstancesList, source) is not ListBoxItem item
            || item.DataContext is not ManagerInstance instance)
        {
            return;
        }

        SelectedInstance = instance;
        e.Handled = true;
        await StartSelectedInstanceAsync();
    }

    private void SwitchContextInstance(ManagerInstance target, string section)
    {
        SelectedInstance = target;
        MarkInstanceUsed(target.Id);
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

        _currentSection = section;
        SetNavigationSelection(section);
        VersionSettingsBackButton.Visibility = Visibility.Collapsed;
        StartupBrandText.Visibility = section == "启动"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContextInstanceSelector.Visibility = section is "扩展" or "Agent" && Instances.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        PageNoticeVisibility = Visibility.Collapsed;

        if (section == "启动")
        {
            PageTitle = "启动";
            PageSubtitle = "启动实例并查看正在运行的 DeepSeek Harness";
            ShowMainDashboard();
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
                        pluginInstallMode: () => _versionSettingsService.ReadLauncherSettings().PluginInstallMode,
                        stopInstanceForPluginRetry: StopInstanceForPluginRetryAsync,
                        handoffPluginFailure: SendPluginFailureToCurrentInstanceAsync,
                        versionSettingsService: _versionSettingsService,
                        versionSnapshotService: _versionSnapshotService),
                    "Agent" => new ExtensionWindow(
                        instance,
                        _extensionService,
                        () => _nodeRuntime,
                        agentOnly: true,
                        marketplaceService: _marketplaceService,
                        skillMarketService: _skillMarketService,
                        versionSettingsService: _versionSettingsService,
                        versionSnapshotService: _versionSnapshotService),
                    _ => new ConversationWindow(
                        instance,
                        _conversationService,
                        OpenConversationAsync,
                        () => SynchronizeConversationsAsync(instance),
                        relativePath => PropagateConversationDeletionAsync(instance, relativePath),
                        instances: Instances.ToArray(),
                        selectInstance: candidate => SwitchContextInstance(candidate, section))
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
        AnimateIn(MainDashboardGrid);
    }

    private void ShowEmbeddedPage(object page)
    {
        MainDashboardGrid.Visibility = Visibility.Collapsed;
        EmbeddedPageHost.Content = page;
        EmbeddedPageHost.Visibility = Visibility.Visible;
        if (page is FrameworkElement fe)
        {
            AnimateIn(fe);
        }
    }

    /// <summary>
    /// PCL 式页面切换动画：淡入 + 8px 上浮（EaseOut）。
    /// 动画结束后清除 RenderTransform / Opacity 基值——残留的 Transform
    /// 会让 WPF 走非像素对齐渲染路径，导致文本发虚（125% DPI 下尤甚）。
    /// </summary>
    private static void AnimateIn(FrameworkElement element)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var translate = new TranslateTransform { Y = 8 };
        element.RenderTransform = translate;
        var rise = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        rise.Completed += (_, _) =>
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            element.RenderTransform = null;
        };
        translate.BeginAnimation(TranslateTransform.YProperty, rise);

        element.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            // 先恢复基值再移除动画：否则 BeginAnimation(null) 会回落到基值 0（透明）。
            element.Opacity = 1;
            element.BeginAnimation(OpacityProperty, null);
        };
        element.BeginAnimation(OpacityProperty, fade);
    }

    private void VersionControl_Click(object sender, RoutedEventArgs e) => ShowVersionControl();

    private void VersionSettings_Click(object sender, RoutedEventArgs e) => ShowVersionSettings();

    private void ShowVersionControl()
    {
        VersionSettingsBackButton.Visibility = Visibility.Collapsed;
        StartupBrandText.Visibility = Visibility.Collapsed;
        ContextInstanceSelector.Visibility = Visibility.Collapsed;
        PageTitle = "版本控制";
        PageSubtitle = "按版本选择、复制版本或导入整合包；每个版本使用独立 DSH_HOME";
        ShowEmbeddedPage(new VersionControlWindow(
            Instances,
            SelectedInstance,
            _versionPackageService,
            GetVersionTemplate,
            AddCreatedVersion,
            RemoveDeletedVersion,
            version =>
            {
                SelectedInstance = version;
                MarkInstanceUsed(version.Id);
            },
            ScanAndRegisterRuntimeDirectoryAsync,
            _versionHealthService,
            _versionSnapshotService,
            () => _nodeRuntime,
            () => _dshRuntime,
            id => _instanceRunner.IsRunning(id),
            updated =>
            {
                UpdateInstance(updated);
                return Instances.First(instance =>
                    string.Equals(instance.Id, updated.Id, StringComparison.Ordinal));
            },
            () =>
            {
                ApplySelectedVersionSettings(SelectedInstance);
            },
            _windowCancellation.Token));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
    }

    public void ShowVersionSettings(bool openPluginPage = false)
    {
        _versionSettingsReturnSection = _currentSection is "扩展" or "Agent"
            ? _currentSection
            : "启动";
        VersionSettingsBackText.Text = _versionSettingsReturnSection == "启动"
            ? "返回启动"
            : $"返回{_versionSettingsReturnSection}";
        VersionSettingsBackButton.Visibility = Visibility.Visible;
        StartupBrandText.Visibility = Visibility.Collapsed;
        ContextInstanceSelector.Visibility = Visibility.Collapsed;
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
            _versionSnapshotService,
            (current, name) =>
            {
                var updated = current with { Name = name };
                UpdateInstance(updated);
                var saved = Instances.First(instance =>
                    string.Equals(instance.Id, current.Id, StringComparison.Ordinal));
                PageTitle = $"版本设置 - {saved.Name}";
                PageSubtitle = $"当前实例：{saved.Name} · 管理个性化、配置、插件和分享导出";
                OnPropertyChanged(nameof(PageTitle));
                OnPropertyChanged(nameof(PageSubtitle));
                return saved;
            },
            () =>
            {
                ApplySelectedVersionSettings(SelectedInstance);
                _ = RefreshNodeAsync();
                if (SelectedInstance is { } current)
                {
                    _ = SynchronizeModelProvidersAsync(current, notifyNoConfiguration: true);
                    _ = SynchronizeConversationsAsync(current);
                }
            },
            openPluginPage,
            new InstanceHealthProviders(
                instance => _watchdog.GetResource(instance.Id),
                instance => _watchdog.GetResourceHistory(instance.Id),
                instance => InstanceResourceSampler.SampleProcesses(_watchdog.GetProcessId(instance.Id)),
                instance => _instanceRunner.GetLogs(instance.Id),
                instance => _instanceRunner.ClearLogs(instance.Id),
                instance =>
                {
                    var keepPid = _watchdog.GetProcessId(instance.Id);
                    return _watchdog.Cleanup(instance.Id, keepPid > 0 ? keepPid : null);
                })));
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
            OnPropertyChanged(nameof(StartInstanceButtonText));
            OnPropertyChanged(nameof(LauncherStartVisibility));
            OnPropertyChanged(nameof(DesktopShellVisibility));
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

        OnPropertyChanged(nameof(StartInstanceButtonText));
        OnPropertyChanged(nameof(LauncherStartVisibility));
        OnPropertyChanged(nameof(DesktopShellVisibility));
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
            BuildDefaultFirstVersionNameForRuntime(_dshRuntime),
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
        MarkInstanceUsed(created.Id);
        RefreshRecentInstances();
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
            RefreshRecentInstances();
            SelectedInstance = RecentInstancesView.Cast<ManagerInstance>().FirstOrDefault()
                ?? Instances.FirstOrDefault();
        }
        else
        {
            RefreshRecentInstances();
        }

        OnPropertyChanged(nameof(InstanceCountText));
        OnPropertyChanged(nameof(NoInstancesVisibility));
        OnPropertyChanged(nameof(InstancesVisibility));
        RefreshRunningInstances();
    }

    private FrameworkElement CreateSettingsPage()
    {
        StackPanel NewCategoryPanel() => new()
        {
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 980
        };

        void AddPageHeader(StackPanel target, string title, string description)
        {
            target.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold
            });
            target.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = (WpfBrush)FindResource("MutedBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 18)
            });
        }

        var runtimePanel = NewCategoryPanel();
        var generalPanel = NewCategoryPanel();
        var networkPanel = NewCategoryPanel();
        var diagnosePanel = NewCategoryPanel();
        AddPageHeader(runtimePanel, "运行环境", "Node.js 与 DeepSeek Harness 的检测、安装位置与准备。");
        AddPageHeader(generalPanel, "常规", "插件安装方式、关闭行为、版本同步与实例守护。");
        AddPageHeader(networkPanel, "网络与账户", "代理设置与 DeepSeek 余额显示。");
        AddPageHeader(diagnosePanel, "诊断与日志", "导出脱敏诊断包、查看日志与错误码。");

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
        runtimePanel.Children.Add(nodeStatus);
        runtimePanel.Children.Add(nodeDetail);
        runtimePanel.Children.Add(dshStatus);
        runtimePanel.Children.Add(dshDetail);

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
        dshInstallRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dshInstallBox = new System.Windows.Controls.TextBox
        {
            Height = 38,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Text = GetConfiguredDshInstallDirectory(),
            ToolTip = $"留空时恢复 Launcher 默认位置（exe 同目录的 run_time）：{_versionSettingsService.DefaultDshInstallDirectory}"
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
        var moveDshInstallButton = new System.Windows.Controls.Button
        {
            Name = "MoveInstallButton",
            Content = "移动已有安装",
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "把当前选中实例正在使用的 DSh 运行目录（含各版本）整体搬到上面填写的新位置，并自动更新引用它的实例"
        };
        Grid.SetColumn(browseDshInstallButton, 1);
        Grid.SetColumn(saveDshInstallButton, 2);
        Grid.SetColumn(moveDshInstallButton, 3);
        dshInstallRow.Children.Add(dshInstallBox);
        dshInstallRow.Children.Add(browseDshInstallButton);
        dshInstallRow.Children.Add(saveDshInstallButton);
        dshInstallRow.Children.Add(moveDshInstallButton);
        runtimePanel.Children.Add(dshInstallLabel);
        runtimePanel.Children.Add(dshInstallRow);
        runtimePanel.Children.Add(new TextBlock
        {
            Text = $"Launcher 默认把 @deepseek-ai/dsh 安装到 {_versionSettingsService.DefaultDshInstallDirectory}（exe 同目录的 run_time，可整个文件夹拷走做成便携版）；可以在这里改为其它目录（改动只影响后续安装，想连同已安装的一起搬走请点「移动已有安装」）。实例的 Plugin、Skill、Provider、设置和对话仍保存在各自独立的 DSH_HOME。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

        // 重新检测 / 扫描自定义目录上移到“要移动的运行时”卡片之上。
        var refreshButton = new System.Windows.Controls.Button
        {
            Content = "重新检测",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        var scanDirectoryButton = new System.Windows.Controls.Button
        {
            Content = "扫描自定义目录",
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            ToolTip = "只扫描你选择的目录；适用于手动解压或自定义位置安装的 DSH Desktop / DeepSeek Harness"
        };
        var detectionButtons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        detectionButtons.Children.Add(refreshButton);
        detectionButtons.Children.Add(scanDirectoryButton);
        runtimePanel.Children.Add(detectionButtons);

        // “要移动的运行时”：圆角卡片 + 复选框行（标签 + 当前完整位置）。
        runtimePanel.Children.Add(new TextBlock
        {
            Text = "要移动的运行时",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 0)
        });
        var moveSourceList = new StackPanel();
        var moveSourceCard = new Border
        {
            Name = "MoveSourceCard",
            Background = (WpfBrush)FindResource("CardBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 12),
            Margin = new Thickness(0, 6, 0, 0),
            Child = moveSourceList
        };
        runtimePanel.Children.Add(moveSourceCard);

        // 多选：勾选的运行时目录集合（独立勾选，不互相取消）。
        var checkedMoveSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moveSourcesInitialized = false;

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
        var prepareButtons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        prepareButtons.Children.Add(prepareButton);
        prepareButtons.Children.Add(prepareMirrorButton);
        runtimePanel.Children.Add(prepareButtons);
        var hint = new TextBlock
        {
            Text = "准备运行环境会下载 Node.js 官方安装程序并按系统授权安装，再通过 npm 安装 @deepseek-ai/dsh；Launcher 启动时不会自动下载或安装任何内容。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        runtimePanel.Children.Add(hint);

        void UpdateStatus()
        {
            var preferredDshDirectory = GetConfiguredDshInstallDirectory();
            nodeStatus.Text = $"Node.js：{NodeStatusText}";
            nodeDetail.Text = _nodeRuntime.IsAvailable
                ? $"{_nodeRuntime.VersionText} · {(_nodeRuntime.ExecutablePath ?? "路径未知")}"
                : _nodeRuntime.Error ?? "未安装";
            dshStatus.Text = $"DeepSeek Harness：{DshStatusText}";
            dshDetail.Text = _dshRuntime.IsAvailable
                ? $"{_dshRuntime.DisplayVersionText} · {(_dshRuntime.ExecutablePath ?? "路径未知")}"
                : _dshRuntime.Error ?? "未安装";
            // 设置页按全局环境判定就绪：DSh 声明的 engines.node 与现有 Node
            // 不兼容时保持“未就绪”，让状态和不兼容提示可见，而不是隐藏准备按钮。
            var ready = IsPreferredDshRuntimeReady(_dshRuntime, preferredDshDirectory)
                && IsDetectedRuntimeReady(_nodeRuntime, _dshRuntime);
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
                var normalized = DshInstallService.NormalizeInstallDirectory(dshInstallBox.Text)
                    ?? _versionSettingsService.DefaultDshInstallDirectory;
                var launcherSettings = _versionSettingsService.ReadLauncherSettings();
                launcherSettings.DshInstallDirectory = normalized;
                _versionSettingsService.SaveLauncherSettings(launcherSettings);
                dshInstallBox.Text = normalized;
                await RefreshDshAsync(forceRefresh: true);
                UpdateStatus();
                if (showNotice)
                {
                    ShowNotice($"DSh 安装位置已保存：{normalized}。现有运行时不会被自动移动；下次准备或修复时安装到这里。");
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
            await RefreshDshAsync(forceRefresh: true);
            await RefreshNodeAsync();
            UpdateStatus();
        };
        scanDirectoryButton.Click += async (_, _) =>
        {
            var selected = PickFolder("扫描自定义 DSH Desktop / DeepSeek Harness 目录");
            if (selected is not null)
            {
                await ScanAndRegisterRuntimeDirectoryAsync(selected);
                UpdateStatus();
            }
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
        moveDshInstallButton.Click += async (_, _) =>
        {
            var mover = new DshInstallMoveService(
                registry: _instanceRegistry,
                settings: _versionSettingsService);
            // 源 = 卡片里勾选的运行时（可多选）；没勾选时回退到启动页选中实例、配置目录。
            var sources = checkedMoveSources
                .Where(DshInstallMoveService.LooksLikeInstall)
                .ToArray();
            if (sources.Length == 0)
            {
                var fallback = ResolveMovableRuntimeDirectory(SelectedInstance)
                    ?? mover.ResolveCurrentInstallDirectory();
                if (fallback is null)
                {
                    ShowNotice("没有找到可移动的 DSh 运行时（可能尚未安装）。");
                    return;
                }

                sources = new[] { fallback };
            }

            var target = DshInstallService.NormalizeInstallDirectory(dshInstallBox.Text)
                ?? _versionSettingsService.DefaultDshInstallDirectory;
            if (sources.Length == 1
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(sources[0])),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)),
                    StringComparison.OrdinalIgnoreCase))
            {
                ShowNotice("目标位置与当前安装位置相同，无需移动。");
                return;
            }

            var multiple = sources.Length > 1;
            var sourceList = string.Join(Environment.NewLine, sources);
            var confirmed = System.Windows.MessageBox.Show(
                this,
                $"将以下 DSh 运行时：\n{sourceList}\n\n移动到：\n{target}\n\n"
                + (multiple ? "（多个运行时会分别放入目标目录下的同名子文件夹）\n\n" : string.Empty)
                + "会同步更新引用它们的实例；请先停止正在运行的实例。继续吗？",
                "移动 DSh 安装位置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!confirmed)
            {
                return;
            }

            moveDshInstallButton.IsEnabled = false;
            var originalContent = moveDshInstallButton.Content;
            moveDshInstallButton.Content = "正在移动…";
            try
            {
                var result = await mover.MoveManyAsync(
                    sources,
                    target,
                    id => _instanceRunner.IsRunning(id),
                    _windowCancellation.Token);
                ShowNotice(result.Message);
                if (result.Ok)
                {
                    // 移动后实例的运行时路径已被重写；内存列表必须重载，否则
                    // 接下来的自动注册会按旧路径去重失败、多出一个重复实例。
                    checkedMoveSources.Clear();
                    LoadCachedInstances();
                    RefreshMoveSources();
                }

                if (result.OldDirectory is not null)
                {
                    dshInstallBox.Text = result.NewDirectory ?? target;
                }

                await RefreshDshAsync(forceRefresh: true);
                await RefreshNodeAsync();
                UpdateStatus();
            }
            finally
            {
                moveDshInstallButton.Content = originalContent;
                moveDshInstallButton.IsEnabled = true;
            }
        };
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

        AddPluginInstallModeSection(generalPanel);
        AddCloseBehaviorSection(generalPanel);
        AddVersionSyncSection(generalPanel);
        AddWatchdogSection(generalPanel);
        AddProxySection(networkPanel);
        AddBalanceSection(networkPanel);
        AddDiagnoseSection(diagnosePanel);

        // 左侧分类 + 右侧内容（与版本设置页同一套导航外观，见 docs/UI-DESIGN.md）
        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navPanel = new StackPanel { Margin = new Thickness(0) };
        var navCard = new Border
        {
            Background = (WpfBrush)FindResource("CardBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 10, 8, 10),
            VerticalAlignment = VerticalAlignment.Top,
            Child = navPanel
        };
        Grid.SetColumn(navCard, 0);
        host.Children.Add(navCard);

        var contentScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly,
            Content = runtimePanel
        };
        Grid.SetColumn(contentScroller, 2);
        host.Children.Add(contentScroller);

        var categories = new (string Title, StackPanel Panel)[]
        {
            ("运行环境", runtimePanel),
            ("常规", generalPanel),
            ("网络与账户", networkPanel),
            ("诊断与日志", diagnosePanel)
        };
        var navButtons = new List<System.Windows.Controls.Button>();
        var scrollOffsets = new double[categories.Length];
        var currentCategory = -1;
        void SelectCategory(int index)
        {
            if (currentCategory >= 0)
            {
                scrollOffsets[currentCategory] = contentScroller.VerticalOffset;
            }

            contentScroller.Content = categories[index].Panel;
            contentScroller.UpdateLayout();
            contentScroller.ScrollToVerticalOffset(scrollOffsets[index]);
            currentCategory = index;
            for (var i = 0; i < navButtons.Count; i++)
            {
                var selected = i == index;
                navButtons[i].Background = selected
                    ? new SolidColorBrush(WpfColor.FromRgb(227, 240, 253))
                    : WpfBrushes.Transparent;
                navButtons[i].Foreground = selected
                    ? (WpfBrush)FindResource("BlueBrush")
                    : (WpfBrush)FindResource("TextBrush");
            }
        }

        for (var i = 0; i < categories.Length; i++)
        {
            var index = i;
            var button = new System.Windows.Controls.Button
            {
                Content = categories[i].Title,
                Style = (Style)FindResource("NavButton")
            };
            button.Click += (_, _) => SelectCategory(index);
            navButtons.Add(button);
            navPanel.Children.Add(button);
        }

        // 运行时源列表：配置的安装位置 + 各实例正在用的运行时（去重）。
        void RefreshMoveSources()
        {
            var items = new List<MoveSourceItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string label, string directory)
            {
                var full = Path.GetFullPath(directory);
                if (seen.Add(full))
                {
                    items.Add(new MoveSourceItem(label, full));
                }
            }

            // 按运行时目录聚合所有实例：同一目录被多个实例共用时合并为一行
            // （移动该目录会同时更新这些实例）。
            var runtimeInstances = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var instance in Instances)
            {
                var directory = ResolveMovableRuntimeDirectory(instance);
                if (directory is null)
                {
                    continue;
                }

                if (!runtimeInstances.TryGetValue(directory, out var names))
                {
                    names = new List<string>();
                    runtimeInstances[directory] = names;
                }

                names.Add(instance.Name);
            }

            foreach (var pair in runtimeInstances)
            {
                Add(string.Join("、", pair.Value), pair.Key);
            }

            // 卡片只列“实例”；没有任何实例运行时可用时给一行提示。
            if (items.Count == 0)
            {
                items.Add(new MoveSourceItem("没有可移动的实例运行时（先在启动页准备或导入实例）", string.Empty));
            }

            moveSourceList.Children.Clear();
            // 首次构建时默认勾选启动页选中的实例（或第一项）；之后保持用户勾选。
            var applyDefault = checkedMoveSources.Count == 0 && !moveSourcesInitialized;
            moveSourcesInitialized = true;
            if (items.Count > 0 && items[0].Directory.Length == 0)
            {
                moveSourceList.Children.Add(new TextBlock
                {
                    Text = items[0].Label,
                    Foreground = (WpfBrush)FindResource("MutedBrush"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 4)
                });
                return;
            }

            var defaultDirectory = applyDefault
                ? ResolveMovableRuntimeDirectory(SelectedInstance) ?? items.FirstOrDefault()?.Directory
                : null;
            foreach (var item in items.Where(item => item.Directory.Length > 0))
            {
                var row = new System.Windows.Controls.CheckBox
                {
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = item.Label,
                                FontWeight = FontWeights.SemiBold,
                                FontSize = 13
                            },
                            new TextBlock
                            {
                                Text = item.Directory,
                                Foreground = (WpfBrush)FindResource("MutedBrush"),
                                FontSize = 11,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                ToolTip = item.Directory,
                                Margin = new Thickness(0, 2, 0, 0)
                            }
                        }
                    },
                    IsChecked = checkedMoveSources.Contains(item.Directory)
                        || (defaultDirectory is not null
                            && string.Equals(item.Directory, defaultDirectory, StringComparison.OrdinalIgnoreCase)),
                    Margin = new Thickness(0, 5, 0, 5)
                };
                var captured = item;
                row.Checked += (_, _) => checkedMoveSources.Add(captured.Directory);
                row.Unchecked += (_, _) => checkedMoveSources.Remove(captured.Directory);
                if (row.IsChecked == true)
                {
                    checkedMoveSources.Add(item.Directory);
                }

                moveSourceList.Children.Add(row);
            }
        }

        RefreshMoveSources();
        host.Loaded += (_, _) => RefreshMoveSources();
        SelectCategory(0);
        return host;
    }

    private void AddProxySection(StackPanel panel)
    {
        panel.Children.Add(new TextBlock
        {
            Text = "代理",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 32, 0, 0)
        });

        var content = new StackPanel();
        var card = new Border
        {
            Background = (WpfBrush)FindResource("CardBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 14, 0, 0),
            Child = content
        };

        LauncherSettingsData current;
        try
        {
            current = _versionSettingsService.ReadLauncherSettings();
        }
        catch
        {
            current = new LauncherSettingsData();
        }

        var enabled = new System.Windows.Controls.CheckBox
        {
            Content = "启用代理（Launcher 联网与实例启动均使用）",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            IsChecked = current.ProxyEnabled
        };
        var urlBox = new System.Windows.Controls.TextBox
        {
            Text = current.ProxyUrl ?? string.Empty,
            Width = 360,
            FontSize = 13,
            Margin = new Thickness(0, 14, 0, 0),
            ToolTip = "形如 http://127.0.0.1:7890"
        };
        var noProxyBox = new System.Windows.Controls.TextBox
        {
            Text = current.NoProxy ?? string.Empty,
            Width = 360,
            FontSize = 13,
            Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "不走代理的地址，逗号分隔；默认已跳过本机回环"
        };
        var applyDsh = new System.Windows.Controls.CheckBox
        {
            Content = "同时注入启动的 dsh 实例（HTTP_PROXY / HTTPS_PROXY / NO_PROXY）",
            FontSize = 13,
            Margin = new Thickness(0, 12, 0, 0),
            IsChecked = current.ProxyApplyDsh
        };
        var saveButton = new System.Windows.Controls.Button
        {
            Content = "保存代理设置",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var status = new TextBlock
        {
            Foreground = (WpfBrush)FindResource("BlueBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        content.Children.Add(enabled);
        content.Children.Add(new TextBlock
        {
            Text = "代理地址（http(s)://主机:端口）",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 14, 0, 0)
        });
        content.Children.Add(urlBox);
        content.Children.Add(new TextBlock
        {
            Text = "NO_PROXY（逗号分隔，可空）",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0)
        });
        content.Children.Add(noProxyBox);
        content.Children.Add(applyDsh);
        content.Children.Add(saveButton);
        content.Children.Add(status);
        panel.Children.Add(card);

        saveButton.Click += (_, _) =>
        {
            try
            {
                var settings = _versionSettingsService.ReadLauncherSettings();
                settings.ProxyEnabled = enabled.IsChecked == true;
                settings.ProxyUrl = string.IsNullOrWhiteSpace(urlBox.Text) ? null : urlBox.Text.Trim();
                settings.NoProxy = string.IsNullOrWhiteSpace(noProxyBox.Text) ? null : noProxyBox.Text.Trim();
                settings.ProxyApplyDsh = applyDsh.IsChecked == true;
                if (settings.ProxyEnabled
                    && !ProxySettings.TryNormalizeServer(settings.ProxyUrl, out _, out var error))
                {
                    status.Text = $"代理地址无效：{error}";
                    return;
                }

                _versionSettingsService.SaveLauncherSettings(settings);
                ProxyConfigurator.ApplyGlobal(settings);
                status.Text = settings.ProxyEnabled
                    ? "已保存并生效（新启动的实例会注入代理环境变量）。"
                    : "已保存：代理关闭，使用直连。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                status.Text = $"保存失败：{ex.Message}";
            }
        };
    }

    private void AddBalanceSection(StackPanel panel)
    {
        panel.Children.Add(new TextBlock
        {
            Text = "余额显示",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 32, 0, 0)
        });

        var content = new StackPanel();
        var card = new Border
        {
            Background = (WpfBrush)FindResource("CardBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 14, 0, 0),
            Child = content
        };

        var enabled = true;
        try
        {
            enabled = _versionSettingsService.ReadLauncherSettings().BalanceEnabled;
        }
        catch
        {
        }

        var toggle = new System.Windows.Controls.CheckBox
        {
            Content = "在启动页显示 DeepSeek 余额（默认关闭）",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            IsChecked = enabled
        };
        var status = new TextBlock
        {
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "开启后，Launcher 会在本机内存读取所选实例 DSH_HOME 下的 .credentials.yaml 并请求 api.deepseek.com 查询余额；" +
                   "Key 不落盘、不写日志、不进诊断包。"
        };
        content.Children.Add(toggle);
        content.Children.Add(status);
        panel.Children.Add(card);

        toggle.Checked += (_, _) => SaveBalance(true);
        toggle.Unchecked += (_, _) => SaveBalance(false);
        return;

        void SaveBalance(bool value)
        {
            try
            {
                var settings = _versionSettingsService.ReadLauncherSettings();
                settings.BalanceEnabled = value;
                _versionSettingsService.SaveLauncherSettings(settings);
                RefreshBalanceAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ShowNotice($"保存余额显示设置失败：{ex.Message}");
            }
        }
    }

    private void AddDiagnoseSection(StackPanel panel)
    {
        panel.Children.Add(new TextBlock
        {
            Text = "诊断与日志",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 32, 0, 0)
        });

        var content = new StackPanel();
        var card = new Border
        {
            Background = (WpfBrush)FindResource("CardBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 14, 0, 0),
            Child = content
        };

        var exportButton = new System.Windows.Controls.Button
        {
            Content = "导出诊断包",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        var openLogButton = new System.Windows.Controls.Button
        {
            Content = "打开日志目录",
            Margin = new Thickness(8, 0, 0, 0)
        };
        var buttons = new WrapPanel();
        buttons.Children.Add(exportButton);
        buttons.Children.Add(openLogButton);
        var status = new TextBlock
        {
            Foreground = (WpfBrush)FindResource("BlueBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "诊断包包含 Launcher 日志、崩溃/守护日志、环境与版本、设置与状态文件（已脱敏）；" +
                   "不包含 .credentials.yaml / 会话内容，产物只落在本机，由你自行决定是否分享。"
        };
        content.Children.Add(buttons);
        content.Children.Add(status);
        panel.Children.Add(card);

        exportButton.Click += async (_, _) =>
        {
            exportButton.IsEnabled = false;
            status.Text = "正在导出诊断包…";
            try
            {
                var result = await Task.Run(() => new DiagnoseExportService().Export());
                status.Text = result.Ok
                    ? $"已导出：{result.ArchivePath}"
                    : $"导出失败：{result.Error}";
            }
            finally
            {
                exportButton.IsEnabled = true;
            }
        };
        openLogButton.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(LauncherLog.LogDirectory);
                Process.Start(new ProcessStartInfo(LauncherLog.LogDirectory) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ShowNotice($"打开日志目录失败：{ex.Message}");
            }
        };
    }

    /// <summary>余额刷新（异步、可取消；未启用或未选实例时隐藏卡片）。</summary>
    private void RefreshBalanceAsync()
    {
        _balanceCancellation?.Cancel();
        _balanceCancellation?.Dispose();
        _balanceCancellation = null;

        var enabled = false;
        try
        {
            enabled = _versionSettingsService.ReadLauncherSettings().BalanceEnabled;
        }
        catch
        {
        }

        if (!enabled || SelectedInstance is not { } instance)
        {
            BalanceText = string.Empty;
            BalanceDetailText = string.Empty;
            BalanceVisibility = Visibility.Collapsed;
            OnPropertyChanged(nameof(BalanceText));
            OnPropertyChanged(nameof(BalanceDetailText));
            OnPropertyChanged(nameof(BalanceVisibility));
            OnPropertyChanged(nameof(BalanceForeground));
            return;
        }

        var cancellation = new CancellationTokenSource();
        _balanceCancellation = cancellation;
        var dshHome = instance.DshHome;
        _ = Task.Run(async () =>
        {
            BalanceResult result;
            try
            {
                result = await _balanceService.GetAsync(dshHome, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                if (result is { Ok: true, Data: { } data })
                {
                    BalanceText = $"余额 {data.Currency} {data.Total}（赠送 {data.Granted} · 充值 {data.ToppedUp}）";
                    BalanceDetailText = data.Available ? "账户可用" : "账户不可用或余额不足";
                    BalanceForeground = new SolidColorBrush(WpfColor.FromRgb(46, 166, 107));
                }
                else
                {
                    // 已开启但取不到：给可见的灰提示（悬停看原因），否则用户会以为开关无效。
                    BalanceText = "余额不可用（悬停查看原因）";
                    BalanceDetailText = result.Error ?? "未知错误";
                    BalanceForeground = new SolidColorBrush(WpfColor.FromRgb(140, 140, 140));
                }

                BalanceVisibility = Visibility.Visible;
                OnPropertyChanged(nameof(BalanceText));
                OnPropertyChanged(nameof(BalanceDetailText));
                OnPropertyChanged(nameof(BalanceVisibility));
                OnPropertyChanged(nameof(BalanceForeground));
            });
        });
    }

    private void AddPluginInstallModeSection(StackPanel panel)
    {
        panel.Children.Add(new TextBlock
        {
            Text = "Plugin 安装",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 32, 0, 0)
        });

        var content = new StackPanel();
        var card = new Border
        {
            Background = (WpfBrush)FindResource("CardBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 14, 0, 0),
            Child = content
        };
        var currentMode = _versionSettingsService.ReadLauncherSettings().PluginInstallMode;
        var compatibility = new System.Windows.Controls.RadioButton
        {
            GroupName = "PluginInstallMode",
            Content = "兼容性安装",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            IsChecked = currentMode == PluginInstallMode.Compatibility
        };
        var fast = new System.Windows.Controls.RadioButton
        {
            GroupName = "PluginInstallMode",
            Content = "快速安装（默认）",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 0),
            IsChecked = currentMode == PluginInstallMode.Fast
        };
        var status = new TextBlock
        {
            Foreground = (WpfBrush)FindResource("BlueBrush"),
            FontSize = 11,
            Margin = new Thickness(24, 12, 0, 0)
        };
        content.Children.Add(compatibility);
        content.Children.Add(new TextBlock
        {
            Text = "复制依赖并重建 pnpm 依赖目录，适合硬链接、权限或旧安装状态异常的电脑；耗时会更长。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 5, 0, 0)
        });
        content.Children.Add(fast);
        content.Children.Add(new TextBlock
        {
            Text = "优先复用本地 pnpm 缓存和默认链接方式；安装更快，遇到链接或旧依赖问题时请切回兼容性安装。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 5, 0, 0)
        });
        content.Children.Add(status);
        panel.Children.Add(card);

        void SaveMode(PluginInstallMode mode)
        {
            try
            {
                var settings = _versionSettingsService.ReadLauncherSettings();
                settings.PluginInstallMode = mode;
                _versionSettingsService.SaveLauncherSettings(settings);
                status.Text = mode == PluginInstallMode.Fast
                    ? "已使用快速安装。"
                    : "已使用兼容性安装。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                status.Text = $"保存失败：{ex.Message}";
            }
        }

        compatibility.Checked += (_, _) => SaveMode(PluginInstallMode.Compatibility);
        fast.Checked += (_, _) => SaveMode(PluginInstallMode.Fast);
    }

    private void AddCloseBehaviorSection(StackPanel panel)
    {
        panel.Children.Add(new TextBlock
        {
            Text = "关闭主窗口时",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 32, 0, 0)
        });

        var content = new StackPanel();
        var card = new Border
        {
            Background = (WpfBrush)FindResource("CardBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 14, 0, 0),
            Child = content
        };
        var current = CloseBehavior.MinimizeToTray;
        try
        {
            current = _versionSettingsService.ReadLauncherSettings().CloseBehavior;
        }
        catch
        {
        }

        var minimize = new System.Windows.Controls.RadioButton
        {
            GroupName = "CloseBehavior",
            Content = "最小化到托盘",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            IsChecked = current == CloseBehavior.MinimizeToTray
        };
        var exitAndStop = new System.Windows.Controls.RadioButton
        {
            GroupName = "CloseBehavior",
            Content = "关闭启动器与实例",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 0),
            IsChecked = current == CloseBehavior.ExitAndStopInstances
        };
        var status = new TextBlock
        {
            Foreground = (WpfBrush)FindResource("BlueBrush"),
            FontSize = 11,
            Margin = new Thickness(24, 12, 0, 0)
        };
        content.Children.Add(minimize);
        content.Children.Add(new TextBlock
        {
            Text = "主窗口隐藏到托盘，实例继续运行；双击托盘图标或菜单“打开”恢复。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 5, 0, 0)
        });
        content.Children.Add(exitAndStop);
        content.Children.Add(new TextBlock
        {
            Text = "退出应用并停止该实例（与托盘菜单“退出”相同）；外部 Attached 实例不受影响。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 5, 0, 0)
        });
        content.Children.Add(status);
        panel.Children.Add(card);

        void SaveBehavior(CloseBehavior behavior)
        {
            try
            {
                var settings = _versionSettingsService.ReadLauncherSettings();
                settings.CloseBehavior = behavior;
                _versionSettingsService.SaveLauncherSettings(settings);
                status.Text = behavior == CloseBehavior.MinimizeToTray
                    ? "已设置：最小化到托盘。"
                    : "已设置：关闭启动器与实例。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                status.Text = $"保存失败：{ex.Message}";
            }
        }

        minimize.Checked += (_, _) => SaveBehavior(CloseBehavior.MinimizeToTray);
        exitAndStop.Checked += (_, _) => SaveBehavior(CloseBehavior.ExitAndStopInstances);
    }

    private void AddWatchdogSection(StackPanel panel)
    {
        panel.Children.Add(new TextBlock
        {
            Text = "实例守护",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 32, 0, 0)
        });

        var content = new StackPanel();
        var card = new Border
        {
            Background = (WpfBrush)FindResource("CardBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 14, 0, 0),
            Child = content
        };

        var current = 5;
        try
        {
            current = _versionSettingsService.ReadLauncherSettings().WatchdogProbeSeconds;
        }
        catch
        {
        }

        var intervalBox = new System.Windows.Controls.TextBox
        {
            Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Width = 80,
            FontSize = 15,
            Padding = new Thickness(8, 5, 8, 5),
            VerticalAlignment = VerticalAlignment.Center
        };
        // 只允许数字输入
        intervalBox.PreviewTextInput += (_, args) =>
        {
            args.Handled = args.Text.Any(char.IsDigit) == false;
        };
        intervalBox.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Space)
            {
                args.Handled = true;
            }
        };

        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4)
        };
        row.Children.Add(new TextBlock
        {
            Text = "每（秒）检查一次实例存活",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new System.Windows.Controls.Border
        {
            Width = 8
        });
        row.Children.Add(intervalBox);
        row.Children.Add(new TextBlock
        {
            Text = "秒",
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        });
        content.Children.Add(row);
        content.Children.Add(new TextBlock
        {
            Text = "启动器每 N 秒探测一次实例（插件市场的「重启以生效」会把 dsh 进程换成新实例，更短的间隔能更快发现并接管；范围 2–120 秒，默认 5）。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 8)
        });

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        var saveButton = new System.Windows.Controls.Button
        {
            Content = "保存",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(14, 9, 14, 9)
        };
        var status = new TextBlock
        {
            Foreground = (WpfBrush)FindResource("BlueBrush"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(status);
        content.Children.Add(buttons);
        panel.Children.Add(card);

        saveButton.Click += (_, _) =>
        {
            try
            {
                if (!int.TryParse(intervalBox.Text.Trim(), out var seconds)
                    || seconds is < 2 or > 120)
                {
                    status.Text = "请输入 2–120 之间的整数";
                    return;
                }

                var settings = _versionSettingsService.ReadLauncherSettings();
                settings.WatchdogProbeSeconds = seconds;
                _versionSettingsService.SaveLauncherSettings(settings);
                _watchdog.SetProbeInterval(seconds);   // 即时生效
                status.Text = $"已保存：每 {seconds} 秒检查一次";
                ShowNotice($"实例守护探测间隔已设为 {seconds} 秒，立即生效。");
            }
            catch (Exception ex)
            {
                status.Text = "保存失败";
                ShowNotice($"保存实例守护设置失败：{ex.Message}");
            }
        };
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
                    NodeExecutablePath = current.NodeExecutablePath,
                    OpenMode = current.OpenMode,
                    CustomOpenTargetPath = current.CustomOpenTargetPath,
                    UseDshMarketHotReload = current.UseDshMarketHotReload
                };
                var snapshot = instance.RuntimeStatus != InstanceRuntimeStatus.Running
                    && instance.RuntimeOwnership != InstanceRuntimeOwnership.Attached
                    && !_instanceRunner.IsRunning(instance.Id)
                    ? _versionSnapshotService.CreateSnapshot(instance, "保存设置页版本同步配置前", automatic: true)
                    : null;
                _versionSettingsService.Save(instance, updated);
                RefreshWorkspaceChoices(workspace);
                versionStatus.Text = snapshot is null
                    ? $"已保存 {instance.Name} 的同步配置。"
                    : $"已保存 {instance.Name} 的同步配置，并保留修改前快照。";
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

    private bool IsPreparedNodeUsable(string? requiredEngine) =>
        _nodeRuntime.IsAvailable
        && (string.IsNullOrWhiteSpace(requiredEngine)
            || _nodeRuntime.GetCompatibility(requiredEngine) == NodeRuntimeCompatibility.Compatible);

    private async Task<bool> PrepareRuntimeAsync(string sourceName, string nodeDistBase, string? npmRegistry, ManagerInstance? target)
    {
        if (_isRuntimePrepareInProgress)
        {
            return false;
        }

        var packagedRuntime = ResolveUsablePackagedRuntime(target, _dshRuntime);
        if (_isNodeDetectionInProgress && packagedRuntime is null)
        {
            ShowNotice("Node.js 检测进行中，请稍候再准备运行环境。");
            return false;
        }

        var prepareNodeEngine = GetNodeEngineRequirement(target);
        if (packagedRuntime is null
            && _nodeRuntime.IsAvailable
            && !string.IsNullOrWhiteSpace(prepareNodeEngine)
            && _nodeRuntime.GetCompatibility(prepareNodeEngine) != NodeRuntimeCompatibility.Compatible)
        {
            var usePortable = System.Windows.MessageBox.Show(this,
                $"当前 Node.js {_nodeRuntime.VersionText} 与 DeepSeek Harness 要求（{prepareNodeEngine}）不兼容。\n\n是否下载便携版 Node.js 到 Launcher 自己的目录？\n（免管理员、不修改系统 PATH、不卸载或影响现有 Node.js）",
                "运行环境不兼容",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!usePortable)
            {
                return false;
            }
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

            // 便携版 Node 优先（免管理员、不写系统 PATH）：既解决“没装 Node”，
            // 也解决“现有 Node 版本低于 dsh 红线（≥22.19）”；下载失败时再询问
            // 是否改用官方 MSI（会弹 UAC）。
            var nodeNeedsPreparation = packagedRuntime is null && !IsPreparedNodeUsable(prepareNodeEngine);
            if (nodeNeedsPreparation)
            {
                progressWindow.SetIndeterminate(false);
                progressWindow.SetStatus(_nodeRuntime.IsAvailable
                    ? $"当前 Node.js {_nodeRuntime.VersionText} 不满足要求，正在通过 {sourceName} 下载便携版 Node.js（免管理员）…"
                    : $"正在通过 {sourceName} 下载便携版 Node.js（免管理员）…");
                var nodeResult = await _portableNodeInstaller.InstallAsync(
                    nodeDistBase,
                    progress,
                    cancellation.Token,
                    prepareNodeEngine);
                if (!nodeResult.IsSuccess && !nodeResult.IsCancelled)
                {
                    var useInstaller = System.Windows.MessageBox.Show(
                        this,
                        $"便携版 Node.js 准备失败：\n{nodeResult.Error}\n\n是否改用官方安装程序？（需要管理员权限，会弹出 UAC）",
                        "准备运行环境",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes;
                    if (useInstaller)
                    {
                        progressWindow.SetStatus($"正在通过 {sourceName} 下载 Node.js 安装程序…");
                        nodeResult = await _nodeInstaller.InstallAsync(
                            nodeDistBase,
                            progress,
                            onInstallStarted: () =>
                            {
                                progressWindow.SetInstallPhase(true);
                                SetRuntimeInstallPhase(true);
                            },
                            cancellation.Token,
                            prepareNodeEngine);
                    }
                }

                if (!nodeResult.IsSuccess)
                {
                    progressWindow.SetStatus(nodeResult.Error ?? "Node.js 准备失败。");
                    System.Windows.MessageBox.Show(this, nodeResult.Error ?? "Node.js 准备失败。", "准备运行环境", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                RevealRuntimeAfterBootstrap(TestRuntimeKind.Node);
                progressWindow.SetStatus("Node.js 已就绪，正在重新检测…");
                for (var attempt = 0; attempt < 5 && !IsPreparedNodeUsable(prepareNodeEngine); attempt++)
                {
                    await RefreshNodeAsync();
                    if (!IsPreparedNodeUsable(prepareNodeEngine))
                    {
                        await Task.Delay(1000, cancellation.Token);
                    }
                }

                if (!IsPreparedNodeUsable(prepareNodeEngine))
                {
                    progressWindow.SetStatus("Node.js 准备后仍未被检测到或版本不满足要求，请重新检测或检查安装路径。");
                    System.Windows.MessageBox.Show(this, "Node.js 准备后仍未被检测到或版本不满足要求，请重新检测或检查安装路径。", "准备运行环境", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                nodeWasInstalled = true;
            }

            // MSI 只更新系统环境，当前 Launcher 进程的 PATH 仍是旧值；
            // 把新检测到的 Node 目录补到进程 PATH，DSh 检测/启动才能解析 node。
            if (packagedRuntime is null)
            {
                EnsureNodeDirectoryOnPath(_nodeRuntime.ExecutablePath);
            }

            progressWindow.SetInstallPhase(false);
            SetRuntimeInstallPhase(false);

            if (nodeWasInstalled && !_dshRuntime.IsAvailable)
            {
                // 启动时缺 Node 会让已有的 dsh shim 无法执行、DSh 检测误报缺失；
                // Node 就绪后先重新检测 DSh 再决定是否安装，避免无谓重装
                // （npm 失败时还会把本来已可用的环境误报为准备失败）。
                await RefreshDshAsync(forceRefresh: true);
            }

            var shouldInstallDsh = packagedRuntime is null
                && (DshInstallService.ShouldInstallGlobalDSh(
                    _dshRuntime.IsAvailable,
                    target?.Kind)
                || (target?.Kind is null or InstanceKind.Installed
                    && !string.IsNullOrWhiteSpace(dshInstallDirectory)
                    && !DshRuntimeDetector.IsExecutableInInstallDirectory(
                        _dshRuntime.ExecutablePath,
                        dshInstallDirectory)));
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
                    await RefreshDshAsync(forceRefresh: true);
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
            if (packagedRuntime is null
                && _nodeRuntime.GetCompatibility(finalRequirement) != NodeRuntimeCompatibility.Compatible)
            {
                var message = $"Node.js {_nodeRuntime.VersionText} 与当前 DSh 要求（{finalRequirement ?? "未声明"}）不兼容。\n\n"
                    + "Launcher 不会自动卸载现有 Node.js。请安装满足要求的兼容版本后重试。";
                progressWindow.SetStatus(message);
                System.Windows.MessageBox.Show(this, message, "运行环境不兼容", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            progressWindow.SetStatus("运行环境已就绪。");
            ShowNotice(packagedRuntime is not null
                ? $"运行环境已就绪：{packagedRuntime.ProductName ?? "封装应用"} 内置 DSh，不需要系统 Node.js。"
                : target?.Kind == InstanceKind.Source
                ? $"运行环境已准备完成：Node.js {_nodeRuntime.VersionText}。"
                : $"运行环境已准备完成：Node.js {_nodeRuntime.VersionText}，{_dshRuntime.DisplayVersionText}。");
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
        // 入口 shim 仍在但 package 目录已删除的实例同样无法启动，
        // 必须一并视为运行目录失效（否则会被 Runner 的 RootPath 检查拒绝）。
        var installedRuntimeValid = target.Kind != InstanceKind.Installed
            || (DshRuntimeCommandFactory.IsUsable(target.EffectiveDshLaunchSpec)
                && DshRuntimeDetector.TryResolvePackageRoot(target.RootPath) is not null);
        if (!installedRuntimeValid)
        {
            return false;
        }

        if (target.Kind == InstanceKind.Installed
            && target.EffectiveDshLaunchSpec?.UsesPackagedNode == true)
        {
            return true;
        }

        return nodeRuntime.IsAvailable
            && nodeRuntime.GetCompatibility(nodeEngineRequirement) == NodeRuntimeCompatibility.Compatible;
    }

    internal static bool IsGlobalRuntimeReady(NodeRuntimeInfo nodeRuntime, string? dshNodeEngine) =>
        nodeRuntime.IsAvailable
        && (string.IsNullOrWhiteSpace(dshNodeEngine)
            || nodeRuntime.GetCompatibility(dshNodeEngine) == NodeRuntimeCompatibility.Compatible);

    internal static bool IsDetectedRuntimeReady(NodeRuntimeInfo nodeRuntime, DshRuntimeInfo dshRuntime) =>
        dshRuntime.EffectiveLaunchSpec is { UsesPackagedNode: true } packagedRuntime
            && DshRuntimeCommandFactory.IsUsable(packagedRuntime)
            || IsGlobalRuntimeReady(nodeRuntime, dshRuntime.NodeEngine);

    private static DshRuntimeLaunchSpec? ResolveUsablePackagedRuntime(
        ManagerInstance? target,
        DshRuntimeInfo detectedRuntime)
    {
        var runtime = target?.EffectiveDshLaunchSpec ?? detectedRuntime.EffectiveLaunchSpec;
        return runtime is { UsesPackagedNode: true }
            && DshRuntimeCommandFactory.IsUsable(runtime)
                ? runtime
                : null;
    }

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

        var packagedRuntime = ResolveUsablePackagedRuntime(instance, _dshRuntime);
        if (packagedRuntime is null && !_nodeRuntime.IsAvailable)
        {
            missing.Add("Node.js 未安装（可一键准备）");
        }
        else if (packagedRuntime is null
            && _nodeRuntime.GetCompatibility(requirement) != NodeRuntimeCompatibility.Compatible)
        {
            missing.Add($"Node.js 版本不兼容（当前 {_nodeRuntime.VersionText}，要求 {(string.IsNullOrWhiteSpace(requirement) ? "未声明" : requirement)}）");
            needsManualNode = true;
        }

        if (instance.Kind == InstanceKind.Installed
            && (!DshRuntimeCommandFactory.IsUsable(instance.EffectiveDshLaunchSpec)
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

    private async void AddInstance_Click(object sender, RoutedEventArgs e)
    {
        var selectedDirectory = PickFolder("选择已安装 DSh 的目录", _dshRuntime.PackageRoot);
        if (selectedDirectory is null)
        {
            return;
        }

        await ScanAndRegisterRuntimeDirectoryAsync(selectedDirectory);
    }

    private async Task<IReadOnlyList<ManagerInstance>> ScanAndRegisterRuntimeDirectoryAsync(string selectedDirectory)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        var progressWindow = new RuntimeProgressWindow(
            this,
            cancellation,
            "扫描 DeepSeek Harness",
            "只检查所选目录，不扫描整块磁盘；可以随时取消。");
        progressWindow.SetIndeterminate(true);
        progressWindow.SetStatus("正在查找 DSH Desktop、npm 安装和源码目录…");
        progressWindow.Show();
        try
        {
            var progress = new Progress<DshRuntimeScanProgress>(item =>
                progressWindow.SetProgress(item.Completed, item.Total, item.Message));
            var scan = await _dshDetector.ScanDirectoryAsync(
                selectedDirectory,
                progress,
                cancellation.Token);
            if (scan.Runtimes.Count == 0)
            {
                ShowNotice(scan.FoundCandidate
                    ? "找到了疑似 DSh 文件，但版本命令或 package.json 校验未通过。"
                    : "所选目录中没有找到可用的 DSH Desktop 或 @deepseek-ai/dsh。源码目录请使用版本控制中的源码导入入口。");
                return Array.Empty<ManagerInstance>();
            }

            _detectedDshRuntimes = scan.Runtimes
                .Concat(_detectedDshRuntimes)
                .Where(static runtime => !string.IsNullOrWhiteSpace(runtime.PackageRoot))
                .GroupBy(static runtime => Path.GetFullPath(runtime.PackageRoot!), StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToArray();
            _dshRuntime = _detectedDshRuntimes[0];
            progressWindow.SetIndeterminate(true);
            progressWindow.SetStatus("正在导入新实例或更新同地址实例…");
            var import = await AutoImportDetectedDshRuntimesAsync(
                scan.Runtimes,
                refreshRegisteredRuntimeRoots: true,
                cancellationToken: cancellation.Token);
            var changed = import.AddedInstances
                .Concat(import.UpdatedInstances)
                .Concat(import.BackfilledInstances)
                .GroupBy(static instance => instance.Id, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
            OnPropertyChanged(nameof(DshStatusText));
            OnPropertyChanged(nameof(DshStatusBrush));
            OnPropertyChanged(nameof(DshVersionText));
            OnPropertyChanged(nameof(NodeStatusText));
            OnPropertyChanged(nameof(NodeVersionText));
            OnPropertyChanged(nameof(NodePathText));
            if (changed.Length == 0)
            {
                ShowNotice($"已识别 {scan.Runtimes.Count} 个运行环境，但没有可导入或更新的实例。");
            }

            return changed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowNotice("目录扫描已取消。");
            return Array.Empty<ManagerInstance>();
        }
        catch (Exception ex)
        {
            ShowNotice($"导入实例失败：{ex.Message}");
            return Array.Empty<ManagerInstance>();
        }
        finally
        {
            progressWindow.Close();
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
            MarkInstanceUsed(instance.Id);
            RefreshRecentInstances();
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
        if (IsCustomOpenBound)
        {
            OpenCustomTarget();
            return;
        }

        await StartSelectedInstanceAsync();
    }

    private async void StartLauncherInstance_Click(object sender, RoutedEventArgs e)
    {
        await StartSelectedInstanceAsync();
    }

    private async Task StartSelectedInstanceAsync()
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
                if (GetSelectedOpenMode() == VersionOpenMode.Web)
                {
                    // Web 启动模式：重新打开 = 在默认浏览器打开（与 dsh 原生行为一致）；
                    // 0.1.2-rc.1 起页面需要 launch token，优先用带 token 的地址。
                    OpenWebUrlInBrowser(selected.AuthenticatedWebUrl ?? selected.WebUrl);
                    ShowNotice($"实例仍在运行，已在默认浏览器打开：{selected.WebUrl}。");
                }
                else
                {
                    if (!TryFocusChatWindow(selected.Id))
                    {
                        OpenChatWindow(selected.Id, selected.AuthenticatedWebUrl ?? selected.WebUrl);
                    }

                    ShowNotice($"实例仍在运行，已重新打开：{selected.Name}。关闭窗口不会停止实例。 ");
                }
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
            UpdateInstanceStatus(selected, InstanceRuntimeStatus.Error, ex.ToString());
            ShowStartFailure(ex.ToString());
        }
        finally
        {
            EndLifecycleOperation();
        }
    }

    private void OpenDesktopShell_Click(object sender, RoutedEventArgs e)
    {
        var instance = SelectedInstance;
        var runtime = instance?.EffectiveDshLaunchSpec;
        if (instance is null
            || runtime?.Mode != DshRuntimeLaunchMode.ElectronBootstrap
            || !DshRuntimeCommandFactory.IsUsable(runtime))
        {
            ShowNotice("当前版本不是可用的 DSH Desktop 封装运行环境。");
            return;
        }

        if (_instanceRunner.IsRunning(instance.Id)
            || instance.RuntimeStatus == InstanceRuntimeStatus.Running)
        {
            ShowNotice("请先停止这个版本，再打开 DSH Desktop 原生窗口，避免两个进程同时写入同一个 DSH_HOME。");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = runtime.HostPath,
                WorkingDirectory = Path.GetDirectoryName(runtime.HostPath) ?? instance.RootPath,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            startInfo.Environment["DSH_HOME"] = instance.DshHome;
            startInfo.Environment["DSH_AGENTS_HOME"] = Path.Combine(instance.DshHome, ".agents");
            startInfo.Environment["PATH"] = RuntimeSearchPaths.BuildCurrentPath(runtime.HostPath);
            if (Process.Start(startInfo) is null)
            {
                ShowNotice("DSH Desktop 原生窗口启动失败。");
                return;
            }

            ShowNotice($"已使用 {instance.Name} 的隔离数据打开 DSH Desktop 原生窗口。此实验窗口由 DSH Desktop 自己管理。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            ShowNotice($"打开 DSH Desktop 原生窗口失败：{ex.Message}");
        }
    }

    private async Task<bool> StopInstanceForPluginRetryAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken)
    {
        if (instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            ShowNotice("当前实例连接的是外部 DSh 服务，Launcher 不会停止该进程。");
            return false;
        }

        if (!TryBeginLifecycleOperation())
        {
            return false;
        }

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _windowCancellation.Token);
            var result = await _instanceRunner.StopAsync(instance.Id, linkedCancellation.Token);
            if (!result.IsSuccess)
            {
                UpdateInstanceStatus(instance, InstanceRuntimeStatus.Error, result.Error);
                ShowNotice(result.Error ?? "停止 DSh 失败。");
                return false;
            }

            CloseChatWindow(instance.Id);
            var stopped = instance with
            {
                RuntimeStatus = InstanceRuntimeStatus.Stopped,
                RuntimeOwnership = InstanceRuntimeOwnership.None,
                ProcessId = null,
                Port = null,
                WebUrl = null,
                AuthenticatedWebUrl = null,
                LastError = null
            };
            UpdateInstance(stopped);
            await SynchronizeConversationsAsync(stopped);
            ReportInstanceStoppedAsync(instance.Id);
            ShowNotice($"实例已停止，正在继续安装 Plugin：{instance.Name}。");
            return true;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || _windowCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            ShowNotice($"停止 DSh 失败：{ex.Message}");
            return false;
        }
        finally
        {
            EndLifecycleOperation();
        }
    }

    private void OpenCustomTarget()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            ShowNotice("请先选择版本。");
            return;
        }

        if (_instanceRunner.IsRunning(instance.Id)
            || instance.RuntimeStatus == InstanceRuntimeStatus.Running)
        {
            ShowNotice("请先停止这个版本，再使用绑定的打开方式，避免两个进程同时写入同一个 DSH_HOME。");
            return;
        }

        try
        {
            var settings = _versionSettingsService.Read(instance);
            var startInfo = VersionOpenTargetService.CreateStartInfo(
                instance,
                settings.CustomOpenTargetPath ?? string.Empty);
            if (Process.Start(startInfo) is null)
            {
                ShowNotice("绑定的打开方式启动失败。");
                return;
            }

            ShowNotice($"已通过 {Path.GetFileName(settings.CustomOpenTargetPath)} 打开 {instance.Name}，并传入该版本的隔离数据目录。");
        }
        catch (Exception ex) when (ex is Win32Exception
            or IOException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            ShowNotice($"打开绑定入口失败：{ex.Message}");
        }
    }

    private async void StopInstance_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is null)
        {
            return;
        }

        await StopInstanceAsync(SelectedInstance);
    }

    private async Task StopInstanceAsync(ManagerInstance selected)
    {
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
                AuthenticatedWebUrl = null,
                LastError = null
            });
            await SynchronizeConversationsAsync(selected with
            {
                RuntimeStatus = InstanceRuntimeStatus.Stopped,
                RuntimeOwnership = InstanceRuntimeOwnership.None,
                ProcessId = null,
                Port = null,
                WebUrl = null,
                AuthenticatedWebUrl = null,
                LastError = null
            });
            ReportInstanceStoppedAsync(selected.Id);
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
                    AuthenticatedWebUrl = null,
                    LastError = null
                };
                UpdateInstance(selected);
                await SynchronizeConversationsAsync(selected);
                ReportInstanceStoppedAsync(selected.Id);
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
                ShowStartFailure(result.Error, "DSh 重启失败。");
                return;
            }

            UpdateInstance(selected with
            {
                RuntimeStatus = InstanceRuntimeStatus.Running,
                RuntimeOwnership = InstanceRuntimeOwnership.Managed,
                ProcessId = result.ProcessId,
                Port = result.Port,
                WebUrl = result.WebUrl,
                AuthenticatedWebUrl = result.AuthenticatedWebUrl,
                LastError = null,
                LastUsedAt = DateTimeOffset.UtcNow
            });
            OpenChatWindow(selected.Id, result.AuthenticatedWebUrl ?? result.WebUrl);
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
            await RefreshDshAsync(forceRefresh: true);
            ShowNotice(installDirectory is null
                ? $"DSh 安装/更新完成：{_dshRuntime.DisplayVersionText}。可以重新检测并注册实例。"
                : $"DSh 安装/更新完成：{_dshRuntime.DisplayVersionText} · {installDirectory}。");
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
                AuthenticatedWebUrl = status == InstanceRuntimeStatus.Running ? original.AuthenticatedWebUrl : null,
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
        if (!project.IsValid || !project.IsDshSource)
        {
            ShowNotice(project.Error ?? "所选目录已不再是可识别的 DeepSeek Harness 源码项目。");
            return false;
        }

        if (!project.DependenciesPresent || project.BuiltCliEntrypoint is null)
        {
            var steps = new List<string>();
            if (!project.DependenciesPresent)
            {
                steps.Add($"安装依赖（{project.PackageManager ?? "npm"}）");
            }

            if (project.BuiltCliEntrypoint is null)
            {
                steps.Add($"构建源码（{project.BuildCommand}）");
            }

            var confirm = System.Windows.MessageBox.Show(
                this,
                "这个源码版本还不能直接启动。Launcher 将在源码目录执行：\n\n"
                + string.Join("\n", steps.Select(static step => "• " + step))
                + "\n\n这可能联网下载依赖，并会修改该源码目录。是否继续？",
                "准备源码版本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                ShowNotice("已取消准备源码版本。");
                return false;
            }
        }

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

    private async Task<DshInstanceRunResult?> StartManagedInstanceAsync(
        ManagerInstance instance,
        bool openBrowser = false)
    {
        await SynchronizeModelProvidersAsync(instance, notifyNoConfiguration: true);
        await SynchronizeConversationsAsync(instance);
        if (!await PrepareSourceAsync(instance))
        {
            return null;
        }

        var result = await _instanceRunner.StartAsync(
            instance,
            _nodeRuntime,
            _windowCancellation.Token,
            openBrowser);
        if (!result.IsSuccess || result.ProcessId is null || result.Port is null || result.WebUrl is null)
        {
            UpdateInstanceStatus(instance, InstanceRuntimeStatus.Error, result.Error);
            return result;
        }

        // 浏览器守卫：仅当 dsh 不支持 --no-open（可能自行弹出浏览器）且本次不由
        // Launcher 开浏览器时启用；守卫只在启动后 30 秒窗口内结束命令行带该端口的浏览器进程。
        if (!openBrowser
            && result.Port is { } guardedPort
            && !DshInstanceRunner.SupportsNoOpen(instance.DetectedVersion))
        {
            _browserGuard.Watch(guardedPort);
        }

        UpdateInstance(instance with
        {
            RuntimeStatus = InstanceRuntimeStatus.Running,
            RuntimeOwnership = InstanceRuntimeOwnership.Managed,
            ProcessId = result.ProcessId,
            Port = result.Port,
            WebUrl = result.WebUrl,
            AuthenticatedWebUrl = result.AuthenticatedWebUrl,
            LastError = null,
            LastUsedAt = DateTimeOffset.UtcNow
        });
        ReportInstanceStartedAsync(
            instance,
            result.ProcessId.Value,
            result.Port.Value,
            result.WebUrl,
            result.AuthenticatedWebUrl);
        return result;
    }

    private void UpdateInstance(ManagerInstance updated)
    {
        updated = _instanceRegistry.Update(updated);
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

        RefreshRecentInstances();

        OnPropertyChanged(nameof(SelectedInstanceStatus));
        OnPropertyChanged(nameof(SelectedInstanceStatusBrush));
        OnPropertyChanged(nameof(SelectedInstanceStatusBackgroundBrush));
        OnPropertyChanged(nameof(SelectedInstanceStatusTextBrush));
        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(StartInstanceButtonText));
        OnPropertyChanged(nameof(LauncherStartVisibility));
        OnPropertyChanged(nameof(DesktopShellVisibility));
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
            if (e.ClickCount == 2)
            {
                // 双击标题栏：最大化/还原（标准窗口行为）
                ToggleMaximize();
            }
            else
            {
                DragMove();
            }
        }
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizeGlyph();
        UpdateMaximizeVisuals();
        if (WindowState == WindowState.Maximized)
        {
            // AllowsTransparency 的分层窗口不走系统最大化路径，WM_GETMINMAXINFO
            // 的工作区限制会被 WPF 忽略（实测按“显示器+外框”铺满，底边越过任务栏）。
            // 在布局完成后把窗口显式对齐到当前显示器的工作区。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyMaximizedWorkArea));
        }
    }

    private void ApplyMaximizedWorkArea()
    {
        if (WindowState != WindowState.Maximized)
        {
            return;
        }

        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new NativeMonitorInfo
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        // 外层矩形 == 客户端（无边框）；直接把窗口定到工作区矩形。
        SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            info.rcWork.Left,
            info.rcWork.Top,
            info.rcWork.Right - info.rcWork.Left,
            info.rcWork.Bottom - info.rcWork.Top,
            SetWindowPosNoZOrder | SetWindowPosNoActivate);
    }

    private void UpdateMaximizeGlyph()
    {
        if (MaximizeGlyph is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Text = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
    }

    /// <summary>
    /// 最大化时把外层边距/圆角/描边归零，直角占满工作区（配合 WM_GETMINMAXINFO）；
    /// 恢复普通状态时还原圆角外观。
    /// </summary>
    private void UpdateMaximizeVisuals()
    {
        if (AppRootBorder is null || TitleBarBorder is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        AppRootBorder.Margin = maximized ? new Thickness(0) : new Thickness(4);
        AppRootBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(18);
        AppRootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        TitleBarBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(17, 17, 0, 0);
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

    private void ContextInstanceSettings_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is null)
        {
            ShowNotice("请先选择一个实例。 ");
            return;
        }

        ShowVersionSettings(openPluginPage: true);
    }

    private void VersionSettingsBack_Click(object sender, RoutedEventArgs e)
    {
        SwitchSection(_versionSettingsReturnSection);
    }

    private void ContextInstanceMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || Instances.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            Width = 280,
            Style = (Style)FindResource("ContextInstanceMenuStyle")
        };
        foreach (var instance in Instances.OrderByDescending(static item => item.RecentSortAt))
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = instance.RuntimeStatus switch
                {
                    InstanceRuntimeStatus.Running => new SolidColorBrush(WpfColor.FromRgb(46, 166, 107)),
                    InstanceRuntimeStatus.Error => new SolidColorBrush(WpfColor.FromRgb(217, 74, 74)),
                    _ => new SolidColorBrush(WpfColor.FromRgb(150, 163, 181))
                },
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            header.Children.Add(dot);
            header.Children.Add(new TextBlock
            {
                Text = instance.Name,
                MaxWidth = 222,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var item = new MenuItem
            {
                Header = header,
                IsChecked = string.Equals(instance.Id, SelectedInstance?.Id, StringComparison.Ordinal),
                Style = (Style)FindResource("ContextInstanceMenuItemStyle")
            };
            item.Click += (_, _) => SwitchContextInstance(instance, _currentSection);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void RenameSelectedInstance_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } selected)
        {
            ShowNotice("请先选择一个实例。 ");
            return;
        }

        var name = TextPromptWindow.Show(this, "重命名实例", "输入新的实例名称：", selected.Name);
        if (name is null)
        {
            return;
        }

        try
        {
            UpdateInstance(selected with { Name = name });
            ShowNotice($"实例已重命名为“{SelectedInstance?.Name}”。 ");
        }
        catch (Exception ex)
        {
            ShowNotice($"重命名实例失败：{ex.Message}");
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_blockWindowCloseForMsi)
        {
            e.Cancel = true;
            ShowNotice(_nodeInstaller.HasLingeringInstaller
                ? "Windows Installer 仍在后台完成 Node.js 安装，结束后才能关闭 Launcher。"
                : "Node.js 系统安装正在进行，请等待安装完成后再关闭 Launcher。");
            return;
        }

        // 点击 × 的行为由设置（设置 / 诊断 → 关闭主窗口时）决定：
        // MinimizeToTray —— 隐藏到托盘（默认，实例继续运行）；
        // ExitAndStopInstances —— 退出并停止 Launcher 管理的实例（等同托盘菜单“退出”）。
        // 窗口位置/尺寸在任何关闭路径下都持久化（包括隐藏到托盘）。
        if (!_shutdownCleanupStarted)
        {
            _windowStateStore.Save(WindowStateStore.Capture(this));
        }

        var closeBehavior = CloseBehavior.MinimizeToTray;
        try
        {
            closeBehavior = _versionSettingsService.ReadLauncherSettings().CloseBehavior;
        }
        catch
        {
            // 设置不可读时按默认行为处理
        }

        if (closeBehavior == CloseBehavior.MinimizeToTray
            && !_shutdownFromTray
            && !_shutdownCleanupStarted)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (!_shutdownCleanupCompleted)
        {
            e.Cancel = true;
            if (_shutdownCleanupStarted)
            {
                return;
            }

            _shutdownCleanupStarted = true;
            _windowCancellation.Cancel();
            CloseAllChatWindows();
            try
            {
                await _instanceRunner.DisposeAsync();
            }
            catch
            {
                // A failed DSh child-process cleanup must not leave Launcher in the background.
            }

            _shutdownCleanupCompleted = true;
            _watchdog.TerminateAllAndClear();
            _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Background);
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowProcedure);
        _trayIconService = new Services.TrayIconService(
            LoadLauncherIcon(),
            "DSH Launcher",
            BuildTrayRunningItems,
            OpenInstanceFromTray,
            StopInstanceFromTray,
            ShowMainWindowFromTray,
            RequestShutdownFromTray);
    }

    private IReadOnlyList<TrayRunningItem> BuildTrayRunningItems() =>
        Instances
            .Where(instance => _instanceRunner.IsRunning(instance.Id))
            .Select(instance => new TrayRunningItem(
                instance.Id,
                instance.Name,
                $"{instance.DshVersionText} · {instance.StatusText}"))
            .ToArray();

    /// <summary>托盘菜单“打开”：Web 模式开浏览器，Desktop/Custom 模式开 Chat 窗口（运行中实例）。</summary>
    private void OpenInstanceFromTray(string instanceId)
    {
        var instance = Instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, instanceId, StringComparison.Ordinal));
        if (instance is null)
        {
            return;
        }

        SelectedInstance = instance;
        _ = StartSelectedInstanceAsync();
    }

    /// <summary>托盘菜单“停止”：仅停止 Launcher 管理的实例（Attached 外部实例不受影响）。</summary>
    private void StopInstanceFromTray(string instanceId)
    {
        var instance = Instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, instanceId, StringComparison.Ordinal));
        if (instance is null)
        {
            return;
        }

        SelectedInstance = instance;
        _ = StopInstanceAsync(instance);
    }

    private static System.Drawing.Icon LoadLauncherIcon()
    {
        using var stream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/DSHLauncher.ico"))!.Stream;
        return new System.Drawing.Icon(stream);
    }

    private void ShowMainWindowFromTray()
    {
        if (_shutdownCleanupStarted)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void RequestShutdownFromTray()
    {
        if (_shutdownCleanupStarted)
        {
            return;
        }

        _shutdownFromTray = true;
        if (!IsVisible)
        {
            Show();
        }

        Close();
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmSettingChange = 0x001A;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr windowHandle, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPos
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;

    private void HandleWmGetMinMaxInfo(IntPtr windowHandle, IntPtr wordParameter)
    {
        // 窗口早期（Handle 已建但系统尚未回传 lParam）可能收到空指针，直接跳过
        if (wordParameter == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new NativeMonitorInfo
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var minMax = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMinMaxInfo>(wordParameter);
        // 无边框分层窗口外层==客户端；但 WindowChrome 的 ResizeBorderThickness
        // （默认 8px）会被 WPF 当作“假想非客户区”整体平移 (-8,-8)，导致右/下
        // 各留 8px 空隙。ptMaxPosition 加回该值抵消，ptMaxSize 保持工作区尺寸。
        var border = SystemParameters.WindowResizeBorderThickness;
        var maxWidth = info.rcWork.Right - info.rcWork.Left;
        var maxHeight = info.rcWork.Bottom - info.rcWork.Top;
        minMax.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left + (int)border.Left;
        minMax.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top + (int)border.Top;
        minMax.ptMaxSize.X = maxWidth;
        minMax.ptMaxSize.Y = maxHeight;
        minMax.ptMaxTrackSize.X = Math.Max(minMax.ptMaxTrackSize.X, maxWidth);
        minMax.ptMaxTrackSize.Y = Math.Max(minMax.ptMaxTrackSize.Y, maxHeight);
        System.Runtime.InteropServices.Marshal.StructureToPtr(minMax, wordParameter, false);
    }

    private void ClampMaximizedWindowPos(IntPtr windowHandle, IntPtr wordParameter)
    {
        if (wordParameter == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new NativeMonitorInfo
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeWindowPos>(wordParameter);
        const uint swpNoMove = 0x0002;
        const uint swpNoSize = 0x0001;
        if ((pos.Flags & swpNoMove) == 0)
        {
            pos.X = info.rcWork.Left;
            pos.Y = info.rcWork.Top;
        }

        if ((pos.Flags & swpNoSize) == 0)
        {
            pos.Width = info.rcWork.Right - info.rcWork.Left;
            pos.Height = info.rcWork.Bottom - info.rcWork.Top;
        }

        System.Runtime.InteropServices.Marshal.StructureToPtr(pos, wordParameter, false);
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        // 最大化尺寸/位置 = 当前显示器的“工作区”（避开任务栏、无溢出空隙），
        // 与 UpdateMaximizeVisuals 的直角化配合实现“占满”。
        if (message == WmGetMinMaxInfo)
        {
            HandleWmGetMinMaxInfo(windowHandle, wordParameter);
            handled = true;
            return IntPtr.Zero;
        }

        // 分层窗口（AllowsTransparency）的两条最大化路径最终都经 WM_WINDOWPOSCHANGING
        // 定位；在此同步钳制到工作区，WPF 自身的布局无法覆盖。
        if (message == WmWindowPosChanging && WindowState == WindowState.Maximized)
        {
            ClampMaximizedWindowPos(windowHandle, wordParameter);
            return IntPtr.Zero;
        }

        // 任务栏显隐/显示器变化会广播 SPI_SETWORKAREA：已最大化的窗口不会自动
        // 重算尺寸，这里重新钳制，避免隐藏任务栏后底部留下工作区空隙("收过了")。
        if (message == WmSettingChange && WindowState == WindowState.Maximized)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyMaximizedWorkArea));
        }

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

    internal static string BuildDefaultFirstVersionNameForRuntime(DshRuntimeInfo runtime)
        => runtime.SuggestedInstanceName;

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
            && IsDetectedRuntimeReady(_nodeRuntime, _dshRuntime);
        var choice = FirstRunSetupWindow.Show(
            this,
            _nodeRuntime.IsAvailable ? $"{_nodeRuntime.VersionText} · {NodeStatusText}" : "未安装",
            _dshRuntime.IsAvailable
                ? $"{_dshRuntime.DisplayVersionText} · 已找到启动文件"
                : _dshRuntime.Error ?? "未安装",
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
            await RefreshDshAsync(forceRefresh: true);

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
                BuildDefaultFirstVersionNameForRuntime(_dshRuntime)));
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
        var openBrowser = GetSelectedOpenMode() == VersionOpenMode.Web;
        var result = await StartManagedInstanceAsync(selected, openBrowser);
        if (result is null)
        {
            return false;
        }

        if (!result.IsSuccess || result.ProcessId is null || result.Port is null || result.WebUrl is null)
        {
            ShowStartFailure(result.Error);
            return false;
        }

        if (openBrowser)
        {
            // Web 启动：dsh 已带 --no-open，由 Launcher 用系统默认方式打开；
            // 0.1.2-rc.1 起页面需要 launch token，必须用带 token 的地址。
            OpenWebUrlInBrowser(result.AuthenticatedWebUrl ?? result.WebUrl);
            ShowNotice($"实例已启动：{selected.Name}，{result.WebUrl}（已在默认浏览器打开）。");
            return true;
        }

        // Desktop 启动（启动器方式）：Launcher 用内部 Chat 窗口承载 WebUI；
        // 0.1.2-rc.1 起页面需要 launch token，优先用带 token 的地址。
        OpenChatWindow(selected.Id, result.AuthenticatedWebUrl ?? result.WebUrl);
        ShowNotice($"实例已启动：{selected.Name}，运行地址 {result.WebUrl}。健康检查已通过。");
        return true;
    }

    private async Task<bool> SendPluginFailureToCurrentInstanceAsync(
        ManagerInstance target,
        string prompt)
    {
        var instance = ResolveInstanceById(Instances, target.Id) ?? target;
        if (instance.RuntimeOwnership != InstanceRuntimeOwnership.Attached
            && !_instanceRunner.IsRunning(instance.Id)
            && instance.RuntimeStatus != InstanceRuntimeStatus.Running)
        {
            if (!TryBeginLifecycleOperation())
            {
                return false;
            }

            try
            {
                if (!await EnsureRuntimeReadyAsync(instance))
                {
                    return false;
                }

                var resolved = ResolveInstanceById(Instances, instance.Id);
                if (resolved is null)
                {
                    return false;
                }

                var started = await StartManagedInstanceAsync(resolved);
                if (started is null || !started.IsSuccess || started.WebUrl is null)
                {
                    return false;
                }

                instance = ResolveInstanceById(Instances, instance.Id) ?? resolved with
                {
                    RuntimeStatus = InstanceRuntimeStatus.Running,
                    RuntimeOwnership = InstanceRuntimeOwnership.Managed,
                    WebUrl = started.WebUrl,
                    AuthenticatedWebUrl = started.AuthenticatedWebUrl
                };
            }
            finally
            {
                EndLifecycleOperation();
            }
        }

        if (string.IsNullOrWhiteSpace(instance.WebUrl)
            || (instance.RuntimeStatus != InstanceRuntimeStatus.Running
                && instance.RuntimeOwnership != InstanceRuntimeOwnership.Attached))
        {
            return false;
        }

        ChatWindow? chat = null;
        if (TryFocusChatWindow(instance.Id))
        {
            _chatWindows.TryGetValue(instance.Id, out chat);
        }
        else
        {
            chat = OpenChatWindow(instance.Id, instance.AuthenticatedWebUrl ?? instance.WebUrl);
        }

        if (chat is null)
        {
            return false;
        }

        return await chat.SendMessageAsync(prompt, _windowCancellation.Token);
    }

    protected override void OnClosed(EventArgs e)
    {
        _browserGuard.Dispose();
        _balanceCancellation?.Cancel();
        _balanceCancellation?.Dispose();
        _windowSource?.RemoveHook(WindowProcedure);
        _windowSource = null;
        CloseAllChatWindows();
        _watchdog.Dispose();
        _windowCancellation.Dispose();
        base.OnClosed(e);
    }

    private ChatWindow? OpenChatWindow(string instanceId, string address, string? conversationId = null)
    {
        // dsh 版本变化时清一次 WebView2 磁盘缓存（仅在当前没有其它 Chat 窗口时，
        // 避免删除正在使用的缓存）。
        if (_chatWindows.Count == 0
            && Instances.FirstOrDefault(instance =>
                string.Equals(instance.Id, instanceId, StringComparison.Ordinal)) is { } cacheInstance)
        {
            try
            {
                _webCacheLedger.EnsureCacheMatches(cacheInstance.DetectedVersion, message => ShowNotice(message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                LauncherLog.Warn("WebView2 缓存版本检查失败（继续打开窗口）。", ErrorCodes.E1002,
                    new { instance = cacheInstance.Name, error = ex.Message });
            }
        }

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
            MarkInstanceUsed(instanceId);
            return chat;
        }
        catch (Exception ex)
        {
            ShowNotice($"Chat 窗口无法打开：{ex.Message}。Launcher 和实例仍保持运行。");
            return null;
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
        MarkInstanceUsed(instanceId);
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

            OpenChatWindow(instance.Id, instance.AuthenticatedWebUrl ?? instance.WebUrl, entry.SessionId);
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
                ShowStartFailure(result.Error, "无法启动实例，暂时不能打开对话。");
                return false;
            }

            OpenChatWindow(instance.Id, result.AuthenticatedWebUrl ?? result.WebUrl, entry.SessionId);
            ShowNotice($"实例已启动，正在打开对话：{entry.SessionId}。 ");
            return true;
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            UpdateInstanceStatus(instance, InstanceRuntimeStatus.Error, ex.ToString());
            ShowStartFailure(ex.ToString(), "启动实例后打开对话失败。");
            return false;
        }
        finally
        {
            EndLifecycleOperation();
        }
    }

    private void OpenWebUrlInBrowser(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || parsed.Scheme is not ("http" or "https"))
            {
                ShowNotice($"地址无效，无法用浏览器打开：{url}");
                return;
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or IOException)
        {
            ShowNotice($"打开浏览器失败：{ex.Message}");
        }
    }

    private void ShowNotice(string message, string? detail = null)
    {
        PageNotice = message;
        PageNoticeVisibility = Visibility.Visible;
        PageNoticeDetail = string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.Trim();
        PageNoticeDetailVisibility = PageNoticeDetail.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        OnPropertyChanged(nameof(PageNotice));
        OnPropertyChanged(nameof(PageNoticeVisibility));
        OnPropertyChanged(nameof(PageNoticeDetail));
        OnPropertyChanged(nameof(PageNoticeDetailVisibility));
    }

    private void ShowStartFailure(string? detail, string fallback = "DSh 启动失败。")
    {
        var summary = FormatStartFailure(detail, fallback);
        ShowNotice(summary, string.IsNullOrWhiteSpace(detail) ? null : detail);
    }

    internal static string FormatStartFailure(string? detail, string fallback = "DSh 启动失败。")
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return fallback;
        }

        var normalized = detail.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Contains("settings-file: invalid document", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("BLOCK_IN_FLOW", StringComparison.Ordinal)
            || normalized.Contains("MULTILINE_IMPLICIT_KEY", StringComparison.Ordinal)
            || normalized.Contains("BAD_INDENT", StringComparison.Ordinal))
        {
            var position = Regex.Match(
                normalized,
                @"line\s+(?<line>\d+)\s*,\s*column\s+(?<column>\d+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var location = position.Success
                ? $"（第 {position.Groups["line"].Value} 行，第 {position.Groups["column"].Value} 列）"
                : string.Empty;
            return $"DSh 配置文件 settings.yaml 格式无效{location}。请在“版本控制 → 检查版本”查看。";
        }

        var firstLine = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !line.StartsWith("at ", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return fallback;
        }

        firstLine = Regex.Replace(firstLine, "\\x1B\\[[0-9;]*[A-Za-z]", string.Empty);
        if (firstLine.Length > 180)
        {
            firstLine = firstLine[..177] + "…";
        }

        return firstLine.StartsWith("DSh", StringComparison.OrdinalIgnoreCase)
            ? firstLine
            : $"{fallback.TrimEnd('。')}：{firstLine}";
    }

    private void ViewNoticeDetail_Click(object sender, RoutedEventArgs e)
    {
        if (PageNoticeDetail.Length == 0)
        {
            return;
        }

        System.Windows.MessageBox.Show(
            this,
            PageNoticeDetail,
            "启动详情",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
