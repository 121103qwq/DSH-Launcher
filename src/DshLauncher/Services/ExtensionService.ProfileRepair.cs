// ExtensionService.ProfileRepair.cs —— 方案 A（启动前插件依赖自愈）
//
// 本文件是相对上游 v1.0.7 的**唯一功能新增**（与 DshInstanceRunner/MainWindow 的
// 接线改动共同构成修复集）。所有辅助成员均复用 ExtensionService 既有实现
// （GetProfileManifestPath / ReadJsonObject / GetBundles / GetString /
// TryReadPackageManifest / PreparePnpmEnvironment / PnpmEnvironment / FindExecutable /
// RunProcessAsync / FormatProcessOutput / TryParsePnpmProgress / BuiltInBase /
// BuiltInWeb / ProfileName），本文件只新增方法，不改动既有代码。
//
// 背景与验证：导入或扫描已有 DSH_HOME 时，插件实体（node_modules）不会随之
// 复制，而 package.json 的 dependencies/dsh.profile.bundles 与 cordis.patch.yml
// 会被保留；dsh web 启动时 loadProfile→resolveBundleDir 对缺失 bundle 是
// fail-loud 退出（"cannot resolve profile bundle ..."），导致"健康检查前退出"
// 与 Chat 窗口无法访问 127.0.0.1:<端口>。本方法在启动前把"声明-实体"恢复一致：
// 缺失时在 profile 目录执行 pnpm install（lock 文件驱动、幂等、可离线恢复全部
// 传递依赖）。见 fix-explore/（实验 5：真实插件 dsh-at-file 复现 + pnpm install
// 0.65s 恢复 + dsh web HTTP 200；verify 工程 8/8 场景 PASS）。

using DshLauncher.Models;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace DshLauncher.Services;

public sealed partial class ExtensionService
{
    private static readonly TimeSpan ProfileRepairTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 幂等自愈：web profile 声明的 bundle/依赖在 node_modules 中不可解析时，
    /// 在 profile 目录执行 pnpm install 恢复完整依赖图；全部可解析时零进程开销
    /// 返回 false（快速路径）。恢复后复检，仍缺失则抛出带指引的错误。
    /// </summary>
    public async Task<bool> EnsureProfileDependenciesAsync(
        ManagerInstance instance,
        NodeRuntimeInfo? nodeRuntime,
        CancellationToken cancellationToken,
        IProgress<PluginCommandProgress>? progress = null)
    {
        var profilePath = GetProfileManifestPath(instance);
        if (!File.Exists(profilePath))
        {
            return false;   // 无 profile：dsh 启动时会 initProfile，不属于本方法职责
        }

        var root = ReadJsonObject(profilePath);
        var missing = FindMissingBundlePackages(profilePath, root, out var declaredCount);
        if (missing.Count == 0 || declaredCount == 0)
        {
            return false;   // 快速路径：零进程、毫秒级
        }

        using var pnpmEnvironment = PreparePnpmEnvironment(instance, nodeRuntime);
        var startInfo = BuildPnpmStartInfo(pnpmEnvironment, profilePath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProfileRepairTimeout);
        var output = await RunProcessAsync(startInfo, timeout.Token, line =>
        {
            if (progress is not null && TryParsePnpmProgress(line, out var update))
            {
                progress.Report(update);
            }
        });
        if (output.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"插件依赖恢复失败（退出码 {output.ExitCode}）：pnpm install 未成功。"
                + FormatProcessOutput(output, failure: true)
                + $"请运行 dsh plugin --profile {_activeProfile(instance)} add <插件标识> 手动恢复。");
        }

        var stillMissing = FindMissingBundlePackages(profilePath, root, out _);
        if (stillMissing.Count > 0)
        {
            throw new InvalidOperationException(
                $"pnpm install 完成后仍有 bundle/依赖不可解析：{string.Join("、", stillMissing)}。"
                + "请检查插件 spec 是否有效，或运行 dsh plugin --profile web add <插件标识>。");
        }

        return true;   // 本次实际执行了恢复
    }

    /// <summary>
    /// 收集 profile 中"已声明但 node_modules 不可解析"的包名（模仿 dsh 源码
    /// packageDirFromAnchor 的语义：仅要求 node_modules/&lt;pkg&gt;/package.json 存在）。
    /// bundles 与 dependencies 都会检查；内置包（@deepseek-ai/dsh-base /
    /// dsh-web-app）由 dsh 安装闭包保障，无需也不应恢复。
    /// </summary>
    private static IReadOnlyList<string> FindMissingBundlePackages(
        string profilePath,
        JsonObject root,
        out int declaredCount)
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in GetBundles(root))
        {
            var name = GetString(bundle);
            if (!string.IsNullOrWhiteSpace(name) && !IsBuiltInPluginName(name))
            {
                declared.Add(name);
            }
        }

        if (root["dependencies"] is JsonObject dependencies)
        {
            foreach (var dependency in dependencies)
            {
                if (!IsBuiltInPluginName(dependency.Key))
                {
                    declared.Add(dependency.Key);
                }
            }
        }

        declaredCount = declared.Count;
        var missing = new List<string>();
        foreach (var name in declared)
        {
            if (TryReadPackageManifest(profilePath, name) is null)
            {
                missing.Add(name);
            }
        }

        return missing.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsBuiltInPluginName(string packageName) =>
        string.Equals(packageName, BuiltInBase, StringComparison.OrdinalIgnoreCase)
        || string.Equals(packageName, BuiltInWeb, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 构造 pnpm install 的启动信息。Windows 上 pnpm 只有 .cmd shim 而无 .exe，
    /// ProcessStartInfo 不能直接启动 .cmd（与 DshRuntimeCommandFactory.ConfigureDirect
    /// 的处理同理），需经 cmd.exe 包装；非 Windows 直接走可执行文件。
    /// stdout/stderr 必须重定向：RunProcessAsync 依赖流读取。
    /// </summary>
    private static ProcessStartInfo BuildPnpmStartInfo(PnpmEnvironment pnpmEnvironment, string profilePath)
    {
        var pnpm = FindExecutable(pnpmEnvironment.DirectoryPath, "pnpm")
            ?? throw new InvalidOperationException(
                "pnpm 不在 PATH 中，无法恢复插件依赖；请安装 pnpm 后重试。");

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetDirectoryName(profilePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetExtension(pnpm).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(pnpm).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var command = $"\"{pnpm}\" install --reporter=append-only";
            startInfo.Arguments = $"/d /s /c \"{command.Replace("\"", "\"\"")}\"";
        }
        else
        {
            startInfo.FileName = pnpm;
            startInfo.ArgumentList.Add("install");
            startInfo.ArgumentList.Add("--reporter=append-only");
        }

        pnpmEnvironment.Apply(startInfo);
        return startInfo;
    }
}
