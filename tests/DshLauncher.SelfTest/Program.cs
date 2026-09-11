using System.IO;
using System.Text;
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
