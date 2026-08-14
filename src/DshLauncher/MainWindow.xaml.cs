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
    private readonly CancellationTokenSource _windowCancellation = new();
    private NodeRuntimeInfo _nodeRuntime = NodeRuntimeInfo.Missing();
    private DshRuntimeInfo _dshRuntime = DshRuntimeInfo.Missing();
    private ManagerInstance? _selectedInstance;
    private bool _isNodeDetectionInProgress;

    public MainWindow()
    {
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
            OnPropertyChanged(nameof(CanStartInstance));
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

    public bool CanStartInstance => SelectedInstance is not null && _dshRuntime.IsAvailable;

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
    }

    private void LoadInstances()
    {
        try
        {
            Instances.Clear();
            foreach (var instance in _instanceRegistry.Load())
            {
                Instances.Add(instance);
            }

            SelectedInstance = Instances.FirstOrDefault();
            OnPropertyChanged(nameof(InstanceCountText));
            OnPropertyChanged(nameof(NoInstancesVisibility));
            OnPropertyChanged(nameof(InstancesVisibility));
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
        else
        {
            PageTitle = section;
            PageSubtitle = "DSH Launcher Core 已保留该工作区入口";
            ShowNotice($"“{section}”工作区已预留，当前版本先完成独立 Launcher、Node.js 检测和启动页骨架。");
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

    private void StartInstance_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is null)
        {
            ShowNotice("请先注册并选择一个 DSh 实例。");
            return;
        }

        if (!_dshRuntime.IsAvailable)
        {
            ShowNotice("当前没有可运行的 DSh。请先安装 DSh 或注册 Source 实例后执行构建。");
            return;
        }

        ShowNotice($"已完成实例选择：{SelectedInstance.Name}。启动、端口分配和健康检查将在下一模块接入。");
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

    private void InstallDsh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/deepseek-ai/deepseek-harness#run",
                UseShellExecute = true
            });
            ShowNotice("已打开 DeepSeek Harness 官方运行说明。正式安装流程会使用当前检测到的 Node.js 执行 npm 安装。");
        }
        catch (Exception ex)
        {
            ShowNotice($"无法打开 DSh 官方安装说明：{ex.Message}");
        }
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
        _windowCancellation.Dispose();
        base.OnClosed(e);
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
