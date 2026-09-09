using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// 插件 × 实例矩阵：行 = 插件（至少在一个实例启用），列 = 实例，
/// 单元格三态（已启用 / 已装未启用 / 未安装）。
///
/// 行只收**至少在一个实例启用**的直装插件，并排除内置核心与已知运行时依赖
/// （schemastery/cosmokit/cordis 等会被 dsh 直装进 profile 但不是插件），
/// 避免它们变成"假插件行"、用户一启用就 boot 失败。
/// </summary>
public sealed class PluginMatrixService
{
    /// <summary>内置核心与运行时依赖：不进矩阵。</summary>
    private static readonly HashSet<string> ExcludedNames = new(
        DshCoreBundles.Minimal.Concat(new[]
        {
            "schemastery",
            "cosmokit",
            "cordis",
            "@cordisjs/core",
            "@cordisjs/loader",
            "@cordisjs/plugin-http",
            "@cordisjs/plugin-server"
        }),
        StringComparer.OrdinalIgnoreCase);

    private readonly ExtensionService _extensions;

    public PluginMatrixService(ExtensionService extensions)
    {
        _extensions = extensions;
    }

    /// <summary>读取一个实例的插件列表（只取 Plugin 类，失败返回空）。</summary>
    public async Task<IReadOnlyList<ExtensionEntry>> ListPluginsAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken)
    {
        try
        {
            var entries = await _extensions.ListAsync(instance, cancellationToken);
            return entries
                .Where(entry => entry.Kind == ExtensionKind.Plugin)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            return Array.Empty<ExtensionEntry>();
        }
    }

    /// <summary>由每实例的插件列表构建矩阵（纯函数，可单测）。</summary>
    public static PluginMatrix Build(
        IReadOnlyList<PluginMatrixColumn> columns,
        IReadOnlyDictionary<string, IReadOnlyList<ExtensionEntry>> pluginsByInstance)
    {
        var cellsByName = new Dictionary<string, Dictionary<string, PluginMatrixCellState>>(StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var descriptions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (instanceId, plugins) in pluginsByInstance)
        {
            foreach (var plugin in plugins)
            {
                if (plugin.Kind != ExtensionKind.Plugin
                    || string.IsNullOrWhiteSpace(plugin.Name)
                    || ExcludedNames.Contains(plugin.Name))
                {
                    continue;
                }

                if (!cellsByName.TryGetValue(plugin.Name, out var cells))
                {
                    cells = new Dictionary<string, PluginMatrixCellState>(StringComparer.Ordinal);
                    cellsByName[plugin.Name] = cells;
                }

                cells[instanceId] = plugin.Enabled
                    ? PluginMatrixCellState.Enabled
                    : PluginMatrixCellState.InstalledDisabled;

                if (plugin.Enabled)
                {
                    versions[plugin.Name] = plugin.Version;
                    descriptions[plugin.Name] = plugin.Description;
                }
            }
        }

        // 只保留“至少在一个实例启用”的插件（排除只作为依赖被直装的运行时包）。
        var rows = cellsByName
            .Where(pair => pair.Value.Values.Any(state => state == PluginMatrixCellState.Enabled))
            .Select(pair => new PluginMatrixRow(
                pair.Key,
                versions.TryGetValue(pair.Key, out var version) ? version : null,
                descriptions.TryGetValue(pair.Key, out var description) ? description : null,
                pair.Value))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PluginMatrix(columns, rows);
    }

    /// <summary>读取全部实例并构建矩阵。</summary>
    public async Task<PluginMatrix> LoadAsync(
        IEnumerable<ManagerInstance> instances,
        Func<string, bool> isRunning,
        CancellationToken cancellationToken)
    {
        var list = instances.ToArray();
        var columns = list
            .Select(instance => new PluginMatrixColumn(
                instance.Id,
                instance.Name ?? instance.Id,
                isRunning(instance.Id)))
            .ToArray();
        var map = new Dictionary<string, IReadOnlyList<ExtensionEntry>>(StringComparer.Ordinal);
        foreach (var instance in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            map[instance.Id] = await ListPluginsAsync(instance, cancellationToken);
        }

        return Build(columns, map);
    }
}
