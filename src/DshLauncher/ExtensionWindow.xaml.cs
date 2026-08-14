using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DshLauncher.Models;
using DshLauncher.Services;
using Forms = System.Windows.Forms;

namespace DshLauncher;

public partial class ExtensionWindow : Window
{
    private readonly ManagerInstance _instance;
    private readonly ExtensionService _service;
    private readonly Func<NodeRuntimeInfo?> _nodeRuntime;

    public ExtensionWindow(
        ManagerInstance instance,
        ExtensionService service,
        Func<NodeRuntimeInfo?> nodeRuntime)
    {
        _instance = instance;
        _service = service;
        _nodeRuntime = nodeRuntime;
        InitializeComponent();
        InstanceText.Text = $"当前实例：{instance.Name} · {_instance.DshHome}";
    }

    private ObservableCollection<ExtensionEntry> Entries { get; } = new();

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            var selectedId = (ExtensionList.SelectedItem as ExtensionEntry)?.Id;
            var entries = await _service.ListAsync(_instance);
            Entries.Clear();
            foreach (var entry in entries) Entries.Add(entry);
            ExtensionList.ItemsSource = Entries;
            if (selectedId is not null)
            {
                ExtensionList.SelectedItem = Entries.FirstOrDefault(entry => entry.Id == selectedId);
            }
            StatusText.Text = $"已读取 {Entries.Count} 个 Plugin / Skill / MCP / Workflow / Preset。";
            UpdateSelection();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void InstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        var source = TextPromptWindow.Show(this, "安装 Plugin", "输入 npm 包名、Git 仓库或本地路径：");
        if (string.IsNullOrWhiteSpace(source)) return;
        try
        {
            var output = await _service.InstallPluginAsync(_instance, source, _nodeRuntime());
            StatusText.Text = string.IsNullOrWhiteSpace(output) ? "Plugin 安装完成。" : $"Plugin 安装完成：{output}";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ImportSkill_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择包含 SKILL.md 的 Skill 目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            var entry = await _service.ImportSkillAsync(_instance, dialog.SelectedPath);
            StatusText.Text = $"Skill 已导入：{entry.Name}。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void AddMcp_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Show(this, "添加 MCP", "输入 serverName（仅字母、数字、-、_）：");
        if (string.IsNullOrWhiteSpace(name)) return;
        var transport = TextPromptWindow.Show(this, "添加 MCP", "输入 transport：stdio 或 streamable-http", "stdio");
        if (string.IsNullOrWhiteSpace(transport)) return;
        var commandOrUrl = TextPromptWindow.Show(this, "添加 MCP", transport == "stdio" ? "输入 MCP command：" : "输入 MCP URL：");
        if (string.IsNullOrWhiteSpace(commandOrUrl)) return;
        var arguments = Array.Empty<string>();
        string? workingDirectory = null;
        string? url = null;
        if (transport == "stdio")
        {
            var rawArguments = TextPromptWindow.Show(this, "添加 MCP", "输入参数（用 | 分隔，可留空）：") ?? string.Empty;
            arguments = rawArguments.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            workingDirectory = TextPromptWindow.Show(this, "添加 MCP", "输入工作目录（可留空）：");
        }
        else
        {
            url = commandOrUrl;
        }

        try
        {
            await _service.AddMcpAsync(
                _instance,
                new McpServerDefinition(name, transport, transport == "stdio" ? commandOrUrl : string.Empty, arguments, url, new Dictionary<string, string>(), workingDirectory),
                _nodeRuntime());
            StatusText.Text = $"MCP 已添加：{name}。下次启动实例时加载。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ImportPreset_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择包含 agent.cordis.yml 的 Agent Preset 目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            var entry = await _service.ImportPresetAsync(_instance, dialog.SelectedPath);
            StatusText.Text = $"Agent Preset 已导入：{entry.Name}。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Enable_Click(object sender, RoutedEventArgs e) => await ToggleSelectedAsync(true);

    private async void Disable_Click(object sender, RoutedEventArgs e) => await ToggleSelectedAsync(false);

    private async Task ToggleSelectedAsync(bool enabled)
    {
        if (ExtensionList.SelectedItem is not ExtensionEntry entry) return;
        try
        {
            if (entry.Kind == ExtensionKind.Plugin)
            {
                await _service.SetPluginEnabledAsync(_instance, entry, enabled);
            }
            else if (entry.Kind == ExtensionKind.Mcp)
            {
                await _service.SetMcpEnabledAsync(_instance, entry.Name, enabled);
            }
            else
            {
                throw new InvalidOperationException("当前条目不支持独立启用/禁用。");
            }

            StatusText.Text = $"已{(enabled ? "启用" : "禁用")}：{entry.Name}。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (ExtensionList.SelectedItem is not ExtensionEntry entry || entry.Kind != ExtensionKind.Plugin || !entry.Managed) return;
        try
        {
            var output = await _service.UpdatePluginAsync(_instance, entry.Name, _nodeRuntime());
            StatusText.Text = string.IsNullOrWhiteSpace(output) ? "Plugin 更新完成。" : $"Plugin 更新完成：{output}";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ExtensionList.SelectedItem is not ExtensionEntry entry) return;
        if (System.Windows.MessageBox.Show(this, $"确定删除“{entry.Name}”？该操作只针对当前实例。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            switch (entry.Kind)
            {
                case ExtensionKind.Plugin when entry.Managed:
                    await _service.RemovePluginAsync(_instance, entry.Name, _nodeRuntime());
                    break;
                case ExtensionKind.Skill when entry.Managed:
                    await _service.RemoveSkillAsync(_instance, entry);
                    break;
                case ExtensionKind.Preset when entry.Managed:
                    await _service.RemovePresetAsync(_instance, entry);
                    break;
                case ExtensionKind.Mcp:
                    await _service.RemoveMcpAsync(_instance, entry.Name);
                    break;
                default:
                    throw new InvalidOperationException("内置条目不能删除。");
            }

            StatusText.Text = $"已删除：{entry.Name}。";
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ExtensionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (ExtensionList.SelectedItem is not ExtensionEntry entry)
        {
            SelectedName.Text = "未选择条目";
            SelectedDetails.Text = string.Empty;
            return;
        }

        SelectedName.Text = entry.Name;
        SelectedDetails.Text = $"类型：{entry.Kind}\n状态：{(entry.Enabled ? "已启用" : "已禁用")}\n来源：{entry.Location}\n{entry.Description}";
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        System.Windows.MessageBox.Show(this, ex.Message, "扩展操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
