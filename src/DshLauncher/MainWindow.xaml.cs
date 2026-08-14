using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly NodeRuntimeDetector _nodeDetector = new();
    private readonly CancellationTokenSource _windowCancellation = new();
    private NodeRuntimeInfo _nodeRuntime = NodeRuntimeInfo.Missing();
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

    public bool CanRefreshNode => !_isNodeDetectionInProgress;

    public string NodeStatusText => _isNodeDetectionInProgress
        ? "检测中…"
        : _nodeRuntime.IsAvailable ? "可用" : "未安装";

    public Brush NodeStatusBrush => _isNodeDetectionInProgress
        ? new SolidColorBrush(Color.FromRgb(113, 129, 150))
        : _nodeRuntime.IsAvailable
            ? new SolidColorBrush(Color.FromRgb(37, 135, 90))
            : new SolidColorBrush(Color.FromRgb(190, 105, 30));

    public string NodeVersionText => _isNodeDetectionInProgress
        ? "请稍候"
        : _nodeRuntime.IsAvailable ? _nodeRuntime.VersionText : "需要安装 Node.js";

    public string NodePathText => _isNodeDetectionInProgress
        ? "正在检查 PATH 和 Windows 常见安装位置…"
        : _nodeRuntime.IsAvailable
            ? _nodeRuntime.ExecutablePath ?? "已找到 node.exe，但路径不可用"
            : _nodeRuntime.Error ?? "未找到 PATH 中的 node.exe，也没有发现常见安装位置";

    public string DshStatusText => "未注册实例";

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshNodeAsync();
    }

    private async void RefreshNode_Click(object sender, RoutedEventArgs e)
    {
        var runtime = await RefreshNodeAsync();
        if (runtime is null)
        {
            return;
        }

        ShowNotice(runtime.IsAvailable
            ? $"Node.js 检测完成：{runtime.VersionText}。DSh 仍需要单独注册实例。"
            : "Node.js 检测完成：当前没有找到可用的 node.exe。Launcher 本身仍可继续运行。");
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

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string section)
        {
            return;
        }

        if (section == "启动")
        {
            PageTitle = "启动";
            PageSubtitle = "管理 DeepSeek Harness 实例与运行环境";
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
        ShowNotice("实例注册将在下一阶段接入；当前页面已经区分 Launcher 自身与外部 DSh 运行环境。");
    }

    private void ImportSource_Click(object sender, RoutedEventArgs e)
    {
        ShowNotice("Source 实例导入将在下一阶段接入；添加前不会要求本机已经安装 Node.js。");
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
