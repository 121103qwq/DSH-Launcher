using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DshLauncher.Models;
using DshLauncher.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace DshLauncher;

/// <summary>
/// 扫描本机 DSH 环境（内嵌页，work-log/50）：列出 %USERPROFILE%\.dsh*、DSH_HOME
/// 与 WSL 里的 home，勾选后按 home 建实例。扫描/导入分别复用
/// <see cref="DshEnvironmentScanner"/> 与 <see cref="ScannedHomeImportService"/>。
/// </summary>
public partial class EnvironmentScanWindow : UserControl, INotifyPropertyChanged
{
    private static readonly Brush WebBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20));
    private static readonly Brush TuiBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x3D, 0x9A));
    private static readonly Brush OtherBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C));

    private readonly DshEnvironmentScanner _scanner;
    private readonly Func<IReadOnlyCollection<ManagerInstance>> _instancesProvider;
    private readonly Func<ManagerInstance?> _templateProvider;
    private readonly ScannedHomeImportService _importer;
    private readonly Action<ManagerInstance> _imported;
    private readonly Action _goBack;
    private readonly CancellationTokenSource _cancellation;
    private CancellationTokenSource? _scanCancellation;
    private bool _busy;

    public event PropertyChangedEventHandler? PropertyChanged;

    public EnvironmentScanWindow(
        DshEnvironmentScanner scanner,
        Func<IReadOnlyCollection<ManagerInstance>> instancesProvider,
        Func<ManagerInstance?> templateProvider,
        ScannedHomeImportService importer,
        Action<ManagerInstance> imported,
        Action goBack,
        CancellationToken cancellationToken = default)
    {
        _scanner = scanner;
        _instancesProvider = instancesProvider;
        _templateProvider = templateProvider;
        _importer = importer;
        _imported = imported;
        _goBack = goBack;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        InitializeComponent();
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e) => await RescanAsync();

    private void Window_OnUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _scanCancellation?.Cancel();
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：无所谓。
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await RescanAsync();

    private void Back_Click(object sender, RoutedEventArgs e) => _goBack();

    private void RefreshTemplatePicker()
    {
        var templates = _instancesProvider()
            .Where(static instance =>
                instance.Kind == InstanceKind.Installed
                && DshRuntimeCommandFactory.IsUsable(instance.EffectiveDshLaunchSpec))
            .ToArray();
        var preferred = _templateProvider();
        TemplateCombo.ItemsSource = templates;
        TemplateCombo.SelectedItem = templates.FirstOrDefault(instance =>
                preferred is not null
                && string.Equals(instance.Id, preferred.Id, StringComparison.Ordinal))
            ?? templates.FirstOrDefault();
    }

    private async Task RescanAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        ImportButton.IsEnabled = false;
        StatusText.Text = "正在扫描本机 DSH 环境…";
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        _scanCancellation = cancellation;
        try
        {
            RefreshTemplatePicker();
            var result = await _scanner.ScanAsync(_instancesProvider(), cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            var items = result.Homes
                .Select(static home => new ScanHomeItem(home))
                .ToArray();
            HomeList.ItemsSource = items;
            EmptyText.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (result.Warnings.Count > 0)
            {
                WarningText.Text = string.Join("\n", result.Warnings);
                WarningPanel.Visibility = Visibility.Visible;
            }
            else
            {
                WarningPanel.Visibility = Visibility.Collapsed;
            }

            var selectable = items.Count(static item => item.CanSelect);
            StatusText.Text = items.Length == 0
                ? "没有扫描到 DSH home。"
                : $"扫描到 {items.Length} 个 home，可导入 {selectable} 个；已登记或不含 profiles/web 的不可选。";
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：忽略。
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"扫描失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
            }

            cancellation.Dispose();
            // 必须在 _busy 复位后刷新：扫描期间按钮保持禁用。
            UpdateImportButton();
        }
    }

    private void UpdateImportButton() =>
        ImportButton.IsEnabled = !_busy
            && HomeList.ItemsSource is IEnumerable<ScanHomeItem> items
            && items.Any(static item => item.IsSelected && item.CanSelect);

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || HomeList.ItemsSource is not IEnumerable<ScanHomeItem> items
            || TemplateCombo.SelectedItem is not ManagerInstance template)
        {
            return;
        }

        var selected = items
            .Where(static item => item.IsSelected && item.CanSelect)
            .Select(static item => item.Home)
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        _busy = true;
        ImportButton.IsEnabled = false;
        var importedCount = 0;
        var skipped = 0;
        var failures = new List<string>();
        try
        {
            foreach (var home in selected)
            {
                StatusText.Text = $"正在导入 {ScannedHomeImportService.DisplayName(home)}…";
                var outcome = await _importer.ImportAsync(
                    home,
                    template,
                    _instancesProvider(),
                    _cancellation.Token);
                if (outcome.Skipped)
                {
                    skipped++;
                    continue;
                }

                if (outcome.Instance is null)
                {
                    failures.Add(outcome.Error ?? $"{ScannedHomeImportService.DisplayName(home)} 导入失败。");
                    continue;
                }

                importedCount++;
                _imported(outcome.Instance);
            }

            StatusText.Text = failures.Count == 0
                ? $"已导入 {importedCount} 个实例" + (skipped > 0 ? $"，跳过 {skipped} 个已登记 home。" : "。")
                : $"已导入 {importedCount} 个，跳过 {skipped} 个，失败 {failures.Count} 个：{string.Join("；", failures)}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "导入已取消。";
        }
        finally
        {
            _busy = false;
            await RescanAsync();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>一行 home 的展示状态（勾选、来源标签、profile 列表、不可选原因）。</summary>
    public sealed class ScanHomeItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ScanHomeItem(ScannedDshHome home)
        {
            Home = home;
            DisplayName = ScannedHomeImportService.DisplayName(home);
            SourceText = home.Source switch
            {
                DshHomeSource.UserProfile => "用户目录",
                DshHomeSource.DshHomeEnvironment => "DSH_HOME",
                _ => "WSL"
            };
            WslText = home.WslDistro is null ? string.Empty : $"WSL: {home.WslDistro}";
            WslVisibility = home.WslDistro is null ? Visibility.Collapsed : Visibility.Visible;
            RegisteredVisibility = home.AlreadyRegistered ? Visibility.Visible : Visibility.Collapsed;
            Profiles = home.Profiles
                .Select(static profile => new ScanProfileItem(profile))
                .ToArray();
            var hasWeb = home.Profiles.Any(static profile =>
                string.Equals(profile.Name, "web", StringComparison.OrdinalIgnoreCase));
            HintText = hasWeb ? string.Empty : "没有 profiles/web，暂不支持导入（TUI profile 需 #19）";
            HintVisibility = hasWeb ? Visibility.Collapsed : Visibility.Visible;
            CanSelect = hasWeb && !home.AlreadyRegistered;
            _isSelected = CanSelect;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ScannedDshHome Home { get; }

        public string DisplayName { get; }

        public string PathText => Home.Path;

        public string SourceText { get; }

        public string WslText { get; }

        public Visibility WslVisibility { get; }

        public Visibility RegisteredVisibility { get; }

        public string HintText { get; }

        public Visibility HintVisibility { get; }

        public bool CanSelect { get; }

        public IReadOnlyList<ScanProfileItem> Profiles { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    /// <summary>profile 标签（名字 + web/tui/other 分类）。</summary>
    public sealed class ScanProfileItem
    {
        public ScanProfileItem(ScannedDshProfile profile)
        {
            Name = profile.Name;
            KindText = profile.Kind switch
            {
                DshProfileKind.Web => "web",
                DshProfileKind.Tui => "tui",
                _ => "other"
            };
            KindBrush = profile.Kind switch
            {
                DshProfileKind.Web => WebBrush,
                DshProfileKind.Tui => TuiBrush,
                _ => OtherBrush
            };
        }

        public string Name { get; }

        public string KindText { get; }

        public Brush KindBrush { get; }
    }
}
