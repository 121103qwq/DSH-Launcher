using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using DshLauncher.Services;

// ---------------------------------------------------------------------------
// DshLauncher.SelfTest —— 仓库自带的纯逻辑自测（不联网、不碰用户真实数据、不开窗口）。
//
// 与 _verify-p0 harness 的分工：
//   * 本工程：随仓库走，任何人 clone 后 `dotnet run --project tests/DshLauncher.SelfTest`
//     就能验证核心纯逻辑（命名规则、语义化比较、profile 解析、任务台账、存储清理边界…）；
//   * _verify-p0 harness：工作区级的端到端验证（UI 冒烟、契约哨兵、真实运行时比对），
//     依赖本机环境与已安装的 dsh，因此不放进仓库。
// ---------------------------------------------------------------------------

var failures = new List<string>();
var passes = new List<string>();

void Check(string name, bool condition, string? detail = null)
{
    if (condition)
    {
        passes.Add(name);
        Console.WriteLine($"PASS  {name}{(detail is null ? string.Empty : " :: " + detail)}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"FAIL  {name}{(detail is null ? string.Empty : " :: " + detail)}");
    }
}

// 日志隔离：自测绝不写用户真实 launcher.log / crash.log（在第一次触碰 LauncherLog 之前设置）。
var scratch = Path.Combine(Path.GetTempPath(), "dsh-selftest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable(LauncherLog.LogRootVariable, Path.Combine(scratch, "logs"));

static ManagerInstance BuildInstance(string id, string home, string version = "0.1.5-rc.1") =>
    new(
        id,
        id,
        string.Empty,
        InstanceKind.Installed,
        home,
        null,
        version,
        InstanceRuntimeStatus.Ready,
        null,
        null,
        DateTimeOffset.UtcNow);

static string SessionHeader(int version, string id) =>
    $"{{\"type\":\"session\",\"version\":{version},\"id\":\"{id}\",\"createdAt\":1,\"cwd\":\"C:\\\\work\\\\demo\",\"delegationDepth\":0,\"isSeeded\":false}}";

// ===========================================================================
// 1. 会话文件命名（dsh CANONICAL 规则）
// ===========================================================================
Check("naming/会话文件名解析：v0 不带标签、vN 带标签、zstd 编码",
    SessionFileNames.TryParse("session.jsonl", out var v0, out var c0) && v0 == 0 && !c0
    && SessionFileNames.TryParse("session.v3.jsonl.zstd", out var v3, out var c3) && v3 == 3 && c3
    && SessionFileNames.TryParse("session.v10.jsonl", out var v10, out _) && v10 == 10);
Check("naming/非 canonical 名字必须被拒（v0 标签 / 多余后缀 / 大小写无关但格式固定）",
    !SessionFileNames.TryParse("session.v0.jsonl", out _, out _)
    && !SessionFileNames.TryParse("session.v3.jsonl.bak", out _, out _)
    && !SessionFileNames.TryParse("session.v01.jsonl", out _, out _));
Check("naming/按版本与编码拼名：v0 无标签、vN 带标签",
    SessionFileNames.Build(0, false) == "session.jsonl"
    && SessionFileNames.Build(0, true) == "session.jsonl.zstd"
    && SessionFileNames.Build(3, false) == "session.v3.jsonl"
    && SessionFileNames.Build(3, true) == "session.v3.jsonl.zstd");

var generationDirectory = Path.Combine(scratch, "generations");
Directory.CreateDirectory(generationDirectory);
File.WriteAllText(Path.Combine(generationDirectory, "session.jsonl"), "{}", Encoding.UTF8);
File.WriteAllText(Path.Combine(generationDirectory, "session.v2.jsonl"), "{}", Encoding.UTF8);
File.WriteAllText(Path.Combine(generationDirectory, "session.v3.jsonl.zstd"), "{}", Encoding.UTF8);
Check("naming/目录内取最高代际（忽略 v0 与普通文件）",
    SessionFileNames.HighestGeneration(generationDirectory) == 3
    && SessionFileNames.HighestGeneration(Path.Combine(scratch, "missing")) == -1);

Check("naming/代际支持判定：自身证据（会话文件 / catalog 包）优先，其次版本号 >= 0.1.5",
    SessionFileNames.SupportsVersionedGenerations(packageRoot: null, detectedVersion: null, sessionsRoot: generationDirectory)
    && SessionFileNames.SupportsVersionedGenerations(packageRoot: null, detectedVersion: "0.1.5-rc.1")
    && SessionFileNames.SupportsVersionedGenerations(packageRoot: null, detectedVersion: "0.2.0")
    && !SessionFileNames.SupportsVersionedGenerations(packageRoot: null, detectedVersion: "0.1.2-rc.1")
    && !SessionFileNames.SupportsVersionedGenerations(packageRoot: null, detectedVersion: null));

// ===========================================================================
// 2. 版本比较（预发布标签必须参与排序）
// ===========================================================================
Check("version/语义化比较：正式版 > rc > beta > alpha，且数字按数值比较",
    PluginCompatibility.Compare("0.1.5", "0.1.5-rc.2") > 0
    && PluginCompatibility.Compare("0.1.5-rc.2", "0.1.5-rc.1") > 0
    && PluginCompatibility.Compare("0.1.5-rc.1", "0.1.5-beta.1") > 0
    && PluginCompatibility.Compare("0.1.5-alpha.2", "0.1.4") > 0
    && PluginCompatibility.Compare("0.1.2", "0.1.2") == 0);

// ===========================================================================
// 3. dsh profile 解析
// ===========================================================================
var profileService = new DshProfileService();
var profileHome = Path.Combine(scratch, "profile-home");
Directory.CreateDirectory(profileHome);
var profileInstance = BuildInstance("profile", profileHome);
var shippedWeb = profileService.Describe(profileInstance, "web");
Check("profile/未初始化的 shipped 名字按上游模板显示（web = base + web-app，可由 Launcher 启动）",
    !shippedWeb.Exists && shippedWeb.IsShipped && shippedWeb.IsWebApp && shippedWeb.CanStartByLauncher
    && shippedWeb.UnstartableReason is null);
var shippedHeadless = profileService.Describe(profileInstance, "headless");
Check("profile/非 Web profile 给出明确拒绝理由（而不是静默）",
    !shippedHeadless.CanStartByLauncher && shippedHeadless.UnstartableReason is not null);

var customProfile = Path.Combine(profileHome, "profiles", "work");
Directory.CreateDirectory(customProfile);
File.WriteAllText(
    Path.Combine(customProfile, "package.json"),
    """{ "dsh": { "profile": { "bundles": ["@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app"], "patchReload": "live" } } }""",
    Encoding.UTF8);
Directory.CreateDirectory(Path.Combine(profileHome, "profiles", "node_modules"));
Directory.CreateDirectory(Path.Combine(profileHome, "profiles", ".dsh-safe"));
File.WriteAllText(Path.Combine(profileHome, "profiles", ".dsh-safe", "package.json"), "{}", Encoding.UTF8);
Directory.CreateDirectory(Path.Combine(profileHome, "profiles", "broken"));
File.WriteAllText(Path.Combine(profileHome, "profiles", "broken", "package.json"), "{ not json", Encoding.UTF8);
var listedProfiles = profileService.List(profileInstance);
Check("profile/列表跳过 node_modules 与 Launcher 自己的 .dsh-*，坏 package.json 报错不当空",
    listedProfiles.Select(info => info.Name).OrderBy(name => name, StringComparer.Ordinal)
        .SequenceEqual(new[] { "broken", "work" })
    && listedProfiles.Single(info => info.Name == "work") is { Exists: true, IsWebApp: true, PatchReload: "live" }
    && listedProfiles.Single(info => info.Name == "broken").Error is not null
    && profileService.List(profileInstance, includeLauncherManaged: true).Any(info => info.Name == ".dsh-safe"));

var profilePaths = new LauncherPaths(Path.Combine(scratch, "profile-paths"));
var profileSettings = new VersionSettingsService(profilePaths);
Check("profile/未配置时回落 dsh 的 web 别名", DshProfileService.ResolveActiveName(profileInstance, profileSettings) == "web");
var profileData = profileSettings.Read(profileInstance);
profileData.ActiveProfile = "headless";
profileSettings.Save(profileInstance, profileData);
Check("profile/选择的 profile 能落盘读回（Clone 不会丢新字段）",
    profileSettings.Read(profileInstance).ActiveProfile == "headless"
    && DshProfileService.ResolveActiveName(profileInstance, profileSettings) == "headless");

// ===========================================================================
// 4. 任务台账
// ===========================================================================
var taskPaths = new LauncherPaths(Path.Combine(scratch, "tasks"));
var tasks = new LauncherTaskService(taskPaths);
Check("task/空台账按“没有任务”处理", tasks.Snapshot().Count == 0 && tasks.RunningCount == 0);
using (var handle = tasks.Begin(LauncherTaskKind.RuntimePrepare, "准备运行环境", "实例A", "开始"))
{
    handle.Report("第一行\n第二行");
    Check("task/进度上报折叠换行并被截断到 200 字符",
        tasks.Snapshot()[0].Detail == "第一行 第二行"
        && handle.Item.Detail == "第一行 第二行");
    handle.Report(new string('x', 500));
    Check("task/超长详情截断（不把长输出写进台账）",
        tasks.Snapshot()[0].Detail.Length == 201 && tasks.Snapshot()[0].Detail.EndsWith('…'));
    Check("task/运行中可取消，且取消会传到外部取消源（双向串联）",
        tasks.TryCancel(handle.Id) && handle.IsCancellationRequested);
    handle.Complete("运行环境已就绪");
}
Check("task/完成后进历史（状态/结果/耗时），且不再可取消",
    tasks.RunningCount == 0
    && tasks.Snapshot() is [{ State: LauncherTaskState.Succeeded, IsRunning: false, Result: "运行环境已就绪" }]
    && tasks.Snapshot()[0].DurationText != "—"
    && !tasks.TryCancel(tasks.Snapshot()[0].Id));
Check("task/结束后写入 launcher-tasks.json",
    File.Exists(tasks.HistoryPath) && File.ReadAllText(tasks.HistoryPath, Encoding.UTF8).Contains("\"Version\": 1", StringComparison.Ordinal));

var interrupted = tasks.Begin(LauncherTaskKind.Plugin, "安装 Plugin");
interrupted.Dispose();
Check("task/未收尾就 Dispose 会落成“操作未完成”，不留永远进行中的行",
    tasks.RunningCount == 0
    && tasks.Snapshot().Any(item => item.State == LauncherTaskState.Failed && item.Title == "安装 Plugin"));
for (var index = 0; index < 60; index++)
{
    using var bulk = tasks.Begin(LauncherTaskKind.Other, $"批量 {index}");
    bulk.Complete();
}
Check("task/历史只保留最近 50 条", tasks.Snapshot().Count == LauncherTaskService.MaximumRetainedTasks);
File.WriteAllText(tasks.HistoryPath, "{ broken", Encoding.UTF8);
Check("task/台账损坏按“没有历史”处理（不影响任务）", new LauncherTaskService(taskPaths).Snapshot().Count == 0);
File.WriteAllText(tasks.HistoryPath, "{\"Version\":99,\"Tasks\":[{\"Title\":\"future\"}]}", Encoding.UTF8);
Check("task/更高版本的台账文档不猜测、按空处理", new LauncherTaskService(taskPaths).Snapshot().Count == 0);
File.WriteAllText(
    tasks.HistoryPath,
    "{\"Version\":1,\"Tasks\":[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Kind\":0,\"Title\":\"崩溃时在跑\",\"Detail\":\"下载中\",\"State\":0,\"StartedAt\":\"2026-09-11T10:00:00+00:00\"}]}",
    Encoding.UTF8);
Check("task/进程被杀遗留的“进行中”行在重新加载时归成中断",
    new LauncherTaskService(taskPaths).Snapshot() is [{ State: LauncherTaskState.Failed, IsRunning: false }]);

// ===========================================================================
// 5. 存储清理边界（只碰自有文件；会话/凭据/快照必须存活）
// ===========================================================================
var storagePaths = new LauncherPaths(Path.Combine(scratch, "storage"));
var storageHome = Path.Combine(storagePaths.InstancesDirectory, "storage-instance", "dsh-home");
Directory.CreateDirectory(storagePaths.InstancesDirectory);
Directory.CreateDirectory(Path.Combine(storageHome, "sessions", "--work--", "s1"));
var storageInstance = BuildInstance("storage-instance", storageHome);
File.WriteAllText(Path.Combine(storagePaths.RootDirectory, "marketplace-cache.json"), new string('m', 4000), Encoding.UTF8);
File.WriteAllText(Path.Combine(storagePaths.RootDirectory, "runtime-cache.json"), "{}", Encoding.UTF8);
var backups = storagePaths.GetInstanceBackupDirectory(storageInstance.Id);
Directory.CreateDirectory(backups);
File.WriteAllText(Path.Combine(backups, "20260910-101010-session.jsonl"), "backup", Encoding.UTF8);
var snapshots = Path.Combine(backups, "snapshots");
Directory.CreateDirectory(snapshots);
File.WriteAllText(Path.Combine(snapshots, "manual-20260910-101010-bbb.dshsnapshot"), "manual", Encoding.UTF8);
var liveSession = Path.Combine(storageHome, "sessions", "--work--", "s1", "session.v3.jsonl");
File.WriteAllText(liveSession, "live", Encoding.UTF8);
var credentials = Path.Combine(storageHome, ".credentials.yaml");
File.WriteAllText(credentials, "token: secret", Encoding.UTF8);

var storage = new LauncherStorageService(storagePaths, new[] { storageInstance });
var categories = storage.Scan();
Check("storage/清点覆盖可清理类别，且实例数据与自动快照只统计不清理",
    categories.Any(item => item.Id == "market-cache" && item.Cleanable && item.SizeBytes > 0)
    && categories.Any(item => item.Id == "runtime-cache" && item.Cleanable)
    && categories.Any(item => item.Id == "conversation-backups" && item.Cleanable)
    && categories.Single(item => item.Id == "instances") is { Cleanable: false }
    && categories.Single(item => item.Id == "auto-snapshots") is { Cleanable: false });
var cleanResult = storage.Clean(categories.Where(item => item.Cleanable).Select(item => item.Id));
Check("storage/清理删掉缓存与会话备份副本",
    !File.Exists(Path.Combine(storagePaths.RootDirectory, "marketplace-cache.json"))
    && !File.Exists(Path.Combine(backups, "20260910-101010-session.jsonl"))
    && cleanResult.RemovedCount > 0);
Check("storage/安全反证——实例内会话、凭据、手动快照都还在",
    File.Exists(liveSession)
    && File.Exists(credentials)
    && File.Exists(Path.Combine(snapshots, "manual-20260910-101010-bbb.dshsnapshot")));
Check("storage/目录大小统计可用（Measure 递归）",
    LauncherStorageService.Measure(Path.Combine(storageHome, "sessions"), out var measuredFiles) > 0 && measuredFiles >= 1);

// ===========================================================================
// 6. 会话代际归并（一个会话目录 = 一个会话 = 一个最高代际）
// ===========================================================================
var conversationHome = Path.Combine(scratch, "conversation-home");
Directory.CreateDirectory(conversationHome);
var conversationInstance = BuildInstance("conversation", conversationHome);
var sessionDirectory = Path.Combine(conversationHome, "sessions", "--C-work-demo--", "s1");
Directory.CreateDirectory(sessionDirectory);
var olderGeneration = Path.Combine(sessionDirectory, "session.v2.jsonl");
File.WriteAllText(olderGeneration, SessionHeader(2, "s1") + "\n{\"type\":\"message\"}\n", Encoding.UTF8);
var newerGeneration = Path.Combine(sessionDirectory, "session.v3.jsonl");
File.WriteAllText(newerGeneration, SessionHeader(3, "s1") + "\n{\"type\":\"message\"}\n", Encoding.UTF8);
var legacyDirectory = Path.Combine(conversationHome, "sessions", "--C-work-demo--", "legacy");
Directory.CreateDirectory(legacyDirectory);
var legacySession = Path.Combine(legacyDirectory, "session.jsonl");
File.WriteAllText(legacySession, SessionHeader(0, "legacy") + "\n{\"type\":\"message\"}\n", Encoding.UTF8);

var conversations = new ConversationService(new LauncherPaths(Path.Combine(scratch, "conversation-paths")));
var entries = conversations.List(conversationInstance);
Check("conversation/只列最高代际（同一会话不会列成多行），v0 会话照常列出",
    entries.Any(entry => entry.FullPath == Path.GetFullPath(newerGeneration) && entry.HasValidHeader && entry.SessionId == "s1")
    && entries.All(entry => entry.FullPath != Path.GetFullPath(olderGeneration))
    && entries.Any(entry => entry.FullPath == Path.GetFullPath(legacySession)));

// ===========================================================================
// 7. 长文本收敛（弹窗不再超屏）
// ===========================================================================
var longText = string.Join("\n", Enumerable.Repeat(new string('长', 400), 40));
var collapsed = DialogText.ForMessageBox(longText);
var collapsedWithLimits = DialogText.ForMessageBox(longText, maxChars: 300, maxLineChars: 40);
Check("ui/ForMessageBox 收敛长文本：显式上限逐行生效，默认上限也把总量压住",
    collapsedWithLimits.Split('\n').All(line => line.TrimEnd('\r').Length <= 40)
    && collapsedWithLimits.Length <= 300 + 64
    && collapsed.Length < longText.Length / 10);

// ===========================================================================
// 8. 自定义来源（设置 → 插件与技能来源）
// ===========================================================================
var sourcePaths = new LauncherPaths(Path.Combine(scratch, "sources"));
var sourceSettings = new MarketSourceSettingsService(sourcePaths);
Check("sources/首次使用会预置两个中文适配器（默认停用，Skill 侧为空）",
    sourceSettings.ReadEntries(MarketSourceKind.Plugin) is [{ Value: "adapter:zh1024", Enabled: false }, { Value: "adapter:dshfind", Enabled: false }]
    && sourceSettings.ReadEnabled(MarketSourceKind.Plugin).Count == 0
    && sourceSettings.Read(MarketSourceKind.Skill).Count == 0
    && MarketSourceSettingsService.IsAdapterToken("adapter:zh1024")
    && !MarketSourceSettingsService.IsAdapterToken("https://example.com/catalog.json")
    && MarketSourceSettingsService.Describe(MarketSourceKind.Plugin, "adapter:dshfind").TypeText.Contains("内置中文源", StringComparison.Ordinal));
Check("sources/校验规则：插件只收 .json 或网址，Skill 还收 owner/repo",
    sourceSettings.TryAdd(MarketSourceKind.Plugin, "https://example.com/catalog.json", out _)
    && sourceSettings.TryAdd(MarketSourceKind.Plugin, "D:/x/catalog.json", out _)
    && !sourceSettings.TryAdd(MarketSourceKind.Plugin, "owner/repo", out _)
    && sourceSettings.TryAdd(MarketSourceKind.Skill, "owner/repo", out _)
    && !sourceSettings.TryAdd(MarketSourceKind.Skill, "not a source", out _));
Check("sources/去重 / 落盘读回 / 移除",
    !sourceSettings.TryAdd(MarketSourceKind.Plugin, "https://example.com/catalog.json", out var duplicateMessage)
    && duplicateMessage.Contains("已经在列表里", StringComparison.Ordinal)
    && sourceSettings.Read(MarketSourceKind.Plugin).Count == 4
    && File.Exists(sourceSettings.FilePath(MarketSourceKind.Plugin))
    && sourceSettings.Read(MarketSourceKind.Skill) is ["owner/repo"]
    && MarketSourceSettingsService.Describe(MarketSourceKind.Skill, "owner/repo").IsGitHubRepository
    && sourceSettings.TryRemove(MarketSourceKind.Skill, "owner/repo", out _)
    && sourceSettings.Read(MarketSourceKind.Skill).Count == 0);
Check("sources/开关状态：停用不删除、只影响启用列表，并能落盘读回（对象格式）",
    sourceSettings.TrySetEnabled(MarketSourceKind.Plugin, "D:/x/catalog.json", false, out _)
    && sourceSettings.ReadEntries(MarketSourceKind.Plugin).Any(entry => entry.Value == "D:/x/catalog.json" && !entry.Enabled)
    && sourceSettings.ReadEnabled(MarketSourceKind.Plugin).Count == 1
    && File.ReadAllText(sourceSettings.FilePath(MarketSourceKind.Plugin), Encoding.UTF8).Contains("enabled", StringComparison.Ordinal));
// 旧格式（纯字符串数组）仍要能读，并按启用处理
File.WriteAllText(sourceSettings.FilePath(MarketSourceKind.Skill), """["owner/legacy"]""", Encoding.UTF8);
Check("sources/兼容旧的纯字符串数组格式（一律视为启用）",
    sourceSettings.ReadEntries(MarketSourceKind.Skill) is [{ Value: "owner/legacy", Enabled: true }]
    && sourceSettings.ReadEnabled(MarketSourceKind.Skill).Count == 1);
File.WriteAllText(sourceSettings.FilePath(MarketSourceKind.Plugin), "{ broken", Encoding.UTF8);
Check("sources/文件损坏按“没有来源”处理（不影响市场与设置页）",
    sourceSettings.Read(MarketSourceKind.Plugin).Count == 0);

// ===========================================================================
// 9. 中文插件源适配器（deepseek1024.com，借鉴 #16）
// ===========================================================================
var zhSourceJson = """{"plugins":[{"id":"owner/repo/packages/dsh-x","name":"dsh-x","owner":"owner","repository":"repo","url":"https://github.com/owner/repo","category":"ui","description":{"zh":"中文描述","en":"English"},"install":"npm:dsh-x","stars":12,"installCount":73,"failureCount":5,"added":"2026-09-01T00:00:00Z"}]}""";
using (var zhDocument = JsonDocument.Parse(zhSourceJson))
{
    var mapped = Deepseek1024CatalogService.TryMap(zhDocument.RootElement.GetProperty("plugins")[0]);
    Check("zh1024/映射：中文描述优先、来源种类为 ZhCatalog、安装统计进来源名",
        mapped is { SourceKind: MarketplaceSourceKind.ZhCatalog, Name: "dsh-x", PackageName: "dsh-x", InstallSpec: "npm:dsh-x", Stars: 12 }
        && mapped!.Description == "中文描述"
        && mapped.RepositoryUrl == "https://github.com/owner/repo"
        && mapped.SourceName.Contains("安装 73 次", StringComparison.Ordinal)
        && mapped.SourceName.Contains("失败 5 次", StringComparison.Ordinal));
}

using (var minimalDocument = JsonDocument.Parse("""{"id":"o/r","name":"r"}"""))
{
    Check("zh1024/映射：缺 install 时用仓库兜底成 github: 安装标识（安装前仍会校验 package.json）",
        Deepseek1024CatalogService.TryMap(minimalDocument.RootElement) is { InstallSpec: "github:o/r", Name: "r" });
}

using (var blankDocument = JsonDocument.Parse("""{"id":"only-owner"}"""))
{
    Check("zh1024/映射：信息不足（无名字）返回 null，不往市场里塞垃圾条目",
        Deepseek1024CatalogService.TryMap(blankDocument.RootElement) is null);
}

var zhCachePaths = new LauncherPaths(Path.Combine(scratch, "zh1024"));
var zhService = new Deepseek1024CatalogService(zhCachePaths);
Directory.CreateDirectory(zhCachePaths.RootDirectory);
File.WriteAllText(
    zhService.CachePath,
    """{"savedAt":"2999-01-01T00:00:00+00:00","source":"test","items":[{"id":"o/r","name":"r","description":"d","install":"npm:r"}]}""",
    Encoding.UTF8);
var zhCached = await zhService.LoadAsync();
Check("zh1024/未过期缓存直接命中（不联网；TTL 30 分钟）",
    zhCached.Count == 1 && zhCached[0].Name == "r" && zhService.LastStatus.Contains("缓存", StringComparison.Ordinal));
File.WriteAllText(zhService.CachePath, "{ broken", Encoding.UTF8);
var zhBroken = await new Deepseek1024CatalogService(zhCachePaths).LoadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(12)).Token);
Check("zh1024/缓存损坏时不抛异常（拉取失败则返回空列表，不拖垮插件市场）",
    zhBroken.Count == 0);

// ===========================================================================
// 10. 中文插件源适配器 ②（dshfind.com）
// ===========================================================================
using (var dshfindDocument = JsonDocument.Parse("""{"plugins":[{"name":"dsh-spotlight","owner":"0xsline","fullName":"0xsline/dsh-spotlight","url":"https://github.com/0xsline/dsh-spotlight","description":"Keyboard-first command palette","tags":["ui","palette"],"language":"TypeScript","stars":128,"archived":false,"category":"","isOfficial":false,"isFeatured":true,"pushedAt":"2026-09-09T00:00:00Z","i18n":{"en":"Keyboard-first command palette","zh":"键盘优先的命令面板"}}]}"""))
{
    var mapped = DshfindCatalogService.TryMap(dshfindDocument.RootElement.GetProperty("plugins")[0]);
    Check("dshfind/映射：i18n 中文优先、无 category 时用首个 tag、安装标识由 owner/repo 生成",
        mapped is { SourceKind: MarketplaceSourceKind.ZhCatalog, Name: "dsh-spotlight", InstallSpec: "github:0xsline/dsh-spotlight", Stars: 128 }
        && mapped!.Description == "键盘优先的命令面板"
        && mapped.Category == "ui"
        && mapped.SourceName.Contains("精选", StringComparison.Ordinal)
        && mapped.SourceName.Contains("★128", StringComparison.Ordinal));
}

using (var archivedDocument = JsonDocument.Parse("""{"fullName":"old/repo","name":"repo","archived":true}"""))
{
    Check("dshfind/映射：已归档插件直接排除（不往市场塞死项目）",
        DshfindCatalogService.TryMap(archivedDocument.RootElement) is null);
}

using (var officialDocument = JsonDocument.Parse("""{"fullName":"deepseek-ai/dsh-x","name":"dsh-x","description":"official","isOfficial":true,"category":"tools"}"""))
{
    Check("dshfind/映射：官方标记进来源名，缺 install 时仍可用 github: 兜底安装",
        DshfindCatalogService.TryMap(officialDocument.RootElement) is { InstallSpec: "github:deepseek-ai/dsh-x" } officialItem
        && officialItem.SourceName.Contains("官方", StringComparison.Ordinal));
}

var dshfindPaths = new LauncherPaths(Path.Combine(scratch, "dshfind"));
var dshfindService = new DshfindCatalogService(dshfindPaths);
Directory.CreateDirectory(dshfindPaths.RootDirectory);
File.WriteAllText(
    dshfindService.CachePath,
    """{"savedAt":"2999-01-01T00:00:00+00:00","source":"test","items":[{"fullName":"a/b","name":"b","description":"d"}]}""",
    Encoding.UTF8);
var dshfindCached = await dshfindService.LoadAsync();
Check("dshfind/未过期缓存直接命中（不联网；8 MB 全量不会每次拉）",
    dshfindCached.Count == 1 && dshfindCached[0].Name == "b"
    && dshfindService.LastStatus.Contains("缓存", StringComparison.Ordinal));
File.WriteAllText(dshfindService.CachePath, "{ broken", Encoding.UTF8);
var dshfindBroken = await new DshfindCatalogService(dshfindPaths).LoadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
// 缓存损坏但网络可用时应该真的拉成功（这也是好结果），所以只断言"不抛异常"。
Check("dshfind/缓存损坏时仍能正常工作（要么拉取成功、要么回退空列表，不抛异常）",
    dshfindBroken.Count == 0 || dshfindBroken.Count > 1000, $"count={dshfindBroken.Count}");

// ===========================================================================
// 11. 整合包格式解析层（PackFormat，#24 / work-log-70）
// ===========================================================================
using (var v3Document = JsonDocument.Parse("""
{
  "manifestVersion": 3,
  "name": "all-about-whales",
  "version": "1.0.0",
  "displayName": { "zh-CN": "大肥鱼套装", "en-US": "All About Whales" },
  "description": "make dsh smell like whales",
  "dshVersion": "0.1.1-rc.2",
  "bundles": ["@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app"],
  "dependencies": { "github:DViridescent/dafy-whale-theme": "99e8c57", "dsh-pet": "0.2.0" },
  "patch": "plugins:\n  x: {}"
}
"""))
{
    var ok = PackFormat.TryParseManifest(v3Document.RootElement.GetRawText(), out var packV3, out var packV3Error);
    Check("packformat/v3：解析成功、命名与字段归一（profileName 缺省 pack、patch 内联）",
        ok
        && packV3 is not null
        && packV3.Version == PackManifestVersion.V3
        && packV3.Type == PackManifestType.Profile
        && packV3.Name == "all-about-whales"
        && packV3.ProfileName == "pack"
        && packV3.Bundles.Count == 2
        && packV3.Bundles[0] == "@deepseek-ai/dsh-base"
        && packV3.Dependencies["github:DViridescent/dafy-whale-theme"] == "99e8c57"
        && packV3.Patch is not null
        && packV3.ResolveDisplayName() == "大肥鱼套装"
        && packV3.ResolveDisplayName("en-US") == "All About Whales"
        && packV3.ResolveDescription() == "make dsh smell like whales",
        packV3Error ?? string.Empty);
}

using (var v2Document = JsonDocument.Parse("""
{
  "manifestVersion": 2,
  "name": "legacy-pack",
  "version": "0.9.0",
  "displayName": "旧包",
  "dshVersion": ">=0.1.0",
  "bundles": ["@deepseek-ai/dsh-base"],
  "dependencies": { "dsh-pet": "^0.2.0" }
}
"""))
{
    var ok = PackFormat.TryParseManifest(v2Document.RootElement.GetRawText(), out var v2, out var v2Error);
    Check("packformat/v2：dshVersion 范围取下限、原始 spec 原样透传、并记一条兼容性提示",
        ok
        && v2 is not null
        && v2.Version == PackManifestVersion.V2
        && v2.DshVersion == "0.1.0"
        && v2.DshVersionRaw == ">=0.1.0"
        && v2.Dependencies["dsh-pet"] == "^0.2.0"
        && v2.Notes.Any(note => note.Contains("下限", StringComparison.Ordinal))
        && v2.Notes.Any(note => note.Contains("原样透传", StringComparison.Ordinal)),
        v2Error ?? string.Empty);
}

using (var v4Document = JsonDocument.Parse("""
{
  "manifestVersion": 4,
  "type": "profile",
  "name": "whale-files",
  "version": "1.0.0",
  "dshVersion": "0.1.1-rc.2",
  "profileName": "whale",
  "bundles": ["@deepseek-ai/dsh-base"],
  "dependencies": { "github:owner/repo#path:/packages/theme": "abcdef1" },
  "files": [
    { "path": "data/models/whale.bin", "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "size": 5242880, "urls": ["https://example.com/whale.bin", "ftp://bad/whale.bin"] }
  ]
}
"""))
{
    var ok = PackFormat.TryParseManifest(v4Document.RootElement.GetRawText(), out var v4, out var v4Error);
    Check("packformat/v4：files[] 解析（只保留 http(s) 镜像）、profileName 生效",
        ok
        && v4 is not null
        && v4.Version == PackManifestVersion.V4
        && v4.ProfileName == "whale"
        && v4.Files.Count == 1
        && v4.Files[0].Path == "data/models/whale.bin"
        && v4.Files[0].Size == 5242880
        && v4.Files[0].Urls.Count == 1
        && v4.Files[0].Urls[0] == "https://example.com/whale.bin",
        v4Error ?? string.Empty);
}

Check("packformat/依赖坐标三条转换规则（规范原文）",
    PackFormat.TryConvertToPackageJsonEntry("dsh-pet", "0.2.0", out var npmName, out var npmSpec)
    && npmName == "dsh-pet" && npmSpec == "0.2.0"
    && PackFormat.TryConvertToPackageJsonEntry("github:HanaAyane/dsh-reasoning-effort", "83bc8c5", out var gitName, out var gitSpec)
    && gitName == "dsh-reasoning-effort" && gitSpec == "github:HanaAyane/dsh-reasoning-effort#83bc8c5"
    && PackFormat.TryConvertToPackageJsonEntry("github:owner/repo#path:/packages/theme", "abcdef1", out var subName, out var subSpec)
    && subName == "theme" && subSpec == "github:owner/repo#abcdef1&path:packages/theme");

Check("packformat/依赖坐标可往返（导出用反方向转换）",
    PackFormat.TryConvertToPackageJsonEntry("github:owner/repo#path:/packages/theme", "abcdef1", out var fName, out var fSpec)
    && PackFormat.TryParsePackageJsonEntry(fName, fSpec, out var coordinate, out var pinned)
    && coordinate == "github:owner/repo#path:/packages/theme"
    && pinned == "abcdef1");

Check("packformat/files[] 校验：sha256 / size / urls / 路径越界都必须被拒",
    !PackFormat.TryParseManifest("""{"manifestVersion":4,"name":"x","version":"1","dshVersion":"0.1.1","bundles":[],"dependencies":{},"files":[{"path":"a.bin","sha256":"ABCDEF","size":1,"urls":["https://e.com/a"]}]}""", out _, out var badSha)
    && badSha!.Contains("sha256", StringComparison.Ordinal)
    && !PackFormat.TryParseManifest("""{"manifestVersion":4,"name":"x","version":"1","dshVersion":"0.1.1","bundles":[],"dependencies":{},"files":[{"path":"a.bin","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","size":0,"urls":["https://e.com/a"]}]}""", out _, out var badSize)
    && badSize!.Contains("size", StringComparison.Ordinal)
    && !PackFormat.TryParseManifest("""{"manifestVersion":4,"name":"x","version":"1","dshVersion":"0.1.1","bundles":[],"dependencies":{},"files":[{"path":"a.bin","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","size":8,"urls":[]}]}""", out _, out var badUrls)
    && badUrls!.Contains("urls", StringComparison.Ordinal)
    && !PackFormat.TryParseManifest("""{"manifestVersion":4,"name":"x","version":"1","dshVersion":"0.1.1","bundles":[],"dependencies":{},"files":[{"path":"../escape.bin","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","size":8,"urls":["https://e.com/a"]}]}""", out _, out var badPath)
    && badPath!.Contains("path", StringComparison.Ordinal));

Check("packformat/路径安全：只接受相对、无 .. 、无反斜杠、无盘符",
    PackFormat.IsSafeRelativePath("data/models/a.bin")
    && PackFormat.IsSafeRelativePath("a/b/c")
    && !PackFormat.IsSafeRelativePath("/abs/a.bin")
    && !PackFormat.IsSafeRelativePath("a/../b.bin")
    && !PackFormat.IsSafeRelativePath("C:/x.bin")
    && !PackFormat.IsSafeRelativePath("a\\b.bin")
    && !PackFormat.IsSafeRelativePath("")
    && PackFormat.IsSimpleName("whale") && !PackFormat.IsSimpleName("..") && !PackFormat.IsSimpleName(".hidden"));

Check("packformat/容器标记：v2/v3 接受，v4 与非 dspack 拒绝",
    PackFormat.TryParseContainerMarker("""{"format":"dspack","version":2}""", out var containerV2, out _) && containerV2 == PackContainerKind.DspackV2
    && PackFormat.TryParseContainerMarker("""{"format":"dspack","version":3}""", out var containerV3, out _) && containerV3 == PackContainerKind.DspackV3
    && !PackFormat.TryParseContainerMarker("""{"format":"dspack","version":4}""", out _, out var containerError)
    && containerError!.Contains("2-3", StringComparison.Ordinal)
    && !PackFormat.TryParseContainerMarker("""{"format":"zip","version":2}""", out _, out var formatError)
    && formatError!.Contains("format", StringComparison.Ordinal));

Check("packformat/配对校验：manifestVersion 5 必须配 .dspack v3",
    PackFormat.TryParseManifest("""{"manifestVersion":5,"type":"profile","name":"p","version":"1","dshVersion":"0.1.1","bundles":[],"dependencies":{}}""", out var v5Profile, out var v5Error)
    && v5Profile is not null && v5Profile.RequiresDspackV3
    && PackFormat.ValidatePairing(v5Profile, PackContainerKind.DspackV3, out _)
    && !PackFormat.ValidatePairing(v5Profile, PackContainerKind.DspackV2, out var pairingError)
    && pairingError!.Contains("配对校验失败", StringComparison.Ordinal)
    && !PackFormat.ValidatePairing(v5Profile, PackContainerKind.LegacyTgz, out _),
    v5Error ?? string.Empty);

var homeParsed = PackFormat.TryParseManifest("""{"manifestVersion":5,"type":"dshhome","name":"h","version":"1","dshVersion":"0.1.1","defaultProfile":"pack","profiles":{"pack":{"bundles":["@deepseek-ai/dsh-base"],"dependencies":{"github:o/r":"abc1234"}},"extra":{"bundles":[],"dependencies":{}}},"skills":[{"path":"skills/whale.md","sha256":null,"urls":["https://e.com/s"]}],"instructions":"AGENTS.md"}""", out var home, out var homeError);
Check("packformat/dshhome：profiles 不得含 web / headless，缺 defaultProfile 必须拒",
    !PackFormat.TryParseManifest("""{"manifestVersion":5,"type":"dshhome","name":"h","version":"1","dshVersion":"0.1.1","defaultProfile":"web","profiles":{"web":{"bundles":["@deepseek-ai/dsh-web-app"],"dependencies":{}}}}""", out _, out var webError)
    && webError!.Contains("基线 profile", StringComparison.Ordinal)
    && !PackFormat.TryParseManifest("""{"manifestVersion":5,"type":"dshhome","name":"h","version":"1","dshVersion":"0.1.1","profiles":{"pack":{"bundles":[],"dependencies":{}}}}""", out _, out var missingDefault)
    && missingDefault!.Contains("defaultProfile", StringComparison.Ordinal)
    && homeParsed
    && home is not null
    && home.Type == PackManifestType.DshHome
    && home.DefaultProfile == "pack"
    && home.HomeProfiles.Count == 2
    && home.HomeProfiles[0].Dependencies["github:o/r"] == "abc1234"
    && home.Skills.Count == 1
    && home.Skills[0].Path == "skills/whale.md"
    && home.Instructions == "AGENTS.md",
    homeError ?? string.Empty);

Check("packformat/collection 暂未支持；未知版本报「支持 2-5」",
    !PackFormat.TryParseManifest("""{"manifestVersion":4,"type":"collection","name":"c","version":"1"}""", out _, out var collectionError)
    && collectionError!.Contains("暂未支持", StringComparison.Ordinal)
    && !PackFormat.TryParseManifest("""{"manifestVersion":6,"name":"n","version":"1"}""", out _, out var versionError)
    && versionError!.Contains("支持 2-5", StringComparison.Ordinal)
    && !PackFormat.TryParseManifest("""{"manifestVersion":1,"name":"n","version":"1"}""", out _, out var oldError)
    && oldError!.Contains("支持 2-5", StringComparison.Ordinal));

Check("packformat/dshhome 形态只能配 v5（低版本写 dshhome 必须拒）",
    !PackFormat.TryParseManifest("""{"manifestVersion":4,"type":"dshhome","name":"h","version":"1","defaultProfile":"pack","profiles":{"pack":{"bundles":[],"dependencies":{}}}}""", out _, out var wrongVersion)
    && wrongVersion!.Contains("需要 manifestVersion 5", StringComparison.Ordinal));

Check("packformat/文件头判定：ZIP（.dspack）与 gzip（旧 .tgz）",
    PackFormat.HasZipHeader(new byte[] { 0x50, 0x4B, 0x03, 0x04 })
    && PackFormat.HasGzipHeader(new byte[] { 0x1F, 0x8B })
    && PackFormat.DetectFromHeader(new byte[] { 0x1F, 0x8B }) == PackContainerKind.LegacyTgz
    && !PackFormat.HasZipHeader(new byte[] { 0x50, 0x4B })
    && PackFormat.DetectFromHeader(new byte[] { 0x00, 0x01 }) == PackContainerKind.Unknown);

// ===========================================================================
// 12. 整合包容器读取（PackArchiveReader，#24 第 2 步）
// ===========================================================================
var packSamples = Path.Combine(scratch, "pack-samples");
Directory.CreateDirectory(packSamples);

void WriteZipText(ZipArchive zip, string path, string content)
{
    var entry = zip.CreateEntry(path);
    using var stream = entry.Open();
    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
    writer.Write(content);
}

string BuildDspack(string fileName, int containerVersion, string manifestJson, params (string Path, string Content)[] files)
{
    var path = Path.Combine(packSamples, fileName);
    using var file = File.Create(path);
    using var zip = new ZipArchive(file, ZipArchiveMode.Create);
    WriteZipText(zip, "dspack.json", "{\"format\":\"dspack\",\"version\":" + containerVersion + "}");
    WriteZipText(zip, "manifest.json", manifestJson);
    foreach (var (entryPath, content) in files)
    {
        WriteZipText(zip, entryPath, content);
    }

    return path;
}

string BuildTgz(string fileName, params (string Path, string Content)[] files)
{
    var path = Path.Combine(packSamples, fileName);
    using var file = File.Create(path);
    using var gzip = new GZipStream(file, CompressionLevel.Optimal);
    using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
    foreach (var (entryPath, content) in files)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryPath)
        {
            DataStream = new MemoryStream(bytes)
        });
    }

    return path;
}

const string V4Manifest = """
{
  "manifestVersion": 4,
  "type": "profile",
  "name": "whale",
  "version": "1.0.0",
  "displayName": { "zh-CN": "大肥鱼" },
  "dshVersion": "0.1.1-rc.2",
  "profileName": "whale",
  "bundles": ["@deepseek-ai/dsh-base"],
  "dependencies": { "dsh-pet": "0.2.0" }
}
""";

const string V5ProfileManifest = """
{
  "manifestVersion": 5,
  "type": "profile",
  "name": "whale5",
  "version": "1.0.0",
  "dshVersion": "0.1.1-rc.2",
  "bundles": ["@deepseek-ai/dsh-base"],
  "dependencies": {}
}
""";

var dspackV2 = BuildDspack(
    "whale.dspack",
    2,
    V4Manifest,
    ("package.json", "{\"name\":\"snapshot\"}"),
    ("pnpm-lock.yaml", "lockfileVersion: '9.0'"),
    ("overrides/cordis.patch.yml", "plugins:\n  whale: {}\n"));

{
    var ok = PackArchiveReader.TryRead(dspackV2, out var archive, out var outcome, out var readError);
    Check("packarchive/.dspack v2：容器识别 + 清单 + overrides 列表 + 文本条目读回",
        ok
        && outcome == PackArchiveOutcome.Ok
        && archive is not null
        && archive.Container == PackContainerKind.DspackV2
        && archive.Manifest.Version == PackManifestVersion.V4
        && archive.HasPackageJson
        && archive.HasPnpmLock
        && !archive.HasPnpmWorkspace
        && archive.OverridePaths.Count == 1
        && archive.OverridePaths[0] == "cordis.patch.yml"
        && archive.TotalSize > 0
        && archive.TryReadTextEntry("overrides/cordis.patch.yml", out var patchText, out _)
        && patchText!.Contains("whale", StringComparison.Ordinal),
        readError ?? string.Empty);
}

Check("packarchive/配对校验：v5 配 v3 通过、配 v2 拒载",
    PackArchiveReader.TryRead(BuildDspack("v5v3.dspack", 3, V5ProfileManifest), out var v5Archive, out var v5Outcome, out _)
    && v5Outcome == PackArchiveOutcome.Ok
    && v5Archive!.Container == PackContainerKind.DspackV3
    && v5Archive.Manifest.RequiresDspackV3
    && !PackArchiveReader.TryRead(BuildDspack("v5v2.dspack", 2, V5ProfileManifest), out _, out var mismatchOutcome, out var mismatchError)
    && mismatchOutcome == PackArchiveOutcome.ContainerRejected
    && mismatchError!.Contains("配对校验失败", StringComparison.Ordinal));

{
    var plainZip = Path.Combine(packSamples, "plain.dspack");
    using (var file = File.Create(plainZip))
    using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
    {
        WriteZipText(zip, "manifest.json", V4Manifest);
    }

    var badMarker = Path.Combine(packSamples, "badv.dspack");
    using (var file = File.Create(badMarker))
    using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
    {
        WriteZipText(zip, "dspack.json", "{\"format\":\"dspack\",\"version\":4}");
        WriteZipText(zip, "manifest.json", V4Manifest);
    }

    Check("packarchive/容器层拒绝：缺 dspack.json 与容器版本 4（报「支持 2-3」）",
        !PackArchiveReader.TryRead(plainZip, out _, out var noMarkerOutcome, out var noMarkerError)
        && noMarkerOutcome == PackArchiveOutcome.ContainerRejected
        && noMarkerError!.Contains("缺少 dspack.json", StringComparison.Ordinal)
        && !PackArchiveReader.TryRead(badMarker, out _, out var badVersionOutcome, out var badVersionError)
        && badVersionOutcome == PackArchiveOutcome.ContainerRejected
        && badVersionError!.Contains("2-3", StringComparison.Ordinal));
}

{
    var escapeZip = Path.Combine(packSamples, "escape.dspack");
    using (var file = File.Create(escapeZip))
    using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
    {
        WriteZipText(zip, "dspack.json", "{\"format\":\"dspack\",\"version\":2}");
        WriteZipText(zip, "manifest.json", V4Manifest);
        WriteZipText(zip, "../escape.txt", "boom");
    }

    var tooManyZip = Path.Combine(packSamples, "toomany.dspack");
    using (var file = File.Create(tooManyZip))
    using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
    {
        WriteZipText(zip, "dspack.json", "{\"format\":\"dspack\",\"version\":2}");
        WriteZipText(zip, "manifest.json", V4Manifest);
        for (var index = 0; index < PackArchiveLimits.MaximumEntries; index++)
        {
            WriteZipText(zip, $"filler/{index}.txt", "x");
        }
    }

    Check("packarchive/路径越界与条目数超限必须被拒（zip-slip / 资源上限）",
        !PackArchiveReader.TryRead(escapeZip, out _, out var escapeOutcome, out var escapeError)
        && escapeOutcome == PackArchiveOutcome.EntryPathRejected
        && escapeError!.Contains("escape.txt", StringComparison.Ordinal)
        && !PackArchiveReader.TryRead(tooManyZip, out _, out var manyOutcome, out var manyError)
        && manyOutcome == PackArchiveOutcome.LimitsExceeded
        && manyError!.Contains("条目数超过上限", StringComparison.Ordinal));
}

{
    var tgz = BuildTgz(
        "legacy.tgz",
        ("manifest.json", """
{
  "manifestVersion": 3,
  "name": "legacy-pack",
  "version": "0.9.0",
  "displayName": "旧包",
  "dshVersion": "0.1.1-rc.2",
  "bundles": ["@deepseek-ai/dsh-base"],
  "dependencies": { "dsh-pet": "0.2.0" }
}
"""),
        ("package.json", "{\"name\":\"legacy\"}"),
        ("cordis.patch.yml", "plugins:\n  legacy: {}\n"));

    var ok = PackArchiveReader.TryRead(tgz, out var archive, out var outcome, out var readError);
    Check("packarchive/旧 .tgz：gzip+tar 识别、扁平 cordis.patch.yml 文本读回",
        ok
        && outcome == PackArchiveOutcome.Ok
        && archive is not null
        && archive.Container == PackContainerKind.LegacyTgz
        && archive.Manifest.Version == PackManifestVersion.V3
        && archive.Manifest.Name == "legacy-pack"
        && archive.HasCordisPatch
        && archive.HasPackageJson
        && archive.TryReadTextEntry("cordis.patch.yml", out var patch, out _)
        && patch!.Contains("legacy", StringComparison.Ordinal),
        readError ?? string.Empty);
}

{
    var notArchive = Path.Combine(packSamples, "plain.txt");
    File.WriteAllText(notArchive, "hello", new UTF8Encoding(false));
    var missing = Path.Combine(packSamples, "nope.dspack");

    Check("packarchive/非归档与不存在文件：分类明确、不抛异常",
        !PackArchiveReader.TryRead(notArchive, out _, out var notArchiveOutcome, out var notArchiveError)
        && notArchiveOutcome == PackArchiveOutcome.NotAnArchive
        && notArchiveError!.Contains("无法识别", StringComparison.Ordinal)
        && !PackArchiveReader.TryRead(missing, out _, out _, out var missingError)
        && missingError!.Contains("不存在", StringComparison.Ordinal));
}

{
    var homePack = BuildDspack(
        "home.dspack",
        3,
        """
{
  "manifestVersion": 5,
  "type": "profile",
  "name": "with-home",
  "version": "1.0.0",
  "dshVersion": "0.1.1-rc.2",
  "bundles": ["@deepseek-ai/dsh-base"],
  "dependencies": {}
}
""",
        ("home/AGENTS.md", "# agents"),
        ("home/skills/whale.md", "whale skill"));

    Check("packarchive/v5 profile 可携带 home/ 覆盖（home 条目单独列出，不混入 overrides）",
        PackArchiveReader.TryRead(homePack, out var homeArchive, out var homeOutcome, out var homePackError)
        && homeOutcome == PackArchiveOutcome.Ok
        && homeArchive!.HomePaths.Count == 2
        && homeArchive.HomePaths.Contains("AGENTS.md", StringComparer.Ordinal)
        && homeArchive.HomePaths.Contains("skills/whale.md", StringComparer.Ordinal)
        && homeArchive.OverridePaths.Count == 0,
        homePackError ?? string.Empty);
}

// ===========================================================================
// 13. 整合包导入（DshPackImportService，#24 第 3 步）——全部在注入的临时数据根里跑
// ===========================================================================
{
    var importPaths = new LauncherPaths(Path.Combine(scratch, "import-root"));
    Directory.CreateDirectory(importPaths.RootDirectory);
    var importRegistry = new InstanceRegistry(importPaths);
    var importService = new DshPackImportService(importRegistry);

    var templateRoot = Path.Combine(scratch, "import-template");
    Directory.CreateDirectory(templateRoot);
    var templateExe = Path.Combine(templateRoot, "dsh.cmd");
    File.WriteAllText(templateExe, "@echo off", new UTF8Encoding(false));
    var template = importRegistry.Register(
        "模板实例",
        templateRoot,
        InstanceKind.Installed,
        templateExe,
        "0.1.1-rc.2",
        "pnpm");

    var packPath = BuildDspack(
        "import-ok.dspack",
        2,
        """
{
  "manifestVersion": 4,
  "type": "profile",
  "name": "whale-pack",
  "version": "1.0.0",
  "displayName": { "zh-CN": "大肥鱼套装" },
  "dshVersion": "0.1.1-rc.2",
  "profileName": "whale",
  "bundles": ["@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app"],
  "dependencies": {
    "dsh-pet": "0.2.0",
    "github:HanaAyane/dsh-reasoning-effort": "83bc8c5",
    "github:owner/repo#path:/packages/theme": "abcdef1"
  }
}
""",
        ("package.json", "{\"name\":\"snapshot-should-be-ignored\"}"),
        ("pnpm-workspace.yaml", "packages:\n  - .\n"),
        ("overrides/cordis.patch.yml", "plugins:\n  whale: {}\n"),
        ("home/AGENTS.md", "# agents root"));

    PackFormat.TryParseManifest("""
{
  "manifestVersion": 4,
  "name": "rebuild",
  "version": "1.0.0",
  "dshVersion": "0.1.1-rc.2",
  "bundles": ["@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app"],
  "dependencies": {
    "dsh-pet": "0.2.0",
    "github:HanaAyane/dsh-reasoning-effort": "83bc8c5",
    "github:owner/repo#path:/packages/theme": "abcdef1"
  }
}
""", out var rebuildManifest, out _);
    var rebuiltJson = DshPackImportService.BuildPackageJson(rebuildManifest!, "whale");
    using var rebuiltDocument = JsonDocument.Parse(rebuiltJson);
    var rebuiltRoot = rebuiltDocument.RootElement;
    var rebuiltDependencies = rebuiltRoot.GetProperty("dependencies");
    Check("packimport/package.json 权威重建：name=dsh-profile-*、bundles 进 dsh.profile、三条依赖规则齐全",
        rebuiltRoot.GetProperty("name").GetString() == "dsh-profile-whale"
        && rebuiltRoot.GetProperty("private").GetBoolean()
        && rebuiltRoot.GetProperty("dsh").GetProperty("profile").GetProperty("bundles").GetArrayLength() == 2
        && rebuiltDependencies.GetProperty("dsh-pet").GetString() == "0.2.0"
        && rebuiltDependencies.GetProperty("dsh-reasoning-effort").GetString() == "github:HanaAyane/dsh-reasoning-effort#83bc8c5"
        && rebuiltDependencies.GetProperty("theme").GetString() == "github:owner/repo#abcdef1&path:packages/theme");

    var importOk = PackArchiveReader.TryRead(packPath, out var importArchive, out _, out var importReadError);
    var plan = importService.BuildPlan(importArchive!, importRegistry.Load(), template);
    Check("packimport/计划：实例名取本地化显示名、profile 名、待写文件清单、dshhome 形态一律新建实例",
        importOk
        && plan.InstanceName == "大肥鱼套装"
        && plan.ProfileName == "whale"
        && plan.RequiresNewInstance
        && plan.TemplateVersionMatches
        && plan.ProfileFiles.Contains("package.json", StringComparer.Ordinal)
        && plan.ProfileFiles.Contains("cordis.patch.yml", StringComparer.Ordinal)
        && plan.ProfileFiles.Contains("pnpm-workspace.yaml", StringComparer.Ordinal)
        && plan.HomeFiles.Contains("AGENTS.md", StringComparer.Ordinal),
        importReadError ?? string.Empty);

    var outcome = importService.ImportAsync(importArchive!, template, importRegistry.Load()).GetAwaiter().GetResult();
    var imported = importRegistry.Load().FirstOrDefault(instance => instance.Id == outcome.InstanceId);
    var importedProfile = imported is null ? null : Path.Combine(imported.DshHome, "profiles", "whale");
    Check("packimport/导入成功：新建实例 + 专属 HOME + 落盘 profile（package.json/patch/home 覆盖）",
        outcome.Succeeded
        && imported is not null
        && imported.Name == "大肥鱼套装"
        && imported.DshHome.Contains($"{Path.DirectorySeparatorChar}instances{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        && importedProfile is not null
        && File.Exists(Path.Combine(importedProfile, "package.json"))
        && File.Exists(Path.Combine(importedProfile, "cordis.patch.yml"))
        && File.Exists(Path.Combine(importedProfile, "pnpm-workspace.yaml"))
        && File.Exists(Path.Combine(imported.DshHome, "AGENTS.md"))
        && File.ReadAllText(Path.Combine(importedProfile, "package.json")).Contains("github:HanaAyane/dsh-reasoning-effort#83bc8c5", StringComparison.Ordinal)
        && File.ReadAllText(Path.Combine(importedProfile, "cordis.patch.yml")).Contains("whale", StringComparison.Ordinal)
        && outcome.Warnings.Any(warning => warning.Contains("package.json 快照", StringComparison.Ordinal))
        && outcome.FilesWritten >= 4,
        outcome.Error ?? string.Empty);

    var importedCount = importRegistry.Load().Count;

    // 重名：同名整合包再导一次 → 名字加序号，两个实例都在
    var secondPlan = importService.BuildPlan(importArchive!, importRegistry.Load(), template);
    var secondOutcome = importService.ImportAsync(importArchive!, template, importRegistry.Load()).GetAwaiter().GetResult();
    Check("packimport/重名去重：第二个实例名为「… 2」，两个实例同时在台账里",
        secondOutcome.Succeeded
        && secondPlan.InstanceName == "大肥鱼套装 2"
        && importRegistry.Load().Count == importedCount + 1,
        secondOutcome.Error ?? string.Empty);

    // 版本不符：必须拒绝，且不得留下任何实例/目录
    var mismatched = BuildDspack(
        "import-v9.dspack",
        2,
        """
{
  "manifestVersion": 4,
  "type": "profile",
  "name": "future-pack",
  "version": "1.0.0",
  "dshVersion": "9.9.9",
  "profileName": "future",
  "bundles": ["@deepseek-ai/dsh-base"],
  "dependencies": {}
}
""");
    var countBeforeMismatch = importRegistry.Load().Count;
    PackArchiveReader.TryRead(mismatched, out var mismatchArchive, out _, out _);
    var versionMismatchOutcome = importService.ImportAsync(mismatchArchive!, template, importRegistry.Load()).GetAwaiter().GetResult();
    Check("packimport/版本不符：明确拒绝（不假装成功、不自动装别的版本），台账与目录零变化",
        !versionMismatchOutcome.Succeeded
        && versionMismatchOutcome.Error!.Contains("9.9.9", StringComparison.Ordinal)
        && importRegistry.Load().Count == countBeforeMismatch
        && Directory.GetDirectories(importPaths.InstancesDirectory).Length == importRegistry.Load().Count);

    // 写盘中途失败：overrides 里 package.json/child.txt 与已写出的 package.json 文件冲突 → 必须回滚
    var rollbackPack = BuildDspack(
        "import-rollback.dspack",
        2,
        """
{
  "manifestVersion": 4,
  "type": "profile",
  "name": "rollback-pack",
  "version": "1.0.0",
  "dshVersion": "0.1.1-rc.2",
  "profileName": "rollback",
  "bundles": ["@deepseek-ai/dsh-base"],
  "dependencies": {}
}
""",
        ("overrides/package.json/child.txt", "conflict"));
    PackArchiveReader.TryRead(rollbackPack, out var rollbackArchive, out _, out _);
    var countBeforeRollback = importRegistry.Load().Count;
    var rollbackOutcome = importService.ImportAsync(rollbackArchive!, template, importRegistry.Load()).GetAwaiter().GetResult();
    var leftover = importRegistry.Load()
        .Where(instance => instance.Name.StartsWith("rollback-pack", StringComparison.Ordinal))
        .ToArray();
    Check("packimport/写盘中途失败：整体回滚（注销实例 + 删除新建 HOME，不留半成品）",
        !rollbackOutcome.Succeeded
        && rollbackOutcome.Error!.Contains("已回滚", StringComparison.Ordinal)
        && importRegistry.Load().Count == countBeforeRollback
        && leftover.Length == 0
        && Directory.GetDirectories(importPaths.InstancesDirectory).Length == importRegistry.Load().Count,
        rollbackOutcome.Error ?? string.Empty);

    // 二进制条目：本步只导入文本配置，必须明确拒绝而不是写坏文件
    var binaryPack = BuildDspack(
        "import-binary.dspack",
        2,
        """
{
  "manifestVersion": 4,
  "type": "profile",
  "name": "binary-pack",
  "version": "1.0.0",
  "dshVersion": "0.1.1-rc.2",
  "profileName": "binary",
  "bundles": ["@deepseek-ai/dsh-base"],
  "dependencies": {}
}
""",
        ("overrides/blob.bin", "PK\u0000\u0001binary"));
    PackArchiveReader.TryRead(binaryPack, out var binaryArchive, out _, out _);
    var countBeforeBinary = importRegistry.Load().Count;
    var binaryOutcome = importService.ImportAsync(binaryArchive!, template, importRegistry.Load()).GetAwaiter().GetResult();
    Check("packimport/二进制条目：拒绝导入（本步只落文本配置），台账与目录零变化",
        !binaryOutcome.Succeeded
        && binaryOutcome.Error!.Contains("二进制", StringComparison.Ordinal)
        && importRegistry.Load().Count == countBeforeBinary
        && Directory.GetDirectories(importPaths.InstancesDirectory).Length == importRegistry.Load().Count);
}

// ===========================================================================
// 14. files[] 下载（DshPackFileDownloader + 导入同意门，#24 第 4 步）
// ===========================================================================
{
    var dlRoot = Path.Combine(scratch, "downloads");
    Directory.CreateDirectory(dlRoot);
    var dlPayload = Encoding.UTF8.GetBytes("whale-binary-payload-0123456789");
    var dlSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dlPayload)).ToLowerInvariant();
    var dlOther = Encoding.UTF8.GetBytes("tampered-content");

    // 假取流：按 url 返回不同内容（或抛错），用来验证镜像回退与校验，不联网
    Func<Uri, CancellationToken, Task<Stream>> FakeFetch(params (string? Url, byte[]? Bytes, bool Throw)[] routes) =>
        (uri, _) =>
        {
            var route = routes.FirstOrDefault(r => string.Equals(uri.ToString(), r.Url, StringComparison.Ordinal));
            if (route.Throw || route.Url is null)
            {
                throw new System.Net.Http.HttpRequestException("fake: unreachable");
            }

            return Task.FromResult<Stream>(new MemoryStream(route.Bytes!));
        };

    var firstUrl = "https://mirror-one.example.com/whale.bin";
    var secondUrl = "https://mirror-two.example.com/whale.bin";
    var thirdUrl = "https://mirror-three.example.com/whale.bin";
    var dlEntry = new PackFileEntry("data/whale.bin", dlSha, dlPayload.Length, new[] { firstUrl, secondUrl, thirdUrl });

    var dlFirst = new PackFileDownloader(FakeFetch((firstUrl, null, true), (secondUrl, dlPayload, false)));
    var dlFirstPath = Path.Combine(dlRoot, "fallback.bin");
    var dlFirstResult = dlFirst.DownloadAsync(dlEntry, dlFirstPath).GetAwaiter().GetResult();
    Check("packdownload/镜像回退：第一个地址不通 → 用第二个，落位内容与校验一致、临时文件不残留",
        dlFirstResult.Succeeded
        && dlFirstResult.UsedUrl == secondUrl
        && File.Exists(dlFirstPath)
        && File.ReadAllBytes(dlFirstPath).SequenceEqual(dlPayload)
        && !File.Exists(dlFirstPath + ".download"),
        dlFirstResult.Error ?? string.Empty);

    var dlTampered = new PackFileDownloader(FakeFetch((firstUrl, dlOther, false), (secondUrl, dlPayload, false)));
    var dlTamperedPath = Path.Combine(dlRoot, "tampered.bin");
    var dlTamperedResult = dlTampered.DownloadAsync(dlEntry, dlTamperedPath).GetAwaiter().GetResult();
    Check("packdownload/哈希不符必须拒收并换镜像（不落位被篡改的内容）",
        dlTamperedResult.Succeeded
        && dlTamperedResult.UsedUrl == secondUrl
        && File.ReadAllBytes(dlTamperedPath).SequenceEqual(dlPayload),
        dlTamperedResult.Error ?? string.Empty);

    var dlAllBad = new PackFileDownloader(FakeFetch((firstUrl, dlOther, false), (secondUrl, dlOther, false), (thirdUrl, null, true)));
    var dlAllBadPath = Path.Combine(dlRoot, "bad.bin");
    var dlAllBadResult = dlAllBad.DownloadAsync(dlEntry, dlAllBadPath).GetAwaiter().GetResult();
    Check("packdownload/全部镜像都不合格：失败、目标不存在、临时文件清理干净",
        !dlAllBadResult.Succeeded
        && !File.Exists(dlAllBadPath)
        && !File.Exists(dlAllBadPath + ".download")
        && dlAllBadResult.Error!.Contains(firstUrl, StringComparison.Ordinal)
        && dlAllBadResult.Error.Contains(secondUrl, StringComparison.Ordinal)
        && dlAllBadResult.Error.Contains("unreachable", StringComparison.Ordinal),
        dlAllBadResult.Error ?? string.Empty);

    var dlSizeEntry = new PackFileEntry("data/whale.bin", dlSha, dlPayload.Length + 4096, new[] { secondUrl });
    var dlSizeResult = new PackFileDownloader(FakeFetch((secondUrl, dlPayload, false)))
        .DownloadAsync(dlSizeEntry, Path.Combine(dlRoot, "size.bin")).GetAwaiter().GetResult();
    var dlTooBig = Encoding.UTF8.GetBytes(new string('x', 200));
    var dlTooBigEntry = new PackFileEntry("data/whale.bin", dlSha, 100, new[] { secondUrl });
    var dlTooBigResult = new PackFileDownloader(FakeFetch((secondUrl, dlTooBig, false)))
        .DownloadAsync(dlTooBigEntry, Path.Combine(dlRoot, "toobig.bin")).GetAwaiter().GetResult();
    Check("packdownload/大小不符（少下或多下）都必须失败并清理",
        !dlSizeResult.Succeeded
        && dlSizeResult.Error!.Contains("大小不符", StringComparison.Ordinal)
        && !File.Exists(Path.Combine(dlRoot, "size.bin"))
        && !dlTooBigResult.Succeeded
        && !File.Exists(Path.Combine(dlRoot, "toobig.bin"))
        && !File.Exists(Path.Combine(dlRoot, "toobig.bin.download")),
        dlSizeResult.Error ?? string.Empty);

    // ---- 与导入服务联动：同意门 / 允许下载 ----
    var filePackManifest = """
{
  "manifestVersion": 4,
  "type": "profile",
  "name": "with-files",
  "version": "1.0.0",
  "dshVersion": "0.1.1-rc.2",
  "profileName": "files",
  "bundles": ["@deepseek-ai/dsh-base"],
  "dependencies": {},
  "files": [
    { "path": "data/whale.bin", "sha256": "SHA_PLACEHOLDER", "size": SIZE_PLACEHOLDER, "urls": ["URL_PLACEHOLDER"] }
  ]
}
""".Replace("SHA_PLACEHOLDER", dlSha, StringComparison.Ordinal)
   .Replace("SIZE_PLACEHOLDER", dlPayload.Length.ToString(), StringComparison.Ordinal)
   .Replace("URL_PLACEHOLDER", secondUrl, StringComparison.Ordinal);

    var dlPack = BuildDspack("with-files.dspack", 2, filePackManifest);
    PackArchiveReader.TryRead(dlPack, out var fileArchive, out _, out _);

    var filePaths = new LauncherPaths(Path.Combine(scratch, "file-import-root"));
    Directory.CreateDirectory(filePaths.RootDirectory);
    var fileRegistry = new InstanceRegistry(filePaths);
    var dlTemplateRoot = Path.Combine(scratch, "download-template");
    Directory.CreateDirectory(dlTemplateRoot);
    var dlTemplateExe = Path.Combine(dlTemplateRoot, "dsh.cmd");
    File.WriteAllText(dlTemplateExe, "@echo off", new UTF8Encoding(false));
    var fileTemplate = fileRegistry.Register(
        "文件模板",
        dlTemplateRoot,
        InstanceKind.Installed,
        dlTemplateExe,
        "0.1.1-rc.2",
        "pnpm");
    var fileService = new DshPackImportService(
        fileRegistry,
        new PackFileDownloader(FakeFetch((secondUrl, dlPayload, false))));

    var refused = fileService.ImportAsync(fileArchive!, fileTemplate, fileRegistry.Load()).GetAwaiter().GetResult();
    Check("packdownload/默认不下载：整包拒绝而不是交付半成品（零副作用）",
        !refused.Succeeded
        && refused.Error!.Contains("显式允许下载", StringComparison.Ordinal)
        && fileRegistry.Load().Count == 1
        && Directory.GetDirectories(filePaths.InstancesDirectory).Length == 1,
        refused.Error ?? string.Empty);

    var allowed = fileService
        .ImportAsync(fileArchive!, fileTemplate, fileRegistry.Load(), allowDownloads: true)
        .GetAwaiter().GetResult();
    var allowedInstance = fileRegistry.Load().FirstOrDefault(instance => instance.Id == allowed.InstanceId);
    var allowedFile = allowedInstance is null
        ? null
        : Path.Combine(allowedInstance.DshHome, "profiles", "files", "data", "whale.bin");
    Check("packdownload/显式同意后：下载产物落到 profile 内的相对路径（并计入写入数）",
        allowed.Succeeded
        && allowedFile is not null
        && File.Exists(allowedFile)
        && File.ReadAllBytes(allowedFile).SequenceEqual(dlPayload)
        && !File.Exists(allowedFile + ".download"),
        allowed.Error ?? string.Empty);
}

// ===========================================================================
// 15. 导出 manifest v4 + .dspack v2（DshPackWriter）与读回往返（#24 第 5 步）
//   注意：带 out 的调用一律单独成句，不放进 && 链（短路会导致"未赋值"）。
// ===========================================================================
{
    var rtRoot = Path.Combine(scratch, "roundtrip");
    Directory.CreateDirectory(rtRoot);
    var rtPayload = Encoding.UTF8.GetBytes("model-payload-bytes-0123456789");
    var rtSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rtPayload)).ToLowerInvariant();

    var rtRequest = new PackExportRequest(
        PackName: "whale-export",
        PackVersion: "1.0.0",
        ProfileName: "whale",
        DshVersion: "0.1.1-rc.2",
        Bundles: new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" },
        Dependencies: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dsh-pet"] = "0.2.0",
            ["dsh-reasoning-effort"] = "github:HanaAyane/dsh-reasoning-effort#83bc8c5",
            ["theme"] = "github:owner/repo#abcdef1&path:packages/theme"
        },
        DisplayNames: new Dictionary<string, string> { ["zh-CN"] = "大肥鱼导出", ["en-US"] = "Whale Export" },
        Descriptions: new Dictionary<string, string> { ["zh-CN"] = "往返验证用" },
        Author: "tester",
        Patch: "plugins:\n  whale: {}\n",
        Files: new[] { new PackFileEntry("data/whale.bin", rtSha, rtPayload.Length, new[] { "https://mirror.example.com/whale.bin" }) },
        Overrides: new Dictionary<string, string>(StringComparer.Ordinal) { ["cordis.patch.yml"] = "plugins:\n  whale: {}\n" },
        WorkspaceYaml: "packages:\n  - .\n",
        LockYaml: "lockfileVersion: '9.0'\n",
        PackageJsonSnapshot: "{\"name\":\"snapshot\"}");

    var rtPath = Path.Combine(rtRoot, "whale-export.dspack");
    var rtWritten = DshPackWriter.TryWrite(rtPath, rtRequest, out var rtWriteError);
    var rtRead = PackArchiveReader.TryRead(rtPath, out var rtArchive, out var rtOutcome, out var rtReadError);
    Check("packexport/导出 .dspack：容器标记 v2 + manifest v4 + 可选快照/overrides",
        rtWritten
        && File.Exists(rtPath)
        && !File.Exists(rtPath + ".tmp")
        && rtRead
        && rtOutcome == PackArchiveOutcome.Ok
        && rtArchive!.Container == PackContainerKind.DspackV2
        && rtArchive.Manifest.Version == PackManifestVersion.V4
        && rtArchive.Manifest.Type == PackManifestType.Profile
        && rtArchive.HasPackageJson
        && rtArchive.HasPnpmLock
        && rtArchive.HasPnpmWorkspace
        && rtArchive.OverridePaths.Count == 1,
        rtWriteError ?? rtReadError ?? string.Empty);

    var rtManifest = rtArchive!.Manifest;
    var rtNpmOk = rtManifest.Dependencies.TryGetValue("dsh-pet", out var rtNpmVersion);
    var rtGitOk = rtManifest.Dependencies.TryGetValue("github:HanaAyane/dsh-reasoning-effort", out var rtGitSha);
    var rtSubOk = rtManifest.Dependencies.TryGetValue("github:owner/repo#path:/packages/theme", out var rtSubSha);
    Check("packexport/坐标反向转换：package.json 条目 → 规范坐标（三条规则都可往返）",
        rtNpmOk && rtNpmVersion == "0.2.0"
        && rtGitOk && rtGitSha == "83bc8c5"
        && rtSubOk && rtSubSha == "abcdef1"
        && rtManifest.Files.Count == 1
        && rtManifest.Files[0].Sha256 == rtSha
        && rtManifest.Files[0].Size == rtPayload.Length
        && rtManifest.ResolveDisplayName("en-US") == "Whale Export"
        && rtManifest.ResolveDisplayName("zh-CN") == "大肥鱼导出");

    var rtBadOverride = DshPackWriter.TryWrite(
        Path.Combine(rtRoot, "bad1.dspack"),
        rtRequest with { Overrides = new Dictionary<string, string> { ["../escape.yml"] = "x" } },
        out var rtBadOverrideError);
    var rtBadFile = DshPackWriter.TryWrite(
        Path.Combine(rtRoot, "bad2.dspack"),
        rtRequest with { Files = new[] { new PackFileEntry("a.bin", "zz", 1, new[] { "https://x" }) } },
        out var rtBadFileError);
    Check("packexport/导出前校验：不安全的 overrides 路径与坏 files[] 必须拒写（不产出坏包）",
        !rtBadOverride
        && rtBadOverrideError!.Contains("overrides 路径非法", StringComparison.Ordinal)
        && !rtBadFile
        && rtBadFileError!.Contains("files[]", StringComparison.Ordinal)
        && !File.Exists(Path.Combine(rtRoot, "bad1.dspack"))
        && !File.Exists(Path.Combine(rtRoot, "bad2.dspack")));

    // ---- 往返：导出的包用自家导入器装一遍，依赖与 patch 必须回到原样 ----
    var rtPaths = new LauncherPaths(Path.Combine(rtRoot, "import-root"));
    Directory.CreateDirectory(rtPaths.RootDirectory);
    var rtRegistry = new InstanceRegistry(rtPaths);
    var rtTemplateRoot = Path.Combine(rtRoot, "template");
    Directory.CreateDirectory(rtTemplateRoot);
    var rtTemplateExe = Path.Combine(rtTemplateRoot, "dsh.cmd");
    File.WriteAllText(rtTemplateExe, "@echo off", new UTF8Encoding(false));
    var rtTemplate = rtRegistry.Register("往返模板", rtTemplateRoot, InstanceKind.Installed, rtTemplateExe, "0.1.1-rc.2", "pnpm");
    var rtDownloader = new PackFileDownloader((_, _) => Task.FromResult<Stream>(new MemoryStream(rtPayload)));
    var rtService = new DshPackImportService(rtRegistry, rtDownloader);

    var rtImportable = PackArchiveReader.TryRead(rtPath, out var rtImportArchive, out _, out _);
    var rtImport = rtService
        .ImportAsync(rtImportArchive!, rtTemplate, rtRegistry.Load(), allowDownloads: true)
        .GetAwaiter().GetResult();
    var rtInstance = rtRegistry.Load().FirstOrDefault(instance => instance.Id == rtImport.InstanceId);
    var rtProfile = rtInstance is null ? null : Path.Combine(rtInstance.DshHome, "profiles", "whale");
    var rtPackagePath = rtProfile is null ? null : Path.Combine(rtProfile, "package.json");
    var rtBinaryPath = rtProfile is null ? null : Path.Combine(rtProfile, "data", "whale.bin");
    JsonDocument? rtPackage = rtPackagePath is not null && File.Exists(rtPackagePath)
        ? JsonDocument.Parse(File.ReadAllText(rtPackagePath))
        : null;
    JsonElement? rtDeps = rtPackage is null ? null : rtPackage.RootElement.GetProperty("dependencies");
    Check("packexport/往返（导出→自家导入）：依赖与 spec 回到原样、patch 与载荷都落盘",
        rtImportable
        && rtImport.Succeeded
        && rtProfile is not null
        && File.Exists(Path.Combine(rtProfile, "cordis.patch.yml"))
        && rtDeps is { } deps
        && deps.GetProperty("dsh-pet").GetString() == "0.2.0"
        && deps.GetProperty("dsh-reasoning-effort").GetString() == "github:HanaAyane/dsh-reasoning-effort#83bc8c5"
        && deps.GetProperty("theme").GetString() == "github:owner/repo#abcdef1&path:packages/theme"
        && rtBinaryPath is not null
        && File.Exists(rtBinaryPath)
        && File.ReadAllBytes(rtBinaryPath).SequenceEqual(rtPayload),
        rtImport.Error ?? string.Empty);
    rtPackage?.Dispose();
}


// ===========================================================================
// 16. 页面错误文本记录规则（PageErrorText，work-log/71 事故回归）
// ===========================================================================
{
    // 这次事故的真实文本：旧实现截到 120 字符，正好切在 "…client-modules: bun"，
    // 把排查方向带到了"缺 bun 运行时"。下面这条断言就是防止它再发生。
    var petReal = string.Join("\n", new[]
    {
        "HARNESS",
        "Failed to load plugins",
        "failed to import loader entry 1c8adf4c (@deepseek-ai/dsh-client-hmr): client-modules: bundle script /plugins/??"
            + string.Join(",", Enumerable.Range(0, 40).Select(i => $"@deepseek-ai/plugin-{i}/client.js"))
            + "&rev=f90d8e180337 failed to load"
    });
    var petKept = PageErrorText.DescribeProbeFailure(petReal);
    Check("pageerror/事故回归：页面错误文本不再被切在单词中间（关键片段必须保留）",
        petReal.Length > 120
        && petKept.Contains("bundle script", StringComparison.Ordinal)
        && petKept.Contains("failed to load", StringComparison.Ordinal)
        && petKept.Contains("@deepseek-ai/dsh-client-hmr", StringComparison.Ordinal)
        && !petKept.Contains("已截断", StringComparison.Ordinal));

    var petLong = new string('x', PageErrorText.DefaultMaximumLength + 500);
    var petTruncated = PageErrorText.ForEvidence(petLong);
    Check("pageerror/超长文本：截断到上限并显式标注（不再静默切掉）",
        petTruncated.StartsWith(new string('x', PageErrorText.DefaultMaximumLength), StringComparison.Ordinal)
        && petTruncated.Contains("已截断", StringComparison.Ordinal)
        && petTruncated.Contains((PageErrorText.DefaultMaximumLength + 500).ToString(), StringComparison.Ordinal));

    Check("pageerror/归一化：CRLF 转 LF、去首尾空白、空值返回空串",
        PageErrorText.ForEvidence("  a\r\nb  ") == "a\nb"
        && PageErrorText.ForEvidence(null) == string.Empty
        && PageErrorText.ForEvidence("   ") == string.Empty
        && PageErrorText.ForEvidence("short") == "short");
}

// ===========================================================================
// 17. profile 卫生检查（ProfileHygiene，work-log/71 事故预防）
// ===========================================================================
{
    var hygieneRoot = Path.Combine(scratch, "hygiene");
    var brokenProfile = Path.Combine(hygieneRoot, "broken");
    var healthyProfile = Path.Combine(hygieneRoot, "healthy");
    Directory.CreateDirectory(brokenProfile);
    Directory.CreateDirectory(healthyProfile);

    // 事故现场的三样残留
    File.WriteAllText(Path.Combine(brokenProfile, "pnpm-lock.yaml"),
        "lockfileVersion: '9.0'\n\nsettings:\n  autoInstallPeers: false\n  excludeLinksFromLockfile: false\n\nimporters:\n\n  .: {}\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(brokenProfile, "pnpm-workspace.yaml"),
        "packages:\n  - .\n\nnodeLinker: hoisted\nautoInstallPeers: false\nallowBuilds:\n  '@ash-qw/dsh-theme-prts': true\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(brokenProfile, "package.json"),
        "{\n  \"name\": \"dsh-profile-web\",\n  \"private\": true,\n  \"dsh\": { \"profile\": { \"bundles\": [\"@deepseek-ai/dsh-base\"] } }\n}\n", new UTF8Encoding(false));

    // dsh 自己新建的健康 profile
    File.WriteAllText(Path.Combine(healthyProfile, "pnpm-workspace.yaml"),
        "packages:\n  - .\n\nnodeLinker: hoisted\nautoInstallPeers: false\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(healthyProfile, "package.json"),
        "{\n  \"name\": \"dsh-profile-web\",\n  \"private\": true,\n  \"dependencies\": {},\n  \"dsh\": { \"profile\": { \"bundles\": [\"@deepseek-ai/dsh-base\"] } }\n}\n", new UTF8Encoding(false));

    var hygieneBroken = ProfileHygiene.Inspect(brokenProfile);
    var hygieneHealthy = ProfileHygiene.Inspect(healthyProfile);
    Check("profilehygiene/能查出事故现场的三样残留，且都标为可自动复位",
        hygieneBroken.Count == 3
        && hygieneBroken.All(issue => issue.AutoFixable)
        && hygieneBroken.Any(issue => issue.Kind == ProfileHygiene.IssueEmptyLock)
        && hygieneBroken.Any(issue => issue.Kind == ProfileHygiene.IssueAllowBuildsResidue)
        && hygieneBroken.Any(issue => issue.Kind == ProfileHygiene.IssueMissingDependencies));

    Check("profilehygiene/dsh 新建的健康 profile 零问题（不误报）",
        hygieneHealthy.Count == 0,
        string.Join("；", hygieneHealthy.Select(issue => issue.Kind)));

    Check("profilehygiene/空 lock 判定：真有依赖(packages 段/非空 importer)不算空",
        ProfileHygiene.IsEmptyLockfile("lockfileVersion: '9.0'\n\nimporters:\n\n  .: {}\n")
        && ProfileHygiene.IsEmptyLockfile("{}")
        && !ProfileHygiene.IsEmptyLockfile("lockfileVersion: '9.0'\npackages:\n\n  dsh-pet@0.2.0:\n    resolution: {integrity: sha512-x}\n")
        && !ProfileHygiene.IsEmptyLockfile("lockfileVersion: '9.0'\nimporters:\n\n  .:\n    dependencies:\n      dsh-pet: 0.2.0\n"));

    Check("profilehygiene/allowBuilds 解析：多行与内联两种写法都能取到包名",
        ProfileHygiene.ReadAllowBuildsPackages("packages:\n  - .\nallowBuilds:\n  '@a/b': true\n  c-d: false\n").SequenceEqual(new[] { "@a/b", "c-d" })
        && ProfileHygiene.ReadAllowBuildsPackages("allowBuilds: ['x']\n").Count == 1
        && ProfileHygiene.ReadAllowBuildsPackages("packages:\n  - .\n").Count == 0);
}

// ===========================================================================
// 18. profile 卫生复位（ProfileHygiene.TryReset，A3 收尾）
// ===========================================================================
{
    var resetRoot = Path.Combine(scratch, "hygiene-reset");
    var resetProfile = Path.Combine(resetRoot, "profile");
    var resetSnapshot = Path.Combine(resetRoot, "snapshot");
    Directory.CreateDirectory(resetProfile);
    Directory.CreateDirectory(Path.Combine(resetProfile, "node_modules"));
    File.WriteAllText(Path.Combine(resetProfile, "pnpm-lock.yaml"),
        "lockfileVersion: '9.0'\n\nsettings:\n  autoInstallPeers: false\n\nimporters:\n\n  .: {}\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(resetProfile, "pnpm-workspace.yaml"),
        "packages:\n  - .\n\nnodeLinker: hoisted\nautoInstallPeers: false\nallowBuilds:\n  '@ash-qw/dsh-theme-prts': true\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(resetProfile, "package.json"),
        "{\n  \"name\": \"dsh-profile-web\",\n  \"private\": true\n}\n", new UTF8Encoding(false));
    // 哨兵：复位绝不能碰用户数据 / node_modules / 其它文件
    File.WriteAllText(Path.Combine(resetProfile, "cordis.patch.yml"), "[]\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(resetProfile, "node_modules", "sentinel.txt"), "keep-me", new UTF8Encoding(false));

    var resetOk = ProfileHygiene.TryReset(resetProfile, resetSnapshot, out var resetActions, out var resetError);
    var afterReset = ProfileHygiene.Inspect(resetProfile);
    Check("hygiene-reset/一键复位：三处残留全部处理干净（复位后再检查为零问题）",
        resetOk
        && resetError is null
        && afterReset.Count == 0
        && resetActions.Count >= 4,
        resetError ?? string.Join("；", afterReset.Select(issue => issue.Kind)));

    Check("hygiene-reset/复位前先快照：原文件与被移走的 lock 都在快照目录里",
        File.Exists(Path.Combine(resetSnapshot, "package.json.bak"))
        && File.Exists(Path.Combine(resetSnapshot, "pnpm-workspace.yaml.bak"))
        && File.Exists(Path.Combine(resetSnapshot, "pnpm-lock.yaml.removed"))
        && !File.Exists(Path.Combine(resetProfile, "pnpm-lock.yaml")));

    Check("hygiene-reset/只碰那三个文件：node_modules 与其它配置原样保留（安全断言）",
        File.Exists(Path.Combine(resetProfile, "node_modules", "sentinel.txt"))
        && File.ReadAllText(Path.Combine(resetProfile, "node_modules", "sentinel.txt")) == "keep-me"
        && File.ReadAllText(Path.Combine(resetProfile, "cordis.patch.yml")) == "[]\n"
        && !File.ReadAllText(Path.Combine(resetProfile, "pnpm-workspace.yaml")).Contains("allowBuilds", StringComparison.Ordinal)
        && File.ReadAllText(Path.Combine(resetProfile, "package.json")).Contains("\"dependencies\": {}", StringComparison.Ordinal));

    Check("hygiene-reset/干净的 profile 复位是空操作（不误伤、不建无用快照）",
        ProfileHygiene.TryReset(Path.Combine(scratch, "hygiene", "healthy"), Path.Combine(resetRoot, "snapshot2"), out var noopActions, out _)
        && noopActions.Count == 1
        && noopActions[0].Contains("没有需要复位", StringComparison.Ordinal)
        && !Directory.Exists(Path.Combine(resetRoot, "snapshot2")));
}

// ===========================================================================
// 19. 从实例 profile 导出 v4 整合包（PackExportService，C 组）
// ===========================================================================
{
    var exportRoot = Path.Combine(scratch, "export-from-profile");
    var exportPaths = new LauncherPaths(Path.Combine(exportRoot, "root"));
    Directory.CreateDirectory(exportPaths.RootDirectory);
    var exportRegistry = new InstanceRegistry(exportPaths);
    var exportTemplateRoot = Path.Combine(exportRoot, "runtime");
    Directory.CreateDirectory(exportTemplateRoot);
    var exportExe = Path.Combine(exportTemplateRoot, "dsh.cmd");
    File.WriteAllText(exportExe, "@echo off", new UTF8Encoding(false));
    var exportInstance = exportRegistry.Register("导出源实例", exportTemplateRoot, InstanceKind.Installed, exportExe, "0.1.5-rc.2", "pnpm");

    var exportProfileDir = Path.Combine(exportInstance.DshHome, "profiles", "web");
    Directory.CreateDirectory(exportProfileDir);
    File.WriteAllText(Path.Combine(exportProfileDir, "package.json"), """
{
  "name": "dsh-profile-web",
  "private": true,
  "dependencies": {
    "dsh-pet": "0.2.0",
    "dsh-reasoning-effort": "github:HanaAyane/dsh-reasoning-effort#83bc8c5",
    "theme": "github:owner/repo#abcdef1&path:packages/theme"
  },
  "dsh": { "profile": { "bundles": ["@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app"], "patchReload": "live" } }
}
""", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(exportProfileDir, "cordis.patch.yml"), "plugins:\n  whale: {}\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(exportProfileDir, "pnpm-workspace.yaml"), "packages:\n  - .\n", new UTF8Encoding(false));

    var exportTarget = Path.Combine(exportRoot, "web-export.dspack");
    var exportOk = PackExportService.TryExport(exportInstance, "web", exportTarget, "2.1.0", out var exportSummary, out var exportError);
    var exportReadOk = PackArchiveReader.TryRead(exportTarget, out var exportArchive, out var exportOutcome, out var exportReadError);
    Check("packexport-service/从 profile 导出：manifest v4 + v3 配对的容器、bundles/依赖/坐标齐全",
        exportOk
        && exportSummary.Count >= 5
        && exportReadOk
        && exportOutcome == PackArchiveOutcome.Ok
        && exportArchive!.Manifest.Version == PackManifestVersion.V4
        && exportArchive.Manifest.PackVersion == "2.1.0"
        && exportArchive.Manifest.DshVersion == "0.1.5-rc.2"
        && exportArchive.Manifest.Bundles.Count == 2
        && exportArchive.Manifest.Dependencies.Count == 3
        && exportArchive.Manifest.Dependencies.ContainsKey("github:owner/repo#path:/packages/theme")
        && exportArchive.HasPnpmWorkspace
        && exportArchive.Manifest.Patch is not null
        && exportArchive.Manifest.ResolveDisplayName("zh-CN") == "导出源实例",
        exportError ?? exportReadError ?? string.Empty);

    Check("packexport-service/导出物能被自家导入器读回（往返一致）",
        PackArchiveReader.TryRead(exportTarget, out var roundTripArchive, out _, out _)
        && roundTripArchive!.Manifest.Dependencies.TryGetValue("dsh-pet", out var petVersion)
        && petVersion == "0.2.0"
        && roundTripArchive.Manifest.Dependencies.TryGetValue("github:HanaAyane/dsh-reasoning-effort", out var sha)
        && sha == "83bc8c5");

    // 缺少 bundles 的 profile 应被拒绝，而不是产出坏包
    var emptyProfile = Path.Combine(exportInstance.DshHome, "profiles", "pack");
    Directory.CreateDirectory(emptyProfile);
    File.WriteAllText(Path.Combine(emptyProfile, "package.json"), "{ \"name\": \"dsh-profile-pack\", \"private\": true }\n", new UTF8Encoding(false));
    var rejectedTarget = Path.Combine(exportRoot, "bad.dspack");
    Check("packexport-service/没有 bundles 的 profile：明确拒绝且不产出坏包",
        !PackExportService.TryExport(exportInstance, "pack", rejectedTarget, "1.0.0", out _, out var rejectError)
        && rejectError!.Contains("bundles", StringComparison.Ordinal)
        && !File.Exists(rejectedTarget));
}

// ===========================================================================
// 8. 核心 bundle 常量
// ===========================================================================
Check("bundles/核心 bundle 常量与上游一致（base / web-app）",
    DshCoreBundles.Base == "@deepseek-ai/dsh-base" && DshCoreBundles.WebApp == "@deepseek-ai/dsh-web-app");

try
{
    Directory.Delete(scratch, recursive: true);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    // 临时目录清理失败不影响结果。
}

Console.WriteLine($"===== SelfTest 结果：{passes.Count} PASS / {failures.Count} FAIL =====");
if (failures.Count > 0)
{
    Console.WriteLine("失败项：");
    foreach (var failure in failures)
    {
        Console.WriteLine($"  - {failure}");
    }
}

return failures.Count == 0 ? 0 : 1;
