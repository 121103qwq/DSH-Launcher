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
    private readonly ConversationService _conversationService;
    private readonly ModelService _modelService;
    private readonly DshInstallService _dshInstaller = new();
    private readonly SourceBuildService _sourceBuilder = new();
    private readonly CancellationTokenSource _windowCancellation = new();
    private NodeRuntimeInfo _nodeRuntime = NodeRuntimeInfo.Missing();
    private DshRuntimeInfo _dshRuntime = DshRuntimeInfo.Missing();
    private ChatWindow? _chatWindow;
    private ManagerInstance? _selectedInstance;
    private bool _isNodeDetectionInProgress;
    private bool _isLifecycleInProgress;
    private bool _isDshInstallInProgress;

    public MainWindow()
    {
        _extensionService = new(id => _instanceRunner.IsRunning(id));
        _conversationService = new(isRunning: id => _instanceRunner.IsRunning(id));
        _modelService = new(id => _instanceRunner.IsRunning(id));
        InitializeComponent();
        DataContext = this;
    }

    public string PageTitle { get; private set; } = "启动";

    public string PageSubtitle { get; private set; } = "管理 DeepSeek Harness 实例与运行环境";

    public string PageNotice { get; private set; } = string.Empty;

    public Visibility PageNoticeVisibility { get; private set; } = Visibility.Collapsed;

    public ObservableCollection<ManagerInstance> Instances { get; } = new();

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
            OnPropertyChanged(nameof(SelectedInstance));
            OnPropertyChanged(nameof(SelectedInstanceName));
            OnPropertyChanged(nameof(SelectedInstanceSummary));
            OnPropertyChanged(nameof(SelectedInstanceStatus));
            OnPropertyChanged(nameof(InstanceEndpointText));
            OnPropertyChanged(nameof(CanStartInstance));
            OnPropertyChanged(nameof(CanStopInstance));
            OnPropertyChanged(nameof(CanRestartInstance));
            OnPropertyChanged(nameof(NodeStatusText));
            OnPropertyChanged(nameof(NodeStatusBrush));
            OnPropertyChanged(nameof(NodeVersionText));
            OnPropertyChanged(nameof(NodePathText));
        }
    }

    public string InstanceCountText => $"{Instances.Count} 个实例";

    public Visibility NoInstancesVisibility => Instances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InstancesVisibility => Instances.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string SelectedInstanceName => SelectedInstance?.Name ?? "等待实例注册";

    public string SelectedInstanceSummary => SelectedInstance is null
        ? "先注册一个 DSh 实例，再从这里启动。"
        : $"{SelectedInstance.KindText} · {SelectedInstance.RootPath}";

    public string SelectedInstanceStatus => SelectedInstance?.StatusText ?? "未选择";

    public bool CanStartInstance => !_isLifecycleInProgress
        && SelectedInstance is not null
        && (SelectedInstance.Kind == InstanceKind.Installed
            ? SelectedInstance.DshExecutablePath is not null
            : GetSelectedNodeCompatibility() == NodeRuntimeCompatibility.Compatible)
        && !_instanceRunner.IsRunning(SelectedInstance.Id);

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

        if (SelectedInstance?.Kind == InstanceKind.Source)
        {
            return _nodeRuntime.GetCompatibility(
                SourceProjectInspector.TryReadNodeEngine(SelectedInstance.RootPath));
        }

        if (_dshRuntime.IsAvailable)
        {
            return _nodeRuntime.GetCompatibility(_dshRuntime.NodeEngine);
        }

        return NodeRuntimeCompatibility.Unknown;
    }

    private string GetNodeRequirementText()
    {
        var requirement = SelectedInstance?.Kind == InstanceKind.Source
            ? SourceProjectInspector.TryReadNodeEngine(SelectedInstance.RootPath)
            : _dshRuntime.NodeEngine;
        return string.IsNullOrWhiteSpace(requirement)
            ? "未声明 engines.node"
            : $"要求 {requirement}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshDshAsync();
        await LoadInstancesAsync();
        SwitchSection("启动");
        await RefreshNodeAsync();
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

        try
        {
            _nodeRuntime = await _nodeDetector.DetectAsync(_windowCancellation.Token);
            OnPropertyChanged(nameof(NodeStatusBrush));
            OnPropertyChanged(nameof(NodeStatusText));
            OnPropertyChanged(nameof(NodeVersionText));
            OnPropertyChanged(nameof(NodePathText));
            OnPropertyChanged(nameof(CanStartInstance));
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
            OnPropertyChanged(nameof(CanInstallDsh));
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
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowNotice($"读取实例注册文件失败：{ex.Message}");
        }
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string section)
        {
            return;
        }

        SwitchSection(section);
    }

    private void SwitchSection(string section)
    {
        SetNavigationSelection(section);
        PageNoticeVisibility = Visibility.Collapsed;

        if (section == "启动" || section == "实例")
        {
            PageTitle = section;
            PageSubtitle = section == "启动"
                ? "启动当前选中的 DeepSeek Harness 实例"
                : "注册并隔离管理 installed / source DSh 实例";
            ShowMainDashboard(showInstanceList: section == "实例");
        }
        else if (section is "扩展" or "Agent" or "模型" or "对话")
        {
            if (SelectedInstance is not { } instance)
            {
                PageTitle = section;
                PageSubtitle = "请先在“实例”工作区注册并选择一个 DSh 实例";
                ShowMainDashboard(showInstanceList: true);
                ShowNotice($"请先注册并选择一个 DSh 实例，再打开“{section}”。");
            }
            else
            {
                PageTitle = section;
                PageSubtitle = section switch
                {
                    "扩展" => "管理当前实例的 Plugin 与 MCP",
                    "Agent" => "管理当前实例的 Skill、Agent Preset 与 Workflow",
                    "模型" => "编辑当前实例的 DSh Provider 与模型列表",
                    _ => "管理当前实例的 session.jsonl / .zstd 对话文件"
                };

                object page = section switch
                {
                    "扩展" => new ExtensionWindow(instance, _extensionService, () => _nodeRuntime),
                    "Agent" => new ExtensionWindow(instance, _extensionService, () => _nodeRuntime, agentOnly: true),
                    "模型" => new ModelWindow(instance, _modelService),
                    _ => new ConversationWindow(instance, _conversationService, OpenConversation)
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

    private void ShowMainDashboard(bool showInstanceList)
    {
        EmbeddedPageHost.Content = null;
        EmbeddedPageHost.Visibility = Visibility.Collapsed;
        MainDashboardGrid.Visibility = Visibility.Visible;
        InstanceListCard.Visibility = showInstanceList ? Visibility.Visible : Visibility.Collapsed;
        MainDashboardGrid.ColumnDefinitions[0].Width = new GridLength(showInstanceList ? 310 : 0);
        MainDashboardGrid.ColumnDefinitions[1].Width = new GridLength(showInstanceList ? 24 : 0);
    }

    private void ShowEmbeddedPage(object page)
    {
        MainDashboardGrid.Visibility = Visibility.Collapsed;
        EmbeddedPageHost.Content = page;
        EmbeddedPageHost.Visibility = Visibility.Visible;
    }

    private FrameworkElement CreateSettingsPage()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Top
        };
        panel.Children.Add(new TextBlock
        {
            Text = "设置 / 诊断",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "这里将集中展示 Launcher 配置、运行环境和诊断信息。当前可用的运行环境检测入口仍在顶部。",
            Foreground = (WpfBrush)FindResource("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        return panel;
    }

    private void SetNavigationSelection(string section)
    {
        var buttons = new[]
        {
            NavigationStart,
            NavigationInstances,
            NavigationExtensions,
            NavigationModels,
            NavigationAgent,
            NavigationConversations,
            NavigationSettings
        };
        foreach (var button in buttons)
        {
            button.Background = WpfBrushes.Transparent;
            button.Foreground = (WpfBrush)FindResource("TextBrush");
        }

        var selected = buttons.FirstOrDefault(button => string.Equals(button.Tag as string, section, StringComparison.Ordinal));
        if (selected is not null)
        {
            selected.Background = new SolidColorBrush(WpfColor.FromRgb(228, 241, 254));
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
        if (selected.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            ShowNotice("当前实例连接的是外部 DSh 服务，Launcher 不会重复启动该进程。");
            return;
        }

        SetLifecycleBusy(true);
        try
        {
            if (!await PrepareSourceAsync(selected))
            {
                return;
            }

            var result = await _instanceRunner.StartAsync(
                selected,
                selected.Kind == InstanceKind.Source ? _nodeRuntime : null,
                _windowCancellation.Token);
            if (!result.IsSuccess || result.ProcessId is null || result.Port is null || result.WebUrl is null)
            {
                UpdateInstanceStatus(selected, InstanceRuntimeStatus.Error, result.Error);
                ShowNotice(result.Error ?? "DSh 启动失败。");
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
            OpenChatWindow(result.WebUrl);
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

            CloseChatWindow();
            UpdateInstance(selected with
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
            if (selected.Kind == InstanceKind.Source && _instanceRunner.IsRunning(selected.Id))
            {
                var stoppedBeforePrepare = await _instanceRunner.StopAsync(selected.Id, _windowCancellation.Token);
                if (!stoppedBeforePrepare.IsSuccess)
                {
                    UpdateInstanceStatus(selected, InstanceRuntimeStatus.Error, stoppedBeforePrepare.Error);
                    ShowNotice(stoppedBeforePrepare.Error ?? "Source 重启前停止失败。");
                    return;
                }
            }

            if (!await PrepareSourceAsync(selected))
            {
                return;
            }

            var result = selected.Kind == InstanceKind.Source
                ? await _instanceRunner.StartAsync(selected, _nodeRuntime, _windowCancellation.Token)
                : await _instanceRunner.RestartAsync(selected, _windowCancellation.Token);
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
            OpenChatWindow(result.WebUrl);
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
        OnPropertyChanged(nameof(CanStopInstance));
        OnPropertyChanged(nameof(CanRestartInstance));
        OnPropertyChanged(nameof(InstanceEndpointText));
        OnPropertyChanged(nameof(NodeStatusText));
        OnPropertyChanged(nameof(NodeStatusBrush));
        OnPropertyChanged(nameof(NodeVersionText));
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
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
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

    protected override void OnClosed(EventArgs e)
    {
        _windowCancellation.Cancel();
        CloseChatWindow();
        try
        {
            _instanceRunner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Window shutdown must not be blocked by a failed child-process cleanup.
        }
        _windowCancellation.Dispose();
        base.OnClosed(e);
    }

    private void OpenChatWindow(string address, string? conversationId = null)
    {
        CloseChatWindow();
        try
        {
            var chat = new ChatWindow(address, conversationId) { Owner = this };
            _chatWindow = chat;
            chat.Closed += (_, _) =>
            {
                if (ReferenceEquals(_chatWindow, chat))
                {
                    _chatWindow = null;
                }
            };
            chat.Show();
        }
        catch (Exception ex)
        {
            ShowNotice($"Chat 窗口无法打开：{ex.Message}。Launcher 和实例仍保持运行。");
        }
    }

    private void CloseChatWindow()
    {
        var chat = _chatWindow;
        _chatWindow = null;
        if (chat is null)
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

    private bool OpenConversation(ConversationEntry entry)
    {
        var instance = SelectedInstance;
        if (instance is null
            || entry.SessionId is null
            || !_instanceRunner.IsRunning(instance.Id)
            || string.IsNullOrWhiteSpace(instance.WebUrl))
        {
            return false;
        }

        OpenChatWindow(instance.WebUrl, entry.SessionId);
        return true;
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
