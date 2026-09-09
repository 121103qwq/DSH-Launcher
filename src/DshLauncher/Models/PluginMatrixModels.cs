namespace DshLauncher.Models;

/// <summary>插件 × 实例矩阵的单元格状态（三态）。</summary>
public enum PluginMatrixCellState
{
    /// <summary>该实例未安装此插件。</summary>
    NotInstalled,

    /// <summary>已安装（在 dependencies 里）但未启用（不在 profile bundles 里）。</summary>
    InstalledDisabled,

    /// <summary>已启用（在 profile bundles 里）。</summary>
    Enabled
}

/// <summary>矩阵的一列（一个实例）。</summary>
public sealed record PluginMatrixColumn(string InstanceId, string InstanceName, bool Running);

/// <summary>
/// 矩阵的一行（一个插件）。索引器按实例 ID 返回中文状态文本，
/// 供 WPF 动态列绑定 <c>{Binding [instanceId]}</c> 使用。
/// </summary>
public sealed record PluginMatrixRow(
    string Name,
    string? Version,
    string? Description,
    IReadOnlyDictionary<string, PluginMatrixCellState> Cells)
{
    public string this[string instanceId] => Cells.TryGetValue(instanceId, out var state)
        ? Label(state)
        : Label(PluginMatrixCellState.NotInstalled);

    public static string Label(PluginMatrixCellState state) => state switch
    {
        PluginMatrixCellState.Enabled => "已启用",
        PluginMatrixCellState.InstalledDisabled => "已装未启用",
        _ => "未安装"
    };
}

/// <summary>插件 × 实例矩阵。</summary>
public sealed record PluginMatrix(
    IReadOnlyList<PluginMatrixColumn> Columns,
    IReadOnlyList<PluginMatrixRow> Rows)
{
    public static readonly PluginMatrix Empty = new(Array.Empty<PluginMatrixColumn>(), Array.Empty<PluginMatrixRow>());

    /// <summary>所有实例中“已启用”单元格的数量。</summary>
    public int EnabledCount => Rows.Sum(row => row.Cells.Values.Count(state => state == PluginMatrixCellState.Enabled));

    /// <summary>复制为文本（表格形式，便于贴到 issue / 聊天）。</summary>
    public string ToText()
    {
        var headers = new List<string> { "插件", "版本" };
        headers.AddRange(Columns.Select(column => column.InstanceName));
        var rows = new List<string> { string.Join("\t", headers) };
        foreach (var row in Rows)
        {
            var cells = new List<string> { row.Name, row.Version ?? "-" };
            cells.AddRange(Columns.Select(column => row[column.InstanceId]));
            rows.Add(string.Join("\t", cells));
        }

        return string.Join(Environment.NewLine, rows);
    }
}
