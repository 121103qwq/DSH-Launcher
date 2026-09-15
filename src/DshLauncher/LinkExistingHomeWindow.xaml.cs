using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using DshLauncher.Models;
using DshLauncher.Services;
using Forms = System.Windows.Forms;

namespace DshLauncher;

public partial class LinkExistingHomeWindow : Window
{
    private readonly HashSet<string> _existingNames;
    private readonly IReadOnlyList<ManagerInstance> _runtimeOptions;
    private readonly IReadOnlyList<ManagerInstance> _existingInstances;
    private readonly Func<IReadOnlyList<DshRuntimeInfo>>? _runtimesProvider;
    private readonly ExistingDshHomeDiscoveryService _discoveryService;
    private readonly ExternalDshHomeGuard _externalHomeGuard = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private int _refreshRevision;
    private int _conflictRevision;
    private bool _isLoaded;
    private bool _isApplyingCandidate;
    private bool _isLinking;
    private string? _homeConflict;

    public LinkExistingHomeWindow(
        Window? owner,
        IReadOnlyList<ManagerInstance> runtimeOptions,
        IEnumerable<string> existingNames,
        IEnumerable<ManagerInstance>? existingInstances = null,
        Func<IReadOnlyList<DshRuntimeInfo>>? runtimesProvider = null,
        ExistingDshHomeDiscoveryService? discoveryService = null)
    {
        InitializeComponent();
        Owner = owner;
        _runtimeOptions = runtimeOptions.ToArray();
        _existingInstances = existingInstances?.ToArray() ?? Array.Empty<ManagerInstance>();
        _runtimesProvider = runtimesProvider;
        _discoveryService = discoveryService ?? new ExistingDshHomeDiscoveryService();
        _existingNames = new HashSet<string>(
            existingNames.Where(static name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        RuntimeBox.ItemsSource = _runtimeOptions;
        RuntimeBox.SelectedItem = _runtimeOptions.FirstOrDefault();
        NameBox.Text = "外部 DSH_HOME";
        NameBox.SelectAll();
        NameBox.Focus();
        ProfileBox.IsEnabled = false;

        DataContext = this;
        if (_runtimeOptions.Count == 0)
        {
            StatusText.Text = "暂时没有已登记的 DSh Runtime；仍可扫描和选择已有 DSH_HOME，关联前请先手动选择可用 Runtime。";
        }
    }

    public ObservableCollection<ExistingDshHomeCandidate> Candidates { get; } = new();

    public string InstanceName => NameBox.Text.Trim();

    public string DshHomePath => HomePathBox.Text.Trim();

    public ManagerInstance? SelectedRuntime => RuntimeBox.SelectedItem as ManagerInstance;

    public string? SelectedProfileName => ProfileBox.SelectedItem as string;

    /// <summary>
    /// A snapshot of the latest local process/lock check. A non-null value is
    /// informational only: the user may still associate the HOME, while the
    /// launcher blocks starting or editing it until the owner exits.
    /// </summary>
    public string? HomeConflict => _homeConflict;

    /// <summary>
    /// Lists only Profile directories that really exist in the selected HOME.
    /// The service method deliberately does not supply its legacy "web"
    /// fallback for a missing or empty profiles root.
    /// </summary>
    internal static IReadOnlyList<string> ListRealProfiles(string dshHome) =>
        DshProfileService.ListExistingProfiles(dshHome);

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        _ = RefreshCandidatesAsync();
    }

    private void Window_OnClosed(object? sender, EventArgs e)
    {
        _isLoaded = false;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private async void RefreshCandidates_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCandidatesAsync();
    }

    private async Task RefreshCandidatesAsync()
    {
        if (!_isLoaded || _lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        var revision = ++_refreshRevision;
        RefreshCandidatesButton.IsEnabled = false;
        CandidateStatusText.Text = "正在查找本机已有 DSH_HOME…";

        try
        {
            // Read providers and WPF-owned collections on the UI thread. Only
            // immutable snapshots are passed into the asynchronous service.
            var existingInstances = _existingInstances.ToArray();
            var runtimeInfo = GetRuntimeInfoSnapshot();
            var candidates = await _discoveryService.DiscoverAsync(
                runtimeInfo,
                existingInstances,
                _lifetimeCancellation.Token);

            if (!IsCurrentRefresh(revision))
            {
                return;
            }

            Candidates.Clear();
            foreach (var candidate in candidates)
            {
                Candidates.Add(candidate);
            }

            CandidateStatusText.Text = Candidates.Count == 0
                ? "没有找到可自动选择的 DSH_HOME；仍可使用下方“选择目录”手动关联。"
                : $"已找到 {Candidates.Count} 个候选。已关联的项目不能再次关联。";
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the dialog is the only expected cancellation path.
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            if (IsCurrentRefresh(revision))
            {
                CandidateStatusText.Text = $"自动查找失败：{ex.Message}。仍可手动选择目录。";
            }
        }
        finally
        {
            if (IsCurrentRefresh(revision))
            {
                RefreshCandidatesButton.IsEnabled = true;
            }
        }
    }

    private IReadOnlyList<DshRuntimeInfo> GetRuntimeInfoSnapshot()
    {
        var supplied = _runtimesProvider?.Invoke();
        if (supplied is { Count: > 0 })
        {
            return supplied.ToArray();
        }

        // Keep the old constructor useful when the host has no detector
        // provider yet. This fallback only describes already registered
        // runtime entries; it never discovers or registers anything.
        return _runtimeOptions
            .Select(static option => new DshRuntimeInfo(
                IsAvailable: DshRuntimeCommandFactory.IsUsable(option.EffectiveDshLaunchSpec),
                ExecutablePath: option.DshExecutablePath,
                Version: option.DetectedVersion,
                PackageRoot: option.RootPath,
                Error: option.LastError,
                LaunchSpec: option.EffectiveDshLaunchSpec,
                ExistingDshHome: option.ImportedFromDshHome))
            .ToArray();
    }

    private bool IsCurrentRefresh(int revision) =>
        _isLoaded
        && !_lifetimeCancellation.IsCancellationRequested
        && revision == _refreshRevision;

    private void CandidateList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isApplyingCandidate || CandidateList.SelectedItem is not ExistingDshHomeCandidate candidate)
        {
            return;
        }

        if (candidate.IsRegistered || IsExternalHomeRegistered(candidate.DshHome))
        {
            _isApplyingCandidate = true;
            CandidateList.SelectedItem = null;
            _isApplyingCandidate = false;
            CandidateStatusText.Text = "这个 DSH_HOME 已经关联到 Launcher，不能再次关联。";
            return;
        }

        _isApplyingCandidate = true;
        try
        {
            NameBox.Text = string.IsNullOrWhiteSpace(candidate.Name)
                ? "外部 DSH_HOME"
                : candidate.Name.Trim();
            HomePathBox.Text = NormalizeHomeForDisplay(candidate.DshHome);
            LoadProfiles(HomePathBox.Text);

            var matchingRuntime = FindMatchingRuntime(candidate.Runtime);
            if (matchingRuntime is not null)
            {
                RuntimeBox.SelectedItem = matchingRuntime;
            }

            CandidateStatusText.Text = matchingRuntime is null
                ? $"已选择“{candidate.Name}”；未匹配到已登记 Runtime，请在下方手动选择。"
                : $"已选择“{candidate.Name}”；已自动匹配可用 Runtime。";
            _ = RefreshConflictAsync(HomePathBox.Text);
        }
        finally
        {
            _isApplyingCandidate = false;
        }
    }

    private ManagerInstance? FindMatchingRuntime(DshRuntimeInfo? runtime)
    {
        if (runtime is null)
        {
            return null;
        }

        return _runtimeOptions.FirstOrDefault(option =>
            PathsEqual(option.RootPath, runtime.PackageRoot)
            || PathsEqual(option.DshExecutablePath, runtime.ExecutablePath)
            || (string.Equals(option.DetectedVersion, runtime.Version, StringComparison.OrdinalIgnoreCase)
                && DshRuntimeCommandFactory.IsUsable(option.EffectiveDshLaunchSpec)));
    }

    private bool IsExternalHomeRegistered(string home)
    {
        return _existingInstances.Any(instance =>
            instance.UsesExternalDshHome
            && PathsEqual(instance.DshHome, home));
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string NormalizeHomeForDisplay(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path.Trim();
        }
    }

    private async Task RefreshConflictAsync(string home)
    {
        if (!_isLoaded)
        {
            return;
        }

        var revision = ++_conflictRevision;
        _homeConflict = null;
        ConflictBorder.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
        {
            return;
        }

        ConflictText.Text = "正在检查其他桌面端是否占用此 DSH_HOME…";
        ConflictBorder.Visibility = Visibility.Visible;
        var probe = CreateConflictProbe(home);
        try
        {
            var conflict = await Task.Run(() => _externalHomeGuard.GetConflict(probe));
            if (!_isLoaded || revision != _conflictRevision)
            {
                return;
            }

            _homeConflict = conflict;
            if (string.IsNullOrWhiteSpace(conflict))
            {
                ConflictBorder.Visibility = Visibility.Collapsed;
                return;
            }

            ConflictText.Text = $"占用检查：{conflict}";
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            if (_isLoaded && revision == _conflictRevision)
            {
                ConflictText.Text = $"占用检查暂未完成：{ex.Message}。仍可关联；启动和编辑前会再次检查。";
            }
        }
    }

    private ManagerInstance CreateConflictProbe(string home)
    {
        if (SelectedRuntime is { } runtime)
        {
            return runtime with
            {
                DshHome = home,
                UsesExternalDshHome = true,
                RuntimeStatus = InstanceRuntimeStatus.Unknown,
                RuntimeOwnership = InstanceRuntimeOwnership.None,
                ProcessId = null,
                Port = null,
                WebUrl = null
            };
        }

        return new ManagerInstance(
            Id: "external-home-probe",
            Name: "外部 DSH_HOME",
            RootPath: home,
            Kind: InstanceKind.Installed,
            DshHome: home,
            DshExecutablePath: null,
            DetectedVersion: null,
            RuntimeStatus: InstanceRuntimeStatus.Unknown,
            PackageManager: null,
            LastError: null,
            RegisteredAt: DateTimeOffset.UtcNow,
            UsesExternalDshHome: true);
    }

    private void BrowseHome_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择原桌面端使用的 DSH_HOME 目录",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(DshHomePath) ? DshHomePath : string.Empty
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        HomePathBox.Text = Path.GetFullPath(dialog.SelectedPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        LoadProfiles(HomePathBox.Text);
        _ = RefreshConflictAsync(HomePathBox.Text);
    }

    private void LoadProfiles(string home)
    {
        ProfileBox.ItemsSource = null;
        ProfileBox.SelectedItem = null;
        try
        {
            var profiles = ListRealProfiles(home);
            ProfileBox.ItemsSource = profiles;
            ProfileBox.IsEnabled = profiles.Count > 0;
            ProfileBox.SelectedItem = profiles.FirstOrDefault(profile =>
                string.Equals(profile, DshProfileService.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
                ?? profiles.FirstOrDefault();
            ProfileHintText.Text = profiles.Count == 0
                ? "没有检测到真实 Profile；仍可关联并管理这个旧 HOME，但启动前需要先在原桌面端创建可用 Profile。"
                : $"已检测到 {profiles.Count} 个 Profile，默认选择 {ProfileBox.SelectedItem}。";
            StatusText.Text = profiles.Count == 0
                ? "此目录没有可供选择的 Profile，关联后不会创建或初始化任何 Profile。"
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ProfileBox.IsEnabled = false;
            ProfileHintText.Text = "无法读取该目录中的 Profile。可以重新选择目录。";
            StatusText.Text = $"读取 Profile 失败：{ex.Message}";
        }
    }

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        if (_isLinking)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(InstanceName))
        {
            ShowValidation("请输入版本名称。", "关联已有 DSH_HOME");
            return;
        }

        if (_existingNames.Contains(InstanceName))
        {
            ShowValidation($"版本名称“{InstanceName}”已经存在，请换一个名称。", "名称重复");
            return;
        }

        if (SelectedRuntime is null)
        {
            ShowValidation("请选择一个已经登记的 DSh Runtime。", "缺少 Runtime");
            return;
        }

        if (!Directory.Exists(DshHomePath))
        {
            ShowValidation("请选择一个仍然存在的 DSH_HOME 目录。", "目录无效");
            return;
        }

        if (ProfileBox.IsEnabled && ProfileBox.SelectedItem is null)
        {
            ShowValidation("请选择要管理的 Profile。", "缺少 Profile");
            return;
        }

        var expectedName = InstanceName;
        // Recheck the selected HOME immediately before closing. The check is
        // advisory here: registration remains allowed, while launch/edit
        // operations enforce the guard again at their actual entry point.
        var home = DshHomePath;
        var expectedRuntime = SelectedRuntime;
        var expectedProfile = SelectedProfileName;
        _isLinking = true;
        LinkButton.IsEnabled = false;
        FormScrollViewer.IsEnabled = false;
        try
        {
            await RefreshConflictAsync(home);
            if (!_isLoaded || _lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }

            // The form is disabled while the asynchronous check runs, but
            // retain a final snapshot check so programmatic changes or a
            // re-entrant event cannot close the dialog for stale values.
            if (!string.Equals(InstanceName, expectedName, StringComparison.Ordinal)
                || !PathsEqual(DshHomePath, home)
                || !ReferenceEquals(SelectedRuntime, expectedRuntime)
                || !string.Equals(SelectedProfileName, expectedProfile, StringComparison.Ordinal))
            {
                StatusText.Text = "关联信息已变化，请重新检查后再点击关联。";
                return;
            }

            DialogResult = true;
        }
        finally
        {
            _isLinking = false;
            if (_isLoaded && !_lifetimeCancellation.IsCancellationRequested)
            {
                FormScrollViewer.IsEnabled = true;
                LinkButton.IsEnabled = true;
            }
        }
    }

    private void ShowValidation(string message, string title) =>
        System.Windows.MessageBox.Show(
            this,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);

}
