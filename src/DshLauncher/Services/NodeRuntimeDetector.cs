using System.Diagnostics;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class NodeRuntimeDetector
{
    public NodeRuntimeInfo Detect()
    {
        foreach (var candidate in GetCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var version = ReadVersion(candidate);
            if (version is not null)
            {
                return new NodeRuntimeInfo(true, candidate, version, null);
            }
        }

        return NodeRuntimeInfo.Missing("PATH 和 Windows 常见安装目录中都没有可用的 node.exe。");
    }

    private static IEnumerable<string> GetCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(trimmed, "node.exe");
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        var commonDirectories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };

        foreach (var directory in commonDirectories.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            var candidate = Path.Combine(directory, "nodejs", "node.exe");
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string? ReadVersion(string executablePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            });

            if (process is null || !process.WaitForExit(2000))
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            return output.StartsWith('v') ? output[1..] : output;
        }
        catch
        {
            return null;
        }
    }
}
