using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text;
using DshLauncher;
using DshLauncher.Models;
using DshLauncher.Services;
using ZstdSharp;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Instance registry round-trip", TestInstanceRegistryRoundTrip),
    ("Instance registry rename persists", TestInstanceRegistryRenamePersists),
    ("Instance registry allows shared roots for isolated versions", TestInstanceRegistryAllowsSharedRoots),
    ("Instance registry rejects missing executable state", TestInstanceRegistryRejectsMissingExecutableState),
    ("Instance registry rejects unsafe homes and corrupt records", TestInstanceRegistryRejectsUnsafeData),
    ("Source project inspection", TestSourceProjectInspection),
    ("Source inspector rejects unrelated workspace", TestSourceInspectorRejectsUnrelatedWorkspace),
    ("DSh runtime detection", TestDshRuntimeDetection),
    ("Node runtime compatibility", TestNodeRuntimeCompatibility),
    ("Node install version resolution", TestNodeInstallVersionResolution),
    ("Node installer download progress", TestNodeInstallerDownloadProgress),
    ("Node install result states", TestNodeInstallResultStates),
    ("Node download cancel cleans part", TestNodeDownloadCancelCleansPart),
    ("Installed instance runtime rebinding", TestInstanceRuntimeRebinding),
    ("Node engine requirement resolution", TestNodeEngineRequirementResolution),
    ("Runtime progress window close guard", TestRuntimeProgressCloseGuard),
    ("Node version selection uses source engine", TestNodeVersionSelectionUsesEngine),
    ("DSh global install target decision", TestDshInstallTargetDecision),
    ("Start flow decisions", TestStartFlowDecisions),
    ("Node path propagation", TestNodePathPropagation),
    ("DSh install guard", TestDshInstallGuard),
    ("Source runner guard", TestSourceRunnerGuard),
    ("Source prepare install/build", TestSourcePrepareInstallAndBuild),
    ("Source runner lifecycle", TestSourceRunnerLifecycle),
    ("DSh early exit cleanup", TestDshEarlyExitCleanup),
    ("DSh instance lifecycle", TestDshInstanceLifecycle),
    ("Attached runtime lifecycle", TestAttachedRuntimeLifecycle),
    ("Extension ecosystem isolation", TestExtensionEcosystemIsolation),
    ("dsh-market theme bridge", TestDshMarketThemeBridge),
    ("Plugin command supplies pnpm runtime", TestPluginCommandSuppliesPnpmRuntime),
    ("Marketplace discovery and verification", TestMarketplaceDiscoveryAndVerification),
    ("Version copy, clean version and package import", TestVersionPackageOperations),
    ("Model settings round-trip", TestModelSettingsRoundTrip),
    ("Provider state and diagnostics", TestProviderStateAndDiagnostics),
    ("Model provider synchronization", TestModelProviderSynchronization),
    ("Conversation file management", TestConversationFileManagement),
    ("Conversation synchronization", TestConversationSynchronization)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"Self-test result: {tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static Task TestInstanceRegistryRoundTrip()
{
    using var temporary = new TestDirectory();
    var launcherRoot = Path.Combine(temporary.Path, "launcher");
    var installedRoot = Path.Combine(temporary.Path, "installed");
    Directory.CreateDirectory(installedRoot);

    var registry = new InstanceRegistry(new LauncherPaths(launcherRoot));
    var created = registry.Register(
        "测试实例",
        installedRoot,
        InstanceKind.Installed,
        detectedVersion: "0.1.0-rc.6",
        packageManager: "npm");

    Assert(created.Id.Length == 32, "实例 ID 应为 32 位不带连字符的 GUID。");
    Assert(Directory.Exists(created.DshHome), "注册实例必须创建独立 DSH_HOME。");
    Assert(File.Exists(registry.StoragePath), "实例注册文件应已写入。");

    var bytes = File.ReadAllBytes(registry.StoragePath);
    Assert(bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF, "JSON 不应写入 BOM。");

    var loaded = registry.Load();
    Assert(loaded.Count == 1, "重新加载后应保留一个实例。");
    Assert(loaded[0] == created, "重新加载的实例内容应与写入内容一致。");
    return Task.CompletedTask;
}

static Task TestInstanceRegistryAllowsSharedRoots()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "shared");
    Directory.CreateDirectory(root);
    var registry = new InstanceRegistry(new LauncherPaths(Path.Combine(temporary.Path, "launcher")));
    var first = registry.Register("一个实例", root, InstanceKind.Source, packageManager: "pnpm");
    var second = registry.Register("另一个实例", root, InstanceKind.Source, packageManager: "pnpm");
    Assert(first.RootPath == second.RootPath, "共享运行目录的版本应保留同一个 DSh 根目录。");
    Assert(!string.Equals(first.DshHome, second.DshHome, StringComparison.OrdinalIgnoreCase), "共享运行目录的版本必须使用不同 DSH_HOME。");
    return Task.CompletedTask;
}

static Task TestInstanceRegistryRenamePersists()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "installed");
    Directory.CreateDirectory(root);
    var registry = new InstanceRegistry(new LauncherPaths(Path.Combine(temporary.Path, "launcher")));
    var created = registry.Register("原始名称", root, InstanceKind.Installed);

    var renamed = registry.Update(created with { Name = "新的版本名称" });
    Assert(renamed.Name == "新的版本名称", "更新实例名称后应返回新名称。 ");
    Assert(registry.Load().Single().Name == "新的版本名称", "更新实例名称后应持久化到注册文件。 ");
    return Task.CompletedTask;
}

static Task TestInstanceRegistryRejectsMissingExecutableState()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "installed");
    Directory.CreateDirectory(root);
    var registry = new InstanceRegistry(new LauncherPaths(Path.Combine(temporary.Path, "launcher")));
    var missingExecutable = Path.Combine(root, "missing.cmd");

    var instance = registry.Register(
        "缺少入口的实例",
        root,
        InstanceKind.Installed,
        dshExecutablePath: missingExecutable,
        detectedVersion: "0.1.0");

    Assert(instance.DshExecutablePath is null, "不存在的可执行入口不能写入注册记录。");
    Assert(instance.RuntimeStatus == InstanceRuntimeStatus.Unknown, "缺少可执行入口的实例不能标记为可用。");
    return Task.CompletedTask;
}

static Task TestInstanceRegistryRejectsUnsafeData()
{
    using var temporary = new TestDirectory();
    var launcherRoot = Path.Combine(temporary.Path, "launcher");
    var root = Path.Combine(temporary.Path, "installed");
    Directory.CreateDirectory(root);
    var registry = new InstanceRegistry(new LauncherPaths(launcherRoot));
    var outsideHome = Path.Combine(temporary.Path, "outside-home");

    var rejectedHome = false;
    try
    {
        registry.Register("越界 HOME", root, InstanceKind.Installed, dshHome: outsideHome);
    }
    catch (InvalidOperationException)
    {
        rejectedHome = true;
    }

    Assert(rejectedHome, "注册实例不能把 DSH_HOME 指向 Launcher 隔离目录之外。");
    Assert(!Directory.Exists(outsideHome), "拒绝越界 DSH_HOME 后不能创建外部目录。");

    Directory.CreateDirectory(launcherRoot);
    File.WriteAllText(registry.StoragePath, "[null]", new UTF8Encoding(false));
    AssertThrows<InvalidDataException>(() => registry.Load(), "注册文件中的空记录必须被拒绝。");
    return Task.CompletedTask;
}

static Task TestSourceProjectInspection()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "deepseek-harness");
    Directory.CreateDirectory(Path.Combine(root, "apps", "cli"));
    File.WriteAllText(
        Path.Combine(root, "package.json"),
        "{\"name\":\"deepseek-harness\",\"version\":\"0.1.0\",\"engines\":{\"node\":\"^22.19.0 || >=24.0.0\"},\"packageManager\":\"pnpm@10.0.0\",\"scripts\":{\"build\":\"pnpm run build\"}}",
        new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages: []", new UTF8Encoding(false));

    var inspector = new SourceProjectInspector();
    var beforeDependencies = inspector.Inspect(root);
    Assert(beforeDependencies.IsValid, "带 package.json 的 Source 项目应可解析。");
    Assert(beforeDependencies.IsDshSource, "DeepSeek Harness 根项目应被识别为 DSh Source。");
    Assert(beforeDependencies.PackageManager == "pnpm", "应读取 packageManager 中的 pnpm。");
    Assert(beforeDependencies.PackageManagerVersion == "10.0.0", "应读取包管理器版本。");
    Assert(beforeDependencies.NodeEngine == "^22.19.0 || >=24.0.0", "应从 Source package.json 读取 engines.node。");
    Assert(beforeDependencies.HasBuildScript, "应识别 build 脚本。");
    Assert(beforeDependencies.StatusText == "需要安装依赖", "没有 node_modules 时应提示安装依赖。");

    Directory.CreateDirectory(Path.Combine(root, "node_modules"));
    var afterDependencies = inspector.Inspect(root);
    Assert(afterDependencies.StatusText == "可构建", "存在 node_modules 时应进入可构建状态。");
    Assert(afterDependencies.BuildCommand == "pnpm run build", "Source 构建命令应尊重 pnpm。");
    return Task.CompletedTask;
}

static Task TestSourceInspectorRejectsUnrelatedWorkspace()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "unrelated-workspace");
    Directory.CreateDirectory(Path.Combine(root, "apps", "cli", "src"));
    File.WriteAllText(
        Path.Combine(root, "package.json"),
        "{\"name\":\"unrelated-workspace\",\"packageManager\":\"pnpm@10.0.0\"}",
        new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages: []", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(root, "apps", "cli", "src", "bin.ts"), "console.log('not dsh');", new UTF8Encoding(false));

    var project = new SourceProjectInspector().Inspect(root);
    Assert(project.IsValid, "有合法 package.json 的项目仍应返回可读检查结果。");
    Assert(!project.IsDshSource, "普通 pnpm workspace 不能仅凭目录结构被识别为 DSh Source。");
    Assert(project.StatusText == "不是 DSh 源码", "非 DSh Source 应给出明确状态。");
    return Task.CompletedTask;
}

static async Task TestDshRuntimeDetection()
{
    Console.WriteLine("INFO DSh candidates: " + string.Join(" | ", DshRuntimeDetector.GetCandidates().Where(File.Exists)));
    var runtime = await new DshRuntimeDetector().DetectAsync();
    Console.WriteLine($"INFO DSh detection: available={runtime.IsAvailable}, executable={runtime.ExecutablePath}, error={runtime.Error}");
    if (!runtime.IsAvailable)
    {
        Console.WriteLine("INFO DSh runtime not installed; detection returned a valid missing state");
        Assert(runtime.Error is not null, "缺少 DSh 时应提供诊断信息。");
        return;
    }

    Assert(File.Exists(runtime.ExecutablePath), "检测到的 DSh 可执行入口必须存在。");
    Assert(!string.IsNullOrWhiteSpace(runtime.Version), "检测到的 DSh 必须有版本。");
    Assert(runtime.PackageRoot is not null, "检测到的官方 DSh 入口应能解析 package root。");
    Assert(DshRuntimeDetector.TryReadPackageVersion(runtime.PackageRoot!) == runtime.Version,
        "DSh 运行时版本应与安装包 package.json 版本一致。");
    Assert(DshRuntimeDetector.TryReadNodeEngine(runtime.PackageRoot!) == runtime.NodeEngine,
        "DSh 运行时应从同一个 package.json 暴露 engines.node，而不是另写版本规则。");
}

static Task TestNodeRuntimeCompatibility()
{
    Assert(NodeRuntimeInfo.EvaluateCompatibility("24.19.0", "^22.19.0 || >=24.0.0") == NodeRuntimeCompatibility.Compatible,
        "Node.js 24.x 应满足官方兼容范围。");
    Assert(NodeRuntimeInfo.EvaluateCompatibility("20.11.0", "^22.19.0 || >=24.0.0") == NodeRuntimeCompatibility.Incompatible,
        "Node.js 20.x 应被标记为 Incompatible。");
    Assert(NodeRuntimeInfo.EvaluateCompatibility("22.18.0", "^22.19.0 || >=24.0.0") == NodeRuntimeCompatibility.Incompatible,
        "低于 engines.node 下限的 Node.js 22.x 应被拒绝。");
    Assert(NodeRuntimeInfo.EvaluateCompatibility("24.19.0", null) == NodeRuntimeCompatibility.Compatible,
        "package metadata 未声明 engines.node 时不能凭空添加长期硬编码限制。");
    Assert(NodeRuntimeInfo.EvaluateCompatibility(null, ">=24") == NodeRuntimeCompatibility.Missing,
        "缺少 Node.js 版本时应返回 Missing。");
    return Task.CompletedTask;
}

static Task TestNodeInstallVersionResolution()
{
    var index = "[{\"version\":\"v20.11.0\",\"lts\":false},{\"version\":\"v22.23.2\",\"lts\":\"Jod\"},{\"version\":\"v24.5.0\",\"lts\":false}]";
    Assert(NodeInstallService.SelectLtsVersion(index) == "v22.23.2", "应优先选择兼容的 22.x LTS。");

    var indexWith24 = "[{\"version\":\"v22.18.0\",\"lts\":true},{\"version\":\"v24.10.0\",\"lts\":\"Krypton\"}]";
    Assert(NodeInstallService.SelectLtsVersion(indexWith24) == "v24.10.0", "22.18 低于 DSh 兼容下限时应选择 24+ LTS。");

    var nonLts = "[{\"version\":\"v25.0.0\",\"lts\":false}]";
    Assert(NodeInstallService.SelectLtsVersion(nonLts) is null, "没有可用兼容 LTS 时应返回 null 并使用固定版本兜底。");
    Assert(NodeInstallService.SelectLtsVersion("not json") is null, "损坏的版本索引应返回 null。");
    return Task.CompletedTask;
}

static async Task TestNodeInstallerDownloadProgress()
{
    var payload = new byte[1_200_000];
    new Random(42).NextBytes(payload);
    var handler = new NodeTestHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(payload)
    });
    var service = new NodeInstallService(handler);
    using var temporary = new TestDirectory();
    var destination = Path.Combine(temporary.Path, "node.msi");
    var sink = new NodeProgressSink();
    var result = await service.DownloadAsync(
        new Uri("https://nodejs.org/dist/v22.23.2/node-v22.23.2-x64.msi"),
        destination,
        sink,
        CancellationToken.None);
    Assert(result.Percent is 100, "下载完成时返回进度应为 100%。");
    Assert(File.Exists(destination) && new FileInfo(destination).Length == payload.Length, "下载文件应与响应内容一致。");
    Assert(sink.Last.Percent is 100, "进度回调最终应报告 100%。");

    var smallService = new NodeInstallService(new NodeTestHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(new byte[512])
    }));
    var rejected = false;
    try
    {
        await smallService.DownloadAsync(
            new Uri("https://nodejs.org/dist/v22.23.2/node-v22.23.2-x64.msi"),
            Path.Combine(temporary.Path, "small.msi"),
            null,
            CancellationToken.None);
    }
    catch (IOException)
    {
        rejected = true;
    }

    Assert(rejected, "过小的下载结果应被拒绝，避免把 HTML 错误页当成安装程序。");
}

static Task TestNodeInstallResultStates()
{
    var cancelled = NodeInstallService.MapExitCode(-3, "v22.23.2");
    Assert(cancelled.IsCancelled && !cancelled.IsSuccess && cancelled.ExitCode == -3,
        "用户取消必须返回独立取消状态，不能与真实超时混淆。");
    var timeout = NodeInstallService.MapExitCode(-2, "v22.23.2");
    Assert(!timeout.IsCancelled && !timeout.IsSuccess && timeout.ExitCode == -2 && timeout.Error?.Contains("10 分钟") == true,
        "真实安装超时必须保持失败状态并提示超时，不能是取消状态。");
    var success = NodeInstallService.MapExitCode(0, "v22.23.2");
    Assert(success.IsSuccess && success.Version == "22.23.2", "安装成功应返回 Node 路径与版本。");
    var failed = NodeInstallService.MapExitCode(1603, "v22.23.2");
    Assert(!failed.IsCancelled && !failed.IsSuccess && failed.ExitCode == 1603, "其它退出码应保留为安装失败。");
    return Task.CompletedTask;
}

static async Task TestNodeDownloadCancelCleansPart()
{
    var payload = new byte[4_000_000];
    new Random(7).NextBytes(payload);
    var handler = new NodeTestHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new SlowCancelStream(payload))
    });
    var service = new NodeInstallService(handler);
    using var temporary = new TestDirectory();
    var destination = Path.Combine(temporary.Path, "node.msi");
    using var cts = new CancellationTokenSource();
    var download = service.DownloadAsync(
        new Uri("https://nodejs.org/dist/v22.23.2/node-v22.23.2-x64.msi"),
        destination,
        null,
        cts.Token);
    for (var attempt = 0; attempt < 50 && !Directory.GetFiles(temporary.Path).Any(); attempt++)
    {
        await Task.Delay(50);
    }

    Assert(Directory.GetFiles(temporary.Path).Any(name => name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)),
        "下载进行中应存在 .part 临时文件。");
    cts.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(() => download, "取消下载应抛 OperationCanceledException。");
    Assert(Directory.GetFiles(temporary.Path).Length == 0, "取消下载后不得残留 .part 临时文件或目标文件。");
}

static Task TestInstanceRuntimeRebinding()
{
    using var temporary = new TestDirectory();
    var launcherRoot = Path.Combine(temporary.Path, "launcher");
    var oldRoot = Path.Combine(temporary.Path, "old-dsh");
    Directory.CreateDirectory(oldRoot);
    var oldExe = Path.Combine(oldRoot, "dsh.cmd");
    File.WriteAllText(oldExe, "@echo off\r\n", new UTF8Encoding(false));

    var newRoot = Path.Combine(temporary.Path, "new-dsh", "node_modules", "@deepseek-ai", "dsh");
    Directory.CreateDirectory(newRoot);
    File.WriteAllText(
        Path.Combine(newRoot, "package.json"),
        "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.2.0\",\"engines\":{\"node\":\"^22.19.0 || >=24.0.0\"}}",
        new UTF8Encoding(false));
    var newExe = Path.Combine(temporary.Path, "new-dsh", "dsh.cmd");
    File.WriteAllText(newExe, "@echo off\r\n", new UTF8Encoding(false));

    var registry = new InstanceRegistry(new LauncherPaths(launcherRoot));
    var stale = registry.Register("旧 DSh", oldRoot, InstanceKind.Installed, oldExe, "0.1.0", "npm");
    var home = stale.DshHome;

    // Simulate the old DSh install being deleted (or npm prefix changed).
    Directory.Delete(oldRoot, recursive: true);

    var detected = new DshRuntimeInfo(true, newExe, "0.2.0", newRoot, null, "^22.19.0 || >=24.0.0");
    var rebound = InstanceRuntimeRebinder.RebindInstalledInstance(stale, detected);
    Assert(rebound is not null, "旧路径失效的 Installed 实例应被重新绑定到新检测到的 DSh。");
    Assert(rebound!.Id == stale.Id && rebound.DshHome == home, "重绑定必须保留实例 Id 与 DSH_HOME。");
    Assert(rebound.RootPath == Path.GetFullPath(newRoot), "重绑定应更新为重新检测到的 package root。");
    Assert(rebound.DshExecutablePath == Path.GetFullPath(newExe), "重绑定应更新为重新检测到的 executable。");
    Assert(rebound.DetectedVersion == "0.2.0" && rebound.RuntimeStatus == InstanceRuntimeStatus.Ready,
        "重绑定应同步版本与 Ready 状态。");

    var sourceRoot = Path.Combine(temporary.Path, "source");
    Directory.CreateDirectory(sourceRoot);
    var source = registry.Register("Source", sourceRoot, InstanceKind.Source, null, null, "pnpm");
    Assert(InstanceRuntimeRebinder.RebindInstalledInstance(source, detected) is null,
        "Source 实例不能被重绑定成 installed runtime。");

    var validRoot = Path.Combine(temporary.Path, "valid-dsh");
    Directory.CreateDirectory(validRoot);
    File.WriteAllText(
        Path.Combine(validRoot, "package.json"),
        "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.2.0\"}",
        new UTF8Encoding(false));
    var validExe = Path.Combine(validRoot, "dsh.cmd");
    File.WriteAllText(validExe, "@echo off\r\n", new UTF8Encoding(false));
    var valid = registry.Register("有效 DSh", validRoot, InstanceKind.Installed, validExe, "0.2.0", "npm");
    Assert(InstanceRuntimeRebinder.RebindInstalledInstance(valid, detected) is null,
        "绑定仍有效的实例不应被重绑定。");

    var attachedStale = stale with
    {
        RuntimeStatus = InstanceRuntimeStatus.Running,
        RuntimeOwnership = InstanceRuntimeOwnership.Attached,
        ProcessId = 4242,
        Port = 19000,
        WebUrl = "http://127.0.0.1:19000"
    };
    Assert(InstanceRuntimeRebinder.RebindInstalledInstance(attachedStale, detected) is null,
        "Attached 实例不能被重绑定成 Ready。");

    var runningStale = stale with
    {
        RuntimeStatus = InstanceRuntimeStatus.Running,
        RuntimeOwnership = InstanceRuntimeOwnership.Managed,
        ProcessId = 4243,
        Port = 19001,
        WebUrl = "http://127.0.0.1:19001"
    };
    Assert(InstanceRuntimeRebinder.RebindInstalledInstance(runningStale, detected) is null,
        "运行中的实例不能被重绑定成 Ready。");
    return Task.CompletedTask;
}

static Task TestNodeEngineRequirementResolution()
{
    using var temporary = new TestDirectory();

    var sourceRoot = Path.Combine(temporary.Path, "source");
    Directory.CreateDirectory(Path.Combine(sourceRoot, "apps", "cli"));
    File.WriteAllText(
        Path.Combine(sourceRoot, "package.json"),
        "{\"name\":\"deepseek-harness\",\"version\":\"0.1.0\",\"engines\":{\"node\":\">=20.0.0\"},\"packageManager\":\"pnpm@10.0.0\"}",
        new UTF8Encoding(false));
    var source = new ManagerInstance(
        "source-engine",
        "Source",
        sourceRoot,
        InstanceKind.Source,
        Path.Combine(temporary.Path, "source-home"),
        null,
        null,
        InstanceRuntimeStatus.Ready,
        "pnpm",
        null,
        DateTimeOffset.UtcNow);
    const string globalEngine = "^22.19.0 || >=24.0.0";
    Assert(DshRuntimeDetector.ResolveNodeEngine(source, globalEngine) == ">=20.0.0",
        "Source 声明 engines.node 时应优先使用 Source 自己的要求。");

    var undeclaredRoot = Path.Combine(temporary.Path, "source-undeclared");
    Directory.CreateDirectory(undeclaredRoot);
    File.WriteAllText(
        Path.Combine(undeclaredRoot, "package.json"),
        "{\"name\":\"deepseek-harness\",\"version\":\"0.1.0\",\"packageManager\":\"pnpm@10.0.0\"}",
        new UTF8Encoding(false));
    Assert(DshRuntimeDetector.ResolveNodeEngine(source with { Id = "source-undeclared", RootPath = undeclaredRoot }, globalEngine) is null,
        "Source 未声明 engines.node 时不能继承全局 installed DSh 的版本要求。");

    var installedRoot = Path.Combine(temporary.Path, "installed-dsh");
    Directory.CreateDirectory(installedRoot);
    File.WriteAllText(
        Path.Combine(installedRoot, "package.json"),
        "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.2.0\",\"engines\":{\"node\":\"^22.19.0 || >=24.0.0\"}}",
        new UTF8Encoding(false));
    var installed = new ManagerInstance(
        "installed-engine",
        "Installed",
        installedRoot,
        InstanceKind.Installed,
        Path.Combine(temporary.Path, "installed-home"),
        Path.Combine(installedRoot, "dsh.cmd"),
        "0.2.0",
        InstanceRuntimeStatus.Ready,
        "npm",
        null,
        DateTimeOffset.UtcNow);
    Assert(DshRuntimeDetector.ResolveNodeEngine(installed, ">=30.0.0") == "^22.19.0 || >=24.0.0",
        "Installed 实例声明 engines.node 时应优先使用实例自己的 metadata。");

    var installedUndeclaredRoot = Path.Combine(temporary.Path, "installed-undeclared");
    Directory.CreateDirectory(installedUndeclaredRoot);
    File.WriteAllText(
        Path.Combine(installedUndeclaredRoot, "package.json"),
        "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.2.0\"}",
        new UTF8Encoding(false));
    Assert(DshRuntimeDetector.ResolveNodeEngine(installed with { Id = "installed-fallback", RootPath = installedUndeclaredRoot }, globalEngine) is null,
        "有效 Installed 实例未声明 engines.node 时应保持未声明，不继承其它 DSh 的要求。");

    var staleRoot = Path.Combine(temporary.Path, "installed-stale");
    Directory.CreateDirectory(staleRoot);
    File.WriteAllText(
        Path.Combine(staleRoot, "package.json"),
        "{\"name\":\"unrelated\",\"version\":\"1.0.0\"}",
        new UTF8Encoding(false));
    Assert(DshRuntimeDetector.ResolveNodeEngine(installed with { Id = "installed-stale", RootPath = staleRoot }, globalEngine) == globalEngine,
        "runtime 已失效的 Installed 实例在重装/重绑定时才可以使用重新检测到的 DSh metadata。");

    Assert(DshRuntimeDetector.ResolveNodeEngine(null, globalEngine) == globalEngine,
        "未选择实例时诊断页应使用全局 DSh engine。");
    return Task.CompletedTask;
}

static Task TestRuntimeProgressCloseGuard()
{
    Assert(RuntimeProgressWindow.IsCloseAllowed(false), "Node 下载阶段应允许关闭窗口。");
    Assert(!RuntimeProgressWindow.IsCloseAllowed(true), "MSI 安装阶段必须阻止窗口关闭。");
    return Task.CompletedTask;
}

static Task TestNodeVersionSelectionUsesEngine()
{
    var index = "[{\"version\":\"v24.5.0\",\"lts\":\"Krypton\"},{\"version\":\"v22.23.2\",\"lts\":\"Jod\"},{\"version\":\"v20.19.0\",\"lts\":\"Iron\"},{\"version\":\"v18.20.4\",\"lts\":false}]";
    Assert(NodeInstallService.SelectLtsVersion(index, "^20.0.0") == "v20.19.0",
        "Source 声明 ^20.0.0 时应选择兼容的 Node 20 LTS，而不是全局 DSh 的 22/24 范围。");
    Assert(NodeInstallService.SelectLtsVersion(index, "^18.0.0") is null,
        "版本索引中没有兼容 LTS 时应返回 null，由固定版本兜底。");
    Assert(NodeInstallService.SelectLtsVersion(index) == "v24.5.0",
        "未指定 engine 时保持官方 DSh 默认兼容策略（22.19+ 或 24+）。");
    Assert(NodeInstallService.SelectLtsVersion(index, "^22.19.0 || >=24.0.0") == "v24.5.0",
        "Installed 场景应优先选择满足官方 DSh engine 的新版 LTS。");
    Assert(NodeInstallService.DefaultVersionSatisfies(null),
        "无 engine 要求时固定版本兜底可用。");
    Assert(NodeInstallService.DefaultVersionSatisfies("^22.19.0 || >=24.0.0"),
        "固定版本满足官方 DSh 要求时允许兜底。");
    Assert(!NodeInstallService.DefaultVersionSatisfies("^20.0.0"),
        "固定版本不满足 Source engine 时不能兜底安装。");
    return Task.CompletedTask;
}

static Task TestDshInstallTargetDecision()
{
    Assert(DshInstallService.ShouldInstallGlobalDSh(false, InstanceKind.Installed),
        "Installed 实例缺 DSh 时应安装全局 DSh。");
    Assert(!DshInstallService.ShouldInstallGlobalDSh(false, InstanceKind.Source),
        "Source 实例缺全局 DSh 时不应安装全局 @deepseek-ai/dsh。");
    Assert(!DshInstallService.ShouldInstallGlobalDSh(true, InstanceKind.Installed),
        "已检测到 DSh 时不应重复安装。");
    Assert(DshInstallService.ShouldInstallGlobalDSh(false, null),
        "设置页未指定实例且缺 DSh 时应允许安装全局 DSh。");
    return Task.CompletedTask;
}

static Task TestNodePathPropagation()
{
    const string nodeExe = @"C:\Program Files\nodejs\node.exe";
    const string existing = @"C:\Windows\System32;D:\Tools";
    var updated = MainWindow.BuildPathWithNodeDirectory(nodeExe, existing);
    Assert(updated.StartsWith(@"C:\Program Files\nodejs" + Path.PathSeparator, StringComparison.OrdinalIgnoreCase),
        "新安装的 Node 目录应补到进程 PATH 最前面。");
    Assert(updated.Contains(@"D:\Tools", StringComparison.OrdinalIgnoreCase),
        "原有 PATH 条目应保留。");

    var alreadyInPath = @"C:\Program Files\nodejs;" + existing;
    Assert(MainWindow.BuildPathWithNodeDirectory(nodeExe, alreadyInPath) == alreadyInPath,
        "Node 目录已在 PATH 中时不应重复添加。");

    Assert(MainWindow.BuildPathWithNodeDirectory(null, existing) == existing,
        "没有可用的 Node 路径时不应改动 PATH。");
    return Task.CompletedTask;
}

static Task TestStartFlowDecisions()
{
    Assert(!MainWindow.CanStartInstanceCore(false, false, true, true, false),
        "Node 检测进行中不能点击启动。");
    Assert(MainWindow.CanStartInstanceCore(false, false, false, true, false),
        "检测完成后且实例可启动时应允许启动。");
    Assert(!MainWindow.CanStartInstanceCore(false, true, false, true, false),
        "runtime 准备进行中不能再次启动。");
    Assert(!MainWindow.CanStartInstanceCore(false, false, false, false, false),
        "没有选中实例时不能启动。");
    Assert(!MainWindow.CanStartInstanceCore(false, false, false, true, true),
        "运行中且没有 Web 地址的实例不能启动。");

    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "dsh");
    Directory.CreateDirectory(root);
    var first = CreateTestInstance("target-first", root, Path.Combine(temporary.Path, "home-1"));
    var second = CreateTestInstance("target-second", root, Path.Combine(temporary.Path, "home-2"));
    Assert(MainWindow.ResolveInstanceById(new[] { first, second }, "target-second") == second,
        "启动目标必须按最初点击的实例 ID 解析，而不是当前 SelectedInstance。");
    Assert(MainWindow.ResolveInstanceById(new[] { first, second }, "missing") is null,
        "按 ID 找不到目标时应返回 null。");

    var node = new NodeRuntimeInfo(true, "node.exe", "24.19.0", null);
    var installedExe = Path.Combine(root, "dsh.cmd");
    File.WriteAllText(installedExe, "@echo off\r\n", new UTF8Encoding(false));
    var installedReady = CreateTestInstance("installed-ready", root, Path.Combine(temporary.Path, "home-3"))
        with { DshExecutablePath = installedExe, DetectedVersion = "0.2.0" };
    var installedStale = CreateTestInstance("installed-stale", root, Path.Combine(temporary.Path, "home-4"));
    Assert(MainWindow.IsRuntimeReadyAfterPreparation(node, "^22.19.0 || >=24.0.0", installedReady),
        "重绑定后重新读取的 engine 兼容且入口存在时应判定就绪。");
    Assert(!MainWindow.IsRuntimeReadyAfterPreparation(node, "^22.19.0 || >=24.0.0", installedStale),
        "Installed 实例入口仍缺失时不能判定就绪（覆盖 npm 成功但重检测失败场景）。");
    Assert(MainWindow.IsRuntimeReadyAfterPreparation(node, null,
        CreateTestInstance("source-ok", root, Path.Combine(temporary.Path, "home-5")) with { Kind = InstanceKind.Source }),
        "Source 实例无需全局 DSh 入口即可判定就绪。");
    Assert(!MainWindow.IsRuntimeReadyAfterPreparation(NodeRuntimeInfo.Missing(), null,
        CreateTestInstance("source-no-node", root, Path.Combine(temporary.Path, "home-6")) with { Kind = InstanceKind.Source }),
        "Node 仍缺失时不能判定就绪。");
    return Task.CompletedTask;
}

static async Task TestDshInstallGuard()
{
    var result = await new DshInstallService().InstallAsync(NodeRuntimeInfo.Missing("测试中模拟 Node.js 缺失"));
    Assert(!result.IsSuccess, "缺少 Node.js 时不应执行 npm 安装。");
    Assert(result.Error?.Contains("Node.js", StringComparison.OrdinalIgnoreCase) == true,
        "缺少 Node.js 时应返回明确错误。");

    var unsupportedRegistry = await new DshInstallService().InstallAsync(
        new NodeRuntimeInfo(true, "node.exe", "24.19.0", null),
        "https://example.invalid/registry");
    Assert(!unsupportedRegistry.IsSuccess && unsupportedRegistry.Error?.Contains("安装源", StringComparison.Ordinal) == true,
        "DSh 安装只能选择官方 npm 源或国内镜像，不能接受任意 registry。");
}

static async Task TestSourceRunnerGuard()
{
    var instance = new ManagerInstance(
        Id: "source-runner-guard",
        Name: "Source 启动保护",
        RootPath: Environment.CurrentDirectory,
        Kind: InstanceKind.Source,
        DshHome: Path.Combine(Path.GetTempPath(), "dsh-launcher-source-guard"),
        DshExecutablePath: null,
        DetectedVersion: null,
        RuntimeStatus: InstanceRuntimeStatus.Unknown,
        PackageManager: "pnpm",
        LastError: null,
        RegisteredAt: DateTimeOffset.UtcNow);

    await using var runner = new DshInstanceRunner();
    var result = await runner.StartAsync(instance);
    Assert(!result.IsSuccess, "Source 实例在构建前不应直接启动。");
    Assert(result.Error?.Contains("Source", StringComparison.OrdinalIgnoreCase) == true,
        "Source 直接启动应返回明确错误。");
}

static async Task TestSourcePrepareInstallAndBuild()
{
    var runtime = await new NodeRuntimeDetector().DetectAsync();
    if (!runtime.IsAvailable || runtime.ExecutablePath is null)
    {
        Console.WriteLine("INFO Source prepare skipped because Node.js is not installed");
        return;
    }

    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "deepseek-harness");
    Directory.CreateDirectory(Path.Combine(root, "apps", "cli"));
    File.WriteAllText(
        Path.Combine(root, "package.json"),
        "{\"name\":\"deepseek-harness\",\"version\":\"0.1.0\",\"packageManager\":\"pnpm@11.7.0\",\"scripts\":{\"build\":\"pnpm run build\"}}",
        new UTF8Encoding(false));

    var packageManager = Path.Combine(temporary.Path, "pnpm.cmd");
    File.WriteAllText(
        packageManager,
        "@echo off\r\n"
        + "if \"%~1\"==\"install\" (\r\n"
        + "  if not exist node_modules mkdir node_modules\r\n"
        + "  exit /b 0\r\n"
        + ")\r\n"
        + "if \"%~1\"==\"run\" if \"%~2\"==\"build\" (\r\n"
        + "  if exist fail-build exit /b 7\r\n"
        + "  if not exist apps\\cli\\lib mkdir apps\\cli\\lib\r\n"
        + "  >apps\\cli\\lib\\bin.js echo // built fixture\r\n"
        + "  exit /b 0\r\n"
        + ")\r\n"
        + "exit /b 9\r\n",
        new UTF8Encoding(false));

    var project = new SourceProjectInspector().Inspect(root);
    var compatibleRuntime = runtime with { Version = "24.0.0" };
    var service = new SourceBuildService(
        commandResolver: name => string.Equals(name, "pnpm", StringComparison.OrdinalIgnoreCase)
            ? packageManager
            : null,
        commandTimeout: TimeSpan.FromSeconds(10));
    var prepared = await service.PrepareAsync(project, compatibleRuntime);
    Assert(prepared.IsSuccess, prepared.Error ?? "Source 依赖安装和构建失败。");
    Assert(prepared.DependenciesInstalled && prepared.BuildExecuted,
        "Source 成功准备必须记录依赖安装和构建步骤。");
    Assert(prepared.EntrypointPath is not null && File.Exists(prepared.EntrypointPath),
        "Source 构建成功必须找到 CLI 构建入口。");

    var failingPackageManager = Path.Combine(temporary.Path, "pnpm-fail.cmd");
    File.WriteAllText(failingPackageManager, "@echo off\r\nexit /b 7\r\n", new UTF8Encoding(false));
    var failingService = new SourceBuildService(
        commandResolver: name => string.Equals(name, "pnpm", StringComparison.OrdinalIgnoreCase)
            ? failingPackageManager
            : null,
        commandTimeout: TimeSpan.FromSeconds(10));
    var failed = await failingService.PrepareAsync(new SourceProjectInspector().Inspect(root), compatibleRuntime);
    Assert(!failed.IsSuccess, "包管理器返回非零退出码时构建必须失败。");
    Assert(failed.Error?.Contains("7", StringComparison.Ordinal) == true,
        "构建失败应保留包管理器退出码诊断。");
}

static async Task TestSourceRunnerLifecycle()
{
    var runtime = await new NodeRuntimeDetector().DetectAsync();
    if (!runtime.IsAvailable || runtime.ExecutablePath is null || !runtime.IsCompatibleWithDshSource)
    {
        Console.WriteLine($"INFO Source lifecycle skipped because compatible Node.js is unavailable ({runtime.VersionText})");
        return;
    }

    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "deepseek-harness");
    var entrypointDirectory = Path.Combine(root, "apps", "cli", "lib");
    Directory.CreateDirectory(entrypointDirectory);
    File.WriteAllText(
        Path.Combine(root, "package.json"),
        "{\"name\":\"deepseek-harness\",\"version\":\"0.1.0\"}",
        new UTF8Encoding(false));
    File.WriteAllText(
        Path.Combine(entrypointDirectory, "bin.js"),
        "const http = require('http');\n"
        + "const args = process.argv;\n"
        + "const port = Number(args[args.indexOf('--port') + 1]);\n"
        + "const host = args[args.indexOf('--host') + 1];\n"
        + "http.createServer((request, response) => { response.statusCode = 200; response.end('ok'); }).listen(port, host);\n",
        new UTF8Encoding(false));

    var instance = new ManagerInstance(
        Id: "source-lifecycle-test",
        Name: "Source 生命周期测试",
        RootPath: root,
        Kind: InstanceKind.Source,
        DshHome: Path.Combine(temporary.Path, "dsh-home"),
        DshExecutablePath: null,
        DetectedVersion: "0.1.0",
        RuntimeStatus: InstanceRuntimeStatus.Ready,
        PackageManager: "pnpm",
        LastError: null,
        RegisteredAt: DateTimeOffset.UtcNow);

    await using var runner = new DshInstanceRunner();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var started = await runner.StartAsync(instance, runtime, cancellation.Token);
    Assert(started.IsSuccess, started.Error ?? "Source 生命周期启动失败。");
    Assert(started.Port is > 0 && started.WebUrl is not null, "Source 启动成功必须返回端口和 URL。");
    var stopped = await runner.StopAsync(instance.Id, cancellation.Token);
    Assert(stopped.IsSuccess, stopped.Error ?? "Source 生命周期停止失败。");
    Assert(!runner.IsRunning(instance.Id), "Source 停止后不能继续报告运行中。");
}

static async Task TestDshEarlyExitCleanup()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "installed");
    Directory.CreateDirectory(root);
    var executable = Path.Combine(root, "dsh.cmd");
    File.WriteAllText(executable, "@echo off\r\nexit /b 9\r\n", new UTF8Encoding(false));
    var instance = new ManagerInstance(
        Id: "early-exit-test",
        Name: "异常退出测试",
        RootPath: root,
        Kind: InstanceKind.Installed,
        DshHome: Path.Combine(temporary.Path, "dsh-home"),
        DshExecutablePath: executable,
        DetectedVersion: "test",
        RuntimeStatus: InstanceRuntimeStatus.Ready,
        PackageManager: "npm",
        LastError: null,
        RegisteredAt: DateTimeOffset.UtcNow);

    await using var runner = new DshInstanceRunner();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var result = await runner.StartAsync(instance, cancellation.Token);
    Assert(!result.IsSuccess, "DSh 在健康检查前异常退出时启动必须失败。");
    Assert(result.Error?.Contains("退出", StringComparison.Ordinal) == true,
        "异常退出应返回进程退出诊断，而不是无期限等待健康检查。");
    Assert(!runner.IsRunning(instance.Id), "异常退出后 Runner 不能保留幽灵运行状态。");
}

static async Task TestDshInstanceLifecycle()
{
    var runtime = await new DshRuntimeDetector().DetectAsync();
    if (!runtime.IsAvailable || runtime.ExecutablePath is null || runtime.PackageRoot is null)
    {
        Console.WriteLine("INFO DSh lifecycle skipped because DSh is not installed");
        return;
    }

    using var temporary = new TestDirectory();
    var instance = new ManagerInstance(
        Id: "lifecycle-test",
        Name: "生命周期测试",
        RootPath: runtime.PackageRoot,
        Kind: InstanceKind.Installed,
        DshHome: Path.Combine(temporary.Path, "dsh-home"),
        DshExecutablePath: Path.GetFullPath(runtime.ExecutablePath),
        DetectedVersion: runtime.Version,
        RuntimeStatus: InstanceRuntimeStatus.Ready,
        PackageManager: "npm",
        LastError: null,
        RegisteredAt: DateTimeOffset.UtcNow);

    await using var runner = new DshInstanceRunner();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var started = await runner.StartAsync(instance, cancellation.Token);
    Console.WriteLine($"INFO DSh lifecycle start: success={started.IsSuccess}, error={started.Error}");
    Assert(started.IsSuccess, started.Error ?? "DSh 生命周期启动失败。");
    Assert(started.ProcessId is > 0 && started.Port is > 0 && started.WebUrl is not null,
        "启动成功必须返回进程、端口和 Web 地址。");
    Assert(runner.IsRunning(instance.Id), "健康检查通过后 Runner 应报告实例运行中。");

    var secondInstance = instance with
    {
        Id = "lifecycle-test-second",
        Name = "生命周期第二实例",
        DshHome = Path.Combine(temporary.Path, "dsh-home-second")
    };
    var secondStarted = await runner.StartAsync(secondInstance, cancellation.Token);
    Assert(secondStarted.IsSuccess, secondStarted.Error ?? "第二个实例应能同时启动。");
    Assert(secondStarted.Port is > 0
        && secondStarted.Port != started.Port
        && secondStarted.WebUrl is not null,
        "同时启动的第二个实例必须使用不同端口和 Web 地址。");
    Assert(runner.IsRunning(instance.Id) && runner.IsRunning(secondInstance.Id),
        "两个不同 DSH_HOME 的实例应同时处于运行状态。");
    await runner.StopAsync(secondInstance.Id, cancellation.Token);
    Assert(runner.IsRunning(instance.Id) && !runner.IsRunning(secondInstance.Id),
        "停止第二个实例不能影响第一个实例。");

    var duplicateStart = await runner.StartAsync(instance, cancellation.Token);
    Console.WriteLine($"INFO DSh lifecycle duplicate: success={duplicateStart.IsSuccess}, error={duplicateStart.Error}");
    Assert(duplicateStart.IsSuccess, duplicateStart.Error ?? "重复启动不应失败。");
    Assert(duplicateStart.ProcessId == started.ProcessId && duplicateStart.Port == started.Port,
        "重复启动应复用同一个受管进程和端口。");

    await using var competingRunner = new DshInstanceRunner();
    var competingStart = await Task.Run(() => competingRunner.StartAsync(instance, cancellation.Token));
    Console.WriteLine($"INFO DSh lifecycle cross-runner: success={competingStart.IsSuccess}, error={competingStart.Error}");
    Assert(!competingStart.IsSuccess, "另一个 Launcher Runner 不应启动相同 DSH_HOME。");

    var stopped = await runner.StopAsync(instance.Id, cancellation.Token);
    Console.WriteLine($"INFO DSh lifecycle stop: success={stopped.IsSuccess}, error={stopped.Error}");
    Assert(stopped.IsSuccess, stopped.Error ?? "DSh 停止失败。");
    Assert(!runner.IsRunning(instance.Id), "停止后 Runner 不应继续报告实例运行中。");

    var restarted = await runner.RestartAsync(instance, cancellation.Token);
    Console.WriteLine($"INFO DSh lifecycle restart: success={restarted.IsSuccess}, error={restarted.Error}");
    Assert(restarted.IsSuccess, restarted.Error ?? "DSh 重启失败。");
    Assert(runner.IsRunning(instance.Id), "重启后 Runner 应报告实例运行中。");
    await runner.StopAsync(instance.Id, cancellation.Token);

    var takeover = await competingRunner.StartAsync(instance, cancellation.Token);
    Assert(takeover.IsSuccess, takeover.Error ?? "首个 Runner 停止后，实例应允许被另一个 Runner 接管。");
    await competingRunner.StopAsync(instance.Id, cancellation.Token);
}

static async Task TestAttachedRuntimeLifecycle()
{
    using var temporary = new TestDirectory();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var request = new byte[2048];
        _ = await stream.ReadAsync(request);
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        await stream.WriteAsync(response);
    });

    var instance = new ManagerInstance(
        Id: "attached-runtime-test",
        Name: "Attached 运行态测试",
        RootPath: temporary.Path,
        Kind: InstanceKind.Installed,
        DshHome: Path.Combine(temporary.Path, "dsh-home"),
        DshExecutablePath: null,
        DetectedVersion: "test",
        RuntimeStatus: InstanceRuntimeStatus.Running,
        PackageManager: "npm",
        LastError: null,
        RegisteredAt: DateTimeOffset.UtcNow,
        ProcessId: 43210,
        Port: port,
        WebUrl: $"http://127.0.0.1:{port}/");

    await using var runner = new DshInstanceRunner();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    Assert(await runner.TryAttachAsync(instance, cancellation.Token), "健康的 loopback DSh Web 端点应可被 Attached。");
    Assert(runner.IsRunning(instance.Id), "Attached 服务应可打开 Web UI 并报告运行中。");
    Assert(runner.IsAttached(instance.Id), "Attached 服务必须标记为外部所有权。");
    Assert(!runner.IsManaged(instance.Id), "Attached 服务不能被当作 Launcher 管理进程。");

    var stop = await runner.StopAsync(instance.Id, cancellation.Token);
    Assert(!stop.IsSuccess && stop.Error?.Contains("外部", StringComparison.Ordinal) == true,
        "Stop 不得停止 Attached 外部进程。");
    var restart = await runner.RestartAsync(instance, cancellation.Token);
    Assert(!restart.IsSuccess && restart.Error?.Contains("外部", StringComparison.Ordinal) == true,
        "Restart 不得重启 Attached 外部进程。");

    await serverTask;
}

static async Task TestExtensionEcosystemIsolation()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "workspace");
    var home = Path.Combine(temporary.Path, "dsh-home");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(home);
    var instance = CreateTestInstance("ecosystem-test", root, home);

    var profile = Path.Combine(home, "profiles", "web");
    Directory.CreateDirectory(profile);
    File.WriteAllText(
        Path.Combine(profile, "package.json"),
        "{\"dependencies\":{\"@deepseek-ai/dsh-base\":\"1.0.0\",\"demo-plugin\":\"1.2.3\",\"@deepseek-ai/dsh-mcp-client\":\"1.0.0\"},\"dsh\":{\"profile\":{\"bundles\":[\"@deepseek-ai/dsh-base\",\"demo-plugin\"]}}}",
        new UTF8Encoding(false));

    var service = new ExtensionService();
    var initial = await service.ListAsync(instance);
    var plugin = initial.Single(entry => entry.Kind == ExtensionKind.Plugin && entry.Name == "demo-plugin");
    Assert(plugin.Enabled, "profile bundles 中的 Plugin 应被列为已启用。");

    await service.SetPluginEnabledAsync(instance, plugin, false);
    var profileAfterDisable = File.ReadAllText(Path.Combine(profile, "package.json"));
    Assert(!profileAfterDisable.Contains("\"demo-plugin\"],", StringComparison.Ordinal), "禁用 Plugin 不能继续留在 bundles 中。");
    await service.SetPluginEnabledAsync(instance, plugin, true);

    var skillSource = Path.Combine(temporary.Path, "skill-source");
    Directory.CreateDirectory(skillSource);
    File.WriteAllText(
        Path.Combine(skillSource, "SKILL.md"),
        "---\nname: demo-skill\ndescription: A test skill\n---\n# Demo\n",
        new UTF8Encoding(false));
    var skill = await service.ImportSkillAsync(instance, skillSource);
    Assert(File.Exists(skill.Location), "导入 Skill 后必须存在 SKILL.md。");
    Assert((await service.ListAsync(instance)).Any(entry => entry.Id == skill.Id), "导入 Skill 后应能从 DSH_HOME 列出。");

    var agentSkillDirectory = Path.Combine(home, ".agents", "skills", "agent-only");
    Directory.CreateDirectory(agentSkillDirectory);
    File.WriteAllText(
        Path.Combine(agentSkillDirectory, "SKILL.md"),
        "---\nname: agent-only\ndescription: Instance-local agent skill\n---\n# Agent only\n",
        new UTF8Encoding(false));
    var agentSkill = (await service.ListAsync(instance)).Single(entry => entry.Name == "agent-only");
    Assert(agentSkill.Managed, "实例 DSH_AGENTS_HOME 下的 Skill 必须被识别为当前实例资源。");
    await service.RemoveSkillAsync(instance, agentSkill);
    Assert(!Directory.Exists(agentSkillDirectory), "删除实例 DSH_AGENTS_HOME 下的 Skill 不能留下目录。");

    await AssertThrowsAsync<InvalidOperationException>(
        () => service.ImportSkillAsync(instance, Path.Combine(home, "skills")),
        "不能把包含 skills 根目录的目录复制到它的子目录。");

    var fakeDsh = Path.Combine(temporary.Path, "dsh.cmd");
    File.WriteAllText(fakeDsh, "@echo off\r\nexit /b 0\r\n", new UTF8Encoding(false));
    var installedInstance = instance with { DshExecutablePath = fakeDsh };
    await service.AddMcpAsync(
        installedInstance,
        new McpServerDefinition("auto-enable", "stdio", "node", Array.Empty<string>(), null, new Dictionary<string, string>(), null),
        null);
    var profileAfterMcp = File.ReadAllText(Path.Combine(profile, "package.json"));
    Assert(profileAfterMcp.Contains("  \"@deepseek-ai/dsh-mcp-client\"", StringComparison.Ordinal), "添加 MCP 时应自动启用已安装但被禁用的 MCP Plugin。");

    await service.AddMcpConfigurationAsync(
        instance,
        new McpServerDefinition(
            "local-test",
            "stdio",
            "node",
            new[] { "server.js" },
            null,
            new Dictionary<string, string> { ["TEST_TOKEN"] = "redacted" },
            root));
    var patchPath = service.GetLauncherPatchPath(instance);
    Assert(File.ReadAllText(patchPath).Contains("local-test", StringComparison.Ordinal), "MCP 配置必须写入 Launcher patch。");
    await service.SetMcpEnabledAsync(instance, "local-test", false);
    var patchAfterLocalDisable = File.ReadAllText(patchPath);
    Assert(!patchAfterLocalDisable.Contains("local-test", StringComparison.Ordinal)
        && patchAfterLocalDisable.Contains("auto-enable", StringComparison.Ordinal), "禁用 MCP 只能移除选中的 server，不能影响其它 server。");
    await service.SetMcpEnabledAsync(instance, "auto-enable", false);
    Assert(File.ReadAllText(patchPath).Trim() == "[]", "禁用全部 MCP 后 patch 不应继续加载 server。");
    await AssertThrowsAsync<ArgumentException>(
        () => service.AddMcpConfigurationAsync(
            instance,
            new McpServerDefinition("bad/name", "stdio", "node", Array.Empty<string>(), null, new Dictionary<string, string>(), null)),
        "MCP serverName 不能通过路径分隔符注入。");

    var presetSource = Path.Combine(temporary.Path, "preset-source");
    Directory.CreateDirectory(presetSource);
    File.WriteAllText(Path.Combine(presetSource, "agent.cordis.yml"), "[]\n", new UTF8Encoding(false));
    var preset = await service.ImportPresetAsync(instance, presetSource);
    Assert(File.Exists(Path.Combine(preset.Location, "agent.cordis.yml")), "导入 Agent Preset 必须复制其 agent.cordis.yml。");
    await service.RemovePresetAsync(instance, preset);
    Assert(!Directory.Exists(preset.Location), "删除 Agent Preset 只能删除实例自己的导入目录。");

    var guarded = new ExtensionService(_ => true);
    await AssertThrowsAsync<InvalidOperationException>(
        () => guarded.ImportSkillAsync(instance, skillSource),
        "实例运行时不能导入 Skill。");
    await service.RemoveSkillAsync(instance, skill);
}

static async Task TestDshMarketThemeBridge()
{
    var requests = new List<HttpRequestMessage>();
    using var client = new HttpClient(new ProviderTestHandler(request =>
    {
        requests.Add(request);
        if (request.RequestUri?.AbsolutePath == "/dsh-market/installed")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"installed\":{\"dsh-theme-dark\":\"github:theme/dark\"},\"live\":[\"dsh-theme-dark\"]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        Assert(request.RequestUri?.AbsolutePath == "/dsh-market/use-skin", "主题应用必须调用 dsh-market 的 use-skin 路由。");
        Assert(request.Headers.TryGetValues("Origin", out var origins)
            && origins.Single() == "http://127.0.0.1:43123", "主题应用必须发送当前实例的同源 Origin。");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"ok\":true,\"live\":[\"dsh-theme-dark\"]}",
                Encoding.UTF8,
                "application/json")
        };
    }));

    using var service = new DshMarketThemeService(client);
    var instance = CreateTestInstance("theme-test", "C:\\workspace", "C:\\dsh-home") with
    {
        RuntimeStatus = InstanceRuntimeStatus.Running,
        WebUrl = "http://127.0.0.1:43123/"
    };
    var state = await service.ReadAsync(instance);
    Assert(state.IsAvailable && state.InstalledNames.Contains("dsh-theme-dark"), "应读取 dsh-market 的已安装主题名称。");
    Assert(state.LiveNames.Contains("dsh-theme-dark"), "应读取 dsh-market 当前热加载资源。");

    var applied = await service.ApplyAsync(instance, "dsh-theme-dark");
    Assert(applied.IsSuccess && applied.LiveNames.Contains("dsh-theme-dark"), "应通过 dsh-market 应用主题并读取结果。");
    Assert(requests.Count == 2, "主题桥接应只执行一次状态读取和一次应用请求。");

    var unavailable = await service.ReadAsync(instance with
    {
        RuntimeStatus = InstanceRuntimeStatus.Stopped,
        WebUrl = null
    });
    Assert(!unavailable.IsAvailable, "未运行实例不能虚构 dsh-market 主题状态。");
}

static async Task TestPluginCommandSuppliesPnpmRuntime()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "workspace");
    var home = Path.Combine(temporary.Path, "dsh-home");
    var runtimeDirectory = Path.Combine(temporary.Path, "node-runtime");
    var marker = Path.Combine(temporary.Path, "pnpm-version.txt");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(home);
    Directory.CreateDirectory(runtimeDirectory);

    var dsh = Path.Combine(runtimeDirectory, "dsh.cmd");
    var node = Path.Combine(runtimeDirectory, "node.exe");
    var corepack = Path.Combine(runtimeDirectory, "corepack.cmd");
    File.WriteAllText(node, string.Empty, new UTF8Encoding(false));
    File.WriteAllText(
        corepack,
        "@echo off\r\n"
        + "if /I \"%~1\"==\"pnpm\" (echo 11.21.0 & exit /b 0)\r\n"
        + "exit /b 1\r\n",
        new UTF8Encoding(false));
    File.WriteAllText(
        dsh,
        "@echo off\r\n"
        + "pnpm --version > \"" + marker + "\" 2>&1\r\n"
        + "if errorlevel 1 exit /b 21\r\n"
        + "exit /b 0\r\n",
        new UTF8Encoding(false));

    var instance = CreateTestInstance("plugin-runtime", root, home) with { DshExecutablePath = dsh };
    var previousPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", Path.Combine(temporary.Path, "empty-path"));
        await new ExtensionService().InstallPluginAsync(instance, "demo-plugin", null);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
    }

    Assert(File.Exists(marker), "Plugin CLI 应能通过 Launcher 提供的 pnpm 环境运行。 ");
    Assert(File.ReadAllText(marker).Contains("11.21.0", StringComparison.Ordinal), "Plugin CLI 应使用可用的 Corepack pnpm shim。 ");
}

static async Task TestMarketplaceDiscoveryAndVerification()
{
    using var temporary = new TestDirectory();
    var launcherRoot = Path.Combine(temporary.Path, "launcher");
    Directory.CreateDirectory(launcherRoot);
    var catalogPath = Path.Combine(launcherRoot, "marketplace.json");
    File.WriteAllText(
        catalogPath,
        "{\"plugins\":[{\"name\":\"custom-plugin\",\"npm\":\"custom-plugin\",\"description\":\"custom\",\"category\":\"tools\"}]}",
        new UTF8Encoding(false));

    using var httpClient = new HttpClient(new ProviderTestHandler(request =>
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        if (url.Contains("awesome-dsh-plugin.com", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse("{\"plugins\":[{\"name\":\"demo-plugin\",\"npm\":\"demo-plugin\",\"description\":\"demo\",\"category\":\"tools\"}]}");
        }

        if (url.Contains("api.github.com/repos/demo/monorepo", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse("{\"default_branch\":\"develop\"}");
        }

        if (url.Contains("api.github.com/repos/demo/community-theme", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse("{\"default_branch\":\"develop\"}");
        }

        if (url.Contains("raw.githubusercontent.com/demo/community-theme/develop/package.json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse("{\"name\":\"community-theme\",\"version\":\"1.0.0\",\"main\":\"index.js\",\"dsh.bundle.patch\":{}}");
        }

        if (url.Contains("raw.githubusercontent.com/demo/monorepo/develop/packages/theme/package.json", StringComparison.OrdinalIgnoreCase)
            || url.Contains("raw.githubusercontent.com/demo/monorepo/feature/packages/theme/package.json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse("{\"name\":\"demo-theme\",\"version\":\"2.0.0\",\"main\":\"index.js\",\"dsh.bundle.patch\":{}}");
        }

        if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse("{\"items\":[]}");
        }

        if (url.Contains("registry.npmjs.org/demo-plugin", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse("{\"dist-tags\":{\"latest\":\"1.2.3\"},\"versions\":{\"1.2.3\":{\"name\":\"demo-plugin\",\"version\":\"1.2.3\",\"main\":\"index.js\",\"dsh.bundle.patch\":{}}}}");
        }

        if (url.Contains("registry.npmjs.org/bad-plugin", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse("{\"dist-tags\":{\"latest\":\"1.0.0\"},\"versions\":{\"1.0.0\":{\"name\":\"bad-plugin\",\"version\":\"1.0.0\",\"main\":\"index.js\"}}}");
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found", Encoding.UTF8, "text/plain")
        };
    }));

    var service = new MarketplaceService(new LauncherPaths(launcherRoot), httpClient);
    var result = await service.SearchAsync(null, "plugin");
    Assert(result.Items.Count == 2, "市场搜索应合并社区目录和本地自定义目录，并按关键词过滤。 ");
    Assert(result.Items.All(item => item.VerificationStatus == MarketplaceVerificationStatus.Unverified), "目录条目默认只能标记为待检查，不能把目录收录当成安全证明。 ");

    var commandCatalog = MarketplaceService.ParseCatalog(
        "{\"plugins\":[{\"name\":\"community-theme\",\"install\":\"dsh plugin --profile web add github:demo/community-theme\",\"category\":\"theme\"}]}",
        MarketplaceSourceKind.CommunityCatalog,
        "社区目录");
    Assert(commandCatalog.Count == 1
        && commandCatalog[0].InstallSpec == "github:demo/community-theme"
        && commandCatalog[0].RepositoryUrl == "https://github.com/demo/community-theme",
        "社区目录的完整 DSh CLI 安装命令应先提取为可传给官方 CLI 的 Plugin spec。 ");
    var commandVerified = await service.VerifyAsync(commandCatalog[0]);
    Assert(commandVerified.Status == MarketplaceVerificationStatus.Verified
        && commandVerified.InstallSpec == "github:demo/community-theme",
        "从社区目录提取出的 GitHub Plugin spec 应能通过 package.json 校验。 ");

    var githubWithPackageName = commandCatalog[0] with
    {
        PackageName = "community-theme",
        RepositoryUrl = "https://github.com/demo/community-theme"
    };
    var githubPackageVerified = await service.VerifyAsync(githubWithPackageName);
    Assert(githubPackageVerified.Status == MarketplaceVerificationStatus.Verified
        && githubPackageVerified.PackageName == "community-theme",
        "同时包含展示用包名和 GitHub 安装地址时，应按 GitHub 来源校验而不是误走 npm。 ");

    var verified = await service.VerifyAsync(new MarketplaceItem(
        "npm:demo-plugin",
        "demo-plugin",
        "demo-plugin",
        null,
        "demo",
        "demo-plugin",
        null,
        "tools",
        MarketplaceSourceKind.CommunityCatalog,
        "test",
        MarketplaceVerificationStatus.Unverified,
        "待检查"));
    Assert(verified.Status == MarketplaceVerificationStatus.Verified && verified.Version == "1.2.3", "有 dsh.bundle.patch 和入口的 npm 包应通过检查。 ");
    var npmWithRepositoryMetadata = await service.VerifyAsync(new MarketplaceItem(
        "npm:demo-plugin-with-repository",
        "demo-plugin",
        "demo-plugin",
        null,
        "demo",
        "demo-plugin",
        "https://github.com/demo/demo-plugin",
        "tools",
        MarketplaceSourceKind.CommunityCatalog,
        "test",
        MarketplaceVerificationStatus.Unverified,
        "待检查"));
    Assert(npmWithRepositoryMetadata.Status == MarketplaceVerificationStatus.Verified
        && npmWithRepositoryMetadata.Version == "1.2.3",
        "npm 安装目标附带 GitHub 仓库元数据时，仍应按 npm 包校验。 ");

    var rejected = await service.VerifyAsync(new MarketplaceItem(
        "npm:bad-plugin",
        "bad-plugin",
        "bad-plugin",
        null,
        "bad",
        "bad-plugin",
        null,
        "tools",
        MarketplaceSourceKind.CommunityCatalog,
        "test",
        MarketplaceVerificationStatus.Unverified,
        "待检查"));
    Assert(rejected.Status == MarketplaceVerificationStatus.Rejected
        && rejected.Message.Contains("dsh.bundle.patch", StringComparison.Ordinal), "没有 DSh bundle 声明的 npm 包必须被拒绝。 ");

    var githubDefaultBranch = await service.VerifyAsync(new MarketplaceItem(
        "github:demo/monorepo#path:/packages/theme",
        "demo-theme",
        null,
        null,
        "theme",
        "github:demo/monorepo#path:/packages/theme",
        "https://github.com/demo/monorepo",
        "theme",
        MarketplaceSourceKind.GitHubTopic,
        "test",
        MarketplaceVerificationStatus.Unverified,
        "待检查"));
    Assert(githubDefaultBranch.Status == MarketplaceVerificationStatus.Verified
        && githubDefaultBranch.PackageName == "demo-theme",
        "GitHub 校验应读取 default_branch 并支持 monorepo #path 子目录。 ");

    var githubExplicitBranch = await service.VerifyAsync(new MarketplaceItem(
        "github:demo/monorepo/tree/feature/packages/theme",
        "demo-theme",
        null,
        null,
        "theme",
        "https://github.com/demo/monorepo/tree/feature/packages/theme",
        "https://github.com/demo/monorepo/tree/feature/packages/theme",
        "theme",
        MarketplaceSourceKind.GitHubTopic,
        "test",
        MarketplaceVerificationStatus.Unverified,
        "待检查"));
    Assert(githubExplicitBranch.Status == MarketplaceVerificationStatus.Verified,
        "GitHub 校验应保留 tree/<branch>/<subpath> 的显式分支。 ");

    var root = Path.Combine(temporary.Path, "workspace");
    var home = Path.Combine(temporary.Path, "dsh-home");
    Directory.CreateDirectory(Path.Combine(home, "profiles", "web"));
    File.WriteAllText(Path.Combine(home, "profiles", "web", "package.json"), "{}", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(home, "profiles", "web", "cordis.patch.yml"), "[]\n", new UTF8Encoding(false));
    var instance = CreateTestInstance("marketplace-test", root, home);
    var snapshot = service.CreatePluginSnapshot(instance);
    Assert(File.Exists(Path.Combine(snapshot, "package.json")) && File.Exists(Path.Combine(snapshot, "cordis.patch.yml")), "市场操作前应备份 web profile 配置。 ");
    File.WriteAllText(Path.Combine(home, "profiles", "web", "package.json"), "{\"broken\":true}", new UTF8Encoding(false));
    Assert(service.RestorePluginSnapshot(instance, snapshot), "Plugin 操作失败后应能恢复操作前的 web profile 配置。 ");
    Assert(File.ReadAllText(Path.Combine(home, "profiles", "web", "package.json")) == "{}", "恢复 Plugin 快照不能留下失败操作写入的配置。 ");

    var cached = service.ReadCached(null, "plugin");
    Assert(cached is not null && cached.Items.Count == 2, "在线市场结果应写入缓存，并可在没有网络请求时读取。 ");
    var sorted = MarketplaceService.FilterAndSort(
        new[]
        {
            new MarketplaceItem("a", "旧但热门", "a", null, "", "a", null, "tools", MarketplaceSourceKind.Custom, "test", MarketplaceVerificationStatus.Unverified, "", Stars: 100, PublishedAt: DateTimeOffset.UtcNow.AddDays(-5)),
            new MarketplaceItem("b", "新但冷门", "b", null, "", "b", null, "tools", MarketplaceSourceKind.Custom, "test", MarketplaceVerificationStatus.Unverified, "", Stars: 2, PublishedAt: DateTimeOffset.UtcNow)
        },
        sortOrder: MarketplaceSortOrder.PublishedAt);
    Assert(sorted[0].Name == "新但冷门", "市场应支持按发布时间排序。 ");
    sorted = MarketplaceService.FilterAndSort(sorted, sortOrder: MarketplaceSortOrder.Stars);
    Assert(sorted[0].Name == "旧但热门", "市场应支持按 Star 数量排序。 ");
    var localFiltered = MarketplaceService.FilterAndSort(
        new[]
        {
            new MarketplaceItem("local-a", "Aegis Tools", "aegis-tools", "1.0.0", "本地搜索测试", "aegis-tools", null, "tools", MarketplaceSourceKind.Custom, "test", MarketplaceVerificationStatus.Unverified, ""),
            new MarketplaceItem("local-b", "Other", "other", "1.0.0", "Aegis description", "other", null, "theme", MarketplaceSourceKind.Custom, "test", MarketplaceVerificationStatus.Unverified, "")
        },
        query: "Aegis",
        category: "工具");
    Assert(localFiltered.Count == 1 && localFiltered[0].Name == "Aegis Tools", "本地搜索应同时支持即时关键词和分类过滤。 ");

    var merged = MarketplaceService.MergeItems(new[]
    {
        new MarketplaceItem(
            "catalog:better-sidebar",
            "Better Sidebar",
            null,
            "1.2.0",
            "中文介绍",
            "https://github.com/demo/better-plugins/tree/main/packages/sidebar",
            "https://github.com/demo/better-plugins/tree/main/packages/sidebar",
            "ui",
            MarketplaceSourceKind.CommunityCatalog,
            "社区目录",
            MarketplaceVerificationStatus.Unverified,
            "目录待检查",
            Stars: 123),
        new MarketplaceItem(
            "github:demo/better-plugins",
            "sidebar",
            null,
            null,
            "GitHub description",
            "github:demo/better-plugins#path:/packages/sidebar",
            "https://github.com/demo/better-plugins",
            "tools",
            MarketplaceSourceKind.GitHubTopic,
            "GitHub 发现",
            MarketplaceVerificationStatus.Unverified,
            "标签只用于发现")
    });
    Assert(merged.Count == 1, "相同 GitHub monorepo 子路径的来源必须合并成一个条目。 ");
    Assert(merged[0].Stars == 123 && merged[0].Category == "UI", "来源合并不能丢失目录的 Star 和分类。 ");
    Assert(merged[0].SourceText.Contains("社区目录", StringComparison.Ordinal)
        && merged[0].SourceText.Contains("GitHub", StringComparison.Ordinal), "合并条目应保留多个来源信息。 ");
    Assert(MarketplaceService.FilterAndSort(merged, sourceKind: MarketplaceSourceKind.GitHubTopic).Count == 1
        && MarketplaceService.FilterAndSort(merged, sourceKind: MarketplaceSourceKind.CommunityCatalog).Count == 1,
        "来源筛选不能因为多来源合并后选择了一个主来源而丢失条目。 ");

    var installed = new ExtensionEntry(
        "plugin:github:demo/better-plugins#path:/packages/sidebar",
        ExtensionKind.Plugin,
        "github:demo/better-plugins#path:/packages/sidebar",
        "1.0.0",
        "installed",
        "profile",
        true,
        true);
    var marketplaceIdentity = MarketplaceService.GetPluginIdentities(merged[0]);
    var installedIdentity = MarketplaceService.GetPluginIdentities(installed);
    Assert(marketplaceIdentity.Any(installedIdentity.Contains), "npm/GitHub/subpath identity 应能匹配已安装 Plugin。 ");
    Assert(MarketplaceService.FindInstalledPlugin(merged[0], new[] { installed }) == installed,
        "市场更新或卸载应使用当前 profile 中匹配到的真实 Plugin 包名。 ");
    Assert(MarketplaceService.GetUpdateStatus("1.1.0", "1.0.0") == MarketplaceUpdateStatus.Available, "较新的市场版本应显示可更新。 ");
    Assert(MarketplaceService.GetUpdateStatus("1.0.0", "1.0.0") == MarketplaceUpdateStatus.UpToDate, "相同版本应显示已是最新。 ");
    Assert(MarketplaceService.GetUpdateStatus(null, "1.0.0") == MarketplaceUpdateStatus.Unknown, "缺少版本信息时更新状态应为未知。 ");

    var invalidOfficial = MarketplaceService.ParseCatalog(
        "{\"plugins\":[{\"name\":\"community\",\"npm\":\"community\"}]}",
        MarketplaceSourceKind.Official,
        "DSh 官方");
    var validOfficial = MarketplaceService.ParseCatalog(
        "{\"plugins\":[{\"name\":\"official\",\"npm\":\"@deepseek-ai/official\"}]}",
        MarketplaceSourceKind.Official,
        "DSh 官方");
    Assert(invalidOfficial.Count == 0 && validOfficial.Count == 1, "官方来源只能接受明确的 DeepSeek 官方包或仓库。 ");
    var oldCachedOfficial = MarketplaceService.MergeItems(new[]
    {
        new MarketplaceItem(
            "official:community",
            "community",
            "community",
            "1.0.0",
            "旧缓存",
            "community",
            null,
            "工具",
            MarketplaceSourceKind.Official,
            "当前 DSh 运行环境",
            MarketplaceVerificationStatus.Verified,
            "旧缓存")
    });
    Assert(oldCachedOfficial[0].SourceKind != MarketplaceSourceKind.Official, "旧缓存中的本地依赖不能继续显示为官方来源。 ");
}

static Task TestVersionPackageOperations()
{
    using var temporary = new TestDirectory();
    var launcherRoot = Path.Combine(temporary.Path, "launcher");
    var runtimeRoot = Path.Combine(temporary.Path, "dsh-runtime");
    Directory.CreateDirectory(runtimeRoot);
    var registry = new InstanceRegistry(new LauncherPaths(launcherRoot));
    var source = registry.Register("基础版本", runtimeRoot, InstanceKind.Installed, detectedVersion: "0.1.0", packageManager: "npm");
    Directory.CreateDirectory(Path.Combine(source.DshHome, "profiles", "web"));
    File.WriteAllText(Path.Combine(source.DshHome, "profiles", "web", "settings.json"), "{}", new UTF8Encoding(false));

    var packages = new VersionPackageService(registry, new LauncherPaths(launcherRoot));
    var clone = packages.CloneVersion(source, "复制版本");
    Assert(clone.RootPath == source.RootPath && clone.DshHome != source.DshHome, "复制版本应共享运行目录但复制到新的 DSH_HOME。 ");
    Assert(File.Exists(Path.Combine(clone.DshHome, "profiles", "web", "settings.json")), "复制版本应复制 DSH_HOME 内容。 ");

    var clean = packages.CreateCleanVersion(source, "干净版本");
    Assert(clean.DshHome != source.DshHome && !Directory.EnumerateFileSystemEntries(clean.DshHome).Any(), "干净版本不能带入旧版本文件。 ");

    var settingsService = new VersionSettingsService();
    settingsService.Save(source, new VersionSettingsData
    {
        ConversationSyncMode = ConversationSyncMode.Workspace,
        ConversationWorkspace = "共享工作区",
        SyncModelProviders = true,
        WindowTitle = "分享用版本",
        NodeExecutablePath = Path.Combine(temporary.Path, "node.exe")
    });
    var workspacePeer = registry.Register("工作区副本", runtimeRoot, InstanceKind.Installed, detectedVersion: "0.1.0", packageManager: "npm");
    settingsService.Save(workspacePeer, new VersionSettingsData
    {
        ConversationSyncMode = ConversationSyncMode.Workspace,
        ConversationWorkspace = "共享工作区"
    });
    Assert(settingsService.ShouldSyncConversations(source, workspacePeer), "相同工作区的版本应同步对话文件。 ");
    Assert(settingsService.ShouldSyncModelProviders(source, workspacePeer), "开启所有版本自动同步模型后，不应受对话文件同步范围影响。 ");
    settingsService.Save(workspacePeer, new VersionSettingsData { ConversationSyncMode = ConversationSyncMode.Independent });
    Assert(!settingsService.ShouldSyncConversations(source, workspacePeer), "独立版本不应与工作区版本同步对话文件。 ");
    settingsService.Save(workspacePeer, new VersionSettingsData
    {
        ConversationSyncMode = ConversationSyncMode.Independent,
        SyncModelProviders = false
    });
    Assert(!settingsService.ShouldSyncModelProviders(source, workspacePeer), "关闭所有版本自动同步模型后不应同步模型提供商。 ");
    settingsService.Save(workspacePeer, new VersionSettingsData { ConversationSyncMode = ConversationSyncMode.All });
    Assert(settingsService.ShouldSyncConversations(source, workspacePeer), "全量模式应兜底同步其它版本。 ");
    settingsService.Save(workspacePeer, new VersionSettingsData { SyncAllConfiguration = true });
    Assert(settingsService.ShouldSyncConfiguration(source, workspacePeer), "和所有版本配置同步应覆盖其它版本的独立设置。 ");
    Assert(settingsService.ShouldSyncConversations(source, workspacePeer), "和所有版本配置同步打开后，对话策略应覆盖其它版本的独立设置。 ");

    var sourceSettings = Path.Combine(source.DshHome, "settings.yaml");
    File.WriteAllText(
        sourceSettings,
        "llm-deepseek:\n  apiKey: super-secret\n  apiKeyEnv: DEEPSEEK_API_KEY\n  baseURL: https://api.example\n  models:\n    - deepseek-chat\n",
        new UTF8Encoding(false));
    var sourceProfile = Path.Combine(source.DshHome, "profiles", "web");
    File.WriteAllText(
        Path.Combine(sourceProfile, "package.json"),
        "{\"dependencies\":{\"demo-plugin\":\"1.2.3\",\"remote-plugin\":\"https://share-user:plugin-secret@github.com/demo/remote-plugin.git?token=plugin-query-secret\"},\"scripts\":{\"leak\":\"secret\"},\"dsh\":{\"profile\":{\"bundles\":[\"demo-plugin\"]}}}",
        new UTF8Encoding(false));
    var sessions = Path.Combine(source.DshHome, "sessions");
    Directory.CreateDirectory(sessions);
    File.WriteAllText(Path.Combine(sessions, "private.jsonl"), "do not export", new UTF8Encoding(false));
    var skillDirectory = Path.Combine(source.DshHome, "skills", "code-review");
    Directory.CreateDirectory(skillDirectory);
    File.WriteAllText(
        Path.Combine(skillDirectory, "SKILL.md"),
        "---\nname: code-review\ndescription: Review code\n---\napiKey: skill-secret\n",
        new UTF8Encoding(false));
    var presetDirectory = Path.Combine(source.DshHome, ".agent-presets", "reviewer");
    Directory.CreateDirectory(presetDirectory);
    File.WriteAllText(Path.Combine(presetDirectory, "agent.cordis.yml"), "name: reviewer\n", new UTF8Encoding(false));
    var exportPath = Path.Combine(temporary.Path, "share.dshpack");
    packages.ExportPackage(
        source,
        exportPath,
        new VersionExportOptions(IncludeProviderConfiguration: true, IncludePluginConfiguration: true));
    using (var exported = ZipFile.OpenRead(exportPath))
    {
        Assert(exported.Entries.All(entry => !entry.FullName.Contains("sessions", StringComparison.OrdinalIgnoreCase)),
            "版本整合包不能包含 sessions。 ");
        var exportedSettings = exported.GetEntry("dsh-home/settings.yaml");
        Assert(exportedSettings is not null, "导出模型提供商配置时应包含 settings.yaml。 ");
        using var settingsReader = new StreamReader(exportedSettings!.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var safeSettings = settingsReader.ReadToEnd();
        Assert(!safeSettings.Contains("super-secret", StringComparison.Ordinal)
            && safeSettings.Contains("DEEPSEEK_API_KEY", StringComparison.Ordinal),
            "导出 Provider 配置必须删除 API Key 值但保留环境变量名。 ");
        Assert(exported.GetEntry("dsh-home/profiles/web/package.json") is not null,
            "导出 Plugin 配置时应保留精简后的 profile package.json。 ");
        using var pluginReader = new StreamReader(exported.GetEntry("dsh-home/profiles/web/package.json")!.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var safePlugins = pluginReader.ReadToEnd();
        Assert(!safePlugins.Contains("plugin-secret", StringComparison.Ordinal)
            && !safePlugins.Contains("plugin-query-secret", StringComparison.Ordinal)
            && !safePlugins.Contains("share-user@", StringComparison.Ordinal),
            "导出 Plugin dependency 不能携带 URL 用户名、密码或 Token。 ");
        using var manifestReader = new StreamReader(exported.GetEntry("manifest.json")!.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var manifestText = manifestReader.ReadToEnd();
        Assert(manifestText.Contains("\"sessions\": false", StringComparison.Ordinal)
            && manifestText.Contains("\"apiKeys\": false", StringComparison.Ordinal)
            && manifestText.Contains("\"plugins\"", StringComparison.Ordinal)
            && manifestText.Contains("\"skills\"", StringComparison.Ordinal)
            && manifestText.Contains("\"agentPresets\"", StringComparison.Ordinal)
            && manifestText.Contains("\"providers\"", StringComparison.Ordinal),
            "整合包 manifest 必须声明不包含会话和 API Key。 ");
    }

    var preview = packages.PreviewPackage(exportPath);
    Assert(preview.PluginCount == 2
        && preview.SkillCount == 1
        && preview.AgentPresetCount == 1
        && preview.ProviderCount == 1
        && preview.Workflow == "standard",
        "整合包预览必须显示实际导出的 Plugin、Skill、Agent Preset、Provider 和 Workflow。 ");
    using (var exported = ZipFile.OpenRead(exportPath))
    {
        var skillEntry = exported.GetEntry("dsh-home/skills/code-review/SKILL.md");
        Assert(skillEntry is not null, "整合包应包含可分享的 Skill 文件。 ");
        using var skillReader = new StreamReader(skillEntry!.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        Assert(!skillReader.ReadToEnd().Contains("skill-secret", StringComparison.Ordinal), "Skill 导出不能携带敏感值。 ");
        Assert(exported.GetEntry("dsh-home/.agent-presets/reviewer/agent.cordis.yml") is not null,
            "整合包应包含 Agent Preset 配置。 ");
    }

    var importedDesign = packages.ImportPackage(exportPath, source);
    Assert(!Directory.Exists(Path.Combine(importedDesign.DshHome, "sessions")),
        "导入分享整合包不能创建 sessions 目录。 ");
    var importedSettings = File.ReadAllText(Path.Combine(importedDesign.DshHome, "settings.yaml"));
    Assert(!importedSettings.Contains("super-secret", StringComparison.Ordinal),
        "导入整合包后也不能恢复被清理的 API Key。 ");
    var importedSkill = Path.Combine(importedDesign.DshHome, "skills", "code-review", "SKILL.md");
    Assert(File.Exists(importedSkill)
        && !File.ReadAllText(importedSkill).Contains("skill-secret", StringComparison.Ordinal),
        "导入整合包应恢复 Skill，但不能恢复敏感值。 ");
    Assert(File.Exists(Path.Combine(importedDesign.DshHome, ".agent-presets", "reviewer", "agent.cordis.yml")),
        "导入整合包应恢复 Agent Preset。 ");
    Assert(settingsService.Read(importedDesign).NodeExecutablePath is null,
        "整合包不能恢复本机 Node.js 路径。 ");

    var packPath = Path.Combine(temporary.Path, "import.dshpack");
    using (var archive = ZipFile.Open(packPath, ZipArchiveMode.Create))
    {
        var manifest = archive.CreateEntry("manifest.json");
        using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8, leaveOpen: false))
        {
            writer.Write("{\"formatVersion\":1,\"name\":\"导入版本\",\"detectedVersion\":\"0.2.0\"}");
        }

        var data = archive.CreateEntry("dsh-home/imported.txt");
        using var dataWriter = new StreamWriter(data.Open(), Encoding.UTF8, leaveOpen: false);
        dataWriter.Write("imported");
    }

    var imported = packages.ImportPackage(packPath, source);
    Assert(imported.DetectedVersion == "0.2.0", "整合包 manifest 应能携带版本信息。 ");
    Assert(File.ReadAllText(Path.Combine(imported.DshHome, "imported.txt")) == "imported", "整合包应解压到新版本的 DSH_HOME。 ");
    packages.SavePackageExtension("zip");
    Assert(packages.PackageExtension == ".zip", "整合包扩展名设置应自动补点并保存。 ");

    var deletable = packages.CreateCleanVersion(source, "待删除版本");
    File.WriteAllText(Path.Combine(deletable.DshHome, "delete-me.txt"), "temporary", new UTF8Encoding(false));
    var backupDirectory = Path.Combine(launcherRoot, "backups", deletable.Id);
    Directory.CreateDirectory(backupDirectory);
    File.WriteAllText(Path.Combine(backupDirectory, "backup.txt"), "temporary", new UTF8Encoding(false));
    AssertThrows<InvalidOperationException>(
        () => packages.DeleteVersion(deletable with { RuntimeStatus = InstanceRuntimeStatus.Running }),
        "运行中的版本不能删除。 ");
    Assert(Directory.Exists(deletable.DshHome), "删除保护失败时不能提前删除版本目录。 ");
    packages.DeleteVersion(deletable);
    Assert(!registry.Load().Any(item => item.Id == deletable.Id), "删除版本后注册记录必须移除。 ");
    Assert(!Directory.Exists(deletable.DshHome), "删除版本后必须清理 DSH_HOME。 ");
    Assert(!Directory.Exists(backupDirectory), "删除版本后必须清理该版本的 Launcher 备份。 ");
    return Task.CompletedTask;
}

static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};

static async Task TestModelSettingsRoundTrip()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "workspace");
    var home = Path.Combine(temporary.Path, "dsh-home");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(home);
    var instance = CreateTestInstance("model-test", root, home);
    var settings = Path.Combine(home, "settings.yaml");
    File.WriteAllText(
        settings,
        "unrelated:\n  keep: true\nllm-pi-ai:\n  providers:\n    existing:\n      apiKeyEnv: EXISTING_KEY\n      baseURL: https://existing.example/v1\nllm-deepseek:\n  apiKeyEnv: OLD_KEY\n  baseURL: https://old.example\n",
        new UTF8Encoding(false));

    var service = new ModelService();
    await service.SaveDeepSeekAsync(instance, "DEEPSEEK_API_KEY", "https://api.deepseek.com", new[] { "deepseek-chat", "deepseek-reasoner" });
    await service.SaveOpenAiCompatibleAsync(instance, "gateway", "GATEWAY_KEY", "http://127.0.0.1:8080/v1", new[] { "model-a" });
    var document = File.ReadAllText(settings);
    Assert(document.Contains("keep: true", StringComparison.Ordinal), "模型保存不能删除无关顶层设置。");
    Assert(document.Contains("existing:", StringComparison.Ordinal), "新增 Provider 不能删除已有 Provider。");
    Assert(document.Contains("gateway:", StringComparison.Ordinal), "OpenAI-compatible Provider 必须写入 providers。");
    Assert(!document.Contains("sk-", StringComparison.Ordinal), "模型设置文件不能包含 API Key 明文。");
    var bytes = File.ReadAllBytes(settings);
    Assert(bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF, "settings.yaml 不应写入 BOM。");

    var providers = service.Read(instance);
    var deepseek = providers.Single(provider => provider.SettingsNamespace == "llm-deepseek");
    Assert(deepseek.ApiKeyEnvironment == "DEEPSEEK_API_KEY", "读取 DeepSeek Provider 应返回环境变量名。");
    Assert(deepseek.Models.SequenceEqual(new[] { "deepseek-chat", "deepseek-reasoner" }), "读取模型列表应保持顺序。");
    Assert(providers.Any(provider => provider.Provider == "existing"), "读取时应保留既有 Provider。");
    Assert(providers.Any(provider => provider.Provider == "gateway" && provider.Models.Contains("model-a")), "读取时应识别新 Provider 的模型。");

    await AssertThrowsAsync<ArgumentException>(
        () => service.SaveDeepSeekAsync(instance, "BAD-NAME", "https://api.deepseek.com", Array.Empty<string>()),
        "API Key 环境变量名中的连字符必须被拒绝。");
    var guarded = new ModelService(_ => true);
    await AssertThrowsAsync<InvalidOperationException>(
        () => guarded.SaveDeepSeekAsync(instance, "DEEPSEEK_API_KEY", null, Array.Empty<string>()),
        "实例运行时不能修改模型 settings。");
}

static async Task TestProviderStateAndDiagnostics()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "workspace");
    var home = Path.Combine(temporary.Path, "dsh-home");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(home);
    var instance = CreateTestInstance("provider-test", root, home);
    var provider = new ModelProviderInfo(
        "gateway",
        "测试网关",
        "llm-pi-ai",
        null,
        "http://127.0.0.1:8080/v1",
        new[] { "model-a" },
        Configured: true);

    var state = new ProviderStateService();
    Assert(state.IsEnabled(instance, provider.Provider), "没有 Launcher 状态记录时 Provider 默认应启用。 ");
    state.SetEnabled(instance, provider.Provider, false);
    Assert(!state.IsEnabled(instance, provider.Provider), "禁用 Provider 后应从 DSH_HOME 状态文件读取为禁用。 ");
    Assert(File.ReadAllText(state.GetStatePath(instance)).Contains("\"gateway\": false", StringComparison.Ordinal), "Provider 禁用状态应写入实例隔离目录。 ");
    state.SetEnabled(instance, provider.Provider, true);
    Assert(state.IsEnabled(instance, provider.Provider), "重新启用 Provider 后应恢复启用。 ");

    using var client = new HttpClient(new ProviderTestHandler(request =>
    {
        Assert(request.RequestUri?.AbsolutePath == "/v1/models", "Provider 检测应调用配置 Base URL 下的 /models。 ");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"data\":[{\"id\":\"model-a\",\"reasoningEfforts\":[\"off\",\"high\"]}]}",
                Encoding.UTF8,
                "application/json")
        };
    }));
    using var diagnostics = new ProviderDiagnosticService(client);
    var healthy = await diagnostics.CheckAsync(provider);
    Assert(healthy.IsHealthy && healthy.DiscoveredModelCount == 1, "Provider 检测应识别可用的模型列表。 ");
    Assert(healthy.ThinkingText.Contains("off", StringComparison.Ordinal)
        && healthy.ThinkingText.Contains("high", StringComparison.Ordinal), "Provider 检测应读取模型的思考档位。 ");

    var mismatch = ProviderDiagnosticService.AnalyzeModelListing(
        provider with { Models = new[] { "missing-model" } },
        HttpStatusCode.OK,
        "{\"data\":[{\"id\":\"model-a\"}]}" );
    Assert(!mismatch.IsHealthy && mismatch.StatusText == "模型不匹配", "配置模型不在接口列表中时应显示明确问题。 ");

    var unauthorized = ProviderDiagnosticService.AnalyzeModelListing(
        provider,
        HttpStatusCode.Unauthorized,
        "{}" );
    Assert(!unauthorized.IsHealthy && unauthorized.StatusText == "认证失败", "Provider 返回 401 时应显示认证问题。 ");
}

static Task TestModelProviderSynchronization()
{
    using var temporary = new TestDirectory();
    var runtime = Path.Combine(temporary.Path, "runtime");
    Directory.CreateDirectory(runtime);
    var registry = new InstanceRegistry(new LauncherPaths(Path.Combine(temporary.Path, "launcher")));
    var first = registry.Register("模型版本 A", runtime, InstanceKind.Installed, detectedVersion: "test", packageManager: "npm");
    var second = registry.Register("模型版本 B", runtime, InstanceKind.Installed, detectedVersion: "test", packageManager: "npm");
    var independent = registry.Register("独立模型版本", runtime, InstanceKind.Installed, detectedVersion: "test", packageManager: "npm");
    var settings = new VersionSettingsService();
    settings.Save(first, new VersionSettingsData { SyncModelProviders = true });
    settings.Save(second, new VersionSettingsData { SyncModelProviders = true });
    settings.Save(independent, new VersionSettingsData { SyncModelProviders = false });

    var models = new ModelService();
    models.SaveDeepSeekAsync(
        first,
        "FIRST_KEY",
        "https://first.example/v1",
        new[] { "first-model" }).GetAwaiter().GetResult();
    models.SaveOpenAiCompatibleAsync(
        second,
        "gateway",
        "SECOND_KEY",
        "https://second.example/v1",
        new[] { "second-model" }).GetAwaiter().GetResult();
    models.SaveDeepSeekAsync(
        independent,
        "INDEPENDENT_KEY",
        "https://independent.example/v1",
        new[] { "independent-model" }).GetAwaiter().GetResult();

    File.SetLastWriteTimeUtc(
        Path.Combine(first.DshHome, "settings.yaml"),
        DateTime.UtcNow.AddMinutes(-2));
    File.SetLastWriteTimeUtc(
        Path.Combine(second.DshHome, "settings.yaml"),
        DateTime.UtcNow.AddMinutes(-1));

    var states = new ProviderStateService();
    states.SetEnabled(second, "gateway", false);
    var sync = new ModelProviderSyncService(settings, models, states);
    var result = sync.Synchronize(first, new[] { first, second, independent });
    Assert(result.CopiedVersions == 1 && !result.HasErrors, "模型 Provider 应从最新停止版本同步到同策略版本。 ");

    var synchronized = models.Read(first);
    var gateway = synchronized.Single(provider => provider.Provider == "gateway");
    Assert(gateway.BaseUrl == "https://second.example/v1"
        && gateway.Models.SequenceEqual(new[] { "second-model" }), "同步后应保留最新 Provider 的地址和模型列表。 ");
    Assert(!synchronized.Any(provider => provider.Configured && provider.SettingsNamespace == "llm-deepseek"), "同步 Provider 不能保留源版本已经不存在的 DeepSeek 配置。 ");
    Assert(!states.IsEnabled(first, "gateway"), "同步后应复制 Provider 的禁用状态。 ");

    var independentProvider = models.Read(independent).Single(provider => provider.SettingsNamespace == "llm-deepseek");
    Assert(independentProvider.BaseUrl == "https://independent.example/v1"
        && independentProvider.Models.SequenceEqual(new[] { "independent-model" }), "关闭自动同步的版本不能被其它版本覆盖。 ");

    var runningSecond = second with
    {
        RuntimeStatus = InstanceRuntimeStatus.Running,
        RuntimeOwnership = InstanceRuntimeOwnership.Managed
    };
    var skipped = sync.Synchronize(first, new[] { first, runningSecond });
    Assert(skipped.SkippedRunningVersions == 1 && skipped.CopiedVersions == 0, "运行中的版本不能被 Provider 同步写入。 ");
    return Task.CompletedTask;
}

static Task TestConversationFileManagement()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "workspace");
    var home = Path.Combine(temporary.Path, "dsh-home");
    var importedHome = Path.Combine(temporary.Path, "imported-home");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(home);
    Directory.CreateDirectory(importedHome);
    var instance = CreateTestInstance("conversation-test", root, home);
    var importedInstance = CreateTestInstance("conversation-import", root, importedHome);

    var sourceDirectory = Path.Combine(home, "sessions", "--C-work-demo--", "session-1");
    Directory.CreateDirectory(sourceDirectory);
    var sourcePath = Path.Combine(sourceDirectory, "session.jsonl");
    File.WriteAllText(
        sourcePath,
        "{\"type\":\"session\",\"version\":0,\"id\":\"session-1\",\"createdAt\":1,\"cwd\":\"C:\\\\work\\\\demo\",\"delegationDepth\":0}\n{\"type\":\"message\"}\n",
        new UTF8Encoding(false));
    var compressedDirectory = Path.Combine(home, "sessions", "--C-work-demo--", "session-2");
    Directory.CreateDirectory(compressedDirectory);
    var compressedPath = Path.Combine(compressedDirectory, "session.jsonl.zstd");
    var compressedHeader = "{\"type\":\"session\",\"version\":0,\"id\":\"session-2\",\"createdAt\":1,\"cwd\":\"C:\\\\work\\\\demo\",\"delegationDepth\":0}\n";
    var compressedEvents = "{\"type\":\"message\"}\n";
    File.WriteAllBytes(compressedPath, CompressZstd(compressedHeader).Concat(CompressZstd(compressedEvents)).ToArray());
    var invalidCompressedPath = Path.Combine(home, "sessions", "--C-work-demo--", "session-3", "session.jsonl.zstd");
    Directory.CreateDirectory(Path.GetDirectoryName(invalidCompressedPath)!);
    File.WriteAllBytes(invalidCompressedPath, new byte[] { 1, 2, 3 });
    var malformedPath = Path.Combine(home, "sessions", "--C-work-demo--", "session-4", "session.jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(malformedPath)!);
    File.WriteAllText(malformedPath, "{\"type\":42,\"version\":0,\"id\":\"session-4\",\"createdAt\":1,\"delegationDepth\":0}\n", new UTF8Encoding(false));

    var service = new ConversationService(new LauncherPaths(Path.Combine(temporary.Path, "launcher")));
    var entries = service.List(instance);
    var session = entries.Single(entry => entry.FullPath == Path.GetFullPath(sourcePath));
    Assert(session.HasValidHeader && session.SessionId == "session-1", "应读取 JSONL 首行会话头部。");
    var projectionCacheDirectory = Path.Combine(home, "storages");
    Directory.CreateDirectory(projectionCacheDirectory);
    File.WriteAllText(
        Path.Combine(projectionCacheDirectory, "session_projcache.json"),
        "{\"tables\":{\"sessions\":{\"session-1\":{\"rows\":{\"title\":{\"val\":\"测试标题\"}}}}}}",
        new UTF8Encoding(false));
    var titled = service.List(instance).Single(entry => entry.FullPath == Path.GetFullPath(sourcePath));
    Assert(titled.DisplayName == "测试标题", "应优先显示 DSh session 投影缓存中的标题。");
    Assert(service.List(instance).Single(entry => entry.FullPath == Path.GetFullPath(compressedPath)).DisplayName.Contains("demo"), "无标题会话应回退到工作目录名称。");
    var compressedSession = entries.Single(entry => entry.FullPath == Path.GetFullPath(compressedPath));
    Assert(compressedSession.IsCompressed && compressedSession.HasValidHeader && compressedSession.SessionId == "session-2", "应解压压缩会话的首个 Zstandard frame 并读取会话头部。");
    Assert(entries.Any(entry => entry.IsCompressed && !entry.HasValidHeader && entry.FullPath == Path.GetFullPath(invalidCompressedPath)), "损坏的压缩会话应列出但标记为不可解析。");
    Assert(entries.Any(entry => !entry.IsCompressed && !entry.HasValidHeader && entry.FullPath == Path.GetFullPath(malformedPath)), "字段类型异常的会话应列出但不能让整个会话页刷新失败。");

    var backup = service.Backup(instance, session);
    Assert(File.Exists(backup), "备份对话必须生成独立文件。");
    var exportPath = Path.Combine(temporary.Path, "exported-session.jsonl");
    service.Export(instance, session, exportPath);
    Assert(File.Exists(exportPath), "导出对话必须生成用户指定的文件。");
    var importedPath = service.Import(importedInstance, exportPath);
    Assert(File.Exists(importedPath), "导入对话必须按 DSh projectKey 和 session ID 落位。");

    var compressedBackup = service.Backup(instance, compressedSession);
    Assert(File.Exists(compressedBackup) && compressedBackup.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase), "压缩会话备份必须保留原始格式。");
    var compressedExportPath = Path.Combine(temporary.Path, "exported-session.jsonl.zstd");
    service.Export(instance, compressedSession, compressedExportPath);
    Assert(File.Exists(compressedExportPath), "压缩会话导出必须生成用户指定的文件。");
    var importedCompressedPath = service.Import(importedInstance, compressedExportPath);
    Assert(importedCompressedPath.EndsWith("session.jsonl.zstd", StringComparison.OrdinalIgnoreCase), "压缩会话导入必须保留 session.jsonl.zstd 格式。");
    Assert(new ConversationService().List(importedInstance).Any(entry => entry.SessionId == "session-2" && entry.HasValidHeader && entry.IsCompressed), "导入后的压缩会话应可再次读取头部。");

    var duplicatedCompressedExportPath = Path.Combine(temporary.Path, "normalized-session.jsonl.zstd.jsonl.zstd");
    var normalizedCompressedExportPath = Path.Combine(temporary.Path, "normalized-session.jsonl.zstd");
    var normalizedResult = service.Export(instance, compressedSession, duplicatedCompressedExportPath);
    Assert(normalizedResult == Path.GetFullPath(normalizedCompressedExportPath) && File.Exists(normalizedCompressedExportPath), "导出压缩会话不能重复追加 .jsonl.zstd 后缀。");

    var outside = Path.Combine(temporary.Path, "outside.jsonl");
    File.WriteAllText(outside, "not a managed session\n", new UTF8Encoding(false));
    var forged = session with { FullPath = outside };
    AssertThrows<InvalidOperationException>(() => service.Delete(instance, forged), "会话操作不能通过 FullPath 逃出 sessions 根目录。");
    service.Delete(instance, session);
    Assert(!File.Exists(sourcePath), "删除操作应只删除明确选中的 session 文件。");

    var guarded = new ConversationService(isRunning: _ => true);
    AssertThrows<InvalidOperationException>(() => guarded.Export(importedInstance, entries[0], Path.Combine(temporary.Path, "blocked.jsonl")), "实例运行时不能导出可能正在写入的会话快照。");
    return Task.CompletedTask;
}

static Task TestConversationSynchronization()
{
    using var temporary = new TestDirectory();
    var runtime = Path.Combine(temporary.Path, "runtime");
    Directory.CreateDirectory(runtime);
    var registry = new InstanceRegistry(new LauncherPaths(Path.Combine(temporary.Path, "launcher")));
    var first = registry.Register("工作区 A", runtime, InstanceKind.Installed, detectedVersion: "test", packageManager: "npm");
    var second = registry.Register("工作区 B", runtime, InstanceKind.Installed, detectedVersion: "test", packageManager: "npm");
    var independent = registry.Register("独立版本", runtime, InstanceKind.Installed, detectedVersion: "test", packageManager: "npm");
    var settings = new VersionSettingsService();
    settings.Save(first, new VersionSettingsData
    {
        ConversationSyncMode = ConversationSyncMode.Workspace,
        ConversationWorkspace = "编程"
    });
    settings.Save(second, new VersionSettingsData
    {
        ConversationSyncMode = ConversationSyncMode.Workspace,
        ConversationWorkspace = "编程"
    });
    settings.Save(independent, new VersionSettingsData
    {
        ConversationSyncMode = ConversationSyncMode.Independent
    });

    var firstSession = Path.Combine(first.DshHome, "sessions", "--C-work--", "session-a", "session.jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(firstSession)!);
    File.WriteAllText(firstSession, "first workspace session", new UTF8Encoding(false));
    File.SetLastWriteTimeUtc(firstSession, new DateTime(2026, 8, 16, 10, 0, 0, DateTimeKind.Utc));

    var sync = new ConversationSyncService(settings);
    var workspaceResult = sync.Synchronize(first, new[] { first, second, independent });
    var secondSession = Path.Combine(second.DshHome, "sessions", "--C-work--", "session-a", "session.jsonl");
    Assert(workspaceResult.CopiedFiles == 1 && File.Exists(secondSession), "工作区同步应把会话文件复制到同工作区版本。");
    Assert(!File.Exists(Path.Combine(independent.DshHome, "sessions", "--C-work--", "session-a", "session.jsonl")), "独立版本不应收到工作区会话文件。");

    var newer = "newer workspace session";
    File.WriteAllText(firstSession, newer, new UTF8Encoding(false));
    File.SetLastWriteTimeUtc(firstSession, new DateTime(2026, 8, 16, 11, 0, 0, DateTimeKind.Utc));
    var runningSecond = second with { RuntimeStatus = InstanceRuntimeStatus.Running };
    var skipped = sync.Synchronize(first, new[] { first, runningSecond, independent });
    Assert(skipped.SkippedRunningVersions == 1, "运行中的工作区版本应被跳过，不能直接写入它的会话文件。");
    Assert(File.ReadAllText(secondSession) == "first workspace session", "运行中的版本会话文件不应被同步覆盖。");

    settings.Save(independent, new VersionSettingsData { ConversationSyncMode = ConversationSyncMode.All });
    var allResult = sync.Synchronize(independent, new[] { first, second, independent });
    var independentSession = Path.Combine(independent.DshHome, "sessions", "--C-work--", "session-a", "session.jsonl");
    Assert(allResult.CopiedFiles >= 2, "全量模式应把最新会话同步到其它停止版本。");
    Assert(File.ReadAllText(independentSession) == newer, "全量模式应选择停止版本中更新时间较新的会话文件。");
    Assert(File.ReadAllText(secondSession) == newer, "全量模式应覆盖停止的工作区版本。");

    var relativeSession = Path.Combine("--C-work--", "session-a", "session.jsonl");
    var deletion = sync.PropagateDeletion(independent, relativeSession, new[] { first, second, independent });
    Assert(!deletion.HasErrors, "同步删除停止版本中的会话不应产生错误。");
    Assert(!File.Exists(firstSession) && !File.Exists(secondSession) && !File.Exists(independentSession), "同步删除应清理所有关联停止版本的会话文件。");
    sync.SynchronizeAll(new[] { first, second, independent });
    Assert(!File.Exists(firstSession) && !File.Exists(secondSession) && !File.Exists(independentSession), "删除标记应阻止旧会话在下一次同步时复活。");

    File.WriteAllText(firstSession, "new session after deletion", new UTF8Encoding(false));
    File.SetLastWriteTimeUtc(firstSession, DateTime.UtcNow.AddSeconds(2));
    sync.Synchronize(first, new[] { first, second, independent });
    Assert(File.ReadAllText(independentSession) == "new session after deletion", "重新创建同一路径的新会话应清除旧删除标记并同步。");
    return Task.CompletedTask;
}

static byte[] CompressZstd(string text)
{
    using var compressor = new Compressor(3);
    return compressor.Wrap(Encoding.UTF8.GetBytes(text)).ToArray();
}

static ManagerInstance CreateTestInstance(string id, string root, string home) => new(
    Id: id,
    Name: id,
    RootPath: root,
    Kind: InstanceKind.Installed,
    DshHome: home,
    DshExecutablePath: null,
    DetectedVersion: "test",
    RuntimeStatus: InstanceRuntimeStatus.Ready,
    PackageManager: "npm",
    LastError: null,
    RegisteredAt: DateTimeOffset.UtcNow);

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class TestDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("dsh-launcher-self-test-");

    public string Path => _directory.FullName;

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        try
        {
            DeleteTree(Path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"INFO test cleanup left {Path}: {ex}");
        }
    }

    private static void DeleteTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }

                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteTree(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(path, recursive: false);
    }
}

file sealed class ProviderTestHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public ProviderTestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(_handler(request));
}

file sealed class NodeTestHandler : HttpMessageHandler
{
    private readonly Func<HttpResponseMessage> _factory;

    public NodeTestHandler(Func<HttpResponseMessage> factory)
    {
        _factory = factory;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(_factory());
}

file sealed class NodeProgressSink : IProgress<NodeDownloadProgress>
{
    public NodeDownloadProgress Last { get; private set; } = new(0, null, null);

    public void Report(NodeDownloadProgress value) => Last = value;
}

file sealed class SlowCancelStream : Stream
{
    private readonly byte[] _payload;
    private bool _started;

    public SlowCancelStream(byte[] payload)
    {
        _payload = payload;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            _started = true;
            var count = Math.Min(buffer.Length, _payload.Length);
            _payload.AsMemory(0, count).CopyTo(buffer);
            return count;
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
