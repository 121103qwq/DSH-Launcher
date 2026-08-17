using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed record DshSettingsValidationResult(
    bool WasChecked,
    bool IsValid,
    string? Error)
{
    public static DshSettingsValidationResult Valid() => new(true, true, null);

    public static DshSettingsValidationResult Invalid(string error) => new(true, false, error);

    public static DshSettingsValidationResult Unavailable(string error) => new(false, false, error);
}

/// <summary>
/// Validates settings.yaml with the same `yaml` package used by the selected DSh runtime.
/// The helper prints only parser codes and positions, never settings values.
/// </summary>
public sealed class DshSettingsYamlValidator
{
    private const long MaximumSettingsSize = 16 * 1024 * 1024;
    private const int ValidationTimeoutMilliseconds = 5000;
    private const string ValidationScript = """
        const fs = require('node:fs');
        const yaml = require(process.argv[1]);
        const text = fs.readFileSync(process.argv[2], 'utf8');
        const document = yaml.parseDocument(text, { prettyErrors: true });
        if (document.errors.length > 0) {
          const error = document.errors.map(item => {
            const at = item.linePos && item.linePos[0];
            return `${item.code || 'YAML_ERROR'}${at ? ` at line ${at.line}, column ${at.col}` : ''}`;
          }).join('; ');
          console.log(JSON.stringify({ valid: false, error }));
          process.exit(0);
        }
        const root = document.toJS() ?? {};
        if (typeof root !== 'object' || root === null || Array.isArray(root)) {
          console.log(JSON.stringify({ valid: false, error: 'ROOT_NOT_MAP' }));
          process.exit(0);
        }
        console.log(JSON.stringify({ valid: true }));
        """;

    public DshSettingsValidationResult Validate(
        ManagerInstance instance,
        NodeRuntimeInfo nodeRuntime,
        DshRuntimeInfo detectedDshRuntime)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var settingsPath = Path.Combine(instance.DshHome, "settings.yaml");
        if (!File.Exists(settingsPath))
        {
            return DshSettingsValidationResult.Valid();
        }

        if (!nodeRuntime.IsAvailable
            || string.IsNullOrWhiteSpace(nodeRuntime.ExecutablePath)
            || !File.Exists(nodeRuntime.ExecutablePath))
        {
            return DshSettingsValidationResult.Unavailable("Node.js 不可用，无法调用 DSh 的 YAML 解析器。 ");
        }

        try
        {
            if (new FileInfo(settingsPath).Length > MaximumSettingsSize)
            {
                return DshSettingsValidationResult.Invalid("settings.yaml 超过 16 MiB，已拒绝解析。 ");
            }

            var yamlEntry = FindYamlEntry(instance, detectedDshRuntime);
            if (yamlEntry is null)
            {
                return DshSettingsValidationResult.Unavailable("当前 DSh Runtime 缺少 YAML 解析器，无法校验 settings.yaml。 ");
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = nodeRuntime.ExecutablePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("-e");
            process.StartInfo.ArgumentList.Add(ValidationScript);
            process.StartInfo.ArgumentList.Add(yamlEntry);
            process.StartInfo.ArgumentList.Add(settingsPath);
            if (!process.Start())
            {
                return DshSettingsValidationResult.Unavailable("无法启动 DSh YAML 校验器。 ");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ValidationTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return DshSettingsValidationResult.Unavailable("DSh YAML 校验超时。 ");
            }

            var output = outputTask.GetAwaiter().GetResult().Trim();
            _ = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || output.Length == 0)
            {
                return DshSettingsValidationResult.Unavailable("DSh YAML 校验器运行失败。 ");
            }

            using var result = JsonDocument.Parse(output);
            var root = result.RootElement;
            var valid = root.TryGetProperty("valid", out var validValue) && validValue.GetBoolean();
            if (valid)
            {
                return DshSettingsValidationResult.Valid();
            }

            var parserError = root.TryGetProperty("error", out var errorValue)
                ? errorValue.GetString()
                : null;
            return DshSettingsValidationResult.Invalid(FormatParserError(parserError));
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or Win32Exception
            or JsonException)
        {
            return DshSettingsValidationResult.Unavailable("DSh YAML 校验器运行失败。 ");
        }
    }

    internal static string FormatParserError(string? error)
    {
        if (string.Equals(error, "ROOT_NOT_MAP", StringComparison.Ordinal))
        {
            return "settings.yaml 顶层必须是配置分区映射。 ";
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            return "settings.yaml 格式无效。 ";
        }

        return $"settings.yaml 格式无效：{error.Replace(" at line ", "，第 ", StringComparison.Ordinal).Replace(", column ", " 行第 ", StringComparison.Ordinal)} 列。 ";
    }

    private static string? FindYamlEntry(ManagerInstance instance, DshRuntimeInfo detectedDshRuntime)
    {
        var roots = new List<string>();
        var instancePackageRoot = DshRuntimeDetector.TryResolvePackageRoot(instance.RootPath);
        if (instancePackageRoot is not null)
        {
            roots.Add(instancePackageRoot);
        }
        else if (instance.Kind == InstanceKind.Source && Directory.Exists(instance.RootPath))
        {
            roots.Add(instance.RootPath);
        }

        if (!string.IsNullOrWhiteSpace(detectedDshRuntime.PackageRoot)
            && Directory.Exists(detectedDshRuntime.PackageRoot))
        {
            roots.Add(detectedDshRuntime.PackageRoot!);
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DirectoryInfo? current = new(root);
            for (var depth = 0; depth < 5 && current is not null; depth++, current = current.Parent)
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(current.FullName, "node_modules", "yaml", "dist", "index.js"),
                    Path.Combine(current.FullName, "node_modules", "yaml", "index.js")
                })
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }
}
