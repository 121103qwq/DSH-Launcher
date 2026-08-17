using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using DshLauncher.Models;
using DshLauncher.Services;
using Forms = System.Windows.Forms;

namespace DshLauncher;

public partial class VersionControlWindow : UserControl, INotifyPropertyChanged
{
    private readonly VersionPackageService _packageService;
    private readonly Func<ManagerInstance?> _templateProvider;
    private readonly Action<ManagerInstance> _versionCreated;
    private readonly Action<ManagerInstance> _versionDeleted;
    private readonly Action<ManagerInstance> _versionSelected;
    private readonly VersionHealthService _healthService;
    private readonly VersionSnapshotService _snapshotService;
    private readonly Func<NodeRuntimeInfo> _nodeRuntimeProvider;
    private readonly Func<DshRuntimeInfo> _dshRuntimeProvider;
    private readonly Func<string, bool> _isRunning;
    private readonly Func<ManagerInstance, ManagerInstance> _versionUpdated;
    private readonly Action _versionContentChanged;
    private VersionHealthReport? _healthReport;
    private bool _isBusy;

    public VersionControlWindow(
        IEnumerable<ManagerInstance> versions,
        ManagerInstance? selectedVersion,
        VersionPackageService packageService,
        Func<ManagerInstance?> templateProvider,
        Action<ManagerInstance> versionCreated,
        Action<ManagerInstance> versionDeleted,
        Action<ManagerInstance> versionSelected,
        VersionHealthService healthService,
        VersionSnapshotService snapshotService,
        Func<NodeRuntimeInfo> nodeRuntimeProvider,
        Func<DshRuntimeInfo> dshRuntimeProvider,
        Func<string, bool> isRunning,
        Func<ManagerInstance, ManagerInstance> versionUpdated,
        Action versionContentChanged)
    {
        _packageService = packageService;
        _templateProvider = templateProvider;
        _versionCreated = versionCreated;
        _versionDeleted = versionDeleted;
        _versionSelected = versionSelected;
        _healthService = healthService;
        _snapshotService = snapshotService;
        _nodeRuntimeProvider = nodeRuntimeProvider;
        _dshRuntimeProvider = dshRuntimeProvider;
        _isRunning = isRunning;
        _versionUpdated = versionUpdated;
        _versionContentChanged = versionContentChanged;
        foreach (var version in versions)
        {
            Versions.Add(version);
        }

        _selectedVersion = selectedVersion;
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<ManagerInstance> Versions { get; } = new();

    public ObservableCollection<VersionHealthItem> HealthItems { get; } = new();

    public ObservableCollection<VersionSnapshotInfo> Snapshots { get; } = new();

    private VersionSnapshotInfo? _selectedSnapshot;

    public VersionSnapshotInfo? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (ReferenceEquals(_selectedSnapshot, value))
            {
                return;
            }

            _selectedSnapshot = value;
            OnPropertyChanged(nameof(SelectedSnapshot));
            OnPropertyChanged(nameof(CanRollback));
        }
    }

    private ManagerInstance? _selectedVersion;
    private int _selectionRevision;

    public ManagerInstance? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (ReferenceEquals(_selectedVersion, value))
            {
                return;
            }

            _selectedVersion = value;
            _selectionRevision++;
            OnPropertyChanged(nameof(SelectedVersion));
            OnPropertyChanged(nameof(SelectedVersionName));
            OnPropertyChanged(nameof(SelectedVersionDetails));
            OnPropertyChanged(nameof(CanClone));
            OnPropertyChanged(nameof(CloneButtonToolTip));
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(DeleteButtonToolTip));
            _healthReport = null;
            HealthItems.Clear();
            OnPropertyChanged(nameof(HealthSummary));
            OnPropertyChanged(nameof(CanCheck));
            OnPropertyChanged(nameof(CanRepair));
            OnPropertyChanged(nameof(CanSnapshot));
            RefreshSnapshots();
            if (value is not null)
            {
                _versionSelected(value);
            }
        }
    }

    public string VersionCountText => $"{Versions.Count} 个";

    public string SelectedVersionName => SelectedVersion?.Name ?? "尚未创建版本";

    public string SelectedVersionDetails => SelectedVersion is null
        ? _templateProvider() is null
            ? "没有检测到可用的 DSh 运行目录，请先在设置中完成运行环境检测。"
            : "可以直接新建干净版本；首次创建会使用当前检测到的 DSh 运行目录。"
        : $"{SelectedVersion.KindText} · {SelectedVersion.RootPath}\nDSH_HOME：{SelectedVersion.DshHome}\n状态：{SelectedVersion.StatusText}";

    public bool CanClone => !_isBusy
        && SelectedVersion is not null
        && SelectedVersion.RuntimeStatus != InstanceRuntimeStatus.Running;

    public string CloneButtonToolTip => SelectedVersion is null
        ? "请先在左侧选择一个版本。"
        : SelectedVersion.RuntimeStatus == InstanceRuntimeStatus.Running
            ? "请先停止这个版本，再复制完整 DSH_HOME。"
            : "复制当前版本的完整 DSH_HOME、Provider、Plugin、Skill 和对话设置。";

    public bool CanDelete => !_isBusy
        && SelectedVersion is not null
        && SelectedVersion.RuntimeStatus != InstanceRuntimeStatus.Running
        && SelectedVersion.RuntimeOwnership != InstanceRuntimeOwnership.Attached;

    public string DeleteButtonToolTip => SelectedVersion is null
        ? "请先在左侧选择一个版本。"
        : SelectedVersion.RuntimeStatus == InstanceRuntimeStatus.Running
            ? "运行中的版本不能删除，请先停止。"
            : SelectedVersion.RuntimeOwnership == InstanceRuntimeOwnership.Attached
                ? "Attached 版本不能删除，请先解除外部连接。"
                : "删除注册记录、该版本的 DSH_HOME 和 Launcher 备份，且无法恢复。";

    public string PackageFormatText => $"当前格式：{_packageService.PackageExtension}";

    public string HealthSummary => _healthReport?.Summary ?? "尚未检查当前版本。";

    public bool CanCheck => !_isBusy && SelectedVersion is not null;

    public bool CanRepair => !_isBusy
        && SelectedVersion is { } version
        && _healthReport?.RepairableCount > 0
        && !_isRunning(version.Id)
        && version.RuntimeOwnership != InstanceRuntimeOwnership.Attached;

    public bool CanSnapshot => !_isBusy
        && SelectedVersion is { } version
        && !_isRunning(version.Id)
        && version.RuntimeStatus != InstanceRuntimeStatus.Running
        && version.RuntimeOwnership != InstanceRuntimeOwnership.Attached;

    public bool CanRollback => CanSnapshot && SelectedSnapshot is not null;

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is null && Versions.Count > 0)
        {
            SelectedVersion = Versions[0];
        }

        OnPropertyChanged(nameof(VersionCountText));
        OnPropertyChanged(nameof(SelectedVersionName));
        OnPropertyChanged(nameof(SelectedVersionDetails));
        OnPropertyChanged(nameof(CanClone));
        OnPropertyChanged(nameof(CloneButtonToolTip));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(DeleteButtonToolTip));
        OnPropertyChanged(nameof(PackageFormatText));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanRepair));
        OnPropertyChanged(nameof(CanSnapshot));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(HealthSummary));
        RefreshSnapshots();
    }

    private async void CreateCleanVersion_Click(object sender, RoutedEventArgs e)
    {
        await CreateVersionAsync(clone: false);
    }

    private async void CloneVersion_Click(object sender, RoutedEventArgs e)
    {
        await CreateVersionAsync(clone: true);
    }

    private async void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion;
        if (version is null)
        {
            SetStatus("删除版本前请先在左侧选择一个版本。 ");
            return;
        }

        if (!CanDelete)
        {
            SetStatus(DeleteButtonToolTip);
            return;
        }

        var result = System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            $"确定删除版本“{version.Name}”？\n\n这会删除该版本的 DSH_HOME、Launcher 备份和注册记录，操作无法恢复。不会删除共享的 DSh 运行目录。\n\n如果要保留配置，请先导出整合包。",
            "确认删除版本",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await Task.Run(() => _packageService.DeleteVersion(version));
            Versions.Remove(version);
            _versionDeleted(version);
            SelectedVersion = Versions.FirstOrDefault();
            OnPropertyChanged(nameof(VersionCountText));
            SetStatus($"版本已删除：{version.Name}。共享的 DSh 运行目录没有受到影响。 ");
        }
        catch (Exception ex)
        {
            SetStatus($"删除版本失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CreateVersionAsync(bool clone)
    {
        var template = SelectedVersion ?? _templateProvider();
        if (template is null)
        {
            SetStatus("没有可用的 DSh 运行目录，暂时不能创建版本。请先在设置中完成运行环境检测。 ");
            return;
        }

        if (clone && SelectedVersion is null)
        {
            SetStatus("复制版本前请先在左侧选择一个版本。 ");
            return;
        }

        if (clone && SelectedVersion?.RuntimeStatus == InstanceRuntimeStatus.Running)
        {
            SetStatus("请先停止当前版本，再复制它的 DSH_HOME。 ");
            return;
        }

        var name = TextPromptWindow.Show(
            Window.GetWindow(this),
            clone ? "复制版本" : "新建干净版本",
            clone ? "输入复制后的版本名称：" : "输入新版本名称：",
            clone ? $"{template.Name}（副本）" : $"DSh {template.DetectedVersion ?? "新版本"}");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        SetBusy(true);
        try
        {
            var created = await Task.Run(() => clone
                ? _packageService.CloneVersion(template, name)
                : _packageService.CreateCleanVersion(template, name));
            Versions.Add(created);
            SelectedVersion = created;
            _versionCreated(created);
            SetStatus(clone
                ? $"版本已复制：{created.Name}。新的 DSH_HOME：{created.DshHome}"
                : $"干净版本已创建：{created.Name}。新的 DSH_HOME：{created.DshHome}");
        }
        catch (Exception ex)
        {
            SetStatus($"创建版本失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ImportPackage_Click(object sender, RoutedEventArgs e)
    {
        var template = SelectedVersion ?? _templateProvider();
        if (template is null)
        {
            SetStatus("没有可用的 DSh 运行目录，暂时不能导入整合包。 ");
            return;
        }

        using var dialog = new Forms.OpenFileDialog
        {
            Title = "导入 DSH Launcher 整合包",
            Filter = $"DSH 整合包 (*{_packageService.PackageExtension})|*{_packageService.PackageExtension}|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var preview = await Task.Run(() => _packageService.PreviewPackage(dialog.FileName));
            var previewText = $"{preview.Name}\n\n"
                + $"{preview.Description}\n\n"
                + $"DSh：{preview.DshVersion ?? "未标记"}\n"
                + $"Plugins：{preview.PluginCount}\n"
                + $"Skills：{preview.SkillCount}\n"
                + $"Agent Presets：{preview.AgentPresetCount}\n"
                + $"Providers：{preview.ProviderCount}\n"
                + $"Workflow：{preview.Workflow ?? "无"}\n\n"
                + $"将创建新的独立版本“{preview.Name}”，不会覆盖已有版本。\n\n确认导入吗？";
            if (System.Windows.MessageBox.Show(
                    Window.GetWindow(this),
                    previewText,
                    "导入整合包预览",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Information) != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            var created = await Task.Run(() => _packageService.ImportPackage(dialog.FileName, template));
            Versions.Add(created);
            SelectedVersion = created;
            _versionCreated(created);
            SetStatus($"整合包已导入为新版本：{created.Name}。原版本没有被覆盖。 ");
        }
        catch (Exception ex)
        {
            SetStatus($"导入整合包失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ShowVersionSettings();
        }
    }

    private async void CheckVersion_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion;
        if (version is null || !CanCheck)
        {
            return;
        }

        var selectionRevision = _selectionRevision;
        SetBusy(true);
        SetStatus($"正在检查“{version.Name}”…");
        try
        {
            var nodeRuntime = _nodeRuntimeProvider();
            var dshRuntime = _dshRuntimeProvider();
            var actuallyRunning = _isRunning(version.Id);
            var report = await Task.Run(() =>
                _healthService.Inspect(version, nodeRuntime, dshRuntime, actuallyRunning));
            if (_selectionRevision != selectionRevision
                || !string.Equals(SelectedVersion?.Id, version.Id, StringComparison.Ordinal))
            {
                SetStatus("已切换版本，本次检查结果未应用。 ");
                return;
            }

            _healthReport = report;
            HealthItems.Clear();
            foreach (var item in report.Items)
            {
                HealthItems.Add(item);
            }

            OnPropertyChanged(nameof(HealthSummary));
            OnPropertyChanged(nameof(CanRepair));
            SetStatus($"检查完成：{report.Summary}。 ");
        }
        catch (Exception ex)
        {
            if (_selectionRevision == selectionRevision
                && string.Equals(SelectedVersion?.Id, version.Id, StringComparison.Ordinal))
            {
                SetStatus($"检查版本失败：{ex.Message}");
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RepairVersion_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion;
        if (version is null || !CanRepair)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await Task.Run(() => _healthService.Repair(
                version,
                _dshRuntimeProvider(),
                _isRunning(version.Id)));
            var updated = _versionUpdated(result.Instance);
            var index = Versions.ToList().FindIndex(item =>
                string.Equals(item.Id, updated.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                Versions[index] = updated;
            }

            SelectedVersion = updated;
            SetStatus(result.Actions.Count == 0
                ? "没有可自动修复的项目；其余问题需要按检查说明手动处理。"
                : string.Join(string.Empty, result.Actions));
        }
        catch (Exception ex)
        {
            SetStatus($"自动修复失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }

        CheckVersion_Click(sender, e);
    }

    private async void CreateSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion;
        if (version is null || !CanSnapshot)
        {
            SetStatus("请先停止当前版本，再创建配置快照。 ");
            return;
        }

        SetBusy(true);
        try
        {
            var snapshot = await Task.Run(() => _snapshotService.CreateSnapshot(version, "手动快照"));
            RefreshSnapshots();
            SelectedSnapshot = Snapshots.FirstOrDefault(item =>
                string.Equals(item.FilePath, snapshot.FilePath, StringComparison.OrdinalIgnoreCase));
            SetStatus("配置快照已创建。快照由当前 Windows 用户加密，不包含会话文件。 ");
        }
        catch (Exception ex)
        {
            SetStatus($"创建快照失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RollbackSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion;
        var snapshot = SelectedSnapshot;
        if (version is null || snapshot is null || !CanRollback)
        {
            SetStatus("请先停止版本并选择一个可用快照。 ");
            return;
        }

        if (System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                $"确定把“{version.Name}”的配置恢复到 {snapshot.DisplayName}？\n\n恢复前会再自动创建一个回滚点；会话文件不会改变。",
                "确认回滚版本配置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var rollbackPoint = await Task.Run(() =>
                _snapshotService.RestoreSnapshot(version, snapshot.FilePath));
            _versionContentChanged();
            RefreshSnapshots();
            SetStatus($"配置已回滚；恢复前状态保存在：{rollbackPoint.DisplayName}。 ");
        }
        catch (Exception ex)
        {
            SetStatus($"回滚失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshSnapshots()
    {
        Snapshots.Clear();
        SelectedSnapshot = null;
        if (SelectedVersion is not { } version)
        {
            return;
        }

        try
        {
            foreach (var snapshot in _snapshotService.ListSnapshots(version))
            {
                Snapshots.Add(snapshot);
            }

            SelectedSnapshot = Snapshots.FirstOrDefault();
        }
        catch (Exception ex)
        {
            SetStatus($"读取版本快照失败：{ex.Message}");
        }
    }

    private void VersionList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(VersionList, source) is not ListBoxItem item
            || item.DataContext is not ManagerInstance version)
        {
            return;
        }

        SelectedVersion = version;
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ShowVersionSettings();
        }

        e.Handled = true;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        OnPropertyChanged(nameof(CanClone));
        OnPropertyChanged(nameof(CloneButtonToolTip));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanRepair));
        OnPropertyChanged(nameof(CanSnapshot));
        OnPropertyChanged(nameof(CanRollback));
    }

    private void SetStatus(string message) => StatusText.Text = message;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
