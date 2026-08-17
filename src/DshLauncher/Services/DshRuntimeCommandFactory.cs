using System.Diagnostics;
using System.IO;
using System.Text;
using DshLauncher.Models;

namespace DshLauncher.Services;

internal static class DshRuntimeCommandFactory
{
    public static ProcessStartInfo Create(
        DshRuntimeLaunchSpec spec,
        IEnumerable<string> arguments,
        string workingDirectory,
        string? dshHome = null,
        string? dshAgentsHome = null,
        string? fallbackNodeExecutablePath = null,
        bool redirectOutput = true)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var normalizedArguments = arguments.ToArray();
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        switch (spec.Mode)
        {
            case DshRuntimeLaunchMode.DirectCommand:
                ConfigureDirect(startInfo, spec.HostPath, normalizedArguments);
                break;
            case DshRuntimeLaunchMode.NodeScript:
                startInfo.FileName = ResolveNodeHost(spec, fallbackNodeExecutablePath);
                startInfo.ArgumentList.Add(RequireFile(spec.EntryPointPath, "DSh Node.js 入口"));
                AddArguments(startInfo, normalizedArguments);
                break;
            case DshRuntimeLaunchMode.ElectronBootstrap:
                startInfo.FileName = RequireFile(spec.HostPath, "桌面应用宿主");
                startInfo.Environment["ELECTRON_RUN_AS_NODE"] = "1";
                startInfo.ArgumentList.Add(RequireFile(spec.EntryPointPath, "DSh Desktop CLI 入口"));
                AddArguments(startInfo, normalizedArguments);
                break;
            default:
                throw new InvalidOperationException($"不支持的 DSh 启动方式：{spec.Mode}。");
        }

        if (!string.IsNullOrWhiteSpace(dshHome))
        {
            startInfo.Environment["DSH_HOME"] = Path.GetFullPath(dshHome);
        }

        if (!string.IsNullOrWhiteSpace(dshAgentsHome))
        {
            startInfo.Environment["DSH_AGENTS_HOME"] = Path.GetFullPath(dshAgentsHome);
        }

        var preferredNode = spec.NodeExecutablePath ?? fallbackNodeExecutablePath;
        startInfo.Environment["PATH"] = RuntimeSearchPaths.BuildCurrentPath(preferredNode);
        return startInfo;
    }

    public static DshRuntimeLaunchSpec? Resolve(ManagerInstance instance) =>
        instance.EffectiveDshLaunchSpec;

    public static bool IsUsable(DshRuntimeLaunchSpec? spec)
    {
        if (spec is null || !File.Exists(spec.HostPath))
        {
            return false;
        }

        return spec.Mode == DshRuntimeLaunchMode.DirectCommand
            || (!string.IsNullOrWhiteSpace(spec.EntryPointPath) && File.Exists(spec.EntryPointPath));
    }

    private static void ConfigureDirect(
        ProcessStartInfo startInfo,
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        var executable = RequireFile(executablePath, "DSh 启动文件");
        if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var command = QuoteForCmd(executable);
            foreach (var argument in arguments)
            {
                command += " " + QuoteForCmd(argument);
            }

            startInfo.Arguments = $"/d /s /c \"{command}\"";
            return;
        }

        startInfo.FileName = executable;
        AddArguments(startInfo, arguments);
    }

    private static string ResolveNodeHost(
        DshRuntimeLaunchSpec spec,
        string? fallbackNodeExecutablePath)
    {
        var host = !string.IsNullOrWhiteSpace(spec.NodeExecutablePath)
            ? spec.NodeExecutablePath
            : !string.IsNullOrWhiteSpace(spec.HostPath)
                ? spec.HostPath
                : fallbackNodeExecutablePath;
        return RequireFile(host, "Node.js 启动文件");
    }

    private static void AddArguments(ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static string RequireFile(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException($"{label}不存在。", path);
        }

        return Path.GetFullPath(path);
    }

    private static string QuoteForCmd(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
