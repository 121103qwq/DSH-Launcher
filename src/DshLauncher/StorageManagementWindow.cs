using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DshLauncher.Models;
using DshLauncher.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfListView = System.Windows.Controls.ListView;
using WpfBinding = System.Windows.Data.Binding;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using FileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using RecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using UIOption = Microsoft.VisualBasic.FileIO.UIOption;

namespace DshLauncher;

/// <summary>
/// Displays per-instance storage usage and cleans only the file candidates
/// returned by <see cref="InstanceStorageService"/>.
/// </summary>
public sealed class StorageManagementWindow : Window
{
    private readonly ManagerInstance _instance;
    private readonly InstanceStorageService _storageService;
    private readonly CancellationTokenSource _cancellation;
    private readonly TextBlock _statusText;
    private readonly TextBlock _usageSummaryText;
    private readonly TextBlock _previewSummaryText;
    private readonly TextBlock _resultText;
    private readonly WpfListView _categoryList;
    private readonly WpfListView _candidateList;
    private readonly WpfButton _refreshButton;
    private readonly WpfButton _cleanupButton;
    private bool _isBusy;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _loadedOnce;

    public StorageManagementWindow(
        Window? owner,
        ManagerInstance instance,
        InstanceStorageService storageService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(storageService);

        _instance = instance;
        _storageService = storageService;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Title = $"空间管理 · {instance.Name}";
        Width = 820;
        Height = 680;
        MinWidth = 620;
        MinHeight = 480;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Owner = owner;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(184) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "空间管理",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "查看当前实例的存储占用，并只清理 Launcher 判定为安全的文件。",
            Foreground = WpfBrushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var location = new Border
        {
            Background = WpfBrushes.WhiteSmoke,
            BorderBrush = WpfBrushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(13),
            Margin = new Thickness(0, 16, 0, 14)
        };
        var locationPanel = new StackPanel();
        locationPanel.Children.Add(new TextBlock
        {
            Text = instance.Name,
            FontWeight = FontWeights.SemiBold
        });
        locationPanel.Children.Add(new TextBlock
        {
            Text = $"DSH_HOME：{instance.DshHome}",
            Foreground = WpfBrushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0),
            ToolTip = instance.DshHome
        });
        location.Child = locationPanel;
        Grid.SetRow(location, 1);
        root.Children.Add(location);

        var usageHeading = new TextBlock
        {
            Text = "存储占用",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 7)
        };
        Grid.SetRow(usageHeading, 2);
        root.Children.Add(usageHeading);

        _categoryList = new WpfListView
        {
            BorderBrush = WpfBrushes.LightGray,
            BorderThickness = new Thickness(1),
            Background = WpfBrushes.White,
            HorizontalContentAlignment = WpfHorizontalAlignment.Stretch,
            IsTabStop = false
        };
        _categoryList.View = new GridView
        {
            Columns =
            {
                new GridViewColumn
                {
                    Header = "分类",
                    Width = 244,
                    DisplayMemberBinding = new WpfBinding(nameof(StorageCategoryRow.CategoryText))
                },
                new GridViewColumn
                {
                    Header = "大小",
                    Width = 180,
                    DisplayMemberBinding = new WpfBinding(nameof(StorageCategoryRow.SizeText))
                },
                new GridViewColumn
                {
                    Header = "文件数",
                    Width = 120,
                    DisplayMemberBinding = new WpfBinding(nameof(StorageCategoryRow.FileCountText))
                }
            }
        };
        Grid.SetRow(_categoryList, 3);
        root.Children.Add(_categoryList);

        var previewHeading = new Grid { Margin = new Thickness(0, 17, 0, 7) };
        previewHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        previewHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var previewTitle = new TextBlock
        {
            Text = "安全清理预览",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        };
        _previewSummaryText = new TextBlock
        {
            Foreground = WpfBrushes.DimGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 1, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(_previewSummaryText, 1);
        previewHeading.Children.Add(previewTitle);
        previewHeading.Children.Add(_previewSummaryText);
        Grid.SetRow(previewHeading, 4);
        root.Children.Add(previewHeading);

        _candidateList = new WpfListView
        {
            BorderBrush = WpfBrushes.LightGray,
            BorderThickness = new Thickness(1),
            Background = WpfBrushes.White,
            HorizontalContentAlignment = WpfHorizontalAlignment.Stretch,
            IsTabStop = false
        };
        _candidateList.View = new GridView
        {
            Columns =
            {
                new GridViewColumn
                {
                    Header = "分类",
                    Width = 176,
                    DisplayMemberBinding = new WpfBinding(nameof(StorageCandidateRow.CategoryText))
                },
                new GridViewColumn
                {
                    Header = "文件",
                    Width = 494,
                    DisplayMemberBinding = new WpfBinding(nameof(StorageCandidateRow.Path))
                },
                new GridViewColumn
                {
                    Header = "大小",
                    Width = 100,
                    DisplayMemberBinding = new WpfBinding(nameof(StorageCandidateRow.SizeText))
                }
            }
        };
        Grid.SetRow(_candidateList, 5);
        root.Children.Add(_candidateList);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var footerText = new StackPanel();
        _usageSummaryText = new TextBlock
        {
            Foreground = WpfBrushes.DimGray,
            TextWrapping = TextWrapping.Wrap
        };
        _resultText = new TextBlock
        {
            Foreground = WpfBrushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 12, 0)
        };
        footerText.Children.Add(_usageSummaryText);
        footerText.Children.Add(_resultText);
        Grid.SetRow(footerText, 0);
        Grid.SetColumn(footerText, 0);
        footer.Children.Add(footerText);

        var actions = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right
        };
        _refreshButton = new WpfButton
        {
            Content = "重新扫描",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0)
        };
        _refreshButton.Click += Refresh_Click;
        _cleanupButton = new WpfButton
        {
            Content = "清理安全候选",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0)
        };
        ApplyPrimaryButtonStyle(_cleanupButton);
        _cleanupButton.Click += Cleanup_Click;
        var closeButton = new WpfButton
        {
            Content = "关闭",
            IsCancel = true,
            Padding = new Thickness(14, 7, 14, 7)
        };
        closeButton.Click += (_, _) => Close();
        actions.Children.Add(_refreshButton);
        actions.Children.Add(_cleanupButton);
        actions.Children.Add(closeButton);
        Grid.SetRow(actions, 0);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        _statusText = new TextBlock
        {
            Text = "等待扫描…",
            Foreground = WpfBrushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 11, 0, 0)
        };
        Grid.SetRow(_statusText, 1);
        Grid.SetColumnSpan(_statusText, 2);
        footer.Children.Add(_statusText);

        Grid.SetRow(footer, 6);
        root.Children.Add(footer);

        Content = root;
        Loaded += StorageManagementWindow_Loaded;
        WindowSizeHelper.FitInitialSize(this);
        SetBusy(false);
    }

    public InstanceStorageUsage? CurrentUsage { get; private set; }

    public StorageCleanupPreview? CurrentCleanupPreview { get; private set; }

    public CancellationToken CancellationToken => _cancellation.Token;

    private async void StorageManagementWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_isBusy || _cancellation.IsCancellationRequested)
        {
            return;
        }

        SetBusy(true);
        _statusText.Text = "正在扫描 Sessions、Snapshots、Reports 和其他存储…";
        _resultText.Text = string.Empty;
        try
        {
            var scan = await ScanAsync();
            ApplyScan(scan.Usage, scan.Preview);
            _statusText.Text = $"扫描完成：{scan.Usage.ScannedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _statusText.Text = "扫描已取消。";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"扫描失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
            CloseAfterCancellationIfRequested();
        }
    }

    private async void Cleanup_Click(object sender, RoutedEventArgs e) => await CleanupAsync();

    private async Task CleanupAsync()
    {
        var preview = CurrentCleanupPreview;
        if (_isBusy
            || preview is null
            || preview.Candidates.Count == 0
            || _cancellation.IsCancellationRequested)
        {
            return;
        }

        var candidates = preview.Candidates.ToArray();
        var confirmation = WpfMessageBox.Show(
            this,
            $"将把服务返回的 {preview.ReclaimableFiles:N0} 个文件逐个移入 Windows 回收站，共 {FormatBytes(preview.ReclaimableBytes)}。\n\n"
                + "不会处理 Sessions、凭据、手动快照或任何目录，也不会跟随 reparse point。\n\n"
                + "确定继续吗？",
            "确认安全清理",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        _statusText.Text = $"正在逐个移入回收站（共 {candidates.Length:N0} 个文件）…";
        _resultText.Text = string.Empty;
        try
        {
            var result = await Task.Run(
                () => RecycleCandidates(preview, candidates, _cancellation.Token),
                _cancellation.Token);

            _statusText.Text = "清理完成，正在重新扫描…";
            var scan = await ScanAsync();
            ApplyScan(scan.Usage, scan.Preview);

            _resultText.Text = result.FailedFiles == 0
                ? $"本次已送入回收站：{result.RecycledFiles:N0} 个文件，共 {FormatBytes(result.RecycledBytes)}。已重新扫描。"
                : $"本次已送入回收站：{result.RecycledFiles:N0} 个文件，共 {FormatBytes(result.RecycledBytes)}；{result.FailedFiles:N0} 个文件未处理。已重新扫描。"
                    + (string.IsNullOrWhiteSpace(result.FirstFailure)
                        ? string.Empty
                        : $"\n原因：{result.FirstFailure}");
            _statusText.Text = $"重新扫描完成：{scan.Usage.ScannedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _statusText.Text = "清理已取消；正在关闭或保留当前扫描结果。";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"清理失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
            CloseAfterCancellationIfRequested();
        }
    }

    private async Task<(InstanceStorageUsage Usage, StorageCleanupPreview Preview)> ScanAsync()
    {
        var usage = await _storageService.GetUsageAsync(_instance, _cancellation.Token);
        var preview = await _storageService.PreviewCleanupAsync(_instance, _cancellation.Token);
        return (usage, preview);
    }

    private void ApplyScan(InstanceStorageUsage usage, StorageCleanupPreview preview)
    {
        CurrentUsage = usage;
        CurrentCleanupPreview = preview;

        _categoryList.ItemsSource = Enum.GetValues<InstanceStorageCategory>()
            .Select(category =>
            {
                var item = usage.GetCategory(category);
                return new StorageCategoryRow(
                    CategoryText(category),
                    FormatBytes(item.Bytes),
                    item.FileCount.ToString("N0"));
            })
            .ToArray();

        _candidateList.ItemsSource = preview.Candidates
            .Select(candidate => new StorageCandidateRow(
                CategoryText(candidate.Category),
                candidate.FullPath,
                FormatBytes(candidate.Bytes)))
            .ToArray();

        _usageSummaryText.Text =
            $"总计：{FormatBytes(usage.TotalBytes)} · {usage.TotalFiles:N0} 个文件";
        _previewSummaryText.Text = preview.Candidates.Count == 0
            ? "服务当前没有返回可安全清理的文件。"
            : $"服务返回 {preview.ReclaimableFiles:N0} 个文件 · {FormatBytes(preview.ReclaimableBytes)}";
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        _refreshButton.IsEnabled = !busy && !_cancellation.IsCancellationRequested;
        _cleanupButton.IsEnabled = !busy
            && !_cancellation.IsCancellationRequested
            && CurrentCleanupPreview?.Candidates.Count > 0;
    }

    private void CloseAfterCancellationIfRequested()
    {
        if (!_closeRequested || _isBusy || _allowClose)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && _isBusy)
        {
            e.Cancel = true;
            _closeRequested = true;
            _cancellation.Cancel();
            _statusText.Text = "正在取消操作…";
            SetBusy(true);
            return;
        }

        _cancellation.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        base.OnClosed(e);
    }

    private static CleanupResult RecycleCandidates(
        StorageCleanupPreview preview,
        IReadOnlyList<StorageCleanupCandidate> candidates,
        CancellationToken cancellationToken)
    {
        long recycledBytes = 0;
        long recycledFiles = 0;
        var failedFiles = 0;
        string? firstFailure = null;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetSafeCandidatePath(preview, candidate, out var path))
            {
                failedFiles++;
                firstFailure ??= $"已跳过不符合安全条件的候选：{candidate.FullPath}";
                continue;
            }

            try
            {
                // DeleteFile with SendToRecycleBin performs a file-level move;
                // no directory or recursive delete is ever used here.
                FileSystem.DeleteFile(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
                recycledFiles++;
                recycledBytes += Math.Max(0, candidate.Bytes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedCleanupFailure(ex))
            {
                failedFiles++;
                firstFailure ??= $"{Path.GetFileName(path)}：{ex.Message}";
            }
        }

        return new CleanupResult(recycledBytes, recycledFiles, failedFiles, firstFailure);
    }

    private static bool TryGetSafeCandidatePath(
        StorageCleanupPreview preview,
        StorageCleanupCandidate candidate,
        out string path)
    {
        path = string.Empty;
        if (candidate.FileCount != 1
            || string.IsNullOrWhiteSpace(candidate.FullPath)
            || !IsAllowedCleanupCategory(candidate.Category))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(candidate.FullPath);
            var home = Path.GetFullPath(preview.DshHome);
            if (string.Equals(path, home, StringComparison.OrdinalIgnoreCase)
                || IsPathInside(path, Path.Combine(home, "sessions"))
                || IsCredentialFile(path))
            {
                return false;
            }

            if (candidate.Category == InstanceStorageCategory.Snapshots
                && (!Path.GetFileName(path).StartsWith("auto-", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        Path.GetExtension(path),
                        ".dshsnapshot",
                        StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!HasNoReparsePoint(path)
                || !File.Exists(path))
            {
                return false;
            }

            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception ex) when (IsExpectedCleanupFailure(ex))
        {
            path = string.Empty;
            return false;
        }
    }

    private static bool HasNoReparsePoint(string path)
    {
        var current = path;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return true;
    }

    private static bool IsPathInside(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedRoot + Path.AltDirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCredentialFile(string path) =>
        string.Equals(Path.GetFileName(path), ".credentials.yaml", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(path), ".credentials.yml", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedCleanupCategory(InstanceStorageCategory category) =>
        category is InstanceStorageCategory.Snapshots
            or InstanceStorageCategory.Reports
            or InstanceStorageCategory.Cache;

    private static bool IsExpectedCleanupFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException
            or Win32Exception;

    private static void ApplyPrimaryButtonStyle(WpfButton button)
    {
        if (System.Windows.Application.Current?.TryFindResource("PrimaryButton") is Style style)
        {
            button.Style = style;
        }
    }

    private static string CategoryText(InstanceStorageCategory category) => category switch
    {
        InstanceStorageCategory.Sessions => "会话 Sessions",
        InstanceStorageCategory.Snapshots => "快照 Snapshots",
        InstanceStorageCategory.Reports => "报告 Reports",
        InstanceStorageCategory.PluginsAndDependencies => "插件与依赖 Plugins",
        InstanceStorageCategory.Cache => "缓存 Cache",
        _ => "其他 Other"
    };

    private static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        if (value >= 1024L * 1024 * 1024)
        {
            return $"{value / (1024d * 1024 * 1024):0.0} GB";
        }

        if (value >= 1024L * 1024)
        {
            return $"{value / (1024d * 1024):0.0} MB";
        }

        if (value >= 1024L)
        {
            return $"{value / 1024d:0.0} KB";
        }

        return $"{value} B";
    }

    private sealed record StorageCategoryRow(
        string CategoryText,
        string SizeText,
        string FileCountText);

    private sealed record StorageCandidateRow(
        string CategoryText,
        string Path,
        string SizeText);

    private sealed record CleanupResult(
        long RecycledBytes,
        long RecycledFiles,
        int FailedFiles,
        string? FirstFailure);
}
