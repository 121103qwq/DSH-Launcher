using System.Text;
using DshLauncher.Models;
using DshLauncher.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Instance registry round-trip", TestInstanceRegistryRoundTrip),
    ("Instance registry rejects duplicate roots", TestInstanceRegistryRejectsDuplicate),
    ("Instance registry rejects missing executable state", TestInstanceRegistryRejectsMissingExecutableState),
    ("Source project inspection", TestSourceProjectInspection),
    ("Source inspector rejects unrelated workspace", TestSourceInspectorRejectsUnrelatedWorkspace),
    ("DSh runtime detection", TestDshRuntimeDetection)
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
        if (_directory.Exists)
        {
            _directory.Delete(recursive: true);
        }
    }
}
