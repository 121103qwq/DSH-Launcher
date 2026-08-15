using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text;
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
    ("DSh install guard", TestDshInstallGuard),
    ("Source runner guard", TestSourceRunnerGuard),
    ("Source prepare install/build", TestSourcePrepareInstallAndBuild),
    ("Source runner lifecycle", TestSourceRunnerLifecycle),
    ("DSh early exit cleanup", TestDshEarlyExitCleanup),
    ("DSh instance lifecycle", TestDshInstanceLifecycle),
    ("Attached runtime lifecycle", TestAttachedRuntimeLifecycle),
    ("Extension ecosystem isolation", TestExtensionEcosystemIsolation),
    ("Plugin command supplies pnpm runtime", TestPluginCommandSuppliesPnpmRuntime),
    ("Marketplace discovery and verification", TestMarketplaceDiscoveryAndVerification),
    ("Version copy, clean version and package import", TestVersionPackageOperations),
    ("Model settings round-trip", TestModelSettingsRoundTrip),
    ("Provider state and diagnostics", TestProviderStateAndDiagnostics),
    ("Conversation file management", TestConversationFileManagement)
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

    var root = Path.Combine(temporary.Path, "workspace");
    var home = Path.Combine(temporary.Path, "dsh-home");
    Directory.CreateDirectory(Path.Combine(home, "profiles", "web"));
    File.WriteAllText(Path.Combine(home, "profiles", "web", "package.json"), "{}", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(home, "profiles", "web", "cordis.patch.yml"), "[]\n", new UTF8Encoding(false));
    var instance = CreateTestInstance("marketplace-test", root, home);
    var snapshot = service.CreatePluginSnapshot(instance);
    Assert(File.Exists(Path.Combine(snapshot, "package.json")) && File.Exists(Path.Combine(snapshot, "cordis.patch.yml")), "市场操作前应备份 web profile 配置。 ");

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

    var sourceSettings = Path.Combine(source.DshHome, "settings.yaml");
    File.WriteAllText(
        sourceSettings,
        "llm-deepseek:\n  apiKey: super-secret\n  apiKeyEnv: DEEPSEEK_API_KEY\n  baseURL: https://api.example\n  models:\n    - deepseek-chat\n",
        new UTF8Encoding(false));
    var sourceProfile = Path.Combine(source.DshHome, "profiles", "web");
    File.WriteAllText(
        Path.Combine(sourceProfile, "package.json"),
        "{\"dependencies\":{\"demo-plugin\":\"1.2.3\"},\"scripts\":{\"leak\":\"secret\"},\"dsh\":{\"profile\":{\"bundles\":[\"demo-plugin\"]}}}",
        new UTF8Encoding(false));
    var sessions = Path.Combine(source.DshHome, "sessions");
    Directory.CreateDirectory(sessions);
    File.WriteAllText(Path.Combine(sessions, "private.jsonl"), "do not export", new UTF8Encoding(false));
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
        using var manifestReader = new StreamReader(exported.GetEntry("manifest.json")!.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var manifestText = manifestReader.ReadToEnd();
        Assert(manifestText.Contains("\"sessions\": false", StringComparison.Ordinal)
            && manifestText.Contains("\"apiKeys\": false", StringComparison.Ordinal),
            "整合包 manifest 必须声明不包含会话和 API Key。 ");
    }

    var importedDesign = packages.ImportPackage(exportPath, source);
    Assert(!Directory.Exists(Path.Combine(importedDesign.DshHome, "sessions")),
        "导入分享整合包不能创建 sessions 目录。 ");
    var importedSettings = File.ReadAllText(Path.Combine(importedDesign.DshHome, "settings.yaml"));
    Assert(!importedSettings.Contains("super-secret", StringComparison.Ordinal),
        "导入整合包后也不能恢复被清理的 API Key。 ");
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
