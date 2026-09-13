using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
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
    private IReadOnlySet<string>? _searchMatches;
    private CancellationTokenSource _pageCancellation = new();
    private CancellationTokenSource? _conversationRefreshCancellation;
    private CancellationTokenSource? _backupRefreshCancellation;
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<Button> _loadingSensitiveButtons = Array.Empty<Button>();
    private string? _selectedConversationPath;
    private string? _selectedBackupPath;
    private ConversationStorageInfo? _storageInfo;
    private int _entriesVersion;
    private long _conversationRefreshGeneration;
    private long _backupRefreshGeneration;
    private long _searchGeneration;
    private Task? _synchronizationTask;
    private bool _pageActive;
    private bool _conversationLoading;
    private bool _backupLoading;
    private bool _synchronizing;
    private bool _modelConfigurationLoading;
    private bool _actionInProgress;
    private bool _suppressSelectionTracking;
    private bool _searchInProgress;
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
        Unloaded += Window_OnUnloaded;
    }

    private ObservableCollection<ConversationEntry> Entries { get; } = new();

    private ObservableCollection<ConversationBackupEntry> Backups { get; } = new();

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_pageActive)
        {
            return;
        }

        if (_pageCancellation.IsCancellationRequested)
        {
            _pageCancellation = new CancellationTokenSource();
        }

        _pageActive = true;
        _loadingSensitiveButtons = FindVisualChildren<Button>(this)
            .Where(IsLoadingSensitiveButton)
            .ToArray();
        UpdateLoadingUi();

        VersionSelectorBox.ItemsSource = _instances ?? new[] { _instance };
        VersionSelectorBox.SelectedItem = (_instances ?? new[] { _instance }).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _instance.Id, StringComparison.Ordinal));
        _instanceSelectorReady = true;
        await SynchronizeAsync();
        if (!_pageActive)
        {
            return;
        }

        await RefreshAsync();
        if (!_pageActive)
        {
            return;
        }

        await RefreshBackupsAsync(updateStatus: false);
        if (!_pageActive)
        {
            return;
        }

        await RefreshModelConfigurationAsync();
    }

    private void Window_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pageActive = false;
        _pageCancellation.Cancel();
        CancelAndDispose(ref _conversationRefreshCancellation);
        CancelAndDispose(ref _backupRefreshCancellation);
        CancelAndDispose(ref _searchCancellation);
        _conversationRefreshGeneration++;
        _backupRefreshGeneration++;
        _searchGeneration++;
        _conversationLoading = false;
        _backupLoading = false;
        _synchronizing = false;
        _modelConfigurationLoading = false;
        _actionInProgress = false;
        _searchInProgress = false;
        UpdateLoadingUi();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartExternalOperation())
        {
            return;
        }

        await SynchronizeAsync();
        await RefreshAsync();
    }

    private async Task SynchronizeAsync(bool internalOperation = false)
    {
        if (_synchronizeConversations is null
            || !_pageActive
            || (!internalOperation && IsBusy)
            )
        {
            return;
        }

        if (_synchronizationTask is { IsCompleted: false } existing)
        {
            await existing;
            return;
        }

        _synchronizing = true;
        UpdateLoadingUi();
        var task = SynchronizeCoreAsync();
        _synchronizationTask = task;
        await task;
    }

    private async Task SynchronizeCoreAsync()
    {
        try
        {
            if (_pageActive)
            {
                await _synchronizeConversations!();
            }
        }
        catch (OperationCanceledException) when (!_pageActive || _pageCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_pageActive)
            {
                ShowError(ex);
            }
        }
        finally
        {
            if (_pageActive)
            {
                _synchronizing = false;
                UpdateLoadingUi();
            }
        }
    }

    private async Task RefreshAsync(bool internalOperation = false)
    {
        if (!_pageActive
            || _conversationLoading
            || (!internalOperation && IsBusy))
        {
            return;
        }

        var operationGeneration = ++_conversationRefreshGeneration;
        CancelAndDispose(ref _conversationRefreshCancellation);
        _conversationRefreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation.Token);
        var operationToken = _conversationRefreshCancellation.Token;
        _conversationLoading = true;
        CancelAndDispose(ref _searchCancellation);
        _searchInProgress = false;
        UpdateLoadingUi();
        try
        {
            var result = await Task.Run(() => (
                Storage: _service.GetStorageInfo(_instance),
                Entries: _service.List(_instance)), operationToken);
            if (!IsCurrentConversationRefresh(operationGeneration, operationToken))
            {
                return;
            }

            _storageInfo = result.Storage;
            var selectedPath = _selectedConversationPath;
            Entries.Clear();
            foreach (var entry in result.Entries) Entries.Add(entry);
            _entriesVersion++;
            UpdateStorageNotice(result.Storage);
            ApplyConversationFilter(selectedPath);

            StatusText.Text = result.Storage.Kind == ConversationStorageKind.Sqlite
                ? "当前版本的对话由 SQLite 统一会话库管理；Launcher 没有把它误报为缺失的 JSONL 文件。"
                : $"已读取 {ConversationList.Items.Count} / {Entries.Count} 个当前版本对话文件。支持 session.jsonl、session.vN.jsonl 及其 .zstd 压缩格式；同一会话只显示最高代际。";
            UpdateSelection();
            if (_modelChoices.Count > 0)
            {
                RefreshWorkspaceChoices();
                RefreshSessionModelRows();
            }
        }
        catch (OperationCanceledException) when (!_pageActive || operationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentConversationRefresh(operationGeneration, operationToken))
            {
                ShowError(ex);
            }
        }
        finally
        {
            if (IsCurrentConversationRefresh(operationGeneration, operationToken))
            {
                _conversationLoading = false;
                UpdateLoadingUi();
            }
        }
    }

    private bool IsBusy => _conversationLoading
        || _backupLoading
        || _synchronizing
        || _modelConfigurationLoading
        || _actionInProgress
        || _searchInProgress;

    private bool CanStartExternalOperation() => _pageActive && !IsBusy;

    private bool IsCurrentConversationRefresh(long generation, CancellationToken token) =>
        IsCurrentLoad(generation, _conversationRefreshGeneration, _pageActive, token);

    private bool IsCurrentBackupRefresh(long generation, CancellationToken token) =>
        IsCurrentLoad(generation, _backupRefreshGeneration, _pageActive, token);

    private bool IsCurrentSearch(long generation, CancellationToken token) =>
        IsCurrentLoad(generation, _searchGeneration, _pageActive, token);

    internal static bool IsCurrentLoad(
        long requestedGeneration,
        long currentGeneration,
        bool pageActive,
        CancellationToken cancellationToken) =>
        pageActive
        && requestedGeneration == currentGeneration
        && !cancellationToken.IsCancellationRequested;

    internal static string? FindSelectionPath(
        IEnumerable<string> availablePaths,
        string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return null;
        }

        return availablePaths.FirstOrDefault(path =>
            string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        source.Cancel();
        source.Dispose();
        source = null;
    }

    private bool TryBeginAction()
    {
        if (!CanStartExternalOperation())
        {
            return false;
        }

        _actionInProgress = true;
        UpdateLoadingUi();
        return true;
    }

    private void EndAction()
    {
        _actionInProgress = false;
        if (_pageActive)
        {
            UpdateLoadingUi();
        }
    }

    private void UpdateLoadingUi()
    {
        var enabled = _pageActive && !IsBusy;
        foreach (var button in _loadingSensitiveButtons)
        {
            button.IsEnabled = enabled;
        }

        VersionSelectorBox.IsEnabled = enabled;
        ConversationList.IsEnabled = enabled;
        BackupList.IsEnabled = enabled;
        ConversationSearchBox.IsEnabled = enabled;

        var supportsJsonl = _storageInfo?.SupportsJsonlImport ?? true;
        ImportButton.IsEnabled = enabled && supportsJsonl;
        RestoreBackupButton.IsEnabled = enabled && supportsJsonl;
    }

    private static bool IsLoadingSensitiveButton(Button button) => button.Content is string text
        && text is "刷新"
            or "打开选中对话"
            or "导入对话文件"
            or "导出选中对话"
            or "备份选中对话"
            or "删除选中对话"
            or "刷新备份"
            or "恢复选中备份"
            or "搜索正文"
            or "清空"
            or "保存工作区模型"
            or "保存单独对话模型";

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void UpdateStorageNotice(ConversationStorageInfo storage)
    {
        _storageInfo = storage;
        var sqliteOnly = storage.Kind == ConversationStorageKind.Sqlite;
        StorageNoticePanel.Visibility = storage.Kind == ConversationStorageKind.Jsonl
            ? Visibility.Collapsed
            : Visibility.Visible;
        StorageNoticeText.Text = sqliteOnly
            ? "当前版本配置了 SQLite 会话存储。所有对话共用数据库，不存在可单独复制的 session.jsonl；请在 DSh 窗口中查看和管理。Launcher 已停用会写入无效 sessions 目录的导入与恢复操作。"
            : "当前版本同时检测到 SQLite 会话存储和旧 JSONL 文件。下方仍会列出可读取的 JSONL 文件；SQLite 中的对话请在 DSh 窗口中管理。";
    }

    private void VersionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_pageActive
            || !_instanceSelectorReady
            || IsBusy
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

    private void ConversationTime_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyConversationFilter();
        }
    }

    private async void ConversationSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await SearchConversationsAsync();
    }

    private async void SearchConversations_Click(object sender, RoutedEventArgs e) =>
        await SearchConversationsAsync();

    private async Task SearchConversationsAsync()
    {
        if (!CanStartExternalOperation() || _searchInProgress)
        {
            return;
        }

        var query = ConversationSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            _searchMatches = null;
            ApplyConversationFilter();
            StatusText.Text = $"显示 {ConversationList.Items.Count} / {Entries.Count} 个当前版本对话文件。";
            return;
        }

        var operationGeneration = ++_searchGeneration;
        CancelAndDispose(ref _searchCancellation);
        _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation.Token);
        var operationToken = _searchCancellation.Token;
        var entriesVersion = _entriesVersion;
        var entriesSnapshot = Entries.ToArray();
        _searchInProgress = true;
        UpdateLoadingUi();
        StatusText.Text = $"正在搜索 {Entries.Count} 个对话的标题、工作区和正文…";
        try
        {
            var matches = await Task.Run(() =>
                _service.Search(entriesSnapshot, query), operationToken);
            if (!IsCurrentSearch(operationGeneration, operationToken)
                || entriesVersion != _entriesVersion)
            {
                return;
            }

            _searchMatches = matches
                .Select(static entry => entry.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ApplyConversationFilter();
            StatusText.Text = $"“{query}”找到 {ConversationList.Items.Count} 个结果。";
        }
        catch (OperationCanceledException) when (!_pageActive || operationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentSearch(operationGeneration, operationToken))
            {
                ShowError(ex);
            }
        }
        finally
        {
            if (IsCurrentSearch(operationGeneration, operationToken))
            {
                _searchInProgress = false;
                UpdateLoadingUi();
                ConversationSearchBox.Focus();
            }
        }
    }

    private void ClearConversationSearch_Click(object sender, RoutedEventArgs e)
    {
        ConversationSearchBox.Clear();
        _searchMatches = null;
        ApplyConversationFilter();
        StatusText.Text = $"显示 {ConversationList.Items.Count} / {Entries.Count} 个当前版本对话文件。";
    }

    private void ApplyConversationFilter(string? preferredPath = null)
    {
        preferredPath ??= _selectedConversationPath;
        var scope = (ConversationScopeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        IEnumerable<ConversationEntry> filtered = scope switch
        {
            "Isolated" => Entries.Where(static entry => string.IsNullOrWhiteSpace(entry.WorkingDirectory)).ToArray(),
            "Workspace" => Entries.Where(static entry => !string.IsNullOrWhiteSpace(entry.WorkingDirectory)).ToArray(),
            _ => Entries.ToArray()
        };

        var timeTag = (ConversationTimeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        if (int.TryParse(timeTag, out var days) && days > 0)
        {
            var threshold = DateTimeOffset.UtcNow.AddDays(-days);
            filtered = filtered.Where(entry => entry.UpdatedAt >= threshold);
        }

        if (_searchMatches is not null)
        {
            filtered = filtered.Where(entry => _searchMatches.Contains(entry.FullPath));
        }

        var snapshot = filtered.ToArray();
        _suppressSelectionTracking = true;
        try
        {
            ConversationList.ItemsSource = snapshot;
            var selectedPath = FindSelectionPath(snapshot.Select(static entry => entry.FullPath), preferredPath);
            ConversationList.SelectedItem = selectedPath is null
                ? null
                : snapshot.FirstOrDefault(entry =>
                    string.Equals(entry.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressSelectionTracking = false;
        }

        _selectedConversationPath = (ConversationList.SelectedItem as ConversationEntry)?.FullPath;
    }

    private async Task RefreshModelConfigurationAsync()
    {
        if (_modelPolicyService is null || _modelOptionsProvider is null)
        {
            WorkspaceModelBox.IsEnabled = false;
            SessionModelBox.IsEnabled = false;
            return;
        }

        if (!_pageActive)
        {
            return;
        }

        _modelConfigurationLoading = true;
        UpdateLoadingUi();
        try
        {
            var options = await _modelOptionsProvider();
            if (!_pageActive)
            {
                return;
            }

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
            if (_pageActive)
            {
                StatusText.Text = $"读取模型配置失败：{ex.Message}";
            }
        }
        finally
        {
            if (_pageActive)
            {
                _modelConfigurationLoading = false;
                UpdateLoadingUi();
            }
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

    private async Task RefreshBackupsAsync(bool updateStatus = true, bool internalOperation = false)
    {
        if (!_pageActive
            || _backupLoading
            || (!internalOperation && IsBusy))
        {
            return;
        }

        var operationGeneration = ++_backupRefreshGeneration;
        CancelAndDispose(ref _backupRefreshCancellation);
        _backupRefreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation.Token);
        var operationToken = _backupRefreshCancellation.Token;
        _backupLoading = true;
        UpdateLoadingUi();
        try
        {
            var backups = await Task.Run(() => _service.ListBackups(_instance), operationToken);
            if (!IsCurrentBackupRefresh(operationGeneration, operationToken))
            {
                return;
            }

            var preservedPath = _selectedBackupPath;
            Backups.Clear();
            foreach (var backup in backups) Backups.Add(backup);
            _suppressSelectionTracking = true;
            try
            {
                BackupList.ItemsSource = Backups;
                var selectedPath = FindSelectionPath(
                    Backups.Select(static backup => backup.FullPath),
                    preservedPath);
                BackupList.SelectedItem = selectedPath is null
                    ? null
                    : Backups.FirstOrDefault(backup =>
                        string.Equals(backup.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _suppressSelectionTracking = false;
            }
            _selectedBackupPath = (BackupList.SelectedItem as ConversationBackupEntry)?.FullPath;

            if (updateStatus)
            {
                StatusText.Text = $"已读取 {Backups.Count} 个对话备份。";
            }
        }
        catch (OperationCanceledException) when (!_pageActive || operationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentBackupRefresh(operationGeneration, operationToken))
            {
                ShowError(ex);
            }
        }
        finally
        {
            if (IsCurrentBackupRefresh(operationGeneration, operationToken))
            {
                _backupLoading = false;
                UpdateLoadingUi();
            }
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

        if (!TryBeginAction())
        {
            return;
        }

        try
        {
            var check = await Task.Run(() =>
            {
                var allowed = _service.CanOpen(_instance, entry, out var reason);
                return (Allowed: allowed, Reason: reason);
            }, _pageCancellation.Token);
            if (!_pageActive || _pageCancellation.IsCancellationRequested) return;
            if (!check.Allowed)
            {
                StatusText.Text = $"无法打开：{check.Reason}";
                return;
            }
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
            if (_pageActive)
            {
                ShowError(ex);
            }
        }
        finally
        {
            EndAction();
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginAction())
        {
            return;
        }

        try
        {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "导入 DSh 对话文件（JSONL / Zstandard）",
            Filter = "DSh session (*.jsonl;*.jsonl.zstd)|*.jsonl;*.jsonl.zstd|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        var targetInstance = _instance;
        string? workspaceOverride = null;
        if (_instances is { Count: > 0 })
        {
            var choice = await ShowImportTargetDialogAsync();
            if (choice is null)
            {
                StatusText.Text = "已取消导入。";
                return;
            }

            (targetInstance, workspaceOverride) = choice.Value;
        }

        var target = await Task.Run(
            () => _service.Import(targetInstance, dialog.FileName, workspaceOverride),
            _pageCancellation.Token);
        await SynchronizeAsync(internalOperation: true);
        if (!_pageActive)
        {
            return;
        }

        StatusText.Text = $"对话已导入到 {targetInstance.Name}：{target}";
        await RefreshAsync(internalOperation: true);
        }
        catch (OperationCanceledException) when (!_pageActive || _pageCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_pageActive) ShowError(ex);
        }
        finally
        {
            EndAction();
        }
    }

    private const string ImportWorkspaceAuto = "（按文件自带工作目录）";

    private async Task<(ManagerInstance Instance, string? Workspace)?> ShowImportTargetDialogAsync()
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
        var confirmButton = new Button
        {
            Content = "导入",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(16, 8, 16, 8),
            MinWidth = 90
        };

        Window dialog = null!;
        var dialogClosed = false;
        var workspaceLoadGeneration = 0;

        async Task LoadWorkspacesAsync(ManagerInstance selected)
        {
            var generation = ++workspaceLoadGeneration;
            var token = _pageCancellation.Token;
            // A new target must never keep the previous instance's workspace.
            workspaceBox.ItemsSource = new[] { ImportWorkspaceAuto };
            workspaceBox.SelectedIndex = 0;
            workspaceBox.IsEnabled = false;
            confirmButton.IsEnabled = false;
            try
            {
                var workspaces = await Task.Run(() => _service.List(selected), token);
                if (!IsCurrentLoad(generation, workspaceLoadGeneration, _pageActive && !dialogClosed, token))
                {
                    return;
                }

                var workspaceNames = workspaces
                    .Select(entry => entry.WorkingDirectory)
                    .Where(directory => !string.IsNullOrWhiteSpace(directory))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                workspaceBox.ItemsSource = new[] { ImportWorkspaceAuto }.Concat(workspaceNames).ToArray();
            }
            catch (OperationCanceledException) when (!_pageActive || token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Current failures keep the automatic destination; stale failures
                // are discarded just like stale successful snapshots.
            }
            finally
            {
                if (IsCurrentLoad(generation, workspaceLoadGeneration, _pageActive && !dialogClosed, token))
                {
                    workspaceBox.SelectedIndex = 0;
                    workspaceBox.IsEnabled = true;
                    confirmButton.IsEnabled = true;
                }
            }
        }

        await LoadWorkspacesAsync(instances[Math.Max(0, versionBox.SelectedIndex)]);
        if (!_pageActive)
        {
            return null;
        }

        versionBox.SelectionChanged += async (_, _) =>
        {
            if (versionBox.SelectedItem is ManagerInstance selected)
            {
                await LoadWorkspacesAsync(selected);
            }
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

        dialog = new Window
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
        dialog.Closed += (_, _) =>
        {
            dialogClosed = true;
            workspaceLoadGeneration++;
        };

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

        if (!TryBeginAction())
        {
            return;
        }

        try
        {
            var target = await Task.Run(
                () => _service.Export(_instance, entry, dialog.FileName),
                _pageCancellation.Token);
            if (_pageActive)
            {
                StatusText.Text = $"对话已导出：{target}";
            }
        }
        catch (OperationCanceledException) when (!_pageActive || _pageCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_pageActive) ShowError(ex);
        }
        finally
        {
            EndAction();
        }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationEntry entry)
        {
            StatusText.Text = "请先选择一个对话。";
            return;
        }

        if (!TryBeginAction())
        {
            return;
        }

        try
        {
            var target = await Task.Run(() => _service.Backup(_instance, entry), _pageCancellation.Token);
            await RefreshBackupsAsync(updateStatus: false, internalOperation: true);
            if (_pageActive)
            {
                StatusText.Text = $"对话已备份：{target}";
            }
        }
        catch (OperationCanceledException) when (!_pageActive || _pageCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_pageActive) ShowError(ex);
        }
        finally
        {
            EndAction();
        }
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

        if (!TryBeginAction())
        {
            return;
        }

        try
        {
            var target = await Task.Run(
                () => _service.RestoreBackup(_instance, backup),
                _pageCancellation.Token);
            await SynchronizeAsync(internalOperation: true);
            await RefreshAsync(internalOperation: true);
            await RefreshBackupsAsync(updateStatus: false, internalOperation: true);
            if (_pageActive)
            {
                StatusText.Text = $"对话已恢复：{target}";
            }
        }
        catch (OperationCanceledException) when (!_pageActive || _pageCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_pageActive) ShowError(ex);
        }
        finally
        {
            EndAction();
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
                $"确定删除会话文件“{entry.RelativePath}”？此操作不可由 Launcher 撤销。\n\n只删除选中的代际文件；同目录保留的旧代际文件可能在刷新后重新显示。",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!TryBeginAction())
        {
            return;
        }

        try
        {
            await Task.Run(() => _service.Delete(_instance, entry), _pageCancellation.Token);
            if (_propagateDeletion is not null)
            {
                await _propagateDeletion(entry.RelativePath);
            }
            else
            {
                await SynchronizeAsync(internalOperation: true);
            }
            if (_pageActive)
            {
                StatusText.Text = "对话文件已删除。";
            }
            await RefreshAsync(internalOperation: true);
        }
        catch (OperationCanceledException) when (!_pageActive || _pageCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_pageActive) ShowError(ex);
        }
        finally
        {
            EndAction();
        }
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSelectionTracking)
        {
            _selectedConversationPath = (ConversationList.SelectedItem as ConversationEntry)?.FullPath;
        }

        UpdateSelection();
    }

    private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSelectionTracking)
        {
            _selectedBackupPath = (BackupList.SelectedItem as ConversationBackupEntry)?.FullPath;
        }

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
