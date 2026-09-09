using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DshLauncher.Models;
using DshLauncher.Services;
using Binding = System.Windows.Data.Binding;
using GridView = System.Windows.Controls.GridView;
using GridViewColumn = System.Windows.Controls.GridViewColumn;
using UserControl = System.Windows.Controls.UserControl;

namespace DshLauncher;

/// <summary>
/// 插件 × 实例矩阵（内嵌页）：行 = 插件，列 = 实例，单元格三态。
/// 列在代码里按实例动态生成；数据来自各实例 web profile 的插件列表。
/// </summary>
public partial class PluginMatrixWindow : UserControl
{
    private readonly PluginMatrixService _service;
    private readonly IReadOnlyList<ManagerInstance> _instances;
    private readonly Func<string, bool> _isRunning;
    private CancellationTokenSource? _cancellation;
    private PluginMatrix _matrix = PluginMatrix.Empty;

    public PluginMatrixWindow(
        ExtensionService extensionService,
        IEnumerable<ManagerInstance> instances,
        Func<string, bool> isRunning)
    {
        _service = new PluginMatrixService(extensionService);
        _instances = instances.ToArray();
        _isRunning = isRunning;
        InitializeComponent();
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void Window_OnUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
            // 已取消/已释放都无所谓。
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_matrix.Rows.Count == 0)
        {
            StatusText.Text = "没有可复制的数据。";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_matrix.ToText());
            StatusText.Text = "已复制到剪贴板。";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
        }
        catch
        {
            // 忽略取消竞态。
        }

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        StatusText.Text = "正在读取各实例插件…";
        try
        {
            var matrix = await _service.LoadAsync(_instances, _isRunning, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _matrix = matrix;
            RenderMatrix();
        }
        catch (OperationCanceledException)
        {
            // 页面已离开/再次刷新。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"读取失败：{ex.Message}";
        }
    }

    private void RenderMatrix()
    {
        var view = new GridView();
        view.Columns.Add(new GridViewColumn
        {
            Header = "插件",
            Width = 220,
            DisplayMemberBinding = new Binding(nameof(PluginMatrixRow.Name))
        });
        view.Columns.Add(new GridViewColumn
        {
            Header = "版本",
            Width = 110,
            DisplayMemberBinding = new Binding(nameof(PluginMatrixRow.Version))
        });
        foreach (var column in _matrix.Columns)
        {
            var header = column.InstanceName.Length > 16
                ? column.InstanceName[..16] + "…"
                : column.InstanceName;
            view.Columns.Add(new GridViewColumn
            {
                Header = column.Running ? $"{header}（运行中）" : header,
                Width = 130,
                // 绑定到行的字符串索引器：{Binding [instanceId]}
                DisplayMemberBinding = new Binding($"[{column.InstanceId}]")
            });
        }

        MatrixList.View = view;
        MatrixList.ItemsSource = _matrix.Rows;
        EmptyText.Visibility = _matrix.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _matrix.Columns.Count == 0
            ? "还没有实例。"
            : $"{_matrix.Columns.Count} 个实例 · {_matrix.Rows.Count} 个插件 · 已启用 {_matrix.EnabledCount} 处";
    }
}
