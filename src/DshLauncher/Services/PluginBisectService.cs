using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>一轮逐插件试验（纯数据，便于单测）。</summary>
public sealed record PluginBisectTrial(
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> Disabled,
    bool IsConfirmation,
    string Description);

/// <summary>
/// 二分定位规划：禁用集 D 启动**成功** ⇒ 肇事插件 ∈ D；启动失败 ⇒ 肇事 ∉ D。
/// n 个插件通常只需 ⌈log₂n⌉+1 轮（2–8 个插件 = 2–4 轮）。
/// </summary>
public static class PluginBisectPlanner
{
    public const int MaxRounds = 12;

    public static PluginBisectTrial Next(IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0)
        {
            throw new ArgumentException("候选列表为空。", nameof(candidates));
        }

        if (candidates.Count == 1)
        {
            return new PluginBisectTrial(
                candidates.ToArray(),
                new[] { candidates[0] },
                true,
                $"确认轮：只禁用 {candidates[0]}");
        }

        // 多的一半先禁用（对数复杂度与轮数无关，取多的一半可让确认轮更少歧义）。
        var half = (candidates.Count + 1) / 2;
        var disabled = candidates.Take(half).ToArray();
        return new PluginBisectTrial(
            candidates.ToArray(),
            disabled,
            false,
            $"禁用 {string.Join("、", disabled)}（共 {candidates.Count} 个候选）");
    }

    /// <summary>本轮结果收窄候选集。</summary>
    public static IReadOnlyList<string> Narrow(
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> disabled,
        bool startedOk) => startedOk
        ? disabled.ToArray()
        : candidates.Where(item => !disabled.Contains(item, StringComparer.Ordinal)).ToArray();
}

/// <summary>逐插件定位结果。</summary>
public sealed record PluginBisectResult(
    bool Completed,
    string? Culprit,
    int Trials,
    string Summary,
    IReadOnlyList<string> Trace);

/// <summary>
/// 逐插件定位（安全模式的延伸）：不改用户任何文件，在
/// <c>&lt;DSH_HOME&gt;\profiles\.dsh-bisect</c> 里按二分策略禁用一部分第三方插件后启动，
/// 用 runner 既有的健康检查判定成功/失败，逐步锁定肇事插件。
/// 结束（含取消/异常）时删除隔离 profile。
/// </summary>
public sealed class PluginBisectService
{
    private readonly DshInstanceRunner _runner;
    private readonly SafeProfileService _safeProfile;

    public PluginBisectService(DshInstanceRunner runner, SafeProfileService? safeProfile = null)
    {
        _runner = runner;
        _safeProfile = safeProfile ?? new SafeProfileService();
    }

    public async Task<PluginBisectResult> RunAsync(
        ManagerInstance instance,
        NodeRuntimeInfo? nodeRuntime,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var trace = new List<string>();
        if (!_safeProfile.HasThirdPartyBundles(instance, out var thirdParty) || thirdParty.Count == 0)
        {
            return new PluginBisectResult(false, null, 0, "该实例没有第三方插件，无需定位。", trace);
        }

        if (_runner.IsRunning(instance.Id))
        {
            return new PluginBisectResult(false, null, 0, "请先停止实例，再开始定位（定位过程会反复启动/停止它）。", trace);
        }

        var core = _safeProfile.ResolveBundles(instance, SafeProfileTier.Tier1KeepDeepSeekCore);
        var candidates = thirdParty.ToList();
        var trials = 0;
        LauncherLog.Info("开始逐插件定位。", ErrorCodes.E1017,
            new { instance = instance.Name, candidates });

        try
        {
            while (candidates.Count > 0 && trials < PluginBisectPlanner.MaxRounds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trial = PluginBisectPlanner.Next(candidates);
                var kept = core
                    .Concat(thirdParty.Where(item => !trial.Disabled.Contains(item, StringComparer.Ordinal)))
                    .ToArray();
                progress?.Report($"第 {trials + 1} 轮：{trial.Description}，启动中…");

                if (!TryPrepareTrialProfile(instance, kept, out var prepareError))
                {
                    return new PluginBisectResult(false, null, trials, $"隔离 profile 准备失败：{prepareError}", trace);
                }

                DshInstanceRunResult result;
                try
                {
                    result = await _runner.StartAsync(
                        instance,
                        nodeRuntime,
                        cancellationToken,
                        openBrowser: false,
                        profileOverride: SafeProfileService.BisectProfileName);
                }
                finally
                {
                    try
                    {
                        await _runner.StopAsync(instance.Id, cancellationToken, instance.Name);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
                    {
                        // 停止失败不阻断下一轮（下一轮启动会覆盖）。
                    }
                }

                trials++;
                var startedOk = result.IsSuccess;
                var line = $"第 {trials} 轮：{trial.Description} → {(startedOk ? "启动成功" : "启动失败")}" +
                           (startedOk ? string.Empty : $"（{Trim(result.Error)}）");
                trace.Add(line);
                progress?.Report(line);

                if (trial.IsConfirmation)
                {
                    var culprit = startedOk ? trial.Candidates[0] : null;
                    var summary = startedOk
                        ? $"已定位：{culprit} 会导致启动失败。"
                        : "单插件确认轮仍失败：可能是多个插件共同导致，或问题与插件无关。";
                    LauncherLog.Info("逐插件定位结束。", ErrorCodes.E1017,
                        new { instance = instance.Name, culprit, trials });
                    return new PluginBisectResult(true, culprit, trials, summary, trace);
                }

                candidates = PluginBisectPlanner.Narrow(candidates, trial.Disabled, startedOk).ToList();
                if (candidates.Count == 0)
                {
                    return new PluginBisectResult(
                        true,
                        null,
                        trials,
                        "未能定位到具体插件（结果互相矛盾，可能是不稳定问题）。",
                        trace);
                }
            }

            return new PluginBisectResult(
                false,
                null,
                trials,
                $"达到最大轮数（{PluginBisectPlanner.MaxRounds}）仍未收敛，建议改用安全模式启动。",
                trace);
        }
        catch (OperationCanceledException)
        {
            return new PluginBisectResult(false, null, trials, "定位已取消。", trace);
        }
        finally
        {
            CleanupTrialProfile(instance);
        }
    }

    /// <summary>
    /// 准备试验 profile：复制用户 web profile 的清单/配置（只读），把 bundles 换成试验集合，
    /// 再把 <c>node_modules</c> 目录联接（junction）到用户 web profile——第三方插件从那里解析，
    /// 全程不改用户任何文件。
    /// </summary>
    internal bool TryPrepareTrialProfile(
        ManagerInstance instance,
        IReadOnlyList<string> bundles,
        out string? error)
    {
        error = null;
        var webDirectory = Path.Combine(_safeProfile.GetProfilesDirectory(instance), "web");
        var trialDirectory = _safeProfile.GetProfileDirectory(instance, SafeProfileService.BisectProfileName);
        try
        {
            CleanupTrialProfile(instance);
            Directory.CreateDirectory(trialDirectory);

            foreach (var name in new[] { "package.json", "cordis.yml", "cordis.patch.yml", "pnpm-workspace.yaml", "pnpm-lock.yaml" })
            {
                var source = Path.Combine(webDirectory, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(trialDirectory, name), overwrite: true);
                }
            }

            var manifestPath = Path.Combine(trialDirectory, "package.json");
            if (!File.Exists(manifestPath))
            {
                error = "用户 web profile 没有 package.json";
                return false;
            }

            if (JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)) is not JsonObject manifest)
            {
                error = "用户 package.json 无法解析";
                return false;
            }

            manifest["name"] = "dsh-profile-bisect";
            if (manifest["dsh"] is not JsonObject dsh)
            {
                dsh = new JsonObject();
                manifest["dsh"] = dsh;
            }

            if (dsh["profile"] is not JsonObject profile)
            {
                profile = new JsonObject();
                dsh["profile"] = profile;
            }

            var array = new JsonArray();
            foreach (var bundle in bundles)
            {
                array.Add(bundle);
            }

            profile["bundles"] = array;
            // 注意：不要用 JsonNode.ToJsonString(options)（在启用反射限制的构建里会要求 TypeInfoResolver）；
            // 与 SafeProfileService 一致走 JsonSerializer.Serialize。
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));

            var sourceModules = Path.Combine(webDirectory, "node_modules");
            var trialModules = Path.Combine(trialDirectory, "node_modules");
            if (Directory.Exists(sourceModules) && !Directory.Exists(trialModules)
                && !TryCreateJunction(trialModules, sourceModules))
            {
                error = "node_modules 目录联接失败（第三方插件无法解析）";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>删除试验 profile（先摘掉 junction，绝不跟随到用户 web/node_modules）。</summary>
    internal void CleanupTrialProfile(ManagerInstance instance)
    {
        var trialDirectory = _safeProfile.GetProfileDirectory(instance, SafeProfileService.BisectProfileName);
        var trialModules = Path.Combine(trialDirectory, "node_modules");
        try
        {
            if (Directory.Exists(trialModules))
            {
                Directory.Delete(trialModules, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 摘链失败时下面的 CleanupProfile 会再试（不会跟随重解析点）。
        }

        _safeProfile.CleanupProfile(instance, SafeProfileService.BisectProfileName);
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(linkPath);
            startInfo.ArgumentList.Add(targetPath);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(15000);
            return Directory.Exists(linkPath);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string Trim(string? value) => string.IsNullOrWhiteSpace(value)
        ? "未知原因"
        : value.Length <= 120
            ? value
            : value[..120] + "…";
}
