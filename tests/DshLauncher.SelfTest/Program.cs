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
