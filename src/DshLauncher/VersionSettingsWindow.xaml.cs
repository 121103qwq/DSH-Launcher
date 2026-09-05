using System.IO;
using System.Windows;
using System.Windows.Controls;
using DshLauncher.Models;
using DshLauncher.Services;
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
    private readonly LauncherIntegrationService _integrationService = new();
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
        bool openPluginPage = false)
    {
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
        IdleStopMinutesBox.Text = Math.Max(0, _settings.IdleStopMinutes).ToString();
        RestartOnCrashCheckBox.IsChecked = _settings.RestartOnCrash;
        ConfigurationOptionsPanel.IsEnabled = !_settings.SyncAllConfiguration;
        UpdateWorkspaceEnabled();
    }

    private void LoadPluginSettingsControls()
    {
        WindowTitleBox.Text = _settings.WindowTitle ?? string.Empty;
        NodePathBox.Text = _settings.NodeExecutablePath ?? string.Empty;
        var openMode = _settings.OpenMode
            ?? (_instance?.CanOpenDesktopShell == true ? VersionOpenMode.Desktop : VersionOpenMode.Launcher);
        OpenModeBox.SelectedValue = openMode.ToString();
        CustomOpenTargetBox.Text = _settings.CustomOpenTargetPath ?? string.Empty;
        UpdateCustomOpenTargetEnabled();
        OpenModeStatusText.Text = openMode == VersionOpenMode.Custom
            ? "当前版本将使用手动绑定的本地入口，并继承此版本的 DSH_HOME。"
            : _instance?.CanOpenDesktopShell == true
                ? "当前版本已检测到 DSH Desktop 打开入口。"
                : "当前版本没有检测到 DSH Desktop；仍可使用 Launcher 或手动绑定打开方式。";
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

        PageHeaderText.Text = activeButton == ConfigurationButton
            ? "配置"
            : activeButton == PluginsButton
                ? "插件管理"
                : activeButton == SnapshotsButton
                    ? "快照回滚"
                : activeButton == ExportButton
                    ? "导出"
                    : "个性化";
        PageDescriptionText.Text = activeButton == ConfigurationButton
            ? "决定对话文件同步范围，以及是否让所有版本自动同步模型。"
            : activeButton == PluginsButton
                ? "像 PCL2 的 Mod 管理一样，在当前版本快速启用、禁用或删除 Plugin。"
                : activeButton == SnapshotsButton
                    ? "创建本机 DPAPI 快照，或使用密码导入跨电脑快照并回滚当前版本配置。"
                : activeButton == ExportButton
                    ? "导出可以分享的版本设计，不带隐私内容和会话。"
                    : "查看当前版本和它自己的 DSH_HOME。";

        foreach (var button in new[] { PersonalizationButton, ConfigurationButton, PluginsButton, SnapshotsButton, ExportButton })
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
        ExportPasswordSnapshotButton.IsEnabled = !busy && CanMutateSnapshot();
        ImportPasswordSnapshotButton.IsEnabled = !busy && CanMutateSnapshot();
    }

    private void UpdateSnapshotButtons() => SetSnapshotBusy(false);

    private async void ExportPasswordSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is not { } instance || !CanMutateSnapshot())
        {
            PasswordSnapshotStatusText.Text = "请先停止当前版本，再导出跨电脑密码快照。";
            return;
        }

        using var dialog = new Forms.SaveFileDialog
        {
            Title = "导出跨电脑密码快照",
            Filter = $"DSH 跨电脑密码快照 (*{PasswordSnapshotEncryptionService.FileExtension})|*{PasswordSnapshotEncryptionService.FileExtension}|所有文件|*.*",
            AddExtension = true,
            DefaultExt = PasswordSnapshotEncryptionService.FileExtension.TrimStart('.'),
            FileName = $"{SafeFileName(instance.Name)}{PasswordSnapshotEncryptionService.FileExtension}",
            OverwritePrompt = true,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        var password = PasswordPromptWindow.Show(
            Window.GetWindow(this),
            "设置跨电脑快照密码",
            "请输入用于另一台电脑导入的密码。密码不会保存到设置或日志。",
            confirmPassword: true);
        if (password is null)
        {
            return;
        }

        SetSnapshotBusy(true);
        try
        {
            PasswordSnapshotStatusText.Text = "正在生成跨电脑密码快照…";
            var info = await Task.Run(() => _snapshotService.ExportPasswordSnapshot(instance, dialog.FileName, password));
            PasswordSnapshotStatusText.Text = $"跨电脑密码快照已导出：{info.FilePath}。请使用相同密码导入；密码未保存。";
        }
        catch (Exception ex)
        {
            PasswordSnapshotStatusText.Text = $"导出跨电脑密码快照失败：{ex.Message}";
        }
        finally
        {
            password = string.Empty;
            SetSnapshotBusy(false);
        }
    }

    private async void ImportPasswordSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is not { } instance || !CanMutateSnapshot())
        {
            PasswordSnapshotStatusText.Text = "请先停止当前版本，再导入跨电脑密码快照。";
            return;
        }

        using var dialog = new Forms.OpenFileDialog
        {
            Title = "选择跨电脑密码快照",
            Filter = $"DSH 跨电脑密码快照 (*{PasswordSnapshotEncryptionService.FileExtension})|*{PasswordSnapshotEncryptionService.FileExtension}|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        if (System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                $"确定用“{Path.GetFileName(dialog.FileName)}”替换当前版本的受管配置并回滚？\n\n只会处理版本设置、Provider、MCP 和 Plugin 配置，不会改变会话文件。恢复前会自动创建本机 DPAPI 回滚点。",
                "确认导入并回滚",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var password = PasswordPromptWindow.Show(
            Window.GetWindow(this),
            "输入跨电脑快照密码",
            "请输入导出时设置的密码。密码错误或文件被篡改时不会修改当前版本。",
            confirmPassword: false);
        if (password is null)
        {
            return;
        }

        SetSnapshotBusy(true);
        try
        {
            PasswordSnapshotStatusText.Text = "正在验证密码并导入跨电脑快照…";
            var rollbackPoint = await Task.Run(() => _snapshotService.RestorePasswordSnapshot(instance, dialog.FileName, password));
            _settings = _settingsService.Read(instance);
            LoadWorkspaceNames();
            LoadConfigurationControls();
            LoadPluginSettingsControls();
            await LoadPluginsAsync();
            _settingsSaved();
            RefreshSnapshots();
            PasswordSnapshotStatusText.Text = $"跨电脑快照已导入并回滚；恢复前状态保存在：{rollbackPoint.DisplayName}。密码未保存。";
        }
        catch (Exception ex)
        {
            PasswordSnapshotStatusText.Text = $"导入跨电脑密码快照失败：{ex.Message}";
        }
        finally
        {
            password = string.Empty;
            SetSnapshotBusy(false);
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
        ActiveProfileName = _settings.ActiveProfileName,
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
        IdleStopMinutes = ReadIdleStopMinutes(),
        RestartOnCrash = RestartOnCrashCheckBox.IsChecked == true,
        UseDshMarketHotReload = _settings.UseDshMarketHotReload,
        WindowTitle = _settings.WindowTitle,
        NodeExecutablePath = _settings.NodeExecutablePath,
        OpenMode = _settings.OpenMode,
        CustomOpenTargetPath = _settings.CustomOpenTargetPath
    };

    private int ReadIdleStopMinutes()
    {
        if (!int.TryParse(IdleStopMinutesBox.Text.Trim(), out var minutes)
            || minutes is < 0 or > 10080)
        {
            throw new InvalidDataException("空闲停止时间必须是 0 到 10080 分钟之间的整数。");
        }

        return minutes;
    }

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

        if (openMode == VersionOpenMode.Desktop && !_instance.CanOpenDesktopShell)
        {
            OpenModeStatusText.Text = "当前版本没有可用的 DSH Desktop 打开入口，请先导入或检测 DSH Desktop。";
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
                VersionOpenMode.Desktop => "DSH Desktop 打开窗口",
                VersionOpenMode.Custom => $"手动打开 {Path.GetFileName(customOpenTargetPath)}",
                _ => "Launcher 启动"
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

    private void CreateDesktopShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_instance is null)
        {
            OpenModeStatusText.Text = "请先选择版本。";
            return;
        }

        try
        {
            _integrationService.RegisterProtocol();
            var path = _integrationService.CreateDesktopShortcut(_instance);
            OpenModeStatusText.Text = $"桌面快捷方式已创建：{path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            OpenModeStatusText.Text = $"创建桌面快捷方式失败：{ex.Message}";
        }
    }

    private VersionSettingsData CopySettings() => new()
    {
        ActiveProfileName = _settings.ActiveProfileName,
        SyncAllConfiguration = _settings.SyncAllConfiguration,
        ConversationSyncMode = _settings.ConversationSyncMode,
        ConversationWorkspace = _settings.ConversationWorkspace,
        SyncModelProviders = _settings.SyncModelProviders,
        IdleStopMinutes = _settings.IdleStopMinutes,
        RestartOnCrash = _settings.RestartOnCrash,
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
                ActiveProfileName = _settings.ActiveProfileName,
                SyncAllConfiguration = _settings.SyncAllConfiguration,
                ConversationSyncMode = _settings.ConversationSyncMode,
                ConversationWorkspace = _settings.ConversationWorkspace,
                SyncModelProviders = _settings.SyncModelProviders,
                IdleStopMinutes = _settings.IdleStopMinutes,
                RestartOnCrash = _settings.RestartOnCrash,
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

        var exportModPack = PackageFormatBox.SelectedIndex == 1;
        var extension = exportModPack
            ? VersionPackageService.ModPackPackageExtension
            : _packageService.PackageExtension;
        using var dialog = new Forms.SaveFileDialog
        {
            Title = "导出 DSh 版本整合包",
            Filter = exportModPack
                ? "DSH ModPack (*.tgz)|*.tgz"
                : $"DSH Launcher 整合包 (*{extension})|*{extension}",
            AddExtension = true,
            DefaultExt = extension.TrimStart('.'),
            FileName = $"{SafeFileName(_instance.Name)}{extension}",
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
            var format = VersionPackageService.DetectPackageKind(dialog.FileName) == VersionPackageKind.ModPack
                ? "DSH ModPack"
                : "DSH Launcher 整合包";
            ExportStatusText.Text = $"已导出 {format}：{dialog.FileName}。未包含 API Key、隐私值和会话。";
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
