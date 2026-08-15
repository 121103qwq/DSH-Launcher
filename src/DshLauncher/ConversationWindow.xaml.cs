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

    public ConversationWindow(
        ManagerInstance instance,
        ConversationService service,
        Func<ConversationEntry, Task<bool>> openConversation,
        Func<Task>? synchronizeConversations = null,
        Func<string, Task>? propagateDeletion = null)
    {
        _instance = instance;
        _service = service;
        _openConversation = openConversation;
        _synchronizeConversations = synchronizeConversations;
        _propagateDeletion = propagateDeletion;
        InitializeComponent();
    }

    private ObservableCollection<ConversationEntry> Entries { get; } = new();

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        await SynchronizeAsync();
        await RefreshAsync();
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
            var entries = await Task.Run(() => _service.List(_instance));
            Entries.Clear();
            foreach (var entry in entries) Entries.Add(entry);
            ConversationList.ItemsSource = Entries;
            if (selectedPath is not null)
            {
                ConversationList.SelectedItem = Entries.FirstOrDefault(entry =>
                    string.Equals(entry.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            }

            StatusText.Text = $"已读取 {Entries.Count} 个对话文件。压缩 session.jsonl.zstd 可查看、打开和导入，导入时保留原始格式。";
            UpdateSelection();
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

        try
        {
            var target = await Task.Run(() => _service.Import(_instance, dialog.FileName));
            await SynchronizeAsync();
            StatusText.Text = $"对话已导入：{target}";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
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
            StatusText.Text = $"对话已备份：{target}";
        }
        catch (Exception ex) { ShowError(ex); }
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

    private static string ExportFileName(ConversationEntry entry)
    {
        var fileName = Path.GetFileName(entry.FullPath);
        var extension = entry.IsCompressed ? ".jsonl.zstd" : ".jsonl";
        return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^extension.Length]
            : fileName;
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
