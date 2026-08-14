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
    private NodeRuntimeInfo _nodeRuntime = NodeRuntimeInfo.Missing();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public string PageTitle { get; private set; } = "启动";

    public string PageSubtitle { get; private set; } = "管理 DeepSeek Harness 实例与运行环境";

    public string PageNotice { get; private set; } = string.Empty;

    public Visibility PageNoticeVisibility { get; private set; } = Visibility.Collapsed;

    public string NodeStatusText => _nodeRuntime.IsAvailable ? "可用" : "未安装";

    public Brush NodeStatusBrush => _nodeRuntime.IsAvailable
        ? new SolidColorBrush(Color.FromRgb(37, 135, 90))
        : new SolidColorBrush(Color.FromRgb(190, 105, 30));

    public string NodeVersionText => _nodeRuntime.IsAvailable
        ? _nodeRuntime.VersionText
        : "需要安装 Node.js";

    public string NodePathText => _nodeRuntime.IsAvailable
        ? _nodeRuntime.ExecutablePath ?? "已找到 node.exe，但路径不可用"
        : "未找到 PATH 中的 node.exe，也没有发现常见安装位置";

    public string DshStatusText => "未注册实例";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshNode();
    }

    private void RefreshNode_Click(object sender, RoutedEventArgs e)
    {
        RefreshNode();
        ShowNotice(_nodeRuntime.IsAvailable
            ? $"Node.js 检测完成：{_nodeRuntime.VersionText}。DSh 仍需要单独注册实例。"
            : "Node.js 检测完成：当前没有找到可用的 node.exe。Launcher 本身仍可继续运行。", false);
    }

    private void RefreshNode()
    {
        _nodeRuntime = _nodeDetector.Detect();
        OnPropertyChanged(nameof(NodeStatusText));
        OnPropertyChanged(nameof(NodeStatusBrush));
        OnPropertyChanged(nameof(NodeVersionText));
        OnPropertyChanged(nameof(NodePathText));
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
            ShowNotice($"“{section}”工作区已预留，当前版本先完成独立 Launcher、Node.js 检测和启动页骨架。", false);
        }

        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(PageNoticeVisibility));
    }

    private void AddInstance_Click(object sender, RoutedEventArgs e)
    {
        ShowNotice("实例注册将在下一阶段接入；当前页面已经区分 Launcher 自身与外部 DSh 运行环境。", false);
    }

    private void ImportSource_Click(object sender, RoutedEventArgs e)
    {
        ShowNotice("Source 实例导入将在下一阶段接入；添加前不会要求本机已经安装 Node.js。", false);
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
            ShowNotice($"无法打开 Node.js 官方安装页：{ex.Message}", true);
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

    private void ShowNotice(string message, bool isError)
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
