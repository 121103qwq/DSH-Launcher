using System.IO;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// 实例里的一个 dsh profile（<c>$DSH_HOME/profiles/&lt;name&gt;</c>）。
/// <paramref name="Exists"/> 为 false 表示目录还没被 dsh 初始化（shipped 名字首次使用时才生成）。
/// </summary>
public sealed record DshProfileInfo(
    string Name,
    bool Exists,
    IReadOnlyList<string> Bundles,
    string? PatchReload,
    bool IsShipped,
    bool IsLauncherManaged,
    string? Error)
{
    /// <summary>该 profile 是否带 Web App bundle——只有它 Launcher 才能显示界面。</summary>
    public bool IsWebApp => Bundles.Any(bundle =>
        string.Equals(bundle, DshCoreBundles.WebApp, StringComparison.OrdinalIgnoreCase));

    /// <summary>是否允许用 Launcher 启动（非 Web profile 明确拒绝，见 work-log/60 Q4）。</summary>
    public bool CanStartByLauncher => IsWebApp;

    public string BundlesText => Bundles.Count == 0 ? "（未初始化）" : string.Join(" + ", Bundles);

    public string SourceText => IsLauncherManaged ? "Launcher 生成" : IsShipped ? "dsh 内置模板" : "自定义";

    /// <summary>列表里显示的一行：名字 + bundle 组成 + 来源。</summary>
    public string DisplayText => $"{Name} · {BundlesText} · {SourceText}";

    /// <summary>无法由 Launcher 启动时的原因（可直接显示给用户）。</summary>
    public string? UnstartableReason
    {
        get
        {
            if (Error is not null)
            {
                return $"profile 目录读取失败：{Error}";
            }

            if (IsLauncherManaged)
            {
                return "这是 Launcher 自己生成的安全模式 / 逐插件定位 profile，不能当常规 profile 使用。";
            }

            return IsWebApp
                ? null
                : $"「{Name}」不含 Web App bundle（{DshCoreBundles.WebApp}），Launcher 无法显示它的界面。"
                    + "要跑这类 profile，请在命令行用 dsh --profile 启动。";
        }
    }
}

/// <summary>
/// dsh profile 的发现与解析。契约来源：上游 <c>packages/boot/app-boot/src/profile.ts</c>——
/// 一个 profile 是 <c>$DSH_HOME/profiles/&lt;name&gt;</c> 目录，内含 <c>package.json</c>
/// （<c>dsh.profile.bundles</c> 有序 bundle 列表 + <c>patchReload</c>）与 <c>cordis.patch.yml</c>；
/// 共享依赖在 <c>profiles/node_modules</c>；shipped 模板按名首次使用时由 dsh 自动初始化。
/// <para>
/// 本服务**只读**：不创建、不修改任何 profile 目录，避免写坏 dsh 自己的结构。
/// </para>
/// </summary>
public sealed class DshProfileService
{
    public const string DefaultProfileName = "web";

    /// <summary>dsh 的 <c>PROFILES_DIR</c>。</summary>
    public const string ProfilesDirectoryName = "profiles";

    /// <summary>
    /// dsh 内置（shipped）模板：按名首次使用时自动初始化。
    /// 只用于在 UI 上说明"这个名字会变成什么"，不作为我们创建 profile 的依据。
    /// 来源：上游 <c>app-boot/src/profile.ts</c> 的 <c>PROFILE_TEMPLATES</c>（harness 有源码哨兵）。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> ShippedTemplates =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["acp"] = [DshCoreBundles.Base, "@deepseek-ai/dsh-acp-app"],
            ["web"] = [DshCoreBundles.Base, DshCoreBundles.WebApp],
            ["headless"] = [DshCoreBundles.Base, "@deepseek-ai/dsh-headless"],
            ["sdk"] = [DshCoreBundles.Base, "@deepseek-ai/dsh-sdk-app"],
            ["sdk-minimal"] = ["@deepseek-ai/dsh-sdk-minimal"]
        };

    /// <summary>Launcher 自己生成的 profile 前缀（安全模式 <c>.dsh-safe</c>、逐插件定位 <c>.dsh-bisect</c>）。</summary>
    public static bool IsLauncherManaged(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.TrimStart().StartsWith('.');

    public static string ProfilesRoot(ManagerInstance instance) =>
        Path.Combine(instance.DshHome, ProfilesDirectoryName);

    /// <summary>枚举实例里已存在的 profile（跳过 <c>node_modules</c> 与 Launcher 自己的点开头 profile）。</summary>
    public IReadOnlyList<DshProfileInfo> List(ManagerInstance instance, bool includeLauncherManaged = false)
    {
        var root = ProfilesRoot(instance);
        var result = new List<DshProfileInfo>();
        if (!Directory.Exists(root))
        {
            return result;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(name)
                    || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsLauncherManaged(name) && !includeLauncherManaged)
                {
                    continue;
                }

                result.Add(Describe(instance, name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到 profiles 根时按"没有 profile"处理；启动与插件操作仍会各自报错。
        }

        return result
            .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>解析单个 profile（目录不存在时返回"未初始化"的描述，而不是 null）。</summary>
    public DshProfileInfo Describe(ManagerInstance instance, string? name)
    {
        var profileName = string.IsNullOrWhiteSpace(name) ? DefaultProfileName : name.Trim();
        var directory = Path.Combine(ProfilesRoot(instance), profileName);
        var isShipped = ShippedTemplates.TryGetValue(profileName, out var template);
        var manifestPath = Path.Combine(directory, "package.json");
        if (!File.Exists(manifestPath))
        {
            return new DshProfileInfo(
                profileName,
                Exists: false,
                Bundles: isShipped ? template! : Array.Empty<string>(),
                PatchReload: null,
                IsShipped: isShipped,
                IsLauncherManaged: IsLauncherManaged(profileName),
                Error: null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var bundles = Array.Empty<string>();
            string? patchReload = null;
            if (document.RootElement.TryGetProperty("dsh", out var dsh)
                && dsh.ValueKind == JsonValueKind.Object
                && dsh.TryGetProperty("profile", out var profile)
                && profile.ValueKind == JsonValueKind.Object)
            {
                if (profile.TryGetProperty("bundles", out var bundleList) && bundleList.ValueKind == JsonValueKind.Array)
                {
                    bundles = bundleList.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()!)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .ToArray();
                }

                if (profile.TryGetProperty("patchReload", out var reload) && reload.ValueKind == JsonValueKind.String)
                {
                    patchReload = reload.GetString();
                }
            }

            return new DshProfileInfo(
                profileName,
                Exists: true,
                Bundles: bundles,
                PatchReload: patchReload,
                IsShipped: isShipped,
                IsLauncherManaged: IsLauncherManaged(profileName),
                Error: null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new DshProfileInfo(
                profileName,
                Exists: true,
                Bundles: Array.Empty<string>(),
                PatchReload: null,
                IsShipped: isShipped,
                IsLauncherManaged: IsLauncherManaged(profileName),
                Error: ex.Message);
        }
    }

    /// <summary>
    /// 当前生效的 profile 名：取实例设置里的 <c>ActiveProfile</c>；未设置或为空时用 dsh 的
    /// <c>web</c> 别名（保持既有行为不变）。
    /// </summary>
    public static string ResolveActiveName(ManagerInstance instance, VersionSettingsService? settings)
    {
        var configured = settings?.Read(instance).ActiveProfile;
        return string.IsNullOrWhiteSpace(configured) ? DefaultProfileName : configured.Trim();
    }
}
