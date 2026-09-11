using System.IO;
using System.Net.Http;
using System.Windows;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher;

/// <summary>
/// 更换实例运行版本（升级/降级）对话框（work-log/52）：
/// 选目标版本 → 检查（解析/下载 + 预检）→ 切换（可选快照/会话备份，失败回滚绑定）。
/// 只改运行时绑定，不动该实例的 DSH_HOME（配置/插件/会话）。
/// </summary>
public partial class VersionSwitchWindow : Window
{
    private readonly ManagerInstance _instance;
    private readonly InstanceVersionSwitchService _switchService;
    private readonly Func<NodeRuntimeInfo?> _nodeRuntimeProvider;
    private readonly VersionSettingsService _settingsService;
    private readonly List<string> _installedVersions = new();
    private readonly CancellationTokenSource _cancellation = new();
    private InstanceVersionTargetResolution? _target;
    private InstanceVersionSwitchPrecheck? _precheck;
    private bool _busy;

    public VersionSwitchWindow(
        Window? owner,
        ManagerInstance instance,
        InstanceVersionSwitchService switchService,
        Func<NodeRuntimeInfo?> nodeRuntimeProvider,
        VersionSettingsService settingsService,
        string? preselectVersion = null)
    {
        InitializeComponent();
        Owner = owner;
        _instance = instance;
        _switchService = switchService;
        _nodeRuntimeProvider = nodeRuntimeProvider;
        _settingsService = settingsService;
        _preselectVersion = preselectVersion;
        InstanceText.Text = $"实例：{instance.Name}（Kind: {instance.KindText}）"
            + $"\n当前 DSh：{instance.DetectedVersion ?? "未知"} · DSH_HOME 保留：{instance.DshHome}";
        Closed += (_, _) => _cancellation.Cancel();
        Loaded += async (_, _) => await RefreshVersionsAsync();
    }

    /// <summary>预选版本（"一键回退"传入上一条历史的起点版本）；为空时选当前版本。</summary>
    private readonly string? _preselectVersion;

    /// <summary>切换成功后的实例（已写入台账）；失败为 null。</summary>
    public ManagerInstance? SwitchedInstance { get; private set; }

    private async Task RefreshVersionsAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在读取版本列表…";
        try
        {
            _installedVersions.Clear();
            try
            {
                var installDirectory = _settingsService.ResolveDshInstallDirectory();
                // 主运行根本身也可能就是一个版本（如 dsh_runtime = 0.1.2-rc.1）。
                var primaryRoot = DshRuntimeDetector.TryResolvePackageRoot(installDirectory);
                var primaryVersion = primaryRoot is null ? null : DshRuntimeDetector.TryReadPackageVersion(primaryRoot);
                if (!string.IsNullOrWhiteSpace(primaryVersion))
                {
                    _installedVersions.Add(primaryVersion!);
                }

                var versionsDirectory = Path.Combine(installDirectory, "versions");
                if (Directory.Exists(versionsDirectory))
                {
                    foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
                    {
                        var packageRoot = DshRuntimeDetector.TryResolvePackageRoot(directory);
                        var version = packageRoot is null ? null : DshRuntimeDetector.TryReadPackageVersion(packageRoot);
                        if (!string.IsNullOrWhiteSpace(version))
                        {
                            _installedVersions.Add(version!);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到本机版本目录时只列官方版本。
            }

            if (!string.IsNullOrWhiteSpace(_instance.DetectedVersion))
            {
                _installedVersions.Add(_instance.DetectedVersion!);
            }

            var candidates = _installedVersions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _officialVersions = Array.Empty<string>();            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var catalog = new DshVersionCatalogService();
                _officialVersions = await catalog.ReadOfficialVersionsAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException
                or TaskCanceledException
                or OperationCanceledException
                or InvalidDataException)
            {
                StatusText.Text = "官方版本列表获取失败（仍可切换本机已安装版本）。";
            }

            foreach (var version in _officialVersions)
            {
                if (!candidates.Contains(version, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(version);
                }
            }

            candidates.Sort(static (left, right) => PluginCompatibility.Compare(right, left));
            var preferred = !string.IsNullOrWhiteSpace(_preselectVersion)
                && candidates.Contains(_preselectVersion!, StringComparer.OrdinalIgnoreCase)
                    ? _preselectVersion
                    : !string.IsNullOrWhiteSpace(_instance.DetectedVersion)
                        && candidates.Contains(_instance.DetectedVersion, StringComparer.OrdinalIgnoreCase)
                            ? _instance.DetectedVersion
                            : candidates.FirstOrDefault();
            TargetVersionBox.ItemsSource = candidates;
            TargetVersionBox.SelectedItem = preferred;
            _target = null;
            _precheck = null;
            SwitchButton.IsEnabled = false;
            if (StatusText.Text?.StartsWith("官方版本列表获取失败", StringComparison.Ordinal) != true)
            {
                StatusText.Text = candidates.Count == 0
                    ? "没有可用版本：本机没有已安装版本，且官方列表不可用。"
                    : $"共 {candidates.Count} 个候选版本（已装 {_installedVersions.Distinct(StringComparer.OrdinalIgnoreCase).Count()} 个）。";
            }
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private IReadOnlyList<string> _officialVersions = Array.Empty<string>();

    private void TargetVersionBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _target = null;
        _precheck = null;
        SwitchButton.IsEnabled = false;
        SessionBackupBox.Visibility = Visibility.Collapsed;
        // 换版本/重新检查后旧确认作废，避免拿着一屏的旧文案确认新目标。
        ConfirmPanel.Visibility = Visibility.Collapsed;
        if (TargetVersionBox.SelectedItem is not string version)
        {
            return;
        }

        var installed = _installedVersions.Contains(version, StringComparer.OrdinalIgnoreCase);
        TargetHintText.Text = installed
            ? "本机已安装：直接切换（不下载）。"
            : "本机未安装：点「检查」会先从官方 npm 包下载到设定的 DSh 安装位置。";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshVersionsAsync();

    private NodeRuntimeInfo CurrentNode() => _nodeRuntimeProvider() ?? NodeRuntimeInfo.Missing();

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || TargetVersionBox.SelectedItem is not string version || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        _busy = true;
        CheckButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        SwitchButton.IsEnabled = false;
        var progress = new Progress<string>(message => StatusText.Text = message);
        try
        {
            var node = CurrentNode();
            _target = await _switchService.ResolveTargetAsync(
                version,
                node,
                allowDownload: true,
                instance: _instance,
                progress: progress,
                cancellationToken: _cancellation.Token);
            _precheck = _switchService.Precheck(_instance, _target, node);
            ReportText.Text = BuildReport(_precheck, _target);
            SessionBackupBox.Visibility = _precheck.RequiresSessionBackup ? Visibility.Visible : Visibility.Collapsed;
            SessionBackupBox.IsChecked = _precheck.RequiresSessionBackup;
            SwitchButton.IsEnabled = _precheck.CanProceed;
            StatusText.Text = _precheck.CanProceed
                ? "检查通过。确认无误后点「切换」。"
                : "检查未通过：请先处理上面的问题。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex) when (ex is InvalidDataException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            _target = null;
            _precheck = null;
            ReportText.Text = $"检查失败：{ex.Message}";
            StatusText.Text = "检查失败。";
        }
        finally
        {
            _busy = false;
            CheckButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
        }
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _target is null || _precheck is null)
        {
            return;
        }

        // 先出确认面板（不再用 MessageBox：无法 UIA 定位、且会遮住勾选项与预检内容）。
        ShowConfirmPanel();
        await Task.CompletedTask;
    }

    /// <summary>把“即将做什么/哪些勾选生效”摆到窗口内，等用户显式确认。</summary>
    private void ShowConfirmPanel()
    {
        if (_target is null || _precheck is null)
        {
            return;
        }

        var lines = new List<string>
        {
            $"把实例「{_instance.Name}」的运行版本从 {_instance.DetectedVersion ?? "未知"} 换到 {_target.Version}？",
            "• 只改运行程序与启动入口，DSH_HOME（配置/插件/会话）保留；",
            "• 实例必须处于停止状态；",
            "• 切换后首次启动可能需要补装插件依赖（联网）。"
        };
        if (_precheck.RequiresSessionBackup)
        {
            lines.Insert(3, SessionBackupBox.IsChecked == true
                ? "• 目标版本读不了现有的新格式会话，已勾选「导出现有会话备份」。"
                : "• 目标版本读不了现有的新格式会话，建议先勾选「导出现有会话备份」。");
        }

        if (SnapshotBox.IsChecked == true)
        {
            lines.Insert(3, "• 切换前会先创建配置快照。");
        }

        ConfirmText.Text = string.Join("\n", lines);
        ConfirmPanel.Visibility = Visibility.Visible;
        SwitchButton.IsEnabled = false;
        StatusText.Text = "请确认后继续。";
        ConfirmButton.Focus();
    }

    private void HideConfirmPanel()
    {
        ConfirmPanel.Visibility = Visibility.Collapsed;
        SwitchButton.IsEnabled = _target is not null && _precheck is not null && !_busy;
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        HideConfirmPanel();
        StatusText.Text = "已返回，可重新选择版本或勾选项。";
    }

    /// <summary>确认面板里的勾选项改了→作废旧确认（勾选项会改变实际动作）。</summary>
    private void Option_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmPanel.Visibility == Visibility.Visible)
        {
            ConfirmPanel.Visibility = Visibility.Collapsed;
            SwitchButton.IsEnabled = _target is not null && _precheck is not null && !_busy;
            StatusText.Text = "勾选项已变化，请重新点「切换」确认。";
        }
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _target is null || _precheck is null)
        {
            return;
        }

        ConfirmPanel.Visibility = Visibility.Collapsed;
        await RunSwitchAsync();
    }

    private async Task RunSwitchAsync()
    {
        if (_target is null)
        {
            return;
        }

        _busy = true;
        SwitchButton.IsEnabled = false;
        CheckButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        var progress = new Progress<string>(message => StatusText.Text = message);
        try
        {
            string? backupDirectory = null;
            if (SessionBackupBox.Visibility == Visibility.Visible && SessionBackupBox.IsChecked == true)
            {
                var instanceRoot = Path.GetDirectoryName(_instance.DshHome) ?? _instance.DshHome;
                backupDirectory = Path.Combine(
                    instanceRoot,
                    $"session-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
            }

            var result = await _switchService.SwitchAsync(
                _instance,
                _target,
                CurrentNode(),
                createSnapshot: SnapshotBox.IsChecked == true,
                sessionBackupDirectory: backupDirectory,
                progress: progress,
                cancellationToken: _cancellation.Token);
            if (!result.Ok || result.Instance is null)
            {
                ReportText.Text = $"切换失败：{result.Error}";
                StatusText.Text = "切换失败。";
                return;
            }

            SwitchedInstance = result.Instance;
            ReportText.Text = result.Summary
                + (backupDirectory is null ? string.Empty : $"\n会话备份目录：{backupDirectory}");
            StatusText.Text = "切换完成。";
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        finally
        {
            _busy = false;
            CheckButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            SwitchButton.IsEnabled = _precheck?.CanProceed == true;
        }
    }

    private static string BuildReport(InstanceVersionSwitchPrecheck precheck, InstanceVersionTargetResolution target)
    {
        var lines = new List<string>
        {
            $"方向：{precheck.DirectionText}",
            $"当前版本：{precheck.CurrentVersion ?? "未知"}  →  目标版本：{precheck.TargetVersion}"
                + (target.AlreadyInstalled ? "（本机已安装）" : "（已下载到本机）"),
            $"运行目录：{target.PackageRoot}",
            $"Node 引擎：{precheck.TargetNodeEngine ?? "未声明"} · 本机兼容性：{DescribeNode(precheck.NodeCompatibility)}"
        };

        if (precheck.IncompatiblePlugins.Count > 0)
        {
            lines.Add($"插件核心依赖不满足：{string.Join("；", precheck.IncompatiblePlugins)}");
        }
        else
        {
            lines.Add("插件核心依赖：未发现冲突");
        }

        lines.Add($"新格式会话（session.vN）：{precheck.VersionedSessionCount} 个"
            + $" · 目标版本可读：{(precheck.TargetReadsVersionedSessions ? "是（读取时自动迁移）" : "否")}");

        if (precheck.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            foreach (var warning in precheck.Warnings)
            {
                lines.Add($"• {warning}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("说明：只改运行程序与启动入口，DSH_HOME（配置/插件/会话）保留；会话格式迁移由 dsh 自己处理，启动器不做格式转换。");
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeNode(NodeRuntimeCompatibility compatibility) => compatibility switch
    {
        NodeRuntimeCompatibility.Compatible => "兼容",
        NodeRuntimeCompatibility.Incompatible => "不兼容（禁止切换）",
        NodeRuntimeCompatibility.Missing => "未检测到 Node",
        _ => "未知"
    };
}
