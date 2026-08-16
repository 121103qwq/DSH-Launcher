using System.IO;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class VersionHealthService
{
    private const long MaximumCredentialsSize = 1024 * 1024;
    private readonly VersionSettingsService _settingsService;
    private readonly ModelService _modelService;

    public VersionHealthService(
        VersionSettingsService? settingsService = null,
        ModelService? modelService = null)
    {
        _settingsService = settingsService ?? new VersionSettingsService();
        _modelService = modelService ?? new ModelService();
    }

    public VersionHealthReport Inspect(
        ManagerInstance instance,
        NodeRuntimeInfo nodeRuntime,
        DshRuntimeInfo detectedDshRuntime,
        bool isActuallyRunning)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var items = new List<VersionHealthItem>();

        InspectHome(instance, items);
        InspectRuntime(instance, detectedDshRuntime, items);
        InspectNode(instance, nodeRuntime, detectedDshRuntime.NodeEngine, items);
        InspectConfiguration(instance, items);
        InspectRuntimeRecord(instance, isActuallyRunning, items);

        return new VersionHealthReport(instance.Id, DateTimeOffset.UtcNow, items);
    }

    public VersionRepairResult Repair(
        ManagerInstance instance,
        DshRuntimeInfo detectedDshRuntime,
        bool isActuallyRunning)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (isActuallyRunning || instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            throw new InvalidOperationException("运行中或 Attached 版本不能自动修复，请先停止或解除外部连接。 ");
        }

        var updated = instance;
        var actions = new List<string>();
        if (!Directory.Exists(updated.DshHome))
        {
            Directory.CreateDirectory(updated.DshHome);
            actions.Add("已重新创建缺失的 DSH_HOME。 ");
        }

        if (updated.RuntimeStatus == InstanceRuntimeStatus.Running && !isActuallyRunning)
        {
            updated = updated with
            {
                RuntimeStatus = InstanceRuntimeStatus.Ready,
                RuntimeOwnership = InstanceRuntimeOwnership.None,
                ProcessId = null,
                Port = null,
                WebUrl = null,
                LastError = null
            };
            actions.Add("已清除不再存活的旧运行记录。 ");
        }

        var rebound = InstanceRuntimeRebinder.RebindInstalledInstance(updated, detectedDshRuntime);
        if (rebound is not null)
        {
            updated = rebound;
            actions.Add($"已重新绑定到 DSh {detectedDshRuntime.VersionText}。 ");
        }

        return new VersionRepairResult(updated, actions);
    }

    private static void InspectHome(ManagerInstance instance, ICollection<VersionHealthItem> items)
    {
        if (!Directory.Exists(instance.DshHome))
        {
            items.Add(new VersionHealthItem(
                "dsh-home",
                "版本数据目录",
                VersionHealthState.Error,
                $"DSH_HOME 不存在：{instance.DshHome}",
                Repairable: true));
            return;
        }

        if (IsReparsePoint(instance.DshHome))
        {
            items.Add(new VersionHealthItem(
                "dsh-home",
                "版本数据目录",
                VersionHealthState.Error,
                "DSH_HOME 是符号链接或重解析点，Launcher 不会自动修改它。"));
            return;
        }

        items.Add(new VersionHealthItem(
            "dsh-home",
            "版本数据目录",
            VersionHealthState.Healthy,
            "独立 DSH_HOME 存在且可安全访问。"));
    }

    private static void InspectRuntime(
        ManagerInstance instance,
        DshRuntimeInfo detectedDshRuntime,
        ICollection<VersionHealthItem> items)
    {
        if (instance.Kind == InstanceKind.Source)
        {
            var project = new SourceProjectInspector().Inspect(instance.RootPath);
            if (!project.IsValid || !project.IsDshSource)
            {
                items.Add(new VersionHealthItem(
                    "dsh-runtime",
                    "DSh Runtime",
                    VersionHealthState.Error,
                    project.Error ?? "Source 项目结构无效。"));
                return;
            }

            var entrypoint = project.BuiltCliEntrypoint
                ?? SourceProjectInspector.TryFindBuiltCliEntrypoint(instance.RootPath);
            items.Add(entrypoint is null
                ? new VersionHealthItem(
                    "dsh-runtime",
                    "DSh Runtime",
                    VersionHealthState.Error,
                    "Source 尚未构建，找不到 CLI 入口；请在启动时执行 Source 准备。")
                : new VersionHealthItem(
                    "dsh-runtime",
                    "DSh Runtime",
                    VersionHealthState.Healthy,
                    $"Source CLI 已构建：{entrypoint}"));
            return;
        }

        var packageRoot = DshRuntimeDetector.TryResolvePackageRoot(instance.RootPath);
        var executableValid = !string.IsNullOrWhiteSpace(instance.DshExecutablePath)
            && File.Exists(instance.DshExecutablePath);
        if (packageRoot is null || !executableValid)
        {
            items.Add(new VersionHealthItem(
                "dsh-runtime",
                "DSh Runtime",
                VersionHealthState.Error,
                detectedDshRuntime.IsAvailable
                    ? $"当前绑定已失效，可以重新绑定到 {detectedDshRuntime.VersionText}。"
                    : "当前绑定的 DSh 包或命令入口不存在；请先在设置中准备运行环境。",
                Repairable: detectedDshRuntime.IsAvailable));
            return;
        }

        var packageVersion = DshRuntimeDetector.TryReadPackageVersion(packageRoot);
        var versionMatches = string.IsNullOrWhiteSpace(instance.DetectedVersion)
            || string.Equals(instance.DetectedVersion, packageVersion, StringComparison.OrdinalIgnoreCase);
        items.Add(new VersionHealthItem(
            "dsh-runtime",
            "DSh Runtime",
            versionMatches ? VersionHealthState.Healthy : VersionHealthState.Warning,
            versionMatches
                ? $"DSh {packageVersion ?? "版本未标记"} · {packageRoot}"
                : $"注册版本为 {instance.DetectedVersion}，实际包版本为 {packageVersion ?? "未知"}；重新检测后会更新绑定。"));
    }

    private static void InspectNode(
        ManagerInstance instance,
        NodeRuntimeInfo nodeRuntime,
        string? detectedNodeEngine,
        ICollection<VersionHealthItem> items)
    {
        var requirement = DshRuntimeDetector.ResolveNodeEngine(instance, detectedNodeEngine);
        var compatibility = nodeRuntime.GetCompatibility(requirement);
        var detail = compatibility switch
        {
            NodeRuntimeCompatibility.Missing => "没有检测到可用 Node.js；请在设置中准备运行环境。",
            NodeRuntimeCompatibility.Incompatible => $"{nodeRuntime.VersionText} 不满足 DSh 要求：{requirement ?? "未声明"}。",
            NodeRuntimeCompatibility.Unknown => $"无法判断 {nodeRuntime.VersionText} 是否满足：{requirement ?? "未声明"}。",
            _ => $"{nodeRuntime.VersionText} · 要求：{requirement ?? "package metadata 未声明"}"
        };
        items.Add(new VersionHealthItem(
            "node",
            "Node.js",
            compatibility == NodeRuntimeCompatibility.Compatible
                ? VersionHealthState.Healthy
                : VersionHealthState.Error,
            detail));
    }

    private void InspectConfiguration(ManagerInstance instance, ICollection<VersionHealthItem> items)
    {
        var problems = new List<string>();
        try
        {
            _ = _settingsService.Read(instance);
        }
        catch (Exception ex)
        {
            problems.Add($"版本设置损坏：{ex.Message}");
        }

        try
        {
            _ = _modelService.Read(instance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problems.Add($"Provider 配置无法读取：{ex.Message}");
        }

        ValidateJson(Path.Combine(instance.DshHome, ".dsh-launcher", "providers.json"), "Provider 状态", problems);
        ValidateJson(Path.Combine(instance.DshHome, ".dsh-launcher", "mcp.json"), "MCP 配置", problems);
        ValidatePluginManifest(Path.Combine(instance.DshHome, "profiles", "web", "package.json"), problems);

        var credentials = Path.Combine(instance.DshHome, ".credentials.yaml");
        if (File.Exists(credentials))
        {
            if (IsReparsePoint(credentials))
            {
                problems.Add("凭据文件是重解析点，已拒绝读取。");
            }
            else if (new FileInfo(credentials).Length > MaximumCredentialsSize)
            {
                problems.Add("凭据文件超过 1 MiB 安全上限。");
            }
        }

        items.Add(problems.Count == 0
            ? new VersionHealthItem(
                "configuration",
                "配置与 Plugin",
                VersionHealthState.Healthy,
                "版本设置、Provider、MCP 与 web profile 配置可读取。")
            : new VersionHealthItem(
                "configuration",
                "配置与 Plugin",
                VersionHealthState.Error,
                string.Join("\n", problems)));
    }

    private static void InspectRuntimeRecord(
        ManagerInstance instance,
        bool isActuallyRunning,
        ICollection<VersionHealthItem> items)
    {
        if (instance.RuntimeStatus == InstanceRuntimeStatus.Running && !isActuallyRunning)
        {
            items.Add(new VersionHealthItem(
                "runtime-record",
                "运行记录",
                VersionHealthState.Warning,
                "注册记录仍显示运行中，但 Launcher 没有发现可管理或已连接的服务。",
                Repairable: true));
            return;
        }

        items.Add(new VersionHealthItem(
            "runtime-record",
            "运行记录",
            VersionHealthState.Healthy,
            isActuallyRunning ? "实例当前正在运行。" : "没有发现旧进程残留记录。"));
    }

    private static void ValidateJson(string path, string label, ICollection<string> problems)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            if (IsReparsePoint(path))
            {
                problems.Add($"{label}文件是重解析点。");
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            _ = document.RootElement.ValueKind;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problems.Add($"{label}格式无效：{ex.Message}");
        }
    }

    private static void ValidatePluginManifest(string path, ICollection<string> problems)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            if (IsReparsePoint(path))
            {
                problems.Add("web profile package.json 是重解析点。");
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problems.Add("web profile package.json 根节点不是对象。");
            }
            else if (document.RootElement.TryGetProperty("dependencies", out var dependencies)
                && dependencies.ValueKind != JsonValueKind.Object)
            {
                problems.Add("web profile dependencies 不是对象。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problems.Add($"web profile package.json 格式无效：{ex.Message}");
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
