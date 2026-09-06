using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace DshLauncher.Watchdog;

/// <summary>
/// 进程查询原语（Watchdog 专用，独立于 Launcher 进程上下文）。
///
/// 性能与可靠性原则（实战教训）：
///  - 探测循环主路径（每 3s）**只走 Toolhelp32 快照**（CreateToolhelp32Snapshot，
///    毫秒级、不受 WMI 服务状态影响），绝不碰 WMI；
///  - WMI 仅用于按需获取**单进程命令行**（WHERE ProcessId=N，Task.Run + 超时，
///    失败返回 null）和清理场景的一次性全量查询（带超时）；
///  - 端口反查走 GetExtendedTcpTable（P/Invoke），端口字节序用无符号位运算转换
///    （(short) 强转会让 >32767 的高位端口溢出为负 → 反查永远失配）。
/// </summary>
public sealed class ProcessSnapshot
{
    public required int ProcessId { get; init; }
    public required int ParentProcessId { get; init; }
    public required string Name { get; init; } = string.Empty;
    public string CommandLine { get; set; } = string.Empty;

    public string CommandLineUpper => CommandLine.ToUpperInvariant();
}

public static class ProcessQuery
{
    private const int TcpTableOwnerPidAll = 5;
    private const uint MibTcpStateListen = 2;

    private static readonly ConcurrentDictionary<int, string> CommandLineCache = new();
    private static readonly object Gate = new();
    private static DateTimeOffset _snapshotAt;
    private static List<ProcessSnapshot> _snapshot = new();

    // ---------- Toolhelp32 快照（探测主路径，无 WMI） ----------

    /// <summary>
    /// 进程快照（PID/父 PID/进程名；≤3s 缓存）。命令行不在快照内——需要时按 PID
    /// 单独查询（TryGetCommandLine）。绝不因 WMI 挂起阻塞探测循环。
    /// </summary>
    public static IReadOnlyList<ProcessSnapshot> GetSnapshot()
    {
        lock (Gate)
        {
            if ((DateTimeOffset.UtcNow - _snapshotAt).TotalSeconds < 3)
            {
                return _snapshot;
            }

            _snapshot = QuerySnapshotCore();
            _snapshotAt = DateTimeOffset.UtcNow;
            return _snapshot;
        }
    }

    private static List<ProcessSnapshot> QuerySnapshotCore()
    {
        var result = new List<ProcessSnapshot>();
        var snapshotHandle = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshotHandle == IntPtr.Zero || snapshotHandle == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshotHandle, ref entry))
            {
                return result;
            }

            do
            {
                result.Add(new ProcessSnapshot
                {
                    ProcessId = (int)entry.th32ProcessID,
                    ParentProcessId = (int)entry.th32ParentProcessID,
                    Name = entry.szExeFile
                });
            } while (Process32Next(snapshotHandle, ref entry));
        }
        finally
        {
            CloseHandle(snapshotHandle);
        }

        return result;
    }

    /// <summary>按 PID 单进程查命令行（PEB 内存读取，非 WMI：无服务依赖、毫秒级、不挂）。</summary>
    public static string? TryGetCommandLine(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        if (CommandLineCache.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        try
        {
            var commandLine = ReadCommandLineFromPeb(processId);
            if (!string.IsNullOrWhiteSpace(commandLine))
            {
                CommandLineCache[processId] = commandLine;
            }

            return commandLine;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 读进程 PEB 的 RTL_USER_PROCESS_PARAMETERS.CommandLine（x64 布局）。
    /// OpenProcess + NtQueryInformationProcess(ProcessBasicInformation) →
    /// PebBaseAddress → PEB.ProcessParameters(0x20) → CommandLine（0x70 首选，0x68 兜底）。
    /// 权限不足/进程退出返回 null。
    /// </summary>
    private static string? ReadCommandLineFromPeb(int processId)
    {
        var handle = OpenProcess(
            ProcessQueryInformation | ProcessVmRead,
            false,
            processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var basicInfo = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(
                handle,
                ProcessBasicInformationClass,
                ref basicInfo,
                (uint)Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            if (status != 0 || basicInfo.PebBaseAddress == IntPtr.Zero)
            {
                return null;
            }

            // PEB.ProcessParameters @ 0x20（x64）
            if (!TryReadPointer(handle, basicInfo.PebBaseAddress + 0x20, out var parameters)
                || parameters == IntPtr.Zero)
            {
                return null;
            }

            // RTL_USER_PROCESS_PARAMETERS.CommandLine：主流 x64 布局 @ 0x70（部分实现 @ 0x68）
            foreach (var offset in new[] { 0x70, 0x68 })
            {
                if (!TryReadUnicodeString(handle, parameters + offset, out var text)
                    || string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                return text;
            }
        }
        finally
        {
            CloseHandle(handle);
        }

        return null;
    }

    private static bool TryReadPointer(IntPtr handle, IntPtr source, out IntPtr value)
    {
        value = IntPtr.Zero;
        var bytes = new byte[IntPtr.Size];
        if (!ReadProcessMemory(handle, source, bytes, (nuint)bytes.Length, out _))
        {
            return false;
        }

        value = IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(bytes, 0))
            : new IntPtr(BitConverter.ToInt32(bytes, 0));
        return true;
    }

    private static bool TryReadUnicodeString(IntPtr handle, IntPtr unicodeStringAddress, out string text)
    {
        text = string.Empty;
        // UNICODE_STRING：USHORT Length + USHORT MaximumLength + PWSTR Buffer（x64 16 字节）
        var header = new byte[IntPtr.Size == 8 ? 16 : 8];
        if (!ReadProcessMemory(handle, unicodeStringAddress, header, (nuint)header.Length, out _))
        {
            return false;
        }

        var length = BitConverter.ToUInt16(header, 0);
        var bufferAddress = IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(header, 8))
            : new IntPtr(BitConverter.ToInt32(header, 4));
        if (length == 0 || bufferAddress == IntPtr.Zero || length > 32_768)
        {
            return false;
        }

        var buffer = new byte[length];
        if (!ReadProcessMemory(handle, bufferAddress, buffer, (nuint)length, out _))
        {
            return false;
        }

        text = System.Text.Encoding.Unicode.GetString(buffer);
        return true;
    }

    /// <summary>进程是否还活着（Process.HasExited）。</summary>
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

    // ---------- 端口反查（GetExtendedTcpTable） ----------

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

                var address = new IPAddress(new[]
                {
                    (byte)(row.LocalAddress & 0xFF),
                    (byte)((row.LocalAddress >> 8) & 0xFF),
                    (byte)((row.LocalAddress >> 16) & 0xFF),
                    (byte)((row.LocalAddress >> 24) & 0xFF)
                });
                if (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.Any))
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
                // MIB 表内端口为网络字节序。不能 (short) 强转：端口 >32767
                // （launcher 随机分配的高位端口全在此范围）会溢出为负数。
                var port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                list.Add(new TcpRow(row.State, row.LocalAddr, port, (int)row.OwningPid));
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

    // ---------- 树 / 清理 ----------

    /// <summary>pid 的整棵后代树（按 Toolhelp32 快照 ParentProcessId 构建）。</summary>
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

    /// <summary>无条件杀一个进程的整棵树（taskkill /T /F，Windows 自带、幂等）。</summary>
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
    /// （node/cmd/powershell/electron 等）。一次性 WMI 查询（清理场景专用，带超时），
    /// 失败返回空数组（宁可漏清也不阻塞/误杀）。
    /// </summary>
    public static IReadOnlyList<ProcessSnapshot> FindProcessesForDshHome(
        string dshHome,
        IReadOnlyCollection<int>? keepAlivePids = null,
        IReadOnlyCollection<int>? excludePids = null)
    {
        var normalized = System.IO.Path.GetFullPath(dshHome).TrimEnd('\\', '/').ToUpperInvariant();
        var keep = keepAlivePids?.ToHashSet() ?? new HashSet<int>();
        var exclude = excludePids?.ToHashSet() ?? new HashSet<int>();
        exclude.Add(Environment.ProcessId);

        try
        {
            var task = Task.Run(() => QueryCommandLineAll());
            if (!task.Wait(TimeSpan.FromSeconds(8)))
            {
                return Array.Empty<ProcessSnapshot>();
            }

            return task.Result
                .Where(process => !keep.Contains(process.ProcessId)
                                  && !exclude.Contains(process.ProcessId)
                                  && process.CommandLineUpper.Contains(normalized, StringComparison.Ordinal))
                .Where(process => process.Name.Equals("node", StringComparison.OrdinalIgnoreCase)
                                  || process.Name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                                  || process.Name.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                                  || process.Name.Equals("electron", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch
        {
            return Array.Empty<ProcessSnapshot>();
        }
    }

    private static IReadOnlyList<ProcessSnapshot> QueryCommandLineAll()
    {
        // 清理场景的一次性枚举：Toolhelp32 全量 + 逐个 PEB 命令行（无 WMI 依赖）。
        var result = new List<ProcessSnapshot>();
        foreach (var process in GetSnapshot())
        {
            result.Add(new ProcessSnapshot
            {
                ProcessId = process.ProcessId,
                ParentProcessId = process.ParentProcessId,
                Name = process.Name,
                CommandLine = TryGetCommandLine(process.ProcessId) ?? string.Empty
            });
        }

        return result;
    }

    /// <summary>进程名去扩展名比较（Toolhelp32 的 szExeFile 带 .exe，如 "node.exe"）。</summary>
    public static bool IsProcessName(ProcessSnapshot process, string baseName) =>
        string.Equals(
            System.IO.Path.GetFileNameWithoutExtension(process.Name),
            baseName,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>候选进程是否像"实例主进程"（node + bin.js + web + 该端口）。
    /// market 自重启的进程命令行**不含 DSH_HOME**（它只继承环境变量），因此按端口+特征识别。</summary>
    public static bool LooksLikeDshWebMain(ProcessSnapshot process, int port)
    {
        if (!IsProcessName(process, "node"))
        {
            return false;
        }

        var commandLine = process.CommandLine;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            commandLine = TryGetCommandLine(process.ProcessId) ?? string.Empty;
        }

        var upper = commandLine.ToUpperInvariant();
        return upper.Contains("BIN.JS")
               && upper.Contains("WEB")
               && upper.Contains($"--PORT {port}");
    }

    // ---------- Toolhelp32 P/Invoke ----------

    private const uint Th32csSnapProcess = 0x00000002;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ---------- PEB 命令行读取 P/Invoke（x64 布局，无 WMI 依赖） ----------

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const int ProcessBasicInformationClass = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        uint processInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);
}
