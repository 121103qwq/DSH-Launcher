using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DshLauncher.Models;
using DshLauncher.Services;
using UserControl = System.Windows.Controls.UserControl;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfPanel = System.Windows.Controls.Panel;

namespace DshLauncher;

public partial class ProviderManagementWindow : UserControl
{
    private readonly Func<IReadOnlyList<ManagerInstance>> _instancesProvider;
    private readonly ModelService _modelService;
    private readonly CodingModelPolicyService _policyService;
    private readonly DshApiClient _apiClient;
    private readonly VersionSnapshotService _snapshotService;
    private readonly CancellationTokenSource _cancellation;
    private readonly DispatcherTimer _monitorTimer;
    private readonly List<ProviderCardItem> _providers = new();
    private IReadOnlyList<CodingModelOption> _modelOptions = Array.Empty<CodingModelOption>();
    private string _filter = "All";
    private bool _monitorBusy;

    public ProviderManagementWindow(
        Func<IReadOnlyList<ManagerInstance>> instancesProvider,
        ModelService modelService,
        CodingModelPolicyService policyService,
        DshApiClient apiClient,
        VersionSnapshotService snapshotService,
        CancellationToken cancellationToken = default)
    {
        _instancesProvider = instancesProvider;
        _modelService = modelService;
        _policyService = policyService;
        _apiClient = apiClient;
        _snapshotService = snapshotService;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _monitorTimer.Tick += MonitorTimer_Tick;
        InitializeComponent();
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        AllProvidersFilter.Background = SelectedFilterBrush();
        await RefreshAsync();
        if (_cancellation.IsCancellationRequested || !IsLoaded)
        {
            return;
        }

        _monitorTimer.Start();
        await RefreshRuntimeStatusAsync();
    }

    private void Window_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _monitorTimer.Stop();
        _cancellation.Cancel();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            StatusText.Text = "正在读取全局 Provider 与模型目录…";
            var instances = _instancesProvider();
            var providerSnapshots = await Task.Run(() => instances
                .SelectMany(instance =>
                {
                    try
                    {
                        return _modelService.Read(instance)
                            .Where(provider => provider.Configured)
                            .Select(provider => (Instance: instance, Provider: provider))
                            .ToArray();
                    }
                    catch
                    {
                        return Array.Empty<(ManagerInstance, ModelProviderInfo)>();
                    }
                })
                .ToArray(), _cancellation.Token);

            var apiOptions = new List<CodingModelOption>();
            var directoryStates = new List<DshProviderRuntimeState>();
            foreach (var running in instances.Where(IsRunningWithEndpoint))
            {
                try
                {
                    apiOptions.AddRange(await _apiClient.ReadModelsAsync(
                        running.WebUrl!,
                        _cancellation.Token));
                    directoryStates.AddRange(await _apiClient.ReadProviderStatesAsync(
                        running.WebUrl!,
                        _cancellation.Token));
                }
                catch (Exception ex) when (ex is HttpRequestException
                    or InvalidDataException
                    or InvalidOperationException
                    or TaskCanceledException)
                {
                    // One unavailable runtime does not hide models from other runtimes.
                }
            }

            var offlineOptions = providerSnapshots
                .SelectMany(item => item.Item2.Models.Select(model => new CodingModelOption(
                    item.Item2.Provider,
                    item.Item2.DisplayName,
                    model,
                    model)))
                .ToArray();
            var instanceDefaultOptions = instances.Select(instance =>
                {
                    try { return _modelService.ReadDefaultModel(instance); }
                    catch { return null; }
                })
                .Where(selection => selection is not null)
                .Select(selection => selection!)
                .Select(selection => new CodingModelOption(
                    selection.Provider,
                    selection.Provider,
                    selection.Model,
                    selection.Model,
                    selection.ReasoningEffort,
                    selection.ReasoningEffort))
                .ToArray();
            var policy = _policyService.Read();
            var storedSelections = new[] { policy.GlobalDefault }
                .Concat(policy.DshWorkspaces.Select(item => item.Selection))
                .Concat(policy.Sessions.Select(item => item.Selection))
                .Where(selection => selection is not null)
                .Select(selection => selection!)
                .Select(selection => new CodingModelOption(
                    selection.Provider,
                    selection.Provider,
                    selection.Model,
                    selection.Model,
                    selection.ReasoningEffort,
                    selection.ReasoningEffort))
                .ToArray();
            _modelOptions = apiOptions
                .Concat(offlineOptions)
                .Concat(instanceDefaultOptions)
                .Concat(storedSelections)
                .GroupBy(option => option.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(option => option.ProviderName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(option => option.ModelName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            DefaultModelBox.ItemsSource = _modelOptions;

            var currentDefault = policy.GlobalDefault
                ?? instances.Select(instance =>
                {
                    try { return _modelService.ReadDefaultModel(instance); }
                    catch { return null; }
                }).FirstOrDefault(selection => selection is not null);
            DefaultModelBox.SelectedItem = currentDefault is null
                ? _modelOptions.FirstOrDefault()
                : _modelOptions.FirstOrDefault(option => option.Key == currentDefault.Key)
                    ?? new CodingModelOption(
                        currentDefault.Provider,
                        currentDefault.Provider,
                        currentDefault.Model,
                        currentDefault.Model,
                        currentDefault.ReasoningEffort,
                        currentDefault.ReasoningEffort);

            var providerKeys = providerSnapshots.Select(item => item.Item2.Provider)
                .Concat(_modelOptions.Select(option => option.Provider))
                .Concat(directoryStates.Select(state => state.Provider))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _providers.Clear();
            foreach (var providerKey in providerKeys)
            {
                var configured = providerSnapshots
                    .Where(item => string.Equals(item.Item2.Provider, providerKey, StringComparison.Ordinal))
                    .Select(item => item.Item2)
                    .ToArray();
                var providerModels = configured.SelectMany(item => item.Models)
                    .Concat(_modelOptions
                        .Where(option => string.Equals(option.Provider, providerKey, StringComparison.Ordinal))
                        .Select(option => option.Model))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var baseUrls = configured.Select(item => item.BaseUrl)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var configurationSignatures = configured
                    .Select(item => $"{item.BaseUrl}\n{string.Join('\n', item.Models)}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var displayName = configured.Select(item => item.DisplayName)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    ?? directoryStates.FirstOrDefault(state => state.Provider == providerKey)?.DisplayName
                    ?? _modelOptions.FirstOrDefault(option => option.Provider == providerKey)?.ProviderName
                    ?? providerKey;
                var card = new ProviderCardItem(
                    providerKey,
                    displayName,
                    baseUrls.FirstOrDefault(),
                    providerModels,
                    directoryStates.Any(state => state.Provider == providerKey)
                        || IsOfficialProvider(providerKey),
                    configurationSignatures.Length > 1);
                var matchingStates = directoryStates
                    .Where(state => state.Provider == providerKey)
                    .ToArray();
                card.SetRuntimeStatus(
                    matchingStates.Any(state => state.Active)
                        ? ProviderRuntimeMonitorState.Online
                        : matchingStates.Any(state => state.Declared)
                            ? ProviderRuntimeMonitorState.Error
                            : ProviderRuntimeMonitorState.NotLoaded);
                _providers.Add(card);
            }

            ApplyFilter();
            StatusText.Text = _providers.Count == 0
                ? "尚未从任何 Coding 版本读取到 Provider；可以先在 DSh 模型设置中添加。"
                : $"已汇总 {_providers.Count} 个 Provider、{_modelOptions.Count} 个可选模型。运行状态每 15 秒自动更新。";
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取全局 Provider 失败：{ex.Message}";
        }
    }

    private async void SaveDefaultModel_Click(object sender, RoutedEventArgs e)
    {
        if (DefaultModelBox.SelectedItem is not CodingModelOption option)
        {
            StatusText.Text = "请先选择默认模型。";
            return;
        }

        var selection = option.Selection;
        var codingVersions = _instancesProvider()
            .Select(instance =>
            {
                try
                {
                    return (Instance: instance, Providers: _modelService.Read(instance)
                        .Where(provider => provider.Configured)
                        .ToArray());
                }
                catch
                {
                    return (Instance: instance, Providers: Array.Empty<ModelProviderInfo>());
                }
            })
            .Where(item => item.Providers.Length > 0)
            .ToArray();
        var incompatible = codingVersions
            .Where(item => !SupportsSelection(item.Providers, selection))
            .Select(item => item.Instance.Name)
            .ToArray();
        if (incompatible.Length > 0)
        {
            StatusText.Text = $"不能设为全局默认：{selection.DisplayText} 未在 {string.Join("、", incompatible.Take(3))}"
                + (incompatible.Length > 3 ? $" 等 {incompatible.Length} 个版本" : string.Empty)
                + " 的 Provider 目录中提供。";
            return;
        }

        if (codingVersions.Length == 0)
        {
            StatusText.Text = "没有检测到已配置 Provider 的 Coding 版本。";
            return;
        }

        _policyService.SetGlobalDefault(selection);
        var errors = new List<string>();
        var applied = 0;
        foreach (var item in codingVersions)
        {
            var instance = item.Instance;
            try
            {
                if (!IsRunningWithEndpoint(instance))
                {
                    try
                    {
                        _snapshotService.CreateSnapshot(instance, "修改全局默认模型前", automatic: true);
                    }
                    catch
                    {
                        // An empty/new version can still accept its first default.
                    }
                }

                await _modelService.SaveDefaultModelLiveAsync(
                    instance,
                    selection,
                    _cancellation.Token);

                applied++;
            }
            catch (Exception ex) when (ex is IOException
                or InvalidDataException
                or InvalidOperationException
                or HttpRequestException
                or UnauthorizedAccessException
                or TaskCanceledException)
            {
                errors.Add($"{instance.Name}：{ex.Message}");
            }
        }

        StatusText.Text = errors.Count == 0
            ? $"全局默认模型已保存并应用到 {applied} 个 Coding 版本：{selection.DisplayText}。"
            : $"默认模型已保存，应用到 {applied} 个版本；{errors.Count} 个版本失败：{errors[0]}";
    }

    private async void MonitorTimer_Tick(object? sender, EventArgs e) => await RefreshRuntimeStatusAsync();

    internal static bool SupportsSelection(
        IEnumerable<ModelProviderInfo> providers,
        CodingModelSelection selection) =>
        providers.Any(provider => provider.Configured
            && string.Equals(provider.Provider, selection.Provider, StringComparison.OrdinalIgnoreCase)
            && provider.Models.Contains(selection.Model, StringComparer.OrdinalIgnoreCase));

    private async Task RefreshRuntimeStatusAsync()
    {
        if (_monitorBusy || _cancellation.IsCancellationRequested)
        {
            return;
        }

        _monitorBusy = true;
        try
        {
            var running = _instancesProvider().Where(IsRunningWithEndpoint).ToArray();
            if (running.Length == 0)
            {
                foreach (var provider in _providers)
                {
                    provider.SetRuntimeStatus(ProviderRuntimeMonitorState.NotRunning);
                }

                ApplyFilter();
                return;
            }

            var states = new List<DshProviderRuntimeState>();
            var failures = 0;
            foreach (var instance in running)
            {
                try
                {
                    states.AddRange(await _apiClient.ReadProviderStatesAsync(
                        instance.WebUrl!,
                        _cancellation.Token));
                }
                catch (Exception ex) when (ex is HttpRequestException
                    or InvalidDataException
                    or InvalidOperationException
                    or TaskCanceledException)
                {
                    failures++;
                }
            }

            foreach (var provider in _providers)
            {
                var matching = states.Where(state =>
                    string.Equals(state.Provider, provider.Provider, StringComparison.Ordinal)).ToArray();
                provider.SetRuntimeStatus(
                    matching.Any(state => state.Active)
                        ? ProviderRuntimeMonitorState.Online
                        : matching.Any(state => state.Declared)
                            ? ProviderRuntimeMonitorState.Error
                            : failures == running.Length
                                ? ProviderRuntimeMonitorState.Error
                                : ProviderRuntimeMonitorState.NotLoaded);
            }

            ApplyFilter();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _monitorBusy = false;
        }
    }

    private void ProviderSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ProviderFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton selected || selected.Tag is not string filter)
        {
            return;
        }

        _filter = filter;
        if (selected.Parent is WpfPanel panel)
        {
            foreach (var button in panel.Children.OfType<WpfButton>())
            {
                button.Background = ReferenceEquals(button, selected)
                    ? SelectedFilterBrush()
                    : WpfBrushes.Transparent;
            }
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = ProviderSearchBox?.Text?.Trim() ?? string.Empty;
        ProviderList.ItemsSource = _providers.Where(provider =>
                (_filter == "All"
                    || _filter == "Official" && provider.IsOfficial
                    || _filter == "Custom" && !provider.IsOfficial
                    || _filter == "Problem" && provider.NeedsAttention)
                && (query.Length == 0
                    || provider.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || provider.Provider.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || provider.Models.Any(model => model.Contains(query, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
    }

    private static bool IsRunningWithEndpoint(ManagerInstance instance) =>
        !string.IsNullOrWhiteSpace(instance.WebUrl)
        && (instance.RuntimeStatus == InstanceRuntimeStatus.Running
            || instance.RuntimeOwnership != InstanceRuntimeOwnership.None);

    private static bool IsOfficialProvider(string provider) =>
        string.Equals(provider, "deepseek-official", StringComparison.Ordinal)
        || string.Equals(provider, "deepseek", StringComparison.Ordinal)
        || string.Equals(provider, "ollama", StringComparison.Ordinal);

    private static WpfBrush SelectedFilterBrush() =>
        new SolidColorBrush(WpfColor.FromRgb(227, 240, 253));

    private enum ProviderRuntimeMonitorState
    {
        NotRunning,
        NotLoaded,
        Online,
        Error
    }

    private sealed class ProviderCardItem : INotifyPropertyChanged
    {
        private ProviderRuntimeMonitorState _runtimeStatus;

        public ProviderCardItem(
            string provider,
            string displayName,
            string? baseUrl,
            IReadOnlyList<string> models,
            bool isOfficial,
            bool hasConfigurationConflict)
        {
            Provider = provider;
            DisplayName = displayName;
            BaseUrl = baseUrl;
            Models = models;
            IsOfficial = isOfficial;
            HasConfigurationConflict = hasConfigurationConflict;
        }

        public string Provider { get; }
        public string DisplayName { get; }
        public string? BaseUrl { get; }
        public IReadOnlyList<string> Models { get; }
        public bool IsOfficial { get; }
        public bool HasConfigurationConflict { get; }
        public string ModelCountText => $"{Models.Count} 个模型";
        public string KindText => IsOfficial ? "官方目录" : "自定义";
        public string ProviderIdText => $"Provider ID：{Provider}";
        public string BaseUrlText => string.IsNullOrWhiteSpace(BaseUrl)
            ? "Base URL：使用 DSh 目录默认值"
            : $"Base URL：{BaseUrl}";
        public string ConfigurationWarningText => HasConfigurationConflict
            ? "不同 Coding 版本中的 Provider 配置不一致。"
            : string.Empty;
        public bool NeedsAttention => HasConfigurationConflict
            || _runtimeStatus == ProviderRuntimeMonitorState.Error;

        public string RuntimeStatusText => _runtimeStatus switch
        {
            ProviderRuntimeMonitorState.Online => "在线",
            ProviderRuntimeMonitorState.Error => "运行出错",
            ProviderRuntimeMonitorState.NotLoaded => "未加载",
            _ => "未启动"
        };

        public WpfBrush RuntimeStatusBrush => new SolidColorBrush(_runtimeStatus switch
        {
            ProviderRuntimeMonitorState.Online => WpfColor.FromRgb(37, 145, 91),
            ProviderRuntimeMonitorState.Error => WpfColor.FromRgb(213, 67, 67),
            _ => WpfColor.FromRgb(148, 163, 184)
        });

        public void SetRuntimeStatus(ProviderRuntimeMonitorState status)
        {
            if (_runtimeStatus == status)
            {
                return;
            }

            _runtimeStatus = status;
            OnPropertyChanged(nameof(RuntimeStatusText));
            OnPropertyChanged(nameof(RuntimeStatusBrush));
            OnPropertyChanged(nameof(NeedsAttention));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
