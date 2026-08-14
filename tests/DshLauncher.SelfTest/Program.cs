using System.Text;
using DshLauncher.Models;
using DshLauncher.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Instance registry round-trip", TestInstanceRegistryRoundTrip),
    ("Instance registry rejects duplicate roots", TestInstanceRegistryRejectsDuplicate),
    ("Instance registry rejects missing executable state", TestInstanceRegistryRejectsMissingExecutableState),
    ("Instance registry rejects unsafe homes and corrupt records", TestInstanceRegistryRejectsUnsafeData),
    ("Source project inspection", TestSourceProjectInspection),
    ("Source inspector rejects unrelated workspace", TestSourceInspectorRejectsUnrelatedWorkspace),
    ("DSh runtime detection", TestDshRuntimeDetection),
    ("DSh install guard", TestDshInstallGuard),
    ("Source runner guard", TestSourceRunnerGuard),
    ("Source prepare install/build", TestSourcePrepareInstallAndBuild),
    ("Source runner lifecycle", TestSourceRunnerLifecycle),
    ("DSh early exit cleanup", TestDshEarlyExitCleanup),
    ("DSh instance lifecycle", TestDshInstanceLifecycle),
    ("Extension ecosystem isolation", TestExtensionEcosystemIsolation),
    ("Model settings round-trip", TestModelSettingsRoundTrip),
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

static Task TestInstanceRegistryRejectsDuplicate()
{
    using var temporary = new TestDirectory();
    var root = Path.Combine(temporary.Path, "shared");
    Directory.CreateDirectory(root);
    var registry = new InstanceRegistry(new LauncherPaths(Path.Combine(temporary.Path, "launcher")));
    registry.Register("一个实例", root, InstanceKind.Source, packageManager: "pnpm");

    var rejected = false;
    try
    {
        registry.Register("另一个实例", root, InstanceKind.Source, packageManager: "pnpm");
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }

    Assert(rejected, "相同根目录不能重复注册。");
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
        "{\"name\":\"deepseek-harness\",\"version\":\"0.1.0\",\"packageManager\":\"pnpm@10.0.0\",\"scripts\":{\"build\":\"pnpm run build\"}}",
        new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages: []", new UTF8Encoding(false));

    var inspector = new SourceProjectInspector();
    var beforeDependencies = inspector.Inspect(root);
    Assert(beforeDependencies.IsValid, "带 package.json 的 Source 项目应可解析。");
    Assert(beforeDependencies.IsDshSource, "DeepSeek Harness 根项目应被识别为 DSh Source。");
    Assert(beforeDependencies.PackageManager == "pnpm", "应读取 packageManager 中的 pnpm。");
    Assert(beforeDependencies.PackageManagerVersion == "10.0.0", "应读取包管理器版本。");
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
}

static async Task TestDshInstallGuard()
{
    var result = await new DshInstallService().InstallAsync(NodeRuntimeInfo.Missing("测试中模拟 Node.js 缺失"));
    Assert(!result.IsSuccess, "缺少 Node.js 时不应执行 npm 安装。");
    Assert(result.Error?.Contains("Node.js", StringComparison.OrdinalIgnoreCase) == true,
        "缺少 Node.js 时应返回明确错误。");
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
        "{\"type\":\"session\",\"version\":1,\"id\":\"session-1\",\"createdAt\":1,\"cwd\":\"C:\\\\work\\\\demo\"}\n{\"type\":\"message\"}\n",
        new UTF8Encoding(false));
    var compressedDirectory = Path.Combine(home, "sessions", "--C-work-demo--", "session-2");
    Directory.CreateDirectory(compressedDirectory);
    var compressedPath = Path.Combine(compressedDirectory, "session.jsonl.zstd");
    File.WriteAllBytes(compressedPath, new byte[] { 1, 2, 3 });

    var service = new ConversationService(new LauncherPaths(Path.Combine(temporary.Path, "launcher")));
    var entries = service.List(instance);
    var session = entries.Single(entry => entry.FullPath == Path.GetFullPath(sourcePath));
    Assert(session.HasValidHeader && session.SessionId == "session-1", "应读取 JSONL 首行会话头部。");
    Assert(entries.Any(entry => entry.IsCompressed && !entry.HasValidHeader), "压缩会话应列出但标记为未解析头部。");

    var backup = service.Backup(instance, session);
    Assert(File.Exists(backup), "备份对话必须生成独立文件。");
    var exportPath = Path.Combine(temporary.Path, "exported-session.jsonl");
    service.Export(instance, session, exportPath);
    Assert(File.Exists(exportPath), "导出对话必须生成用户指定的文件。");
    var importedPath = service.Import(importedInstance, exportPath);
    Assert(File.Exists(importedPath), "导入对话必须按 DSh projectKey 和 session ID 落位。");

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
