using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using DshLauncher.Models;
using DshLauncher.Services;
using Forms = System.Windows.Forms;

namespace DshLauncher;

public partial class ConversationWindow : UserControl
{
    private readonly ManagerInstance _instance;
    private readonly ConversationService _service;
    private readonly Func<ConversationEntry, bool> _openConversation;

    public ConversationWindow(
        ManagerInstance instance,
        ConversationService service,
        Func<ConversationEntry, bool> openConversation)
    {
        _instance = instance;
        _service = service;
        _openConversation = openConversation;
        InitializeComponent();
        InstanceText.Text = $"当前实例：{instance.Name} · 会话目录：{Path.Combine(instance.DshHome, "sessions")}";
    }

    private ObservableCollection<ConversationEntry> Entries { get; } = new();

    private async void Window_OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

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

            StatusText.Text = $"已读取 {Entries.Count} 个对话文件。压缩 session.jsonl.zstd 可查看，但当前不能导入。";
            UpdateSelection();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationEntry entry)
        {
            StatusText.Text = "请先选择一个对话。";
            return;
        }

        try
        {
            if (!_openConversation(entry))
            {
                StatusText.Text = "当前实例没有运行，或没有可用的 Chat 地址；请先启动实例。";
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
            Filter = "DSh session (*.jsonl)|*.jsonl|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        try
        {
            var target = await Task.Run(() => _service.Import(_instance, dialog.FileName));
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
            FileName = Path.GetFileName(entry.FullPath),
            OverwritePrompt = true,
            AddExtension = false
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
            StatusText.Text = "对话文件已删除。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (ConversationList.SelectedItem is not ConversationEntry entry)
        {
            return;
        }

        StatusText.Text = entry.HasValidHeader
            ? $"已选择 {entry.SessionId} · {entry.RelativePath}"
            : "已选择压缩或无法读取头部的会话文件；可导出/备份，打开前需有可识别的会话 ID。";
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, "对话操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
