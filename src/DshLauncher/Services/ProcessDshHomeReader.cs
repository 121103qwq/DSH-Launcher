using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DshLauncher.Services;

internal sealed record ProcessDshHomeEnvironment(
    bool IsReadable,
    string? DshHome,
    string? UserProfile,
    string? LocalAppData);

/// <summary>
/// Reads only the small set of path variables needed to identify a Desktop
/// process. This intentionally does not expose the rest of the remote
/// environment or the process command line.
/// </summary>
internal static class ProcessDshHomeReader
{
    private const int ProcessBasicInformationClass = 0;
    private const int PebProcessParametersOffset = 0x20;
    private const int ProcessParametersEnvironmentOffset = 0x80;
    private const int EnvironmentChunkBytes = 4096;
    private const int MaximumEnvironmentBytes = 1024 * 1024;
    private const int MaximumEnvironmentValueCharacters = 32 * 1024;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const ushort ImageFileMachineUnknown = 0;
    private const ushort ImageFileMachineAmd64 = 0x8664;

    public static ProcessDshHomeEnvironment Read(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows()
            || !Environment.Is64BitProcess
            || !Environment.Is64BitOperatingSystem)
        {
            return Unreadable();
        }

        try
        {
            if (process.HasExited)
            {
                return Unreadable();
            }

            var processHandle = OpenProcess(
                ProcessQueryInformation | ProcessVmRead,
                inheritHandle: false,
                checked((uint)process.Id));
            if (processHandle == IntPtr.Zero)
            {
                return Unreadable();
            }

            try
            {
                if (!IsAmd64Process(processHandle)
                    || !TryReadProcessParameters(processHandle, out var processParameters)
                    || !TryAdd(processParameters, ProcessParametersEnvironmentOffset, out var environmentPointerAddress)
                    || !TryReadPointer(processHandle, environmentPointerAddress, out var environmentAddress)
                    || !TryReadEnvironment(processHandle, environmentAddress, out var result)
                    || process.HasExited)
                {
                    return Unreadable();
                }

                return result;
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or NotSupportedException
                                   or UnauthorizedAccessException
                                   or IOException
                                   or OverflowException)
        {
            return Unreadable();
        }
    }

    private static bool IsAmd64Process(IntPtr processHandle)
    {
        try
        {
            return IsWow64Process2(processHandle, out var processMachine, out var nativeMachine)
                && nativeMachine == ImageFileMachineAmd64
                && processMachine == ImageFileMachineUnknown;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryReadProcessParameters(
        IntPtr processHandle,
        out IntPtr processParameters)
    {
        processParameters = IntPtr.Zero;
        var information = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            processHandle,
            ProcessBasicInformationClass,
            ref information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        if (status != 0
            || information.PebBaseAddress == IntPtr.Zero
            || !TryAdd(information.PebBaseAddress, PebProcessParametersOffset, out var address))
        {
            return false;
        }

        return TryReadPointer(processHandle, address, out processParameters);
    }

    private static bool TryReadPointer(
        IntPtr processHandle,
        IntPtr address,
        out IntPtr value)
    {
        value = IntPtr.Zero;
        var bytes = new byte[IntPtr.Size];
        if (!ReadProcessMemory(
                processHandle,
                address,
                bytes,
                (UIntPtr)bytes.Length,
                out var bytesRead)
            || bytesRead.ToUInt64() != (ulong)bytes.Length)
        {
            return false;
        }

        var raw = BitConverter.ToUInt64(bytes, 0);
        if (raw == 0 || raw > long.MaxValue)
        {
            return false;
        }

        value = new IntPtr((long)raw);
        return true;
    }

    private static bool TryReadEnvironment(
        IntPtr processHandle,
        IntPtr environmentAddress,
        out ProcessDshHomeEnvironment result)
    {
        result = Unreadable();
        var chunk = new byte[EnvironmentChunkBytes];
        var environment = new byte[EnvironmentChunkBytes];
        var totalBytes = 0;
        var scanStart = 0;

        while (totalBytes < MaximumEnvironmentBytes)
        {
            var requested = Math.Min(EnvironmentChunkBytes, MaximumEnvironmentBytes - totalBytes);
            if (!TryAdd(environmentAddress, totalBytes, out var address)
                || !TryLimitToPage(address, ref requested)
                || requested == 0
                || !ReadProcessMemory(
                    processHandle,
                    address,
                    chunk,
                    (UIntPtr)requested,
                    out var bytesRead))
            {
                return false;
            }

            var read = checked((int)bytesRead.ToUInt64());
            if (read <= 0 || read > requested || (read & 1) != 0)
            {
                return false;
            }

            if (!TryEnsureCapacity(ref environment, totalBytes + read))
            {
                return false;
            }

            Buffer.BlockCopy(chunk, 0, environment, totalBytes, read);
            totalBytes += read;
            var terminator = FindDoubleNul(environment, scanStart, totalBytes);
            if (terminator >= 0)
            {
                return TryParseEnvironment(
                    environment.AsSpan(0, terminator + 4),
                    out result);
            }

            // A UTF-16 NUL is two bytes; retain the last code unit when the
            // next chunk starts so a terminator spanning chunks is found.
            scanStart = Math.Max(0, totalBytes - 2);
        }

        return false;
    }

    private static bool TryLimitToPage(IntPtr address, ref int requested)
    {
        const int pageSize = 4096;
        var raw = address.ToInt64();
        if (raw <= 0)
        {
            return false;
        }

        var pageOffset = (int)(raw & (pageSize - 1));
        requested = Math.Min(requested, pageSize - pageOffset);
        requested &= ~1;
        return requested > 0;
    }

    private static bool TryEnsureCapacity(ref byte[] buffer, int required)
    {
        if (required <= buffer.Length)
        {
            return true;
        }

        if (required > MaximumEnvironmentBytes)
        {
            return false;
        }

        var size = buffer.Length;
        while (size < required)
        {
            var next = Math.Min(MaximumEnvironmentBytes, checked(size * 2));
            if (next <= size)
            {
                return false;
            }

            size = next;
        }

        Array.Resize(ref buffer, size);
        return true;
    }

    private static int FindDoubleNul(byte[] buffer, int start, int end)
    {
        var first = Math.Max(0, start);
        if ((first & 1) != 0)
        {
            first--;
        }

        for (var offset = first; offset + 3 < end; offset += 2)
        {
            if (buffer[offset] == 0
                && buffer[offset + 1] == 0
                && buffer[offset + 2] == 0
                && buffer[offset + 3] == 0)
            {
                return offset;
            }
        }

        return -1;
    }

    private static bool TryParseEnvironment(
        ReadOnlySpan<byte> environment,
        out ProcessDshHomeEnvironment result)
    {
        string? dshHome = null;
        string? userProfile = null;
        string? localAppData = null;
        var entries = MemoryMarshal.Cast<byte, char>(environment);
        var offset = 0;

        while (offset < entries.Length)
        {
            var length = entries[offset..].IndexOf('\0');
            if (length < 0)
            {
                result = Unreadable();
                return false;
            }

            var entry = entries.Slice(offset, length);
            if (entry.IsEmpty)
            {
                result = new(true, dshHome, userProfile, localAppData);
                return true;
            }

            var separator = entry.IndexOf('=');
            if (separator > 0)
            {
                var value = entry[(separator + 1)..];
                if (value.Length <= MaximumEnvironmentValueCharacters)
                {
                    if (entry[..separator].Equals("DSH_HOME", StringComparison.OrdinalIgnoreCase))
                    {
                        dshHome ??= NormalizeValue(value.ToString());
                    }
                    else if (entry[..separator].Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase))
                    {
                        userProfile ??= NormalizeValue(value.ToString());
                    }
                    else if (entry[..separator].Equals("LOCALAPPDATA", StringComparison.OrdinalIgnoreCase))
                    {
                        localAppData ??= NormalizeValue(value.ToString());
                    }
                }
            }

            offset += length + 1;
        }

        result = Unreadable();
        return false;
    }

    private static string? NormalizeValue(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static ProcessDshHomeEnvironment Unreadable() =>
        new(false, null, null, null);

    private static bool TryAdd(IntPtr address, long offset, out IntPtr result)
    {
        result = IntPtr.Zero;
        var raw = address.ToInt64();
        if (raw <= 0 || offset < 0)
        {
            return false;
        }

        var sum = checked(raw + offset);
        if (sum <= 0)
        {
            return false;
        }

        result = new IntPtr(sum);
        return true;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        IntPtr processHandle,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        UIntPtr size,
        out UIntPtr numberOfBytesRead);

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
}
