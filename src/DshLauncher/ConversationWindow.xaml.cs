using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;
using DshLauncher.Models;
using DshLauncher.Services;
using Forms = System.Windows.Forms;

namespace DshLauncher;

public partial class ConversationWindow : UserControl
{
    private readonly ManagerInstance _instance;
    private readonly ConversationService _service;
    private readonly Func<ManagerInstance, ConversationEntry, Task<bool>> _openConversation;
    private readonly Func<Task>? _synchronizeConversations;
    private readonly Func<string, Task>? _propagateDeletion;
    private readonly IReadOnlyList<ManagerInstance>? _instances;
    private readonly Action<ManagerInstance>? _selectInstance;
    private bool _instanceSelectorReady;

    public ConversationWindow(
        ManagerInstance instance,
        ConversationService service,
        Func<ManagerInstance, ConversationEntry, Task<bool>> openConversation,
        Func<Task>? synchronizeConversations = null,
        Func<string, Task>? propagateDeletion = null,
        IReadOnlyList<ManagerInstance>? instances = null,
        Action<ManagerInstance>? selectInstance = null)
    {
        _instance = instance;
        _service = service;
        _openConversation = openConversation;
        _synchronizeConversations = synchronizeConversations;
        _propagateDeletion = propagateDeletion;
        _instances = instances;
        _selectInstance = selectInstance;
        InitializeComponent();

        // 变更集 134（UI 统一 E）：三张表的列宽随可用宽度分配。
        // 为什么在代码里做：GridViewColumn.Width 是像素 double，既没有 MinWidth，也不支持星号比例。
        SizeChanged += (_, _) => ApplyAdaptiveColumns();
        Loaded += (_, _) => ApplyAdaptiveColumns();
    }

    private ObservableCollection<ConversationEntry> Entries { get; } = new();

    private ObservableCollection<ConversationBackupEntry> Backups { get; } = new();

    /// <summary>
    /// 变更集 134（UI 统一 E）：三张表按可用宽度分配列宽——权重分配“最小宽之外的剩余”，窄窗口不出现横向滚动。
    /// </summary>
    private void ApplyAdaptiveColumns()
    {
        DistributeColumns(SearchResultsList,
            new[] { 1.2, 1.2, 2.4, 0.5, 1.0, 1.0 },
            new[] { 150.0, 140.0, 200.0, 56.0, 110.0, 120.0 });
        DistributeColumns(ConversationList,
            new[] { 1.6, 1.2, 1.1, 0.6, 0.7, 1.0 },
            new[] { 170.0, 130.0, 120.0, 70.0, 80.0, 110.0 });
        DistributeColumns(BackupList,
            new[] { 1.6, 1.1, 1.2, 0.6, 1.6 },
            new[] { 170.0, 130.0, 130.0, 70.0, 170.0 });
    }

    private static void DistributeColumns(System.Windows.Controls.ListView? list, double[] weights, double[] minimums)
    {
        if (list?.View is not GridView view || view.Columns.Count != weights.Length)
        {
            return;
        }

        // 预留右侧滚动条与内边距，避免“刚刚好”时又冒出横向滚动条
        var available = list.ActualWidth - SystemParameters.VerticalScrollBarWidth - 16;
        if (available <= 0)
        {
            return;
        }

        var minimumTotal = minimums.Sum();
        var slack = Math.Max(available, minimumTotal) - minimumTotal;
        var weightTotal = weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            view.Columns[i].Width = Math.Round(minimums[i] + (slack * weights[i] / weightTotal));
        }
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        VersionSelectorBox.ItemsSource = _instances ?? new[] { _instance };
        VersionSelectorBox.SelectedItem = (_instances ?? new[] { _instance }).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _instance.Id, StringComparison.Ordinal));
        _instanceSelectorReady = true;
        await SynchronizeAsync();
        await RefreshAsync();
        await RefreshBackupsAsync(updateStatus: false);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await SynchronizeAsync();
        await RefreshAsync();
    }

    // ---------- 会话全文检索 ----------

    private CancellationTokenSource? _searchCancellation;

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = RunSearchAsync();
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    private async Task RunSearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            StatusText.Text = "请输入要搜索的内容。";
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        SearchButton.IsEnabled = false;
        StatusText.Text = $"正在搜索“{query}”…";
        try
        {
            var hits = await _service.SearchAsync(
                _instances ?? new[] { _instance },
                query,
                maxResults: 200,
                cancellationToken: token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            SearchResultsList.ItemsSource = hits;
            var hasResults = hits.Count > 0;
            SearchResultsPanel.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
            ConversationListPanel.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
            ClearSearchButton.Visibility = Visibility.Visible;
            StatusText.Text = hasResults
                ? $"找到 {hits.Count} 条会话（按命中次数排序），双击结果可打开。"
                : $"没有找到包含“{query}”的会话。";
        }
        catch (OperationCanceledException)
        {
            // 新一轮搜索已接管。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"搜索失败：{ex.Message}";
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        _searchCancellation?.Cancel();
        SearchResultsList.ItemsSource = null;
        SearchResultsPanel.Visibility = Visibility.Collapsed;
        ConversationListPanel.Visibility = Visibility.Visible;
        ClearSearchButton.Visibility = Visibility.Collapsed;
        StatusText.Text = $"显示 {ConversationList.Items.Count} / {Entries.Count} 个当前版本对话文件。";
    }

    private async void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || ItemsControl.ContainerFromElement(SearchResultsList, (DependencyObject)e.OriginalSource)
                is not System.Windows.Controls.ListViewItem)
        {
            return;
        }

        if (SearchResultsList.SelectedItem is not ConversationSearchHit hit)
        {
            return;
        }

        var owner = ResolveOwnerInstance(hit.Entry) ?? _instance;
        try
        {
            if (!await _openConversation(owner, hit.Entry))
            {
                StatusText.Text = "该会话无法打开（可能已被移动或删除）。";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            StatusText.Text = $"打开会话失败：{ex.Message}";
        }
    }

    /// <summary>按会话文件路径反查所属实例（检索结果是跨实例的）。</summary>
    private ManagerInstance? ResolveOwnerInstance(ConversationEntry entry) =>
        (_instances ?? new[] { _instance }).FirstOrDefault(instance =>
            !string.IsNullOrWhiteSpace(instance.DshHome)
            && entry.FullPath.StartsWith(
                Path.Combine(instance.DshHome, "sessions"),
                StringComparison.OrdinalIgnoreCase));

    private async Task SynchronizeAsync()
    {
        if (_synchronizeConversations is not null)
        {
            await _synchronizeConversations();
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var selectedPath = (ConversationList.SelectedItem as ConversationEntry)?.FullPath;
            var entries = await Task.Run(() => _service.List(_instance));
            Entries.Clear();
            foreach (var entry in entries) Entries.Add(entry);
            ApplyConversationFilter();
            if (selectedPath is not null)
            {
                ConversationList.SelectedItem = Entries.FirstOrDefault(entry =>
                    string.Equals(entry.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            }

            var historical = Entries.Sum(entry => entry.HistoricalGenerationCount);
            StatusText.Text = $"已读取 {ConversationList.Items.Count} / {Entries.Count} 个当前版本对话文件。"
                + (historical > 0
                    ? $"另有 {historical} 个历史代际未列出（dsh 读最高代际；降级前备份会一并导出）。"
                    : string.Empty)
                + "压缩 session.jsonl.zstd 可查看、打开和导入。";
            UpdateSelection();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void VersionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_instanceSelectorReady
            || VersionSelectorBox.SelectedItem is not ManagerInstance target
            || string.Equals(target.Id, _instance.Id, StringComparison.Ordinal))
        {
            return;
        }

        _selectInstance?.Invoke(target);
    }

    private void ConversationScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyConversationFilter();
            StatusText.Text = $"显示 {ConversationList.Items.Count} / {Entries.Count} 个当前版本对话文件。";
        }
    }

    private void ApplyConversationFilter()
    {
        var scope = (ConversationScopeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        ConversationList.ItemsSource = scope switch
        {
            "Isolated" => Entries.Where(static entry => string.IsNullOrWhiteSpace(entry.WorkingDirectory)).ToArray(),
            "Workspace" => Entries.Where(static entry => !string.IsNullOrWhiteSpace(entry.WorkingDirectory)).ToArray(),
            _ => Entries.ToArray()
        };
    }

    private async void RefreshBackups_Click(object sender, RoutedEventArgs e) =>
        await RefreshBackupsAsync();

    private async Task RefreshBackupsAsync(bool updateStatus = true)
    {
        try
        {
            var selectedPath = (BackupList.SelectedItem as ConversationBackupEntry)?.FullPath;
            var backups = await Task.Run(() => _service.ListBackups(_instance));
            Backups.Clear();
            foreach (var backup in backups) Backups.Add(backup);
            BackupList.ItemsSource = Backups;
            if (selectedPath is not null)
            {
                BackupList.SelectedItem = Backups.FirstOrDefault(backup =>
                    string.Equals(backup.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            }

            if (updateStatus)
            {
                StatusText.Text = $"已读取 {Backups.Count} 个对话备份。";
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();

    private async void ConversationList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(ConversationList, source) is not System.Windows.Controls.ListViewItem)
        {
            return;
        }

        e.Handled = true;
        await OpenSelectedAsync();
    }

    private async Task OpenSelectedAsync()
    {
        if (ConversationList.SelectedItem is not ConversationEntry entry)
        {
            StatusText.Text = "请先选择一个对话。";
            return;
        }

        if (!entry.HasValidHeader || entry.SessionId is null)
        {
            StatusText.Text = entry.IsCompressed
                ? "无法打开：压缩会话的 Zstandard header 无法读取，文件可能已损坏。"
                : "无法打开：会话 header 无法读取。";
            return;
        }

        try
        {
            if (!await _openConversation(_instance, entry))
            {
                StatusText.Text = "当前实例没有运行，或没有可用的 Chat 地址；请先启动实例。";
            }
            else
            {
                StatusText.Text = $"已打开对话：{entry.DisplayName}。";
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "导入 DSh session.jsonl",
            Filter = "DSh session (*.jsonl;*.jsonl.zstd)|*.jsonl;*.jsonl.zstd|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        var targetInstance = _instance;
        string? workspaceOverride = null;
        if (_instances is { Count: > 0 })
        {
            var choice = ShowImportTargetDialog();
            if (choice is null)
            {
                StatusText.Text = "已取消导入。";
                return;
            }

            (targetInstance, workspaceOverride) = choice.Value;
        }

        try
        {
            var target = await Task.Run(() => _service.Import(targetInstance, dialog.FileName, workspaceOverride));
            await SynchronizeAsync();
            StatusText.Text = $"对话已导入到 {targetInstance.Name}：{target}";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private const string ImportWorkspaceAuto = "（按文件自带工作目录）";

    private (ManagerInstance Instance, string? Workspace)? ShowImportTargetDialog()
    {
        var instances = _instances!;
        var versionBox = new System.Windows.Controls.ComboBox
        {
            DisplayMemberPath = "Name",
            Margin = new Thickness(0, 6, 0, 0)
        };
        versionBox.ItemsSource = instances;
        versionBox.SelectedIndex = Math.Max(0, instances.ToList().FindIndex(candidate =>
            string.Equals(candidate.Id, _instance.Id, StringComparison.Ordinal)));

        var workspaceBox = new System.Windows.Controls.ComboBox
        {
            IsEditable = true,
            Margin = new Thickness(0, 6, 0, 0)
        };

        void LoadWorkspaces(ManagerInstance selected)
        {
            try
            {
                var workspaces = _service.List(selected)
                    .Select(entry => entry.WorkingDirectory)
                    .Where(directory => !string.IsNullOrWhiteSpace(directory))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                workspaceBox.ItemsSource = new[] { ImportWorkspaceAuto }.Concat(workspaces).ToArray();
            }
            catch
            {
                workspaceBox.ItemsSource = new[] { ImportWorkspaceAuto };
            }

            workspaceBox.SelectedIndex = 0;
        }

        LoadWorkspaces(instances[Math.Max(0, versionBox.SelectedIndex)]);
        versionBox.SelectionChanged += (_, _) =>
        {
            if (versionBox.SelectedItem is ManagerInstance selected)
            {
                LoadWorkspaces(selected);
            }
        };

        var confirmButton = new System.Windows.Controls.Button
        {
            Content = "导入",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(16, 8, 16, 8),
            MinWidth = 90
        };
        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "取消",
            Padding = new Thickness(16, 8, 16, 8),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttons.Children.Add(confirmButton);
        buttons.Children.Add(cancelButton);

        var dialog = new Window
        {
            Title = "选择导入目标",
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            MinWidth = 460,
            Padding = new Thickness(20),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "导入到版本", FontWeight = FontWeights.SemiBold },
                    versionBox,
                    new TextBlock
                    {
                        Text = "工作区（决定会话在 sessions 下的目录）",
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 14, 0, 0)
                    },
                    workspaceBox,
                    buttons
                }
            }
        };
        confirmButton.Click += (_, _) => dialog.DialogResult = true;
        cancelButton.Click += (_, _) => dialog.DialogResult = false;

        if (dialog.ShowDialog() != true
            || versionBox.SelectedItem is not ManagerInstance target)
        {
            return null;
        }

        var workspace = workspaceBox.Text?.Trim();
        return (target, string.IsNullOrEmpty(workspace) || workspace == ImportWorkspaceAuto ? null : workspace);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationEntry entry)
        {
            StatusText.Text = "请先选择一个对话。";
            return;
        }

        using var dialog = new Forms.SaveFileDialog
        {
            Title = "导出 DSh session",
            Filter = entry.IsCompressed
                ? "压缩 DSh session (*.jsonl.zstd)|*.jsonl.zstd"
                : "DSh session (*.jsonl)|*.jsonl",
            FileName = ExportFileName(entry),
            DefaultExt = entry.IsCompressed ? "jsonl.zstd" : "jsonl",
            OverwritePrompt = true,
            AddExtension = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        try
        {
            var target = await Task.Run(() => _service.Export(_instance, entry, dialog.FileName));
            StatusText.Text = $"对话已导出：{target}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationEntry entry)
        {
            StatusText.Text = "请先选择一个对话。";
            return;
        }

        try
        {
            var target = await Task.Run(() => _service.Backup(_instance, entry));
            await RefreshBackupsAsync(updateStatus: false);
            StatusText.Text = $"对话已备份：{target}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not ConversationBackupEntry backup)
        {
            StatusText.Text = "请先在“备份与恢复”中选择一个备份。";
            return;
        }

        if (!backup.HasValidHeader)
        {
            StatusText.Text = "选中的备份无法读取会话 header，不能恢复。";
            return;
        }

        if (AppDialog.Show(
                Window.GetWindow(this),
                $"确定把“{backup.DisplayName}”恢复到当前实例？已有相同会话 ID 时不会覆盖。",
                "恢复对话备份",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var target = await Task.Run(() => _service.RestoreBackup(_instance, backup));
            await SynchronizeAsync();
            await RefreshAsync();
            await RefreshBackupsAsync(updateStatus: false);
            StatusText.Text = $"对话已恢复：{target}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationEntry entry)
        {
            StatusText.Text = "请先选择一个对话。";
            return;
        }

        if (AppDialog.Show(
                Window.GetWindow(this),
                DialogText.ForMessageBox(entry.HasHistoricalGenerations
                    ? $"确定删除会话文件“{entry.RelativePath}”？\n\n"
                      + $"该会话目录里还有 {entry.HistoricalGenerationCount} 个更早的代际。"
                      + $"删掉当前代际（v{entry.GenerationVersion}）后，dsh 会退回到更早的那一代，"
                      + "也就是升级前的旧内容。此操作不可由 Launcher 撤销。"
                    : $"确定删除会话文件“{entry.RelativePath}”？此操作不可由 Launcher 撤销。"),
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await Task.Run(() => _service.Delete(_instance, entry));
            if (_propagateDeletion is not null)
            {
                await _propagateDeletion(entry.RelativePath);
            }
            else
            {
                await SynchronizeAsync();
            }
            StatusText.Text = "对话文件已删除。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackupList.SelectedItem is ConversationBackupEntry backup)
        {
            StatusText.Text = backup.HasValidHeader
                ? $"已选择备份：{backup.DisplayName} · {backup.BackedUpAt:yyyy-MM-dd HH:mm:ss}"
                : "已选择无法读取的备份；为避免恢复损坏文件，恢复按钮不会执行。";
        }
    }

    private static string ExportFileName(ConversationEntry entry)
    {
        // 导出默认用对话名称命名；没有可读名称时回退到原文件名。
        var named = SafeFileName(entry.DisplayName);
        if (!string.IsNullOrWhiteSpace(named))
        {
            return named;
        }

        var fileName = Path.GetFileName(entry.FullPath);
        var extension = entry.IsCompressed ? ".jsonl.zstd" : ".jsonl";
        return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^extension.Length]
            : fileName;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.ToString().TrimEnd('.', ' ');
    }

    private void UpdateSelection()
    {
        if (ConversationList.SelectedItem is not ConversationEntry entry)
        {
            return;
        }

        StatusText.Text = entry.HasValidHeader
            ? $"已选择 {entry.DisplayName} · {entry.RelativePath}"
            : entry.IsCompressed
                ? "已选择无法读取 header 的压缩会话；可导出/备份或删除，打开前需先确认文件未损坏。"
                : "已选择无法读取 header 的会话文件；可导出/备份或删除。";
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        AppDialog.Show(Window.GetWindow(this), ex.Message, "对话操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
