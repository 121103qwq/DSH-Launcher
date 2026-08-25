using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;
using DshLauncher.Models;
using DshLauncher.Services;
using Forms = System.Windows.Forms;

namespace DshLauncher;

public partial class ConversationWindow : UserControl
{
    private readonly ManagerInstance _instance;
    private readonly ConversationService _service;
    private readonly Func<ConversationEntry, Task<bool>> _openConversation;
    private readonly Func<Task>? _synchronizeConversations;
    private readonly Func<string, Task>? _propagateDeletion;
    private readonly IReadOnlyList<ManagerInstance>? _instances;
    private readonly Action<ManagerInstance>? _selectInstance;
    private readonly CodingModelPolicyService? _modelPolicyService;
    private readonly Func<Task<IReadOnlyList<CodingModelOption>>>? _modelOptionsProvider;
    private IReadOnlyList<ModelChoice> _modelChoices = Array.Empty<ModelChoice>();
    private bool _instanceSelectorReady;

    public ConversationWindow(
        ManagerInstance instance,
        ConversationService service,
        Func<ConversationEntry, Task<bool>> openConversation,
        Func<Task>? synchronizeConversations = null,
        Func<string, Task>? propagateDeletion = null,
        IReadOnlyList<ManagerInstance>? instances = null,
        Action<ManagerInstance>? selectInstance = null,
        CodingModelPolicyService? modelPolicyService = null,
        Func<Task<IReadOnlyList<CodingModelOption>>>? modelOptionsProvider = null)
    {
        _instance = instance;
        _service = service;
        _openConversation = openConversation;
        _synchronizeConversations = synchronizeConversations;
        _propagateDeletion = propagateDeletion;
        _instances = instances;
        _selectInstance = selectInstance;
        _modelPolicyService = modelPolicyService;
        _modelOptionsProvider = modelOptionsProvider;
        InitializeComponent();
    }

    private ObservableCollection<ConversationEntry> Entries { get; } = new();

    private ObservableCollection<ConversationBackupEntry> Backups { get; } = new();

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        VersionSelectorBox.ItemsSource = _instances ?? new[] { _instance };
        VersionSelectorBox.SelectedItem = (_instances ?? new[] { _instance }).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _instance.Id, StringComparison.Ordinal));
        _instanceSelectorReady = true;
        await SynchronizeAsync();
        await RefreshAsync();
        await RefreshBackupsAsync(updateStatus: false);
        await RefreshModelConfigurationAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await SynchronizeAsync();
        await RefreshAsync();
    }

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
            var result = await Task.Run(() => (
                Storage: _service.GetStorageInfo(_instance),
                Entries: _service.List(_instance)));
            var entries = result.Entries;
            Entries.Clear();
            foreach (var entry in entries) Entries.Add(entry);
            UpdateStorageNotice(result.Storage);
            ApplyConversationFilter();
            if (selectedPath is not null)
            {
                ConversationList.SelectedItem = Entries.FirstOrDefault(entry =>
                    string.Equals(entry.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            }

            StatusText.Text = result.Storage.Kind == ConversationStorageKind.Sqlite
                ? "当前版本的对话由 SQLite 统一会话库管理；Launcher 没有把它误报为缺失的 JSONL 文件。"
                : $"已读取 {ConversationList.Items.Count} / {Entries.Count} 个当前版本对话文件。压缩 session.jsonl.zstd 可查看、打开和导入。";
            UpdateSelection();
            if (_modelChoices.Count > 0)
            {
                RefreshWorkspaceChoices();
                RefreshSessionModelRows();
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void UpdateStorageNotice(ConversationStorageInfo storage)
    {
        var sqliteOnly = storage.Kind == ConversationStorageKind.Sqlite;
        ImportButton.IsEnabled = storage.SupportsJsonlImport;
        RestoreBackupButton.IsEnabled = storage.SupportsJsonlImport;
        StorageNoticePanel.Visibility = storage.Kind == ConversationStorageKind.Jsonl
            ? Visibility.Collapsed
            : Visibility.Visible;
        StorageNoticeText.Text = sqliteOnly
            ? "当前版本配置了 SQLite 会话存储。所有对话共用数据库，不存在可单独复制的 session.jsonl；请在 DSh 窗口中查看和管理。Launcher 已停用会写入无效 sessions 目录的导入与恢复操作。"
            : "当前版本同时检测到 SQLite 会话存储和旧 JSONL 文件。下方仍会列出可读取的 JSONL 文件；SQLite 中的对话请在 DSh 窗口中管理。";
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

    private async Task RefreshModelConfigurationAsync()
    {
        if (_modelPolicyService is null || _modelOptionsProvider is null)
        {
            WorkspaceModelBox.IsEnabled = false;
            SessionModelBox.IsEnabled = false;
            return;
        }

        try
        {
            var options = await _modelOptionsProvider();
            _modelChoices = new[] { new ModelChoice("自动继承", null) }
                .Concat(options.Select(option => new ModelChoice(option.DisplayText, option)))
                .ToArray();
            WorkspaceModelBox.ItemsSource = _modelChoices;
            SessionModelBox.ItemsSource = _modelChoices;
            RefreshWorkspaceChoices();
            SelectWorkspacePolicy();
            RefreshSessionModelRows();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取模型配置失败：{ex.Message}";
        }
    }

    private void RefreshWorkspaceChoices()
    {
        var selectedWorkspace = DshWorkspaceBox.SelectedItem as string;
        var workspaces = Entries
            .Select(entry => entry.WorkingDirectory)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => directory!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DshWorkspaceBox.ItemsSource = workspaces;
        DshWorkspaceBox.SelectedItem = selectedWorkspace is not null
            && workspaces.Contains(selectedWorkspace, StringComparer.OrdinalIgnoreCase)
                ? workspaces.First(directory =>
                    string.Equals(directory, selectedWorkspace, StringComparison.OrdinalIgnoreCase))
                : workspaces.FirstOrDefault();
    }

    private void DshWorkspace_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SelectWorkspacePolicy();

    private void SelectWorkspacePolicy()
    {
        if (_modelPolicyService is null || DshWorkspaceBox.SelectedItem is not string workspace)
        {
            WorkspaceModelBox.SelectedItem = _modelChoices.FirstOrDefault();
            return;
        }

        try
        {
            var selection = _modelPolicyService.ReadWorkspaceSelection(workspace);
            WorkspaceModelBox.SelectedItem = FindChoice(selection);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取 DSh 工作区模型失败：{ex.Message}";
        }
    }

    private void SessionModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_modelPolicyService is null
            || SessionModelList.SelectedItem is not ConversationModelEntry item
            || item.Conversation.SessionId is null)
        {
            SessionModelBox.SelectedItem = _modelChoices.FirstOrDefault();
            return;
        }

        try
        {
            var selection = _modelPolicyService.ReadSessionSelection(
                _instance.Id,
                item.Conversation.SessionId);
            SessionModelBox.SelectedItem = FindChoice(selection);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取单独对话模型失败：{ex.Message}";
        }
    }

    private void SaveWorkspaceModel_Click(object sender, RoutedEventArgs e)
    {
        if (_modelPolicyService is null
            || DshWorkspaceBox.SelectedItem is not string workspace
            || WorkspaceModelBox.SelectedItem is not ModelChoice choice)
        {
            StatusText.Text = "请先选择 DSh 工作区和模型。";
            return;
        }

        try
        {
            _modelPolicyService.SetWorkspaceSelection(workspace, choice.Option?.Selection);
            SelectWorkspacePolicy();
            RefreshSessionModelRows();
            StatusText.Text = choice.Option is null
                ? $"DSh 工作区已改为继承全局默认：{workspace}"
                : $"DSh 工作区默认模型已保存：{workspace} → {choice.Option.Selection.DisplayText}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存 DSh 工作区模型失败：{ex.Message}";
        }
    }

    private void SaveSessionModel_Click(object sender, RoutedEventArgs e)
    {
        if (_modelPolicyService is null
            || SessionModelList.SelectedItem is not ConversationModelEntry item
            || item.Conversation.SessionId is null
            || SessionModelBox.SelectedItem is not ModelChoice choice)
        {
            StatusText.Text = "请先选择一个有效对话和模型。";
            return;
        }

        try
        {
            _modelPolicyService.SetSessionSelection(
                _instance.Id,
                item.Conversation.SessionId,
                choice.Option?.Selection);
            RefreshSessionModelRows(item.Conversation.FullPath);
            StatusText.Text = choice.Option is null
                ? $"对话已改为自动继承：{item.Conversation.DisplayName}"
                : $"单独对话模型已保存：{item.Conversation.DisplayName} → {choice.Option.Selection.DisplayText}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存单独对话模型失败：{ex.Message}";
        }
    }

    private void RefreshSessionModelRows(string? selectedPath = null)
    {
        if (_modelPolicyService is null)
        {
            SessionModelList.ItemsSource = Array.Empty<ConversationModelEntry>();
            return;
        }

        selectedPath ??= (SessionModelList.SelectedItem as ConversationModelEntry)?.Conversation.FullPath;
        var data = _modelPolicyService.Read();
        var rows = Entries
            .Where(entry => entry.SessionId is not null)
            .Select(entry => new ConversationModelEntry
            {
                Conversation = entry,
                PolicyText = BuildPolicyText(data, entry)
            })
            .ToArray();
        SessionModelList.ItemsSource = rows;
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            SessionModelList.SelectedItem = rows.FirstOrDefault(row =>
                string.Equals(row.Conversation.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    private string BuildPolicyText(CodingModelPolicyData data, ConversationEntry entry)
    {
        var session = data.Sessions.FirstOrDefault(item =>
            string.Equals(item.InstanceId, _instance.Id, StringComparison.Ordinal)
            && string.Equals(item.SessionId, entry.SessionId, StringComparison.Ordinal));
        if (session is not null)
        {
            return $"单独对话 · {session.Selection.DisplayText}";
        }

        if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory))
        {
            var workspace = data.DshWorkspaces.FirstOrDefault(item =>
                string.Equals(
                    item.WorkingDirectory,
                    NormalizeWorkspaceForComparison(entry.WorkingDirectory),
                    StringComparison.OrdinalIgnoreCase));
            if (workspace is not null)
            {
                return $"DSh 工作区 · {workspace.Selection.DisplayText}";
            }
        }

        return data.GlobalDefault is null
            ? "自动 · DSh 当前默认"
            : $"全局默认 · {data.GlobalDefault.DisplayText}";
    }

    private ModelChoice? FindChoice(CodingModelSelection? selection) =>
        selection is null
            ? _modelChoices.FirstOrDefault()
            : _modelChoices.FirstOrDefault(choice => choice.Option?.Key == selection.Key)
                ?? new ModelChoice(selection.DisplayText, new CodingModelOption(
                    selection.Provider,
                    selection.Provider,
                    selection.Model,
                    selection.Model,
                    selection.ReasoningEffort,
                    selection.ReasoningEffort));

    private static string NormalizeWorkspaceForComparison(string workingDirectory)
    {
        try
        {
            return CodingModelPolicyService.NormalizeWorkingDirectory(workingDirectory);
        }
        catch (ArgumentException)
        {
            return workingDirectory.Trim();
        }
    }

    private sealed record ModelChoice(string DisplayText, CodingModelOption? Option);

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
            if (!await _openConversation(entry))
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

        if (System.Windows.MessageBox.Show(
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

        if (System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                $"确定删除会话文件“{entry.RelativePath}”？此操作不可由 Launcher 撤销。",
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
        System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, "对话操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
