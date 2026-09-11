using System.IO;
using System.Text;
using System.Text.Json;

namespace DshLauncher.Services;

/// <summary>危险配置发现项的风险级别。</summary>
public enum DangerousConfigSeverity
{
    /// <summary>提示：值得知道，但不算违规（例：社区来源依赖）。</summary>
    Info,

    /// <summary>警告：敞口明显变大。</summary>
    Warning,

    /// <summary>危险：免确认的全盘读写/执行，或数据外发到非官方域。</summary>
    Danger
}

/// <summary>
/// 单条危险配置发现。<see cref="Evidence"/> 只包含**键名与配置值**（这些值本身不是凭据）与来源文件，
/// 不含任何凭据片段。
/// </summary>
public sealed record DangerousConfigFinding(
    string Id,
    DangerousConfigSeverity Severity,
    string Title,
    string Evidence,
    string Advice);

/// <summary>危险配置检查报告（纯内存）。</summary>
public sealed record DangerousConfigReport(
    IReadOnlyList<DangerousConfigFinding> Findings,
    IReadOnlyList<string> CheckedItems,
    IReadOnlyList<string> Notes,
    DateTime CompletedAtUtc);

/// <summary>
/// #20 危险配置检查（用户 2026-09-11 勾选范围：权限档位 / DSH_PERMISSION_MODE / approval.policy /
/// 遥测模式与导出口 / 依赖来源 / 凭据文件保护 / HMR）。
///
/// **实现约束（work-log/74 调研结论）**：只**直接读文件**，绝不调用 `dsh --dump-config`
/// （它会写盘生成 profile 脚手架，破坏"体检纯读取"的安全反证）。
///
/// 语义仅覆盖**高置信度**判断；读不出的表达式（YAML 里的 <c>!!js</c>）会如实记入 Notes，绝不猜。
/// </summary>
public static class DangerousConfigAuditService
{
    private const string OfficialTelemetryDomain = "deepseeksvc.com";

    private static readonly string[] OfficialScopes = { "@deepseek-ai/" };

    public static DangerousConfigReport Run(
        string dshHome,
        string? profileName,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var findings = new List<DangerousConfigFinding>();
        var checkedItems = new List<string>();
        var notes = new List<string>();
        var env = environment ?? ReadEnvironment();

        if (string.IsNullOrWhiteSpace(dshHome) || !Directory.Exists(dshHome))
        {
            notes.Add("DSH_HOME 不存在，无法检查危险配置。");
            return new DangerousConfigReport(findings, checkedItems, notes, DateTime.UtcNow);
        }

        var home = Path.GetFullPath(dshHome);
        var profileDirectory = string.IsNullOrWhiteSpace(profileName)
            ? null
            : Path.Combine(home, "profiles", profileName!);

        var layerText = ReadLayerText(profileDirectory, notes);
        var settingsText = ReadTextOrNull(Path.Combine(home, "settings.yaml"));
        var allText = layerText + "\n" + (settingsText ?? string.Empty);

        // ---- 1/4：环境变量 DSH_PERMISSION_MODE（会静默覆盖默认档位）----
        checkedItems.Add("DSH_PERMISSION_MODE 环境变量");
        var permissionMode = env.TryGetValue("DSH_PERMISSION_MODE", out var mode) ? mode : null;
        if (string.Equals(permissionMode, "danger-full-access", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new DangerousConfigFinding(
                "env-permission-mode",
                DangerousConfigSeverity.Danger,
                "环境变量把权限档位设为 danger-full-access",
                "DSH_PERMISSION_MODE=danger-full-access（来自进程环境，会覆盖 profile 默认值）",
                "这会让沙箱变成全盘可写、且审批策略退化为 never（免确认）。"
                    + "请确认这是你有意设置的；若不是，检查启动脚本/快捷方式/上级进程是否注入了该变量。"));
        }
        else if (!string.IsNullOrWhiteSpace(permissionMode))
        {
            findings.Add(new DangerousConfigFinding(
                "env-permission-mode",
                DangerousConfigSeverity.Info,
                "环境变量指定了权限档位",
                $"DSH_PERMISSION_MODE={permissionMode}",
                "该变量会覆盖 profile 默认档位；如果这不是你有意设置的，请注意它的来源。"));
        }

        // ---- 1：profile/settings 层里的 sandbox 档位 ----
        checkedItems.Add("sandbox-policy.mode（profile 层与 settings.yaml）");
        var sandboxMode = ReadPluginValue(allText, "sandbox-policy", "mode");
        if (string.Equals(sandboxMode, "danger-full-access", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new DangerousConfigFinding(
                "sandbox-mode",
                DangerousConfigSeverity.Danger,
                "沙箱档位被设为 danger-full-access",
                "sandbox-policy.config.mode: danger-full-access",
                "沙箱不再限制工作区范围（可读写全盘）。若非有意，请改回 read-only 或 workspace-write。"));
        }
        else if (sandboxMode is null)
        {
            notes.Add("未在 profile 层/settings.yaml 里找到 sandbox-policy.mode（可能仍是出厂默认 workspace-write）。");
        }

        // ---- 3：sandbox-policy.workspaceRoot 过宽 ----
        checkedItems.Add("sandbox-policy.workspaceRoot（沙箱范围）");
        var workspaceRoot = ReadPluginValue(allText, "sandbox-policy", "workspaceRoot");
        if (workspaceRoot is { Length: > 0 } && workspaceRoot.Contains("!!js", StringComparison.Ordinal))
        {
            notes.Add("sandbox-policy.workspaceRoot 是表达式（!!js），静态读不出最终值。");
        }
        else if (workspaceRoot is { Length: > 0 })
        {
            // 归一化分隔符：YAML 里常见正斜杠，而 Path.GetFullPath 给的路径在本机是反斜杠，
            // 不归一化会让"包含关系"判断永远为 false（自测抓到的真实缺陷）。
            var trimmedRoot = workspaceRoot
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
            var homePrefix = home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var isDriveRoot = string.Equals(
                Path.GetPathRoot(trimmedRoot),
                trimmedRoot,
                StringComparison.OrdinalIgnoreCase);
            var containsDshHome = homePrefix.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(homePrefix, trimmedRoot, StringComparison.OrdinalIgnoreCase);
            var relativeSegments = trimmedRoot
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Length;

            if (isDriveRoot || containsDshHome)
            {
                findings.Add(new DangerousConfigFinding(
                    "workspace-root-too-broad",
                    DangerousConfigSeverity.Danger,
                    "沙箱工作区范围过大",
                    $"sandbox-policy.config.workspaceRoot: {workspaceRoot}"
                        + (isDriveRoot ? "（盘符根）" : "（包含本实例的 DSH_HOME）"),
                    "沙箱在这个范围内不做写入限制。指向盘根等于全盘可写；"
                        + "包含 DSH_HOME 则意味着 .credentials.yaml 也在沙箱内可达。"
                        + "建议指向具体项目目录。"));
            }
            else if (relativeSegments <= 1)
            {
                findings.Add(new DangerousConfigFinding(
                    "workspace-root-broad",
                    DangerousConfigSeverity.Warning,
                    "沙箱工作区范围偏宽",
                    $"sandbox-policy.config.workspaceRoot: {workspaceRoot}（目录层级很浅）",
                    "范围越宽，可写面越大。建议指向具体项目目录而不是公共父目录。"));
            }
        }

        // ---- 4：approval.policy ----
        checkedItems.Add("approval.policy（profile 层与 settings.yaml）");
        var approvalPolicy = ReadPluginValue(allText, "approval", "policy");
        var effectiveNever = string.Equals(permissionMode, "danger-full-access", StringComparison.OrdinalIgnoreCase)
            || string.Equals(approvalPolicy, "never", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(approvalPolicy, "never", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new DangerousConfigFinding(
                "approval-policy",
                DangerousConfigSeverity.Danger,
                "审批策略为 never（所有工具调用免确认）",
                "approval.config.policy: never",
                "工具调用、命令执行、文件写入都不会再向你确认。若非有意，请改为 ask。"));
        }
        else if (approvalPolicy is null && effectiveNever)
        {
            findings.Add(new DangerousConfigFinding(
                "approval-policy-effective",
                DangerousConfigSeverity.Danger,
                "按当前环境变量推断：审批策略实际为 never",
                "approval.policy 默认表达式：DSH_PERMISSION_MODE === 'danger-full-access' ? 'never' : 'ask'，"
                    + "当前 DSH_PERMISSION_MODE=danger-full-access",
                "配置层没有显式写死策略，但环境变量让它变成了免确认。"));
        }
        else if (approvalPolicy is { Length: > 0 } && approvalPolicy.Contains("!!js", StringComparison.Ordinal))
        {
            notes.Add("approval.policy 是表达式（!!js），静态读不出最终值，只能按环境变量推断。");
        }

        // ---- 5：遥测模式与导出口 ----
        checkedItems.Add("遥测模式与导出口（DSH_TELEMETRY_MODE / DSH_TELEMETRY_OTLP_URL / profile 层）");
        var telemetryMode = env.TryGetValue("DSH_TELEMETRY_MODE", out var telemetryModeValue) && !string.IsNullOrWhiteSpace(telemetryModeValue)
            ? telemetryModeValue!
            : ReadPluginValue(allText, "session-telemetry-otel", "mode");
        if (!string.IsNullOrWhiteSpace(telemetryMode)
            && !string.Equals(telemetryMode, "FEEDBACK_ONLY", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new DangerousConfigFinding(
                "telemetry-mode",
                DangerousConfigSeverity.Warning,
                "遥测模式不是 FEEDBACK_ONLY",
                $"遥测 mode={telemetryMode}",
                "默认应为 FEEDBACK_ONLY（只在你主动反馈时上报）。其它模式可能自动上报会话内容。"));
        }

        var telemetryUrl = env.TryGetValue("DSH_TELEMETRY_OTLP_URL", out var telemetryUrlValue)
                           && !string.IsNullOrWhiteSpace(telemetryUrlValue)
            ? telemetryUrlValue!
            : ReadPluginValue(allText, "session-telemetry-otel", "url");
        if (!string.IsNullOrWhiteSpace(telemetryUrl) && telemetryUrl!.Contains("://", StringComparison.Ordinal))
        {
            var host = telemetryUrl.Split("://", 2)[1].Split('/', 2)[0];
            if (!host.EndsWith(OfficialTelemetryDomain, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new DangerousConfigFinding(
                    "telemetry-endpoint",
                    DangerousConfigSeverity.Danger,
                    "遥测导出口指向非官方域名",
                    $"exporter.url 主机 = {host}（官方默认是 *.{OfficialTelemetryDomain}）",
                    "会话遥测可能被发送到第三方。请确认该地址可信；若不是你有意配置的，"
                        + "请检查 DSH_TELEMETRY_OTLP_URL 环境变量与 profile 覆盖。"));
            }
        }

        // ---- 10：HMR（热重载）----
        checkedItems.Add("hmr.disabled（热重载开关）");
        var hmrDisabled = ReadPluginValue(allText, "hmr", "disabled");
        if (string.Equals(hmrDisabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new DangerousConfigFinding(
                "hmr-enabled",
                DangerousConfigSeverity.Warning,
                "热重载（HMR）被显式开启",
                "hmr.disabled: false",
                "默认是关闭的（disabled: true）。开启后客户端模块会被动态替换，"
                    + "加载未验证的模块正是 2026-09-11「Failed to load plugins」事故的路径。"));
        }

        // ---- 8：活动 profile 的依赖来源（供应链）----
        checkedItems.Add("profiles/<profile>/package.json 依赖来源");
        if (profileDirectory is not null)
        {
            var packageJson = Path.Combine(profileDirectory, "package.json");
            if (File.Exists(packageJson))
            {
                var thirdParty = new List<string>();
                var nonOfficial = new List<string>();
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
                    if (document.RootElement.TryGetProperty("dependencies", out var dependencies)
                        && dependencies.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in dependencies.EnumerateObject())
                        {
                            var name = property.Name;
                            var spec = property.Value.ValueKind == JsonValueKind.String
                                ? property.Value.GetString() ?? string.Empty
                                : string.Empty;
                            if (OfficialScopes.Any(scope => name.StartsWith(scope, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            thirdParty.Add(name);
                            if (spec.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
                                || spec.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
                                || spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                                || spec.StartsWith("link:", StringComparison.OrdinalIgnoreCase)
                                || spec.Contains("://", StringComparison.Ordinal))
                            {
                                nonOfficial.Add($"{name} ← {spec}");
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    notes.Add("package.json 解析失败：" + ex.Message);
                }

                if (nonOfficial.Count > 0)
                {
                    findings.Add(new DangerousConfigFinding(
                        "plugin-origins",
                        DangerousConfigSeverity.Info,
                        "profile 里有来自非 npm 官方源的依赖",
                        string.Join("；", nonOfficial.Take(8)),
                        "这些插件在你的 DSH_HOME 内运行，理论上能读到本机凭据。"
                            + "这本身不是违规（装它就是你选的），但值得你核对来源是否可信；"
                            + "来源归因可在「插件与技能来源」页查看。"));
                }
                else if (thirdParty.Count > 0)
                {
                    notes.Add($"profile 有 {thirdParty.Count} 个第三方依赖，均来自 npm（未发现 git/本地/URL 来源）。");
                }
            }
        }

        // ---- 9：凭据文件保护（复用既有清单逻辑）----
        checkedItems.Add("凭据文件是否可写");
        foreach (var info in CredentialAuditService
            .Run(new CredentialAuditService.CredentialAuditRequest(home, profileName))
            .Files.Where(file => file.Kind.Contains("凭据", StringComparison.Ordinal)))
        {
            if (!info.ReadOnly)
            {
                findings.Add(new DangerousConfigFinding(
                    "credential-file-writable",
                    DangerousConfigSeverity.Warning,
                    "凭据文件可被任意改写",
                    $"{info.FilePath}（{info.Size} B，未设只读）",
                    "任何以你身份运行的进程都能改它。保持可写是 dsh 正常刷新令牌的前提，"
                        + "所以这里只提示；若长期不用可考虑备份后删除。"));
            }
        }

        if (findings.Count == 0)
        {
            notes.Add("未发现需要提示的危险配置。");
        }

        return new DangerousConfigReport(findings, checkedItems, notes, DateTime.UtcNow);
    }

    /// <summary>读取 profile 层文本（cordis.yml / cordis.patch.yml）。读不到不算错。</summary>
    private static string ReadLayerText(string? profileDirectory, List<string> notes)
    {
        if (profileDirectory is null || !Directory.Exists(profileDirectory))
        {
            notes.Add("没有可读的 profile 目录，只能检查环境变量与 settings.yaml。");
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var name in new[] { "cordis.yml", "cordis.yaml", "cordis.patch.yml", "cordis.patch.yaml" })
        {
            var text = ReadTextOrNull(Path.Combine(profileDirectory, name));
            if (text is not null)
            {
                builder.AppendLine(text);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 极窄的行式扫描：在 YAML 文本里找 <c>- id: &lt;pluginId&gt;</c> 块内的 <c>key: value</c>。
    /// 只用在高置信度键上；找不到就返回 null（调用方记 Notes，不猜）。
    /// </summary>
    private static string? ReadPluginValue(string text, string pluginId, string key)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var lines = text.Split('\n');
        var inside = false;
        var pluginIndent = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (trimmed.StartsWith("- id:", StringComparison.Ordinal) || trimmed.StartsWith("id:", StringComparison.Ordinal))
            {
                var value = trimmed[(trimmed.IndexOf(':') + 1)..].Trim().Trim('"', '\'');
                if (inside && indent <= pluginIndent && !string.Equals(value, pluginId, StringComparison.Ordinal))
                {
                    inside = false;
                }

                if (string.Equals(value, pluginId, StringComparison.Ordinal))
                {
                    inside = true;
                    pluginIndent = indent;
                }

                continue;
            }

            if (!inside)
            {
                continue;
            }

            if (trimmed.StartsWith(key + ":", StringComparison.Ordinal))
            {
                return trimmed[(key.Length + 1)..].Trim().Trim('"', '\'');
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string?> ReadEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "DSH_PERMISSION_MODE", "DSH_TELEMETRY_MODE", "DSH_TELEMETRY_OTLP_URL" })
        {
            result[name] = Environment.GetEnvironmentVariable(name);
        }

        return result;
    }

    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
