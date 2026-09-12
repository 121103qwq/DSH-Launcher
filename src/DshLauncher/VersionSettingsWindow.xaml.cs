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
    private readonly Func<ManagerInstance, string, ManagerInstance> _renameVersion;
    private readonly Action _settingsSaved;
    private readonly bool _openPluginPage;
    private readonly InstanceHealthProviders? _healthProviders;
    /// <summary>保存前自动快照用（版本与快照页已搬到「版本控制」，这里的自动快照仍保留）。</summary>
    private readonly VersionSnapshotService _snapshotService = new();
    private System.Windows.Threading.DispatcherTimer? _healthTimer;
    private int _healthLogLineCount = -1;
    /// <summary>诊断类状态文本的保留期：运行状况页 1s 周期刷新在此期间不覆盖（work-log/87，变更集 104）。</summary>
    private DateTimeOffset _bisectStatusPinnedUntil = DateTimeOffset.MinValue;
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
        RefreshTerminalCommandCard();
        ShowPage(_openPluginPage ? PluginsButton : PersonalizationButton);

        if (_instance is null)
        {
            VersionRequiredText.Text = "请先在启动页选择一个版本；当前页面只展示设置结构。";
            VersionRequiredText.Visibility = Visibility.Visible;
            PersonalizationPage.IsEnabled = false;
            ConfigurationPage.IsEnabled = false;
            PluginPage.IsEnabled = false;
            ExportPage.IsEnabled = false;
            return;
        }

        await LoadPluginsAsync();
    }

    /// <summary>刷新“运行版本”区域（当前 dsh 版本 + 更新提示开关）。</summary>
    /// <summary>“显示 DSh 版本更新提示”开关：写实例设置 + 刷新提示。</summary>
    /// <summary>开启更新提示时联网查官方版本，与当前版本比较后显示一行提示；失败也给出明确文案。</summary>
    /// <summary>刷新「切换历史」卡：列表 + 回退按钮可用性（历史来自 Launcher 数据根的 version-switch-history.json）。</summary>
    /// <summary>「回退到上一版本」：把上一条历史的起点版本预选进切换向导，仍需检查 + 确认。</summary>
    /// <summary>“更换运行版本”：弹对话框，成功后回写实例。</summary>
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
    }

    /// <summary>
    /// 「终端启动（TUI 插件）」（变更集 113/114）：扫到 TUI 插件时给出可粘贴的完整命令。
    /// 命令优先指向**承载 TUI 的终端面 profile**——活动 profile 若不是终端面（如 web 面里也装了 dsh-tui），
    /// 拿它跑出来的不是 TUI；找不到终端面 profile 时才退回活动 profile。命令带 DSH_HOME，
    /// 避开 dsh 退到默认 home（~/.dsh）找不到 profile。
    /// </summary>
    private void RefreshTerminalCommandCard()
    {
        if (_instance is null || TerminalCommandCard is null || TerminalCommandBox is null)
        {
            return;
        }

        var activeProfile = DshProfileService.ResolveActiveName(_instance, _settingsService);
        var profiles = new DshProfileService();
        var candidates = new List<string> { activeProfile };
        try
        {
            foreach (var info in profiles.List(_instance))
            {
                if (!string.Equals(info.Name, activeProfile, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(info.Name);
                }
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Warn("读取 profile 列表失败：" + ex.Message);
        }

        string? chosenProfile = null;
        UiPluginScanResult? chosenScan = null;
        foreach (var name in candidates)
        {
            UiPluginScanResult scan;
            try
            {
                scan = InstanceUiPluginScanner.Scan(_instance, name);
            }
            catch (Exception ex)
            {
                LauncherLog.Warn("扫描 TUI 插件失败：" + ex.Message);
                continue;
            }

            if (!scan.HasTuiProvider)
            {
                continue;
            }

            chosenProfile ??= name;
            chosenScan ??= scan;
            var info = profiles.Describe(_instance, name);
            if (PresentationSurfaceService.Detect(name, info.Bundles) == PresentationSurface.Terminal)
            {
                chosenProfile = name;
                chosenScan = scan;
                break;
            }
        }

        if (chosenProfile is null || chosenScan is null)
        {
            TerminalCommandCard.Visibility = Visibility.Collapsed;
            return;
        }

        // 目标 profile 的栈里含 web-app 时 dsh 会起 Web UI 并默认弹浏览器 → 补 --no-open 抑制
        // （该选项由 dsh-web-app 定义：纯终端面 profile 不加；旧版 dsh 不支持时也不加）。
        var chosenInfo = profiles.Describe(_instance, chosenProfile);
        var noOpen = PresentationSurfaceService.Detect(chosenProfile, chosenInfo.Bundles) == PresentationSurface.Web
            && DshInstanceRunner.SupportsNoOpen(_instance.DetectedVersion);

        var command = TerminalLaunchService.BuildPowerShellCommand(
            _instance.DshHome,
            _instance.DshExecutablePath,
            chosenProfile,
            noOpen);
        if (command is null)
        {
            TerminalCommandCard.Visibility = Visibility.Collapsed;
            return;
        }

        var names = string.Join("、", chosenScan.TuiPlugins.Select(plugin =>
            plugin.Name + (plugin.Enabled ? string.Empty : "（未启用）")));
        TerminalCommandBox.Text = command;
        TerminalCommandHintText.Text = noOpen
            ? $"检测到 {names}（profile：{chosenProfile}）· 已带 --no-open，不会弹浏览器。"
            : $"检测到 {names}（profile：{chosenProfile}）。";
        TerminalCommandCard.Visibility = Visibility.Visible;
    }

    private void CopyTerminalCommand_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TerminalCommandBox.Text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(TerminalCommandBox.Text);
            TerminalCommandHintText.Text = "命令已复制；粘到 PowerShell 里执行即可。";
        }
        catch (Exception ex)
        {
            TerminalCommandHintText.Text = "复制失败：" + ex.Message;
        }
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

    private void Window_OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopHealthRefresh();
        try
        {
            _bisectCancellation?.Cancel();
        }
        catch
        {
            // 已释放/已取消都无所谓。
        }
    }

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

        LoadAutoStopControls();
        LoadCrashControls();
        UpdateBisectControls();
    }

    private bool _autoStopControlsInitialized;

    /// <summary>
    /// 只初始化一次（填充下拉 + 回填当前设置）。刷新时绝不覆盖用户正在编辑的控件——
    /// 否则 1 秒自动刷新会把刚勾上的开关改回去。
    /// </summary>
    private void LoadAutoStopControls()
    {
        if (!_autoStopControlsInitialized)
        {
            _autoStopControlsInitialized = true;
            if (AutoStopMinutesBox.Items.Count == 0)
            {
                foreach (var minutes in new[] { 5, 15, 30, 60, 120, 240 })
                {
                    AutoStopMinutesBox.Items.Add(new System.Windows.Controls.ComboBoxItem
                    {
                        Content = $"{minutes} 分钟",
                        Tag = minutes
                    });
                }
            }

            AutoStopCheckBox.IsChecked = _settings.AutoStopWhenIdle;
            var selected = _settings.AutoStopIdleMinutes ?? 30;
            foreach (System.Windows.Controls.ComboBoxItem item in AutoStopMinutesBox.Items)
            {
                if (Equals(item.Tag, selected))
                {
                    AutoStopMinutesBox.SelectedItem = item;
                    break;
                }
            }

            AutoStopMinutesBox.SelectedItem ??= AutoStopMinutesBox.Items[2];
        }

        UpdateAutoStopStatus();
    }

    private bool _crashControlsInitialized;
    private System.Threading.CancellationTokenSource? _bisectCancellation;
    private string? _bisectCulprit;

    /// <summary>插件排查：状态文案 + 按钮可用性（每次刷新更新）。</summary>
    private void UpdateBisectControls()
    {
        var thirdParty = _healthProviders?.ThirdPartyPlugins?.Invoke(_instance!) ?? Array.Empty<string>();
        var running = _healthProviders?.IsInstanceRunning?.Invoke(_instance!) ?? false;
        var busy = _bisectCancellation is not null;
        BisectButton.IsEnabled = !busy && thirdParty.Count > 0 && !running;
        if (busy)
        {
            return;
        }

        if (_bisectCulprit is { } culprit)
        {
            DisableCulpritButton.Visibility = Visibility.Visible;
            return;
        }

        DisableCulpritButton.Visibility = Visibility.Collapsed;
        // 卫生检查/复位等用户主动触发的结果文本，在保留期内不被周期刷新覆盖（work-log/87）。
        if (DateTimeOffset.Now < _bisectStatusPinnedUntil)
        {
            return;
        }

        if (thirdParty.Count == 0)
        {
            BisectStatusText.Text = "该实例没有第三方插件，无需定位。";
            return;
        }

        BisectStatusText.Text = running
            ? $"当前有 {thirdParty.Count} 个第三方插件；请先停止实例再定位。"
            : $"当前有 {thirdParty.Count} 个第三方插件：{string.Join("、", thirdParty)}";
    }

    /// <summary>固定诊断类状态文本 20 秒（避免被运行状况页的 1s 周期刷新覆盖）。</summary>
    private void PinBisectStatus() => _bisectStatusPinnedUntil = DateTimeOffset.Now.AddSeconds(20);

    /// <summary>A3 收尾：检查并复位 profile 残留（work-log/71 事故预防）。复位前自动快照。</summary>
    private void ProfileHygiene_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            return;
        }

        if (_healthProviders?.IsInstanceRunning?.Invoke(_instance) == true)
        {
            BisectStatusText.Text = "请先停止实例，再检查/复位 profile 配置。";
            PinBisectStatus();
            return;
        }

        var profileName = DshProfileService.ResolveActiveName(_instance, _settingsService);
        var profileDirectory = Path.Combine(_instance.DshHome, "profiles", profileName);
        var issues = ProfileHygiene.Inspect(profileDirectory);
        if (issues.Count == 0)
        {
            BisectStatusText.Text = $"profile「{profileName}」配置干净，没有发现残留。";
            PinBisectStatus();
            return;
        }

        var lines = string.Join("\n", issues.Select(issue => "· " + issue.Detail));
        var confirmed = System.Windows.MessageBox.Show(
            $"profile「{profileName}」发现 {issues.Count} 处残留：\n\n{lines}\n\n"
            + "复位会先快照，然后：移走空的 pnpm-lock.yaml、清除 allowBuilds 残留、补空 dependencies 键。\n"
            + "只动这三个配置文件，不会碰 node_modules、会话与凭据。\n\n确定要复位吗？",
            "复位 profile 配置残留",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed)
        {
            return;
        }

        var snapshotDirectory = Path.Combine(
            new LauncherPaths().RootDirectory,
            "backups",
            $"profile-hygiene-{DateTime.Now:yyyyMMdd-HHmmss}");
        if (!ProfileHygiene.TryReset(profileDirectory, snapshotDirectory, out var actions, out var error))
        {
            BisectStatusText.Text = $"复位失败：{error}";
            PinBisectStatus();
            return;
        }

        BisectStatusText.Text = "复位完成：" + string.Join("；", actions);
        PinBisectStatus();
        LauncherLog.Info("profile 卫生复位完成。", "E3002", new { profileDirectory, snapshotDirectory, actions });
    }

    private async void BisectPlugins_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null || _healthProviders?.RunPluginBisect is null)
        {
            return;
        }

        if (_healthProviders.IsInstanceRunning?.Invoke(_instance) == true)
        {
            BisectStatusText.Text = "请先停止实例，再开始定位。";
            return;
        }

        _bisectCulprit = null;
        DisableCulpritButton.Visibility = Visibility.Collapsed;
        _bisectCancellation = new System.Threading.CancellationTokenSource();
        BisectButton.IsEnabled = false;
        BisectStatusText.Text = "正在定位…（每轮会启动一次实例，请稍候）";
        var progress = new Progress<string>(text => BisectStatusText.Text = text);
        try
        {
            var result = await _healthProviders.RunPluginBisect(_instance, _bisectCancellation.Token, progress);
            _bisectCulprit = result.Culprit;
            BisectStatusText.Text = result.Trace.Count > 0
                ? $"{result.Summary}\n{string.Join("\n", result.Trace)}"
                : result.Summary;
            if (result.Culprit is { } culprit)
            {
                DisableCulpritButton.Content = $"禁用 {culprit} 并正常启动";
                DisableCulpritButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            BisectStatusText.Text = $"定位失败：{ex.Message}";
        }
        finally
        {
            _bisectCancellation?.Dispose();
            _bisectCancellation = null;
            BisectButton.IsEnabled = true;
        }
    }

    private async void DisableCulprit_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null || _bisectCulprit is null || _healthProviders?.DisablePlugin is null)
        {
            return;
        }

        var culprit = _bisectCulprit;
        var owner = Window.GetWindow(this);
        var message = $"禁用插件 {culprit} 并正常启动实例？\n\n会先把当前配置存成回滚点，可随时恢复。";
        var confirmed = owner is null
            ? System.Windows.MessageBox.Show(message, "禁用插件", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
            : System.Windows.MessageBox.Show(owner, message, "禁用插件", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        DisableCulpritButton.IsEnabled = false;
        try
        {
            await _healthProviders.DisablePlugin(_instance, culprit);
            BisectStatusText.Text = $"已禁用 {culprit}，正在正常启动…";
        }
        finally
        {
            DisableCulpritButton.IsEnabled = true;
        }
    }

    /// <summary>崩溃恢复控件：只初始化一次，刷新时只更新状态/列表（不覆盖用户选择）。</summary>
    private void LoadCrashControls()
    {
        if (!_crashControlsInitialized)
        {
            _crashControlsInitialized = true;
            if (CrashPolicyBox.Items.Count == 0)
            {
                foreach (var (policy, label) in new[]
                         {
                             (CrashRecoveryPolicy.NotifyOnly, "仅通知（默认）"),
                             (CrashRecoveryPolicy.AutoRestart, "自动重启（受限退避）"),
                             (CrashRecoveryPolicy.CoolDown, "直接冷却关闭"),
                             (CrashRecoveryPolicy.RestartThenCoolDown, "自动重启后冷却（推荐）")
                         })
                {
                    CrashPolicyBox.Items.Add(new System.Windows.Controls.ComboBoxItem
                    {
                        Content = label,
                        Tag = policy
                    });
                }
            }

            if (CrashLimitBox.Items.Count == 0)
            {
                foreach (var limit in Enumerable.Range(1, 10))
                {
                    CrashLimitBox.Items.Add(new System.Windows.Controls.ComboBoxItem
                    {
                        Content = $"{limit} 次",
                        Tag = limit
                    });
                }
            }

            foreach (System.Windows.Controls.ComboBoxItem item in CrashPolicyBox.Items)
            {
                if (item.Tag is CrashRecoveryPolicy policy && policy == _settings.CrashPolicy)
                {
                    CrashPolicyBox.SelectedItem = item;
                    break;
                }
            }

            CrashPolicyBox.SelectedItem ??= CrashPolicyBox.Items[0];
            var limitValue = _settings.CrashRestartLimit ?? 5;
            foreach (System.Windows.Controls.ComboBoxItem item in CrashLimitBox.Items)
            {
                if (Equals(item.Tag, limitValue))
                {
                    CrashLimitBox.SelectedItem = item;
                    break;
                }
            }

            CrashLimitBox.SelectedItem ??= CrashLimitBox.Items[4];
        }

        UpdateCrashStatus();
    }

    private void UpdateCrashStatus()
    {
        var status = _healthProviders?.CrashStatus?.Invoke(_instance!) ?? CrashRecoveryStatus.Normal;
        var records = _healthProviders?.CrashRecords?.Invoke(_instance!) ?? Array.Empty<CrashRecord>();
        CrashRecordList.ItemsSource = records
            .Select(record => new
            {
                TimeText = record.At.ToString("MM-dd HH:mm:ss"),
                ExitText = record.ExitCode?.ToString() ?? "?",
                UptimeText = FormatCrashUptime(record.Uptime),
                record.Action,
                Cause = record.Cause ?? "未知原因",
                record.Summary
            })
            .ToArray();

        ClearCooldownButton.Visibility = status.Cooldown ? Visibility.Visible : Visibility.Collapsed;
        var causeText = string.IsNullOrWhiteSpace(status.LastCause) ? "未知原因" : status.LastCause;
        if (status.Cooldown)
        {
            CrashStatusText.Text =
                $"冷却中：上次崩溃 {status.LastCrashAt:MM-dd HH:mm:ss}（exitCode={status.LastExitCode?.ToString() ?? "?"}，{status.LastAction}，原因：{causeText}）；已停止自动重启，现场已保留。";
            return;
        }

        if (status.LastCrashAt is { } lastCrash)
        {
            CrashStatusText.Text = status.Attempts > 0
                ? $"最近崩溃 {lastCrash:MM-dd HH:mm:ss}（exitCode={status.LastExitCode?.ToString() ?? "?"}，原因：{causeText}），已自动重启 {status.Attempts} 次（上限见上方设置）。"
                : $"最近崩溃 {lastCrash:MM-dd HH:mm:ss}（exitCode={status.LastExitCode?.ToString() ?? "?"}，{status.LastAction}，原因：{causeText}）。";
            return;
        }

        CrashStatusText.Text = "运行正常，暂无崩溃记录。";
    }

    private static string FormatCrashUptime(TimeSpan uptime) => uptime <= TimeSpan.Zero
        ? "—"
        : uptime.TotalHours >= 1
            ? $"{uptime.TotalHours:0.#} 小时"
            : uptime.TotalMinutes >= 1
                ? $"{uptime.TotalMinutes:0.#} 分钟"
                : $"{uptime.TotalSeconds:0} 秒";

    private void SaveCrashPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            CrashStatusText.Text = "请先选择版本。";
            return;
        }

        var policy = CrashPolicyBox.SelectedItem is System.Windows.Controls.ComboBoxItem policyItem
            && policyItem.Tag is CrashRecoveryPolicy value
                ? value
                : CrashRecoveryPolicy.NotifyOnly;
        var limit = CrashLimitBox.SelectedItem is System.Windows.Controls.ComboBoxItem limitItem
            && limitItem.Tag is int limitValue
                ? limitValue
                : 5;
        try
        {
            _settings.CrashPolicy = policy;
            _settings.CrashRestartLimit = limit;
            _settingsService.Save(_instance, _settings);
            UpdateCrashStatus();
            CrashStatusText.Text = policy switch
            {
                CrashRecoveryPolicy.NotifyOnly => "已保存：崩溃后仅通知，不自动重启。",
                CrashRecoveryPolicy.AutoRestart => $"已保存：崩溃后自动重启（最多 {limit} 次，退避重试）。",
                CrashRecoveryPolicy.CoolDown => "已保存：崩溃后直接冷却关闭并保留现场。",
                _ => $"已保存：崩溃后自动重启（最多 {limit} 次），达上限转冷却关闭。"
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            CrashStatusText.Text = $"保存失败：{ex.Message}";
        }
    }

    private void ClearCooldown_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        var message = $"解除实例 {_instance.Name} 的崩溃冷却并立即重启？";
        var confirmed = owner is null
            ? System.Windows.MessageBox.Show(message, "清冷却并重启", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
            : System.Windows.MessageBox.Show(owner, message, "清冷却并重启", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        _healthProviders?.ClearCrashCooldown?.Invoke(_instance);
        CrashStatusText.Text = "已解除冷却，正在重启…";
    }

    private void UpdateAutoStopStatus()
    {
        var minutes = AutoStopMinutesBox.SelectedItem is System.Windows.Controls.ComboBoxItem item
            && item.Tag is int value
                ? value
                : _settings.AutoStopIdleMinutes ?? 30;
        var lastActivity = _healthProviders?.LastActivity?.Invoke(_instance!);
        if (AutoStopCheckBox.IsChecked != true)
        {
            AutoStopStatusText.Text = "当前关闭。开启后实例长时间无活动会自动停止。";
            return;
        }

        AutoStopStatusText.Text = lastActivity is null
            ? $"已开启（{minutes} 分钟）；尚无活动记录（实例启动后开始计时）。"
            : $"已开启（{minutes} 分钟）；最近活动：{lastActivity.At:HH:mm:ss}（{lastActivity.Source}）。";
    }

    private void ClearStartupEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        var message = $"清空实例 {_instance.Name} 的启动证据？\n\n将同时删除内存记录与落盘文件（.dsh-launcher/startup-evidence.jsonl）。";
        var confirmed = owner is null
            ? System.Windows.MessageBox.Show(
                message, "清空启动证据", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK
            : System.Windows.MessageBox.Show(
                owner, message, "清空启动证据", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        if (!confirmed)
        {
            return;
        }

        _healthProviders?.ClearStartupEvidence?.Invoke(_instance);
        HealthEvidenceList.ItemsSource = Array.Empty<object>();
        HealthActionText.Text = "启动证据已清空。";
    }

    private void SaveAutoStop_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            AutoStopStatusText.Text = "请先选择版本。";
            return;
        }

        var minutes = AutoStopMinutesBox.SelectedItem is System.Windows.Controls.ComboBoxItem item
            && item.Tag is int value
                ? value
                : 30;
        try
        {
            _settings.AutoStopWhenIdle = AutoStopCheckBox.IsChecked == true;
            _settings.AutoStopIdleMinutes = minutes;
            _settingsService.Save(_instance, _settings);
            UpdateAutoStopStatus();
            AutoStopStatusText.Text = _settings.AutoStopWhenIdle
                ? $"已保存：空闲 {minutes} 分钟自动停止（后台任务进行中不会停）。"
                : "已保存：空闲自动停止已关闭。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AutoStopStatusText.Text = $"保存失败：{ex.Message}";
        }
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
                : activeButton == HealthButton
                    ? "运行状况"
                : activeButton == ExportButton
                    ? "导出"
                    : "个性化";
        PageDescriptionText.Text = activeButton == ConfigurationButton
            ? "决定对话文件同步范围，以及是否让所有版本自动同步模型。"
            : activeButton == PluginsButton
                ? "像 PCL2 的 Mod 管理一样，在当前版本快速启用、禁用或删除 Plugin。"
                : activeButton == ExportButton
                    ? "导出可以分享的版本设计，不带隐私内容和会话。"
                    : activeButton == HealthButton
                        ? "实时查看这个实例的 CPU/内存曲线、进程树与运行日志。"
                        : "查看当前版本和它自己的 DSH_HOME。";

        foreach (var button in new[] { PersonalizationButton, ConfigurationButton, PluginsButton, HealthButton, ExportButton })
        {
            button.Background = ReferenceEquals(button, activeButton)
                ? new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(227, 240, 253))
                : WpfBrushes.Transparent;
            button.Foreground = ReferenceEquals(button, activeButton)
                ? (WpfBrush)FindResource("BlueBrush")
                : (WpfBrush)FindResource("TextBrush");
        }
    }

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
        ActiveProfile = _settings.ActiveProfile,   // 防抹：设置窗口的保存路径也要带上活动 profile（work-log/95）
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
    };
    private static string DescribeUiConfidence(UiPluginConfidence confidence) => confidence switch
    {
        UiPluginConfidence.Strong => "spec 声明",
        UiPluginConfidence.Medium => "关键字",
        _ => "包名启发式"
    };
    private VersionSettingsData CopySettings() => new()
    {
        SyncAllConfiguration = _settings.SyncAllConfiguration,
        ActiveProfile = _settings.ActiveProfile,   // 防抹：设置窗口的保存路径也要带上活动 profile（work-log/95）
        ConversationSyncMode = _settings.ConversationSyncMode,
        ConversationWorkspace = _settings.ConversationWorkspace,
        SyncModelProviders = _settings.SyncModelProviders,
        UseDshMarketHotReload = _settings.UseDshMarketHotReload,
        WindowTitle = _settings.WindowTitle,
        NodeExecutablePath = _settings.NodeExecutablePath,
        OpenMode = _settings.OpenMode,
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
                ActiveProfile = _settings.ActiveProfile,   // 防抹：设置窗口的保存路径也要带上活动 profile（work-log/95）
                ConversationSyncMode = _settings.ConversationSyncMode,
                ConversationWorkspace = _settings.ConversationWorkspace,
                SyncModelProviders = _settings.SyncModelProviders,
                WindowTitle = WindowTitleBox.Text,
                NodeExecutablePath = nodePath,
                OpenMode = _settings.OpenMode,
                UseDshMarketHotReload = _settings.UseDshMarketHotReload,
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

    /// <summary>C 组：按规范导出 manifest v4 + .dspack（当前 profile）。</summary>
    private void ExportPackV4_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            ExportStatusText.Text = "请先选择版本。";
            return;
        }

        var profileName = DshProfileService.ResolveActiveName(_instance, _settingsService);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出为整合包（.dspack）",
            Filter = "DSH 整合包 (*.dspack)|*.dspack",
            FileName = $"{PackFormat.ResolveProfileName(profileName)}-{_instance.DetectedVersion ?? "pack"}.dspack",
            AddExtension = true,
            DefaultExt = ".dspack"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var version = string.IsNullOrWhiteSpace(_instance.DetectedVersion) ? "1.0.0" : _instance.DetectedVersion!;
        if (!PackExportService.TryExport(_instance, profileName, dialog.FileName, version, out var summary, out var error))
        {
            ExportStatusText.Text = $"导出失败：{error}";
            return;
        }

        ExportStatusText.Text = "整合包已导出（manifest v4）：" + string.Join("；", summary);
        LauncherLog.Info("已导出 v4 整合包。", "E3002", new { dialog.FileName, summary });
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
