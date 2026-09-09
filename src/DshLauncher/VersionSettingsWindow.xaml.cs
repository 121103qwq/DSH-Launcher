using System.IO;
using System.Windows;
using System.Windows.Controls;
using DshLauncher.Models;
using DshLauncher.Services;
using DshLauncher.Watchdog;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace DshLauncher;

public partial class VersionSettingsWindow : UserControl
{
    private ManagerInstance? _instance;
    private readonly IReadOnlyList<ManagerInstance> _versions;
    private readonly VersionSettingsService _settingsService;
    private readonly ExtensionService _extensionService;
    private readonly Func<NodeRuntimeInfo?> _nodeRuntimeProvider;
    private readonly VersionPackageService _packageService;
    private readonly VersionSnapshotService _snapshotService;
    private readonly Func<ManagerInstance, string, ManagerInstance> _renameVersion;
    private readonly Action _settingsSaved;
    private readonly bool _openPluginPage;
    private readonly InstanceHealthProviders? _healthProviders;
    private System.Windows.Threading.DispatcherTimer? _healthTimer;
    private int _healthLogLineCount = -1;
    private const string EnvironmentNameTag = "EnvironmentName";
    private const string EnvironmentValueTag = "EnvironmentValue";
    private VersionSettingsData _settings = new();

    public VersionSettingsWindow(
        ManagerInstance? instance,
        IEnumerable<ManagerInstance> versions,
        VersionSettingsService settingsService,
        ExtensionService extensionService,
        Func<NodeRuntimeInfo?> nodeRuntimeProvider,
        VersionPackageService packageService,
        VersionSnapshotService snapshotService,
        Func<ManagerInstance, string, ManagerInstance> renameVersion,
        Action settingsSaved,
        bool openPluginPage = false,
        InstanceHealthProviders? healthProviders = null)
    {
        _healthProviders = healthProviders;
        _instance = instance;
        _versions = versions.ToArray();
        _settingsService = settingsService;
        _extensionService = extensionService;
        _nodeRuntimeProvider = nodeRuntimeProvider;
        _packageService = packageService;
        _snapshotService = snapshotService;
        _renameVersion = renameVersion;
        _settingsSaved = settingsSaved;
        _openPluginPage = openPluginPage;

        InitializeComponent();
        try
        {
            _settings = _instance is null ? new VersionSettingsData() : _settingsService.Read(_instance);
        }
        catch (Exception ex)
        {
            ConfigurationStatusText.Text = $"读取版本设置失败，已使用默认值：{ex.Message}";
            _settings = new VersionSettingsData();
        }
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        VersionSettingsHeaderText.Text = _instance is { } instance
            ? $"版本设置 - {instance.Name}"
            : "版本设置";
        VersionIdentityText.Text = _instance?.Name ?? "尚未选择版本";
        PersonalizationVersionText.Text = _instance?.Name ?? "请先创建或选择一个版本";
        VersionNameBox.Text = _instance?.Name ?? string.Empty;
        PersonalizationDetailsText.Text = _instance is null
            ? "版本设置需要绑定到一个真实版本。请先在启动页选择实例，或在版本控制中创建版本。"
            : $"{_instance.KindText} · {_instance.RootPath}\n状态：{_instance.StatusText}";
        DshHomeText.Text = _instance?.DshHome ?? "尚未创建 DSH_HOME";
        PackageExtensionBox.Text = _packageService.PackageExtension;
        NodeRuntimeText.Text = FormatNodeRuntime();

        LoadWorkspaceNames();
        LoadConfigurationControls();
        LoadPluginSettingsControls();
        RefreshSnapshots();
        ShowPage(_openPluginPage ? PluginsButton : PersonalizationButton);

        if (_instance is null)
        {
            VersionRequiredText.Text = "请先在启动页选择一个版本；当前页面只展示设置结构。";
            VersionRequiredText.Visibility = Visibility.Visible;
            PersonalizationPage.IsEnabled = false;
            ConfigurationPage.IsEnabled = false;
            PluginPage.IsEnabled = false;
            SnapshotPage.IsEnabled = false;
            ExportPage.IsEnabled = false;
            return;
        }

        await LoadPluginsAsync();
    }

    private void SaveVersionName_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            PersonalizationStatusText.Text = "请先选择一个版本。";
            return;
        }

        try
        {
            _instance = _renameVersion(_instance, VersionNameBox.Text);
            VersionNameBox.Text = _instance.Name;
            VersionSettingsHeaderText.Text = $"版本设置 - {_instance.Name}";
            VersionIdentityText.Text = _instance.Name;
            PersonalizationVersionText.Text = _instance.Name;
            PersonalizationStatusText.Text = $"版本名称已更新为：{_instance.Name}";
        }
        catch (Exception ex)
        {
            PersonalizationStatusText.Text = $"保存版本名称失败：{ex.Message}";
        }
    }

    private void LoadWorkspaceNames()
    {
        try
        {
            ConversationWorkspaceBox.ItemsSource = _settingsService.GetWorkspaceNames(_versions);
        }
        catch
        {
            ConversationWorkspaceBox.ItemsSource = Array.Empty<string>();
        }

        ConversationWorkspaceBox.Text = _settings.ConversationWorkspace ?? string.Empty;
    }

    private void LoadConfigurationControls()
    {
        SyncAllConfigurationCheckBox.IsChecked = _settings.SyncAllConfiguration;
        ConversationIndependentRadio.IsChecked = _settings.ConversationSyncMode == ConversationSyncMode.Independent;
        ConversationWorkspaceRadio.IsChecked = _settings.ConversationSyncMode == ConversationSyncMode.Workspace;
        ConversationAllRadio.IsChecked = _settings.ConversationSyncMode == ConversationSyncMode.All;
        SyncModelProvidersCheckBox.IsChecked = _settings.SyncModelProviders;
        ConfigurationOptionsPanel.IsEnabled = !_settings.SyncAllConfiguration;
        UpdateWorkspaceEnabled();
    }

    private void LoadPluginSettingsControls()
    {
        WindowTitleBox.Text = _settings.WindowTitle ?? string.Empty;
        NodePathBox.Text = _settings.NodeExecutablePath ?? string.Empty;
        var openMode = _settings.OpenMode ?? VersionOpenMode.Desktop;
        OpenModeBox.SelectedValue = openMode.ToString();
        CustomOpenTargetBox.Text = _settings.CustomOpenTargetPath ?? string.Empty;
        UpdateCustomOpenTargetEnabled();
        OpenModeStatusText.Text = openMode switch
        {
            VersionOpenMode.Custom => "当前版本将使用手动绑定的本地入口，并继承此版本的 DSH_HOME。",
            VersionOpenMode.Web => "当前版本将使用 dsh 原生方式启动：服务启动后由 dsh 在默认浏览器打开 WebUI。",
            _ => "当前版本将使用启动器方式启动：服务启动后自动打开内部 Chat 窗口，不会重复弹浏览器。"
        };
        LoadEnvironmentVariables();
    }

    private void LoadEnvironmentVariables()
    {
        EnvironmentVariableList.Children.Clear();
        if (_settings.EnvironmentVariables is not { Count: > 0 } variables)
        {
            EnvironmentVariableStatusText.Text = "尚未设置。变量只对这个实例生效，下次启动时注入。";
            return;
        }

        EnvironmentVariableStatusText.Text = $"已设置 {variables.Count} 个变量（敏感值已加密落盘），下次启动生效。";
        foreach (var pair in variables.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddEnvironmentVariableRow(pair.Key, pair.Value);
        }
    }

    private void AddEnvironmentVariableRow(string name, string value)
    {
        var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nameBox = new System.Windows.Controls.TextBox
        {
            Text = name,
            MaxLength = DshEnvironmentVariables.MaximumNameLength,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Tag = EnvironmentNameTag
        };
        var valueBox = new System.Windows.Controls.TextBox
        {
            Text = value,
            MaxLength = DshEnvironmentVariables.MaximumValueLength,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Tag = EnvironmentValueTag
        };
        var remove = new System.Windows.Controls.Button
        {
            Content = "删除",
            Padding = new Thickness(12, 7, 12, 7)
        };
        remove.Click += (_, _) => EnvironmentVariableList.Children.Remove(row);
        System.Windows.Controls.Grid.SetColumn(valueBox, 1);
        System.Windows.Controls.Grid.SetColumn(remove, 2);
        row.Children.Add(nameBox);
        row.Children.Add(valueBox);
        row.Children.Add(remove);
        EnvironmentVariableList.Children.Add(row);
    }

    private void AddEnvironmentVariable_Click(object sender, RoutedEventArgs e)
    {
        if (EnvironmentVariableList.Children.Count >= DshEnvironmentVariables.MaximumCount)
        {
            EnvironmentVariableStatusText.Text = $"最多 {DshEnvironmentVariables.MaximumCount} 个变量。";
            return;
        }

        AddEnvironmentVariableRow(string.Empty, string.Empty);
    }

    private void SaveEnvironmentVariables_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            EnvironmentVariableStatusText.Text = "请先选择版本。";
            return;
        }

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in EnvironmentVariableList.Children.OfType<System.Windows.Controls.Grid>())
        {
            var name = row.Children.OfType<System.Windows.Controls.TextBox>()
                .FirstOrDefault(box => Equals(box.Tag, EnvironmentNameTag))?.Text.Trim() ?? string.Empty;
            var value = row.Children.OfType<System.Windows.Controls.TextBox>()
                .FirstOrDefault(box => Equals(box.Tag, EnvironmentValueTag))?.Text ?? string.Empty;
            if (name.Length == 0 && value.Length == 0)
            {
                continue;
            }

            if (!DshEnvironmentVariables.IsValidName(name))
            {
                EnvironmentVariableStatusText.Text = DshEnvironmentVariables.IsReserved(name)
                    ? $"{name} 是保留变量（DSH_HOME / DSH_AGENTS_HOME / PATH），不能覆盖。"
                    : $"变量名无效：{name}（不能为空、含 = 或超过 {DshEnvironmentVariables.MaximumNameLength} 字符）。";
                return;
            }

            if (!variables.TryAdd(name, value))
            {
                EnvironmentVariableStatusText.Text = $"变量名重复：{name}。";
                return;
            }
        }

        try
        {
            _settings.EnvironmentVariables = variables.Count == 0 ? null : variables;
            _settingsService.Save(_instance, _settings);
            EnvironmentVariableStatusText.Text = variables.Count == 0
                ? "已清空环境变量。"
                : $"已保存 {variables.Count} 个变量（敏感值已加密落盘），下次启动生效。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            EnvironmentVariableStatusText.Text = $"保存失败：{ex.Message}";
        }
    }

    private string FormatNodeRuntime()
    {
        var runtime = _nodeRuntimeProvider();
        return runtime is null
            ? "尚未完成 Node.js 检测。"
            : runtime.IsAvailable
                ? $"当前检测结果：{runtime.VersionText} · {runtime.ExecutablePath}"
                : runtime.Error ?? "当前没有检测到可用 Node.js。";
    }

    private void Personalization_Click(object sender, RoutedEventArgs e) => ShowPage(PersonalizationButton);

    private void Health_Click(object sender, RoutedEventArgs e) => ShowPage(HealthButton);

    // ---------- 运行状况页 ----------

    private void StartHealthRefresh()
    {
        if (_healthTimer is null)
        {
            _healthTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _healthTimer.Tick += (_, _) =>
            {
                if (HealthAutoRefreshCheckBox.IsChecked == true)
                {
                    RefreshHealthPage();
                }
            };
        }

        _healthTimer.Start();
    }

    private void StopHealthRefresh() => _healthTimer?.Stop();

    private void Window_OnUnloaded(object sender, RoutedEventArgs e) => StopHealthRefresh();

    private void RefreshHealth_Click(object sender, RoutedEventArgs e) => RefreshHealthPage();

    private void RefreshHealthPage()
    {
        if (_instance is null)
        {
            HealthSummaryText.Text = "尚未选择版本。";
            HealthDetailText.Text = string.Empty;
            HealthCpuChart.SetSeries(Array.Empty<double>());
            HealthMemoryChart.SetSeries(Array.Empty<double>());
            HealthProcessList.ItemsSource = null;
            HealthLogBox.Text = string.Empty;
            return;
        }

        var providers = _healthProviders;
        var current = providers?.CurrentResource(_instance);
        var history = providers is null
            ? (IReadOnlyList<InstanceResourceSnapshot>)Array.Empty<InstanceResourceSnapshot>()
            : providers.ResourceHistory(_instance);
        IReadOnlyList<ProcessResourceLine> processes = Array.Empty<ProcessResourceLine>();
        if (providers is not null)
        {
            processes = providers.Processes(_instance);
        }
        var running = _instance.RuntimeStatus == InstanceRuntimeStatus.Running
            || _instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached;
        HealthSummaryText.Text = current is null
            ? $"{(running ? "运行中" : "未运行")} · 暂无实时数据（实例运行后每 5 秒采样）"
            : $"{(running ? "运行中" : "已停止")} · 已运行 {FormatHealthDuration(current.Uptime)} · {current.ProcessCount} 个进程 · 最近采样 {current.SampledAt:HH:mm:ss}";
        HealthDetailText.Text = $"{_instance.Name} · {_instance.KindText} · {(_instance.WebUrl ?? "未启动")}";

        HealthCpuChart.LineBrush = (WpfBrush)FindResource("BlueBrush");
        HealthCpuChart.MutedBrush = (WpfBrush)FindResource("MutedBrush");
        HealthCpuChart.GridBrush = (WpfBrush)FindResource("LineBrush");
        HealthCpuChart.Caption = "CPU 占用（%）";
        HealthCpuChart.FixedMaximum = 100;
        HealthCpuChart.FormatValue = value => value.ToString("0.#") + "%";
        HealthCpuChart.SetSeries(history.Select(snapshot => snapshot.CpuPercent).ToArray());

        HealthMemoryChart.LineBrush = (WpfBrush)FindResource("SuccessTextBrush");
        HealthMemoryChart.MutedBrush = (WpfBrush)FindResource("MutedBrush");
        HealthMemoryChart.GridBrush = (WpfBrush)FindResource("LineBrush");
        HealthMemoryChart.Caption = "内存占用（MB）";
        HealthMemoryChart.FixedMaximum = 0;
        HealthMemoryChart.FormatValue = value => value.ToString("0.#") + " MB";
        HealthMemoryChart.SetSeries(
            history.Select(snapshot => snapshot.WorkingSetBytes / 1024.0 / 1024).ToArray());

        var processesRows = processes
            .Select(line => new
            {
                line.ProcessId,
                line.Name,
                MemoryText = MainWindow.FormatBytes(line.WorkingSetBytes),
                CpuText = FormatHealthDuration(line.CpuTime)
            })
            .ToArray();
        HealthProcessList.ItemsSource = processesRows;

        var logs = providers?.Logs(_instance) ?? Array.Empty<InstanceLogLine>();
        if (logs.Count != _healthLogLineCount)
        {
            _healthLogLineCount = logs.Count;
            HealthLogBox.Text = string.Join(
                Environment.NewLine,
                logs.TakeLast(500).Select(line => $"{line.At:HH:mm:ss} [{line.Source}] {line.Text}"));
            HealthLogBox.ScrollToEnd();
        }

        var evidence = providers?.StartupEvidence?.Invoke(_instance) ?? Array.Empty<StartupEvidence>();
        HealthEvidenceList.ItemsSource = evidence
            .Select(item => new
            {
                TimeText = item.At.ToString("HH:mm:ss"),
                LayerText = item.Layer switch
                {
                    BootLayer.Process => "进程",
                    BootLayer.Log => "日志",
                    BootLayer.Http => "HTTP",
                    _ => "页面"
                },
                item.Summary,
                Detail = item.Detail ?? string.Empty
            })
            .ToArray();
    }

    private static string FormatHealthDuration(TimeSpan duration) => duration switch
    {
        { TotalHours: >= 1 } => $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分",
        { TotalMinutes: >= 1 } => $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒",
        _ => $"{Math.Max(0, (int)duration.TotalSeconds)} 秒"
    };

    private void ClearHealthLog_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            return;
        }

        _healthProviders?.ClearLogs?.Invoke(_instance);
        _healthLogLineCount = -1;
        HealthActionText.Text = "已清空该实例的日志缓冲。";
        RefreshHealthPage();
    }

    private void CopyHealthLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (HealthLogBox.Text.Length == 0)
            {
                HealthActionText.Text = "日志为空。";
                return;
            }

            System.Windows.Clipboard.SetText(HealthLogBox.Text);
            HealthActionText.Text = "日志已复制到剪贴板。";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            HealthActionText.Text = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 清理该实例的残留进程（桌宠 / 市场 helper / 上次异常退出遗留）。
    /// 实例运行中时把当前 dsh 根进程作为 keepPid 保留，不会误杀实例本体。
    /// </summary>
    private void CleanupHealth_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null || _healthProviders?.CleanupProcesses is null)
        {
            HealthActionText.Text = "当前没有可用的清理入口。";
            return;
        }

        var running = _instance.RuntimeStatus == InstanceRuntimeStatus.Running
            || _instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached;
        var message = running
            ? $"将清理实例 {_instance.Name} 的残留进程（桌宠、插件市场 helper、上次异常退出的旧进程等）。\n\n当前运行的 dsh 主进程会保留。继续？"
            : $"实例 {_instance.Name} 未运行，将清理它遗留的全部进程（可能来自上次异常退出）。\n\n继续？";
        var owner = Window.GetWindow(this);
        var confirmed = owner is null
            ? System.Windows.MessageBox.Show(
                message, "清理残留进程", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes
            : System.Windows.MessageBox.Show(
                owner, message, "清理残留进程", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        try
        {
            var cleaned = _healthProviders.CleanupProcesses(_instance);
            HealthActionText.Text = cleaned == 0
                ? "没有发现需要清理的残留进程。"
                : $"已清理 {cleaned} 个残留进程。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HealthActionText.Text = $"清理失败：{ex.Message}";
        }

        RefreshHealthPage();
    }

    private void Configuration_Click(object sender, RoutedEventArgs e) => ShowPage(ConfigurationButton);

    private void Plugins_Click(object sender, RoutedEventArgs e) => ShowPage(PluginsButton);

    private void Snapshots_Click(object sender, RoutedEventArgs e)
    {
        RefreshSnapshots();
        ShowPage(SnapshotsButton);
    }

    private void Export_Click(object sender, RoutedEventArgs e) => ShowPage(ExportButton);

    private void ShowPage(WpfButton activeButton)
    {
        PersonalizationPage.Visibility = ReferenceEquals(activeButton, PersonalizationButton)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConfigurationPage.Visibility = ReferenceEquals(activeButton, ConfigurationButton)
            ? Visibility.Visible
            : Visibility.Collapsed;
        PluginPage.Visibility = ReferenceEquals(activeButton, PluginsButton)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SnapshotPage.Visibility = ReferenceEquals(activeButton, SnapshotsButton)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExportPage.Visibility = ReferenceEquals(activeButton, ExportButton)
            ? Visibility.Visible
            : Visibility.Collapsed;
        HealthPage.Visibility = ReferenceEquals(activeButton, HealthButton)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (ReferenceEquals(activeButton, HealthButton))
        {
            StartHealthRefresh();
            RefreshHealthPage();
        }
        else
        {
            StopHealthRefresh();
        }

        PageHeaderText.Text = activeButton == ConfigurationButton
            ? "配置"
            : activeButton == PluginsButton
                ? "插件管理"
                : activeButton == SnapshotsButton
                    ? "快照回滚"
                : activeButton == HealthButton
                    ? "运行状况"
                : activeButton == ExportButton
                    ? "导出"
                    : "个性化";
        PageDescriptionText.Text = activeButton == ConfigurationButton
            ? "决定对话文件同步范围，以及是否让所有版本自动同步模型。"
            : activeButton == PluginsButton
                ? "像 PCL2 的 Mod 管理一样，在当前版本快速启用、禁用或删除 Plugin。"
                : activeButton == SnapshotsButton
                    ? "创建加密配置快照，或把当前版本恢复到先前状态。"
                : activeButton == ExportButton
                    ? "导出可以分享的版本设计，不带隐私内容和会话。"
                    : activeButton == HealthButton
                        ? "实时查看这个实例的 CPU/内存曲线、进程树与运行日志。"
                        : "查看当前版本和它自己的 DSH_HOME。";

        foreach (var button in new[] { PersonalizationButton, ConfigurationButton, PluginsButton, SnapshotsButton, HealthButton, ExportButton })
        {
            button.Background = ReferenceEquals(button, activeButton)
                ? new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(227, 240, 253))
                : WpfBrushes.Transparent;
            button.Foreground = ReferenceEquals(button, activeButton)
                ? (WpfBrush)FindResource("BlueBrush")
                : (WpfBrush)FindResource("TextBrush");
        }
    }

    private void SnapshotBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSnapshotButtons();

    private async void CreateSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null || !CanMutateSnapshot())
        {
            SnapshotStatusText.Text = "请先停止当前版本，再创建配置快照。";
            return;
        }

        SetSnapshotBusy(true);
        try
        {
            var snapshot = await Task.Run(() => _snapshotService.CreateSnapshot(_instance, "手动快照"));
            RefreshSnapshots();
            SnapshotBox.SelectedItem = SnapshotBox.Items
                .OfType<VersionSnapshotInfo>()
                .FirstOrDefault(item => string.Equals(item.FilePath, snapshot.FilePath, StringComparison.OrdinalIgnoreCase));
            SnapshotStatusText.Text = "配置快照已创建。快照由当前 Windows 用户加密，不包含会话文件。";
        }
        catch (Exception ex)
        {
            SnapshotStatusText.Text = $"创建快照失败：{ex.Message}";
        }
        finally
        {
            SetSnapshotBusy(false);
        }
    }

    private async void RollbackSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null
            || SnapshotBox.SelectedItem is not VersionSnapshotInfo snapshot
            || !CanMutateSnapshot())
        {
            SnapshotStatusText.Text = "请先停止版本并选择一个可用快照。";
            return;
        }

        if (System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                $"确定把“{_instance.Name}”的配置恢复到 {snapshot.DisplayName}？\n\n恢复前会再自动创建一个回滚点；会话文件不会改变。",
                "确认回滚版本配置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SetSnapshotBusy(true);
        try
        {
            var rollbackPoint = await Task.Run(() => _snapshotService.RestoreSnapshot(_instance, snapshot.FilePath));
            _settings = _settingsService.Read(_instance);
            LoadWorkspaceNames();
            LoadConfigurationControls();
            LoadPluginSettingsControls();
            await LoadPluginsAsync();
            _settingsSaved();
            RefreshSnapshots();
            SnapshotStatusText.Text = $"配置已回滚；恢复前状态保存在：{rollbackPoint.DisplayName}。";
        }
        catch (Exception ex)
        {
            SnapshotStatusText.Text = $"回滚失败：{ex.Message}";
        }
        finally
        {
            SetSnapshotBusy(false);
        }
    }

    private void RefreshSnapshots()
    {
        if (SnapshotBox is null)
        {
            return;
        }

        try
        {
            SnapshotBox.ItemsSource = _instance is null
                ? Array.Empty<VersionSnapshotInfo>()
                : _snapshotService.ListSnapshots(_instance);
            SnapshotBox.SelectedIndex = SnapshotBox.Items.Count > 0 ? 0 : -1;
        }
        catch (Exception ex)
        {
            SnapshotBox.ItemsSource = Array.Empty<VersionSnapshotInfo>();
            SnapshotStatusText.Text = $"读取版本快照失败：{ex.Message}";
        }

        UpdateSnapshotButtons();
    }

    private bool CanMutateSnapshot() => _instance is { } instance
        && instance.RuntimeStatus != InstanceRuntimeStatus.Running
        && instance.RuntimeOwnership != InstanceRuntimeOwnership.Attached;

    private void SetSnapshotBusy(bool busy)
    {
        SnapshotBox.IsEnabled = !busy;
        CreateSnapshotButton.IsEnabled = !busy && CanMutateSnapshot();
        RollbackSnapshotButton.IsEnabled = !busy
            && CanMutateSnapshot()
            && SnapshotBox.SelectedItem is VersionSnapshotInfo;
    }

    private void UpdateSnapshotButtons() => SetSnapshotBusy(false);

    private void SyncAllConfiguration_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        ConfigurationOptionsPanel.IsEnabled = SyncAllConfigurationCheckBox.IsChecked != true;
        UpdateWorkspaceEnabled();
    }

    private void ConversationMode_Changed(object sender, RoutedEventArgs e) => UpdateWorkspaceEnabled();

    private void UpdateWorkspaceEnabled()
    {
        if (ConversationWorkspaceBox is not null && ConfigurationOptionsPanel is not null)
        {
            ConversationWorkspaceBox.IsEnabled = ConfigurationOptionsPanel.IsEnabled
                && ConversationWorkspaceRadio.IsChecked == true;
        }
    }

    private void SaveConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            ConfigurationStatusText.Text = "请先选择版本。";
            return;
        }

        try
        {
            var updated = ReadConfigurationSettings();
            var snapshot = TryCreateSnapshot("保存版本同步配置前");
            _settingsService.Save(_instance, updated);
            _settings = updated;
            ConfigurationStatusText.Text = snapshot is null
                ? "配置已保存。对话文件按当前选项处理，模型按“所有版本自动同步模型”设置处理。"
                : "配置已保存，并已保留修改前快照。";
            _settingsSaved();
        }
        catch (Exception ex)
        {
            ConfigurationStatusText.Text = $"保存配置失败：{ex.Message}";
        }
    }

    private VersionSettingsData ReadConfigurationSettings() => new()
    {
        SyncAllConfiguration = SyncAllConfigurationCheckBox.IsChecked == true,
        ConversationSyncMode = ConversationWorkspaceRadio.IsChecked == true
            ? ConversationSyncMode.Workspace
            : ConversationAllRadio.IsChecked == true
                ? ConversationSyncMode.All
                : ConversationSyncMode.Independent,
        ConversationWorkspace = ConversationWorkspaceRadio.IsChecked == true
            ? ConversationWorkspaceBox.Text
            : null,
        SyncModelProviders = SyncModelProvidersCheckBox.IsChecked == true,
        UseDshMarketHotReload = _settings.UseDshMarketHotReload,
        WindowTitle = _settings.WindowTitle,
        NodeExecutablePath = _settings.NodeExecutablePath,
        OpenMode = _settings.OpenMode,
        CustomOpenTargetPath = _settings.CustomOpenTargetPath
    };

    private void OpenModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateCustomOpenTargetEnabled();

    private void UpdateCustomOpenTargetEnabled()
    {
        if (CustomOpenTargetPanel is null)
        {
            return;
        }

        CustomOpenTargetPanel.IsEnabled = string.Equals(
            OpenModeBox.SelectedValue?.ToString(),
            VersionOpenMode.Custom.ToString(),
            StringComparison.Ordinal);
        CustomOpenTargetPanel.Opacity = CustomOpenTargetPanel.IsEnabled ? 1 : 0.55;
    }

    private void BrowseOpenTarget_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "选择这个版本的打开方式",
            Filter = "程序、脚本和快捷方式|*.exe;*.com;*.bat;*.cmd;*.ps1;*.lnk|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            CustomOpenTargetBox.Text = dialog.FileName;
        }
    }

    private void SaveOpenMode_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            OpenModeStatusText.Text = "请先选择版本。";
            return;
        }

        if (!Enum.TryParse<VersionOpenMode>(OpenModeBox.SelectedValue?.ToString(), out var openMode))
        {
            OpenModeStatusText.Text = "打开方式无效。";
            return;
        }

        var customOpenTargetPath = _settings.CustomOpenTargetPath;
        if (openMode == VersionOpenMode.Custom)
        {
            try
            {
                customOpenTargetPath = Path.GetFullPath(CustomOpenTargetBox.Text.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                OpenModeStatusText.Text = "手动绑定的文件路径无效。";
                return;
            }

            if (!File.Exists(customOpenTargetPath))
            {
                OpenModeStatusText.Text = "手动绑定的文件不存在，请重新选择。";
                return;
            }
        }

        try
        {
            var updated = CopySettings();
            updated.OpenMode = openMode;
            updated.CustomOpenTargetPath = customOpenTargetPath;
            var snapshot = TryCreateSnapshot("保存打开方式前");
            _settingsService.Save(_instance, updated);
            _settings = updated;
            var modeText = openMode switch
            {
                VersionOpenMode.Web => "Web 启动（dsh 原生）",
                VersionOpenMode.Custom => $"手动打开 {Path.GetFileName(customOpenTargetPath)}",
                _ => "Desktop 启动（启动器方式）"
            };
            OpenModeStatusText.Text = snapshot is null
                ? $"已保存：{modeText}。"
                : $"已保存：{modeText}，并已保留修改前快照。";
            _settingsSaved();
        }
        catch (Exception ex)
        {
            OpenModeStatusText.Text = $"保存打开方式失败：{ex.Message}";
        }
    }

    private VersionSettingsData CopySettings() => new()
    {
        SyncAllConfiguration = _settings.SyncAllConfiguration,
        ConversationSyncMode = _settings.ConversationSyncMode,
        ConversationWorkspace = _settings.ConversationWorkspace,
        SyncModelProviders = _settings.SyncModelProviders,
        UseDshMarketHotReload = _settings.UseDshMarketHotReload,
        WindowTitle = _settings.WindowTitle,
        NodeExecutablePath = _settings.NodeExecutablePath,
        OpenMode = _settings.OpenMode,
        CustomOpenTargetPath = _settings.CustomOpenTargetPath
    };

    private async void RefreshPlugins_Click(object sender, RoutedEventArgs e) => await LoadPluginsAsync();

    private async Task LoadPluginsAsync()
    {
        if (_instance is null)
        {
            return;
        }

        try
        {
            var entries = await _extensionService.ListAsync(_instance);
            var plugins = entries.Where(entry => entry.Kind == ExtensionKind.Plugin).ToArray();
            PluginList.ItemsSource = plugins;
            PluginEmptyText.Visibility = plugins.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            PluginStatusText.Text = $"已读取 {plugins.Length} 个 Plugin。内置 Plugin 不能直接修改。";
        }
        catch (Exception ex)
        {
            PluginStatusText.Text = $"读取 Plugin 失败：{ex.Message}";
        }
    }

    private async void PluginToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null || (sender as FrameworkElement)?.Tag is not ExtensionEntry entry)
        {
            return;
        }

        var enabled = string.Equals((sender as WpfButton)?.Content?.ToString(), "启用", StringComparison.Ordinal);
        try
        {
            await _extensionService.SetPluginEnabledAsync(_instance, entry, enabled);
            PluginStatusText.Text = $"Plugin“{entry.Name}”已{(enabled ? "启用" : "禁用")}。重新启动实例后生效。";
            await LoadPluginsAsync();
        }
        catch (Exception ex)
        {
            PluginStatusText.Text = $"修改 Plugin 失败：{ex.Message}";
        }
    }

    private async void PluginDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null || (sender as FrameworkElement)?.Tag is not ExtensionEntry entry)
        {
            return;
        }

        if (System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                $"确定删除“{entry.Name}”？该操作只针对当前版本。",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var output = await _extensionService.RemovePluginAsync(_instance, entry.Name, _nodeRuntimeProvider());
            PluginStatusText.Text = string.IsNullOrWhiteSpace(output)
                ? $"Plugin“{entry.Name}”已删除。"
                : $"Plugin“{entry.Name}”已删除：{output}";
            await LoadPluginsAsync();
        }
        catch (Exception ex)
        {
            PluginStatusText.Text = $"删除 Plugin 失败：{ex.Message}";
        }
    }

    private void BrowseNode_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "选择 Node.js 可执行文件",
            Filter = "Node.js (node.exe)|node.exe|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            NodePathBox.Text = dialog.FileName;
        }
    }

    private void SavePluginSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            PluginStatusText.Text = "请先选择版本。";
            return;
        }

        try
        {
            var nodePath = string.IsNullOrWhiteSpace(NodePathBox.Text)
                ? null
                : Path.GetFullPath(NodePathBox.Text.Trim());
            if (nodePath is not null && !File.Exists(nodePath))
            {
                throw new FileNotFoundException("选择的 Node.js 文件不存在。", nodePath);
            }

            var updated = new VersionSettingsData
            {
                SyncAllConfiguration = _settings.SyncAllConfiguration,
                ConversationSyncMode = _settings.ConversationSyncMode,
                ConversationWorkspace = _settings.ConversationWorkspace,
                SyncModelProviders = _settings.SyncModelProviders,
                WindowTitle = WindowTitleBox.Text,
                NodeExecutablePath = nodePath,
                OpenMode = _settings.OpenMode,
                CustomOpenTargetPath = _settings.CustomOpenTargetPath,
                UseDshMarketHotReload = _settings.UseDshMarketHotReload
            };
            var snapshot = TryCreateSnapshot("保存窗口与 Node 设置前");
            _settingsService.Save(_instance, updated);
            _settings = updated;
            PluginStatusText.Text = snapshot is null
                ? "窗口标题和 Node.js 设置已保存。"
                : "窗口标题和 Node.js 设置已保存，并已保留修改前快照。";
            NodeRuntimeText.Text = FormatNodeRuntime();
            _settingsSaved();
        }
        catch (Exception ex)
        {
            PluginStatusText.Text = $"保存窗口与 Node 设置失败：{ex.Message}";
        }
    }

    private void SavePackageExtension_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _packageService.SavePackageExtension(PackageExtensionBox.Text);
            ExportStatusText.Text = $"已保存整合包格式：{_packageService.PackageExtension}";
        }
        catch (Exception ex)
        {
            ExportStatusText.Text = $"保存整合包格式失败：{ex.Message}";
        }
    }

    private async void ExportPackage_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            ExportStatusText.Text = "请先选择版本。";
            return;
        }

        using var dialog = new Forms.SaveFileDialog
        {
            Title = "导出 DSH Launcher 版本整合包",
            Filter = $"DSH 整合包 (*{_packageService.PackageExtension})|*{_packageService.PackageExtension}|所有文件|*.*",
            AddExtension = true,
            DefaultExt = _packageService.PackageExtension.TrimStart('.'),
            FileName = $"{SafeFileName(_instance.Name)}{_packageService.PackageExtension}",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        var options = new VersionExportOptions(
            IncludeProviderConfiguration: false,
            IncludePluginConfigurationCheckBox.IsChecked == true);
        try
        {
            ExportStatusText.Text = "正在生成整合包…";
            await Task.Run(() => _packageService.ExportPackage(_instance, dialog.FileName, options));
            ExportStatusText.Text = $"已导出：{dialog.FileName}。未包含 API Key、隐私值和会话。";
        }
        catch (Exception ex)
        {
            ExportStatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "dsh-version" : result;
    }

    private VersionSnapshotInfo? TryCreateSnapshot(string reason)
    {
        if (_instance is null
            || _instance.RuntimeStatus == InstanceRuntimeStatus.Running
            || _instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            return null;
        }

        return _snapshotService.CreateSnapshot(_instance, reason, automatic: true);
    }
}
