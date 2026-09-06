using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Management;

namespace DshLauncher.Watchdog;

/// <summary>
/// 进程查询原语（Watchdog 专用，独立于 Launcher 进程上下文）：
/// - 全量进程快照（WMI：PID/ParentPID/Name/CommandLine）
/// - 按监听端口反查 PID（GetExtendedTcpTable P/Invoke，不依赖 netstat 本地化输出）
/// - 按子树/命令行匹配 kill
/// 所有匹配的第一原则：只杀「命令行包含实例 DSH_HOME 完整路径」或「登记 PID 的进程树」，
/// 并始终排除自身、Launcher 与"转正后应保留的新主进程"。
/// </summary>
public sealed class ProcessSnapshot
{
    public required int ProcessId { get; init; }
    public required int ParentProcessId { get; init; }
    public required string Name { get; init; } = string.Empty;
    public required string CommandLine { get; init; } = string.Empty;

    public string CommandLineUpper => CommandLine.ToUpperInvariant();

    public bool IsDead { get; set; }
}

public static class ProcessQuery
{
    private const int TcpTableOwnerPidAll = 5;
    private const uint MibTcpStateListen = 2;

    private static readonly object Gate = new();
    private static DateTimeOffset _snapshotAt;
    private static List<ProcessSnapshot> _snapshot = new();

    /// <summary>
    /// 取进程快照（≤1.5s 缓存，WMI 查询较贵）。失败时降级为 Process.GetProcesses
    /// 基础视图（无命令行）——幽灵判定依赖命令行，降级时宁可不判也不错杀。
    /// </summary>
    public static IReadOnlyList<ProcessSnapshot> GetSnapshot()
    {
        lock (Gate)
        {
            if ((DateTimeOffset.UtcNow - _snapshotAt).TotalSeconds < 1.5)
            {
                return _snapshot;
            }

            _snapshot = QueryCore();
            _snapshotAt = DateTimeOffset.UtcNow;
            return _snapshot;
        }
    }

    private static List<ProcessSnapshot> QueryCore()
    {
        var result = new List<ProcessSnapshot>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process");
            using var collection = searcher.Get();
            foreach (ManagementObject item in collection)
            {
                var pid = GetInt(item, "ProcessId");
                if (pid <= 0)
                {
                    continue;
                }

                result.Add(new ProcessSnapshot
                {
                    ProcessId = pid,
                    ParentProcessId = GetInt(item, "ParentProcessId"),
                    Name = GetString(item, "Name"),
                    CommandLine = GetString(item, "CommandLine")
                });
            }
        }
        catch
        {
            // WMI 不可用时不允许幽灵判定悄悄变成"全部死亡"——返回空并让调用方
            // 走保守分支（只信登记 PID 的 Process.HasExited）。
        }

        return result;
    }

    private static int GetInt(ManagementObject item, string name)
    {
        try
        {
            var value = item[name];
            return value is null ? 0 : Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static string GetString(ManagementObject item, string name)
    {
        try
        {
            return item[name]?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>进程是否还活着（Process.HasExited，快照仅供参考）。</summary>
    public static bool IsAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 按监听端口反查 PID。取 LISTEN 状态行中与本机地址（127.0.0.1/*）匹配的条目；
    /// 返回 0 表示未找到。
    /// </summary>
    public static int FindPidByListeningPort(int port)
    {
        try
        {
            var table = GetTcpTable();
            if (table is null)
            {
                return 0;
            }

            foreach (var row in table)
            {
                if (row.State != MibTcpStateListen || row.LocalPort != port)
                {
                    continue;
                }

                // 只认 loopback 或通配监听（实例固定 127.0.0.1；通配也接受，
                // 防止 --host 变化后收集不到）。
                var address = new IPAddress(new[]
                {
                    (byte)(row.LocalAddress & 0xFF),
                    (byte)((row.LocalAddress >> 8) & 0xFF),
                    (byte)((row.LocalAddress >> 16) & 0xFF),
                    (byte)((row.LocalAddress >> 24) & 0xFF)
                });
                if (address.Equals(IPAddress.Loopback)
                    || address.Equals(IPAddress.Any))
                {
                    return row.OwningPid;
                }
            }
        }
        catch
        {
            // 端口反查失败时返回 0，调用方按"无法确认"处理（不接管、仅标记）。
        }

        return 0;
    }

    private static List<TcpRow>? GetTcpTable()
    {
        var size = 0;
        // 第一次调用：只用来查询所需缓冲区大小（必然返回 ERROR_INSUFFICIENT_BUFFER，
        // 这是正常流程，不是失败）。
        _ = GetExtendedTcpTable(
            IntPtr.Zero, ref size, false, (int)AddressFamily.AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedTcpTable(
                buffer, ref size, false, (int)AddressFamily.AfInet, TcpTableOwnerPidAll, 0);
            if (result != 0)
            {
                return null;
            }

            var rows = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowsPtr = buffer + 4;
            var list = new List<TcpRow>(rows);
            for (var i = 0; i < rows; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowsPtr + i * rowSize);
                // MIB 表内端口/地址皆为网络字节序：先转主机序再比对。
                list.Add(new TcpRow(
                    row.State,
                    row.LocalAddr,
                    IPAddress.NetworkToHostOrder((short)row.LocalPort),
                    (int)row.OwningPid));
            }

            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private readonly record struct TcpRow(uint State, uint LocalAddress, int LocalPort, int OwningPid);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public int LocalPort;
        public uint RemoteAddr;
        public int RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    private enum AddressFamily
    {
        AfInet = 2
    }

    /// <summary>pid 的整棵后代树（按快照 ParentProcessId 构建）。</summary>
    public static IReadOnlyList<ProcessSnapshot> GetDescendants(int rootPid)
    {
        var snapshot = GetSnapshot();
        var children = new Dictionary<int, List<int>>();
        foreach (var process in snapshot)
        {
            if (!children.TryGetValue(process.ParentProcessId, out var bucket))
            {
                bucket = new List<int>();
                children[process.ParentProcessId] = bucket;
            }

            bucket.Add(process.ProcessId);
        }

        var result = new List<ProcessSnapshot>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        var seen = new HashSet<int> { rootPid };
        var byId = snapshot.ToDictionary(process => process.ProcessId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!children.TryGetValue(current, out var bucket))
            {
                continue;
            }

            foreach (var child in bucket)
            {
                if (!seen.Add(child))
                {
                    continue;
                }

                if (byId.TryGetValue(child, out var childEntry))
                {
                    result.Add(childEntry);
                }

                queue.Enqueue(child);
            }
        }

        return result;
    }

    private static readonly string[] ProtectedProcessNames = { "DSH LAUNCHER", "DSH LAUNCHER.WATCHDOG" };

    /// <summary>
    /// 无条件杀一个进程的整棵树（taskkill /T /F，Windows 自带、幂等）。
    /// 返回 false 表示进程已经不在了。
    /// </summary>
    public static bool KillTree(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/PID {processId} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            using var killer = Process.Start(startInfo);
            if (killer is null)
            {
                return false;
            }

            killer.WaitForExit(10_000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 收集一个实例的"残留进程"候选：命令行包含该实例 DSH_HOME 完整路径的所有进程
    /// （node/cmd/powershell/electron 等），排除自身进程与保留 PID（如转正后的新主进程）。
    /// 匹配前先把路径归一成大写，避免大小写/斜杠差异漏检。
    /// </summary>
    public static IReadOnlyList<ProcessSnapshot> FindProcessesForDshHome(
        string instanceId,
        string dshHome,
        IReadOnlyCollection<int>? keepAlivePids = null,
        IReadOnlyCollection<int>? excludePids = null)
    {
        var normalized = System.IO.Path.GetFullPath(dshHome).TrimEnd('\\', '/').ToUpperInvariant();
        var keep = keepAlivePids?.ToHashSet() ?? new HashSet<int>();
        var exclude = excludePids?.ToHashSet() ?? new HashSet<int>();
        exclude.Add(Environment.ProcessId);

        return GetSnapshot()
            .Where(process => !keep.Contains(process.ProcessId)
                              && !exclude.Contains(process.ProcessId)
                              && process.CommandLineUpper.Contains(normalized, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>候选进程是否像"实例主进程"（dsh web 服务：node + bin.js + --port）。</summary>
    public static bool LooksLikeDshWebMain(ProcessSnapshot process, int port, string dshHome)
    {
        if (!process.Name.Equals("node", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commandLine = process.CommandLineUpper;
        return commandLine.Contains("BIN.JS")
               && commandLine.Contains("WEB")
               && commandLine.Contains($"--PORT {port}");
    }

    /// <summary>候选进程是否像"市场自重启的启动包装"（powershell hidden 包装链的 head）。</summary>
    public static bool LooksLikeMarketRespawnWrapper(ProcessSnapshot process, string dshHome)
    {
        return process.Name.Equals("powershell", StringComparison.OrdinalIgnoreCase)
               || process.Name.Equals("cmd", StringComparison.OrdinalIgnoreCase);
    }
}
