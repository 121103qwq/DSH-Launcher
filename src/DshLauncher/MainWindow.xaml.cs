using System.Diagnostics;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private ExtensionWindow? _extensionWindow;
    private ModelWindow? _modelWindow;
    private ConversationWindow? _conversationWindow;
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

            var previousId = _selectedInstance?.Id;
            _selectedInstance = value;
            if (previousId is not null
                && value is not null
                && !string.Equals(previousId, value.Id, StringComparison.Ordinal))
            {
                // Management windows are bound to the instance they opened.
                CloseManagementWindows();
            }
            OnPropertyChanged(nameof(SelectedInstance));
            OnPropertyChanged(nameof(SelectedInstanceName));
            OnPropertyChanged(nameof(SelectedInstanceSummary));
            OnPropertyChanged(nameof(SelectedInstanceStatus));
            OnPropertyChanged(nameof(InstanceEndpointText));
            OnPropertyChanged(nameof(CanStartInstance));
            OnPropertyChanged(nameof(CanStopInstance));
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
            : _nodeRuntime.IsCompatibleWithDshSource)
        && !_instanceRunner.IsRunning(SelectedInstance.Id);

    public bool CanStopInstance => !_isLifecycleInProgress
        && SelectedInstance is not null
        && _instanceRunner.IsRunning(SelectedInstance.Id);

    public string InstanceEndpointText => SelectedInstance?.WebUrl
        ?? (SelectedInstance?.RuntimeStatus == InstanceRuntimeStatus.Running ? "正在检查运行地址…" : "尚未启动");

    public bool CanInstallDsh => !_isDshInstallInProgress
        && !_isNodeDetectionInProgress
        && _nodeRuntime.IsAvailable;

    public string DshInstallButtonText => _isDshInstallInProgress
        ? "安装中…"
        : _dshRuntime.IsAvailable ? "安装/更新 DSh" : "安装 DSh";

    public bool CanRefreshNode => !_isNodeDetectionInProgress;

    public string NodeStatusText => _isNodeDetectionInProgress
        ? "检测中…"
        : _nodeRuntime.IsAvailable ? "可用" : "未安装";

    public System.Windows.Media.Brush NodeStatusBrush => _isNodeDetectionInProgress
        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 129, 150))
        : _nodeRuntime.IsAvailable
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 135, 90))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 105, 30));

    public string NodeVersionText => _isNodeDetectionInProgress
        ? "请稍候"
        : _nodeRuntime.IsAvailable ? _nodeRuntime.VersionText : "需要安装 Node.js";

    public string NodePathText => _isNodeDetectionInProgress
        ? "正在检查 PATH 和 Windows 常见安装位置…"
        : _nodeRuntime.IsAvailable
            ? _nodeRuntime.ExecutablePath ?? "已找到 node.exe，但路径不可用"
            : _nodeRuntime.Error ?? "未找到 PATH 中的 node.exe，也没有发现常见安装位置";

    public string DshStatusText => _dshRuntime.IsAvailable ? "可用" : "未安装";

    public System.Windows.Media.Brush DshStatusBrush => _dshRuntime.IsAvailable
        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 135, 90))
        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 105, 30));

    public string DshVersionText => _dshRuntime.IsAvailable
        ? $"{_dshRuntime.VersionText} · {(_dshRuntime.PackageRoot is null ? "路径未解析" : "已找到安装包")}"
        : "实例注册后由对应运行环境启动";

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadInstances();
        await Task.WhenAll(RefreshNodeAsync(), RefreshDshAsync());
    }

    private async void RefreshNode_Click(object sender, RoutedEventArgs e)
    {
        var runtime = await RefreshNodeAsync();
        if (runtime is null)
        {
            return;
        }

        await RefreshDshAsync();

        ShowNotice(runtime.IsAvailable
            ? $"运行环境检测完成：Node.js {runtime.VersionText}，DSh {_dshRuntime.VersionText}。"
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
        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(DshInstallButtonText));
    }

    private void LoadInstances()
    {
        try
        {
            Instances.Clear();
            foreach (var storedInstance in _instanceRegistry.Load())
            {
                var instance = storedInstance.RuntimeStatus == InstanceRuntimeStatus.Running
                    ? storedInstance with
                    {
                        RuntimeStatus = InstanceRuntimeStatus.Stopped,
                        ProcessId = null,
                        Port = null,
                        WebUrl = null
                    }
                    : storedInstance;
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

        if (section == "启动" || section == "实例")
        {
            PageTitle = section;
            PageSubtitle = section == "启动"
                ? "管理 DeepSeek Harness 实例与运行环境"
                : "注册并隔离管理 installed / source DSh 实例";
            PageNoticeVisibility = Visibility.Collapsed;
        }
        else if (section == "扩展" || section == "Agent")
        {
            PageTitle = section;
            PageSubtitle = "管理当前实例实际使用的 Plugin、Skill、MCP 和 Agent Preset";
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            OpenExtensionWindow();
            return;
        }
        else if (section == "模型")
        {
            PageTitle = section;
            PageSubtitle = "编辑当前实例的 DSh Provider 与模型列表";
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            OpenModelWindow();
            return;
        }
        else if (section == "对话")
        {
            PageTitle = section;
            PageSubtitle = "管理当前实例的 session.jsonl 对话文件";
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            OpenConversationWindow();
            return;
        }
        else
        {
            PageTitle = section;
            PageSubtitle = "DSH Launcher Core 已保留该工作区入口";
            ShowNotice($"“{section}”工作区当前尚未接入独立管理页面。");
        }

        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(PageNoticeVisibility));
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

    private async void InstallDsh_Click(object sender, RoutedEventArgs e)
    {
        if (_isDshInstallInProgress)
        {
            return;
        }

        if (!_nodeRuntime.IsAvailable)
        {
            ShowNotice("当前没有可用的 Node.js。请先安装 Node.js，再执行 DSh 安装。");
            InstallNode_Click(sender, e);
            return;
        }

        _isDshInstallInProgress = true;
        OnPropertyChanged(nameof(CanInstallDsh));
        OnPropertyChanged(nameof(DshInstallButtonText));
        ShowNotice("正在使用当前 Node.js 执行 npm install --global @deepseek-ai/dsh，请稍候…");

        try
        {
            var result = await _dshInstaller.InstallAsync(_nodeRuntime, _windowCancellation.Token);
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

        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(CanStopInstance));
        OnPropertyChanged(nameof(InstanceEndpointText));
    }

    private void SetLifecycleBusy(bool isBusy)
    {
        _isLifecycleInProgress = isBusy;
        OnPropertyChanged(nameof(CanStartInstance));
        OnPropertyChanged(nameof(CanStopInstance));
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
        CloseManagementWindows();
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

    private void OpenExtensionWindow()
    {
        if (SelectedInstance is not { } instance)
        {
            ShowNotice("请先注册并选择一个 DSh 实例。扩展操作必须绑定到具体实例。");
            return;
        }

        if (_extensionWindow is not null)
        {
            _extensionWindow.Activate();
            return;
        }

        try
        {
            var window = new ExtensionWindow(instance, _extensionService, () => _nodeRuntime) { Owner = this };
            _extensionWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_extensionWindow, window)) _extensionWindow = null;
            };
            window.Show();
        }
        catch (Exception ex)
        {
            ShowNotice($"扩展窗口无法打开：{ex.Message}");
        }
    }

    private void OpenModelWindow()
    {
        if (SelectedInstance is not { } instance)
        {
            ShowNotice("请先注册并选择一个 DSh 实例。模型配置必须绑定到具体实例。");
            return;
        }

        if (_modelWindow is not null)
        {
            _modelWindow.Activate();
            return;
        }

        try
        {
            var window = new ModelWindow(instance, _modelService) { Owner = this };
            _modelWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_modelWindow, window)) _modelWindow = null;
            };
            window.Show();
        }
        catch (Exception ex)
        {
            ShowNotice($"模型窗口无法打开：{ex.Message}");
        }
    }

    private void OpenConversationWindow()
    {
        if (SelectedInstance is not { } instance)
        {
            ShowNotice("请先注册并选择一个 DSh 实例。对话管理必须绑定到具体实例。");
            return;
        }

        if (_conversationWindow is not null)
        {
            _conversationWindow.Activate();
            return;
        }

        try
        {
            var window = new ConversationWindow(instance, _conversationService, OpenConversation) { Owner = this };
            _conversationWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_conversationWindow, window)) _conversationWindow = null;
            };
            window.Show();
        }
        catch (Exception ex)
        {
            ShowNotice($"对话窗口无法打开：{ex.Message}");
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

    private void CloseManagementWindows()
    {
        foreach (var window in new Window?[] { _extensionWindow, _modelWindow, _conversationWindow })
        {
            try { window?.Close(); } catch { }
        }

        _extensionWindow = null;
        _modelWindow = null;
        _conversationWindow = null;
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
