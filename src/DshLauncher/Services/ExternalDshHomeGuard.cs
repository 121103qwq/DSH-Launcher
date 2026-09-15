using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// A fresh, read-only preflight for linked HOME operations. Desktop processes
/// do not share our lock protocol, so this detects conflicts, not a global mutex.
/// </summary>
public sealed class ExternalDshHomeGuard
{
    private readonly Func<ManagerInstance, IReadOnlyList<ExternalHomeOwner>> _readOwners;

    public ExternalDshHomeGuard() : this(ReadDesktopOwners) { }

    internal ExternalDshHomeGuard(Func<ManagerInstance, IReadOnlyList<ExternalHomeOwner>> readOwners) =>
        _readOwners = readOwners;

    public void EnsureAvailable(ManagerInstance instance)
    {
        var conflict = GetConflict(instance);
        if (conflict is not null)
        {
            throw new InvalidOperationException(conflict);
        }
    }

    public string? GetConflict(ManagerInstance instance)
    {
        if (!instance.UsesExternalDshHome)
        {
            return null;
        }

        try
        {
            var owners = _readOwners(instance);
            var matched = owners.FirstOrDefault(owner => owner.DshHome is not null
                && SameHome(owner.DshHome, instance.DshHome));
            if (matched is not null)
            {
                return $"{matched.ProductName}（PID {matched.ProcessId}）正在使用此 DSH_HOME，已阻止重复启动或修改。请先退出该桌面端后重试；仍可浏览或解除关联。";
            }

            var unknown = owners.FirstOrDefault(owner => owner.DshHome is null);
            return unknown is null ? null
                : $"检测到 {unknown.ProductName}（PID {unknown.ProcessId}），但无法确认它使用的 DSH_HOME，暂不启动或修改外部目录。请先退出该桌面端后重试。";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return "无法完成桌面端占用检查，暂不启动或修改外部 DSH_HOME。请确认桌面端已退出后重试。";
        }
    }

    internal static bool SameHome(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<ExternalHomeOwner> ReadDesktopOwners(ManagerInstance? instance)
    {
        ProcessTreeSnapshot? processSnapshot = null;
        ProcessTreeSnapshot GetSnapshot() => processSnapshot ??=
            InstanceResourceMonitor.CaptureProcessSnapshot(CancellationToken.None);
        IReadOnlySet<int>? ignored = null;
        var seen = new HashSet<int>();
        var owners = new List<ExternalHomeOwner>();
        var installations = new Dictionary<string, DeepSeekDesktopInstallation?>(StringComparer.OrdinalIgnoreCase);
        // Keep the cheap no-desktop path. Only a detected desktop requires a
        // process-tree snapshot, shared by every owner within this one check.
        foreach (var name in new[] { "DSH Desktop", "DeepSeek Desktop" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    ignored ??= instance is null ? new HashSet<int>() : GetManagedProcessIds(instance, GetSnapshot);
                    if (ignored.Contains(process.Id) || !seen.Add(process.Id)) continue;
                    try
                    {
                        var installRoot = Path.GetDirectoryName(process.MainModule?.FileName);
                        if (installRoot is null) continue;
                        if (!installations.TryGetValue(installRoot, out var installation))
                        {
                            installation = DeepSeekDesktopDetector.TryDetect(installRoot);
                            installations[installRoot] = installation;
                        }
                        if (installation is null || process.HasExited) continue;

                        var isLegacy = installation.ProductName == "DeepSeek Desktop";
                        if (!isLegacy)
                        {
                            var environment = ProcessDshHomeReader.Read(process);
                            if (process.HasExited) continue;
                            owners.Add(new ExternalHomeOwner(process.Id, installation.ProductName, ResolveHome(environment)));
                        }
                        var foundLegacyWriter = false;
                        var snapshot = GetSnapshot();
                        foreach (var childId in snapshot.FindProcessTree(process.Id, CancellationToken.None))
                        {
                            if (ignored.Contains(childId) || !seen.Add(childId)) continue;
                            if (!snapshot.Names.TryGetValue(childId, out var childName)
                                || !string.Equals(childName, "node.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            try
                            {
                                using var child = Process.GetProcessById(childId);
                                if (!string.Equals(child.MainModule?.FileName, installation.NodeExecutablePath,
                                        StringComparison.OrdinalIgnoreCase)) continue;
                                var childEnvironment = ProcessDshHomeReader.Read(child);
                                if (!child.HasExited)
                                {
                                    foundLegacyWriter = true;
                                    owners.Add(new ExternalHomeOwner(child.Id, installation.ProductName, ResolveHome(childEnvironment)));
                                }
                            }
                            catch (ArgumentException) { /* Child exited during enumeration. */ }
                            catch (InvalidOperationException) { /* Child exited during enumeration. */ }
                            catch (Win32Exception)
                            {
                                owners.Add(new ExternalHomeOwner(childId, installation.ProductName, null));
                            }
                        }
                        if (isLegacy && !foundLegacyWriter)
                        {
                            // Only the actual Node writer proves a legacy
                            // shell's HOME; reading the shell environment is wasted work.
                            owners.Add(new ExternalHomeOwner(process.Id, installation.ProductName, null));
                        }
                    }
                    catch (InvalidOperationException) { /* Desktop exited during enumeration. */ }
                    catch (Win32Exception)
                    {
                        owners.Add(new ExternalHomeOwner(process.Id, name, null));
                    }
                }
            }
        }
        return owners;
    }

    internal static string? ResolveHome(ProcessDshHomeEnvironment environment)
    {
        if (!environment.IsReadable) return null;
        if (!string.IsNullOrWhiteSpace(environment.DshHome))
        {
            return Path.IsPathFullyQualified(environment.DshHome) ? Path.GetFullPath(environment.DshHome) : null;
        }
        var basePath = environment.UserProfile;
        return string.IsNullOrWhiteSpace(basePath) || !Path.IsPathFullyQualified(basePath) ? null
            : Path.Combine(basePath, ".dsh");
    }

    private static IReadOnlySet<int> GetManagedProcessIds(ManagerInstance instance, Func<ProcessTreeSnapshot> getSnapshot)
    {
        if (instance.RuntimeOwnership != InstanceRuntimeOwnership.Managed
            || instance.ProcessId is not > 0 || instance.ProcessStartedAt is null)
            return new HashSet<int>();
        try
        {
            using var process = Process.GetProcessById(instance.ProcessId.Value);
            if (Math.Abs((process.StartTime.ToUniversalTime() - instance.ProcessStartedAt.Value.UtcDateTime).TotalSeconds) > 2)
                return new HashSet<int>();
            return getSnapshot().FindProcessTree(process.Id, CancellationToken.None).ToHashSet();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return new HashSet<int>();
        }
    }
}

internal sealed record ExternalHomeOwner(int ProcessId, string ProductName, string? DshHome);
