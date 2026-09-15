using System.Diagnostics;
using DshLauncher.Services;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace DshLauncher.UnitTests;

public sealed class ProcessDshHomeReaderTests
{
    private readonly ITestOutputHelper _output;

    public ProcessDshHomeReaderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PerformanceSample()
    {
        if (!OperatingSystem.IsWindows()
            || !Environment.Is64BitOperatingSystem
            || !Environment.Is64BitProcess)
        {
            return;
        }

        var temporary = Path.Combine(Path.GetTempPath(), "dsh-reader-perf-" + Guid.NewGuid().ToString("N"));
        using var process = StartHelper(
            Path.Combine(temporary, "home"),
            Path.Combine(temporary, "profile"),
            Path.Combine(temporary, "local"));
        try
        {
            Assert.True(ReadUntilReadable(process).IsReadable);
            for (var warmup = 0; warmup < 5; warmup++)
            {
                Assert.True(ProcessDshHomeReader.Read(process).IsReadable);
            }

            const int samples = 40;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var sample = 0; sample < samples; sample++)
            {
                Assert.True(ProcessDshHomeReader.Read(process).IsReadable);
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            _output.WriteLine($"Reader: {elapsed.TotalMilliseconds / samples:F3} ms/call; {allocated / samples:N0} bytes/call ({samples} calls).");
        }
        finally
        {
            TryTerminate(process);
        }
    }

    [Fact]
    public void ReadsOnlySelectedPathVariablesFromA64BitHelper()
    {
        if (!OperatingSystem.IsWindows()
            || !Environment.Is64BitOperatingSystem
            || !Environment.Is64BitProcess)
        {
            return;
        }

        var temporary = Path.Combine(Path.GetTempPath(), "dsh-reader-" + Guid.NewGuid().ToString("N"));
        var dshHome = Path.Combine(temporary, "home");
        var userProfile = Path.Combine(temporary, "profile");
        var localAppData = Path.Combine(temporary, "local");
        using var process = StartHelper(dshHome, userProfile, localAppData);
        try
        {
            var result = ReadUntilReadable(process);

            Assert.True(result.IsReadable);
            Assert.Equal(dshHome, result.DshHome);
            Assert.Equal(userProfile, result.UserProfile);
            Assert.Equal(localAppData, result.LocalAppData);
            Assert.DoesNotContain("dsh-test-unrelated-value", result.ToString(), StringComparison.Ordinal);
            TryTerminate(process);
            Assert.False(ProcessDshHomeReader.Read(process).IsReadable);
        }
        finally
        {
            TryTerminate(process);
        }
    }

    [Fact]
    public void ReadsLargeEnvironmentAndHandlesEmptyAndNonAsciiPaths()
    {
        if (!OperatingSystem.IsWindows()
            || !Environment.Is64BitOperatingSystem
            || !Environment.Is64BitProcess)
        {
            return;
        }

        var temporary = Path.Combine(Path.GetTempPath(), "dsh-reader-large-" + Guid.NewGuid().ToString("N"));
        var userProfile = Path.Combine(temporary, "用户配置");
        var localAppData = Path.Combine(temporary, "本地应用");
        using var process = StartHelper(
            string.Empty,
            userProfile,
            localAppData,
            addLargeEnvironment: true);
        try
        {
            var result = ReadUntilReadable(process);

            Assert.True(result.IsReadable);
            Assert.Null(result.DshHome);
            Assert.Equal(userProfile, result.UserProfile);
            Assert.Equal(localAppData, result.LocalAppData);
        }
        finally
        {
            TryTerminate(process);
        }
    }

    [Fact]
    public void A32BitTargetIsReportedAsUnreadable()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
        {
            return;
        }

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        var helper = Path.Combine(systemDirectory, "cmd.exe");
        if (!File.Exists(helper))
        {
            return;
        }

        using var process = StartHelper(
            Path.Combine(Path.GetTempPath(), "dsh-reader-x86"),
            Path.Combine(Path.GetTempPath(), "profile-x86"),
            Path.Combine(Path.GetTempPath(), "local-x86"),
            helper);
        try
        {
            var result = ProcessDshHomeReader.Read(process);

            Assert.False(result.IsReadable);
            Assert.Null(result.DshHome);
            Assert.Null(result.UserProfile);
            Assert.Null(result.LocalAppData);
        }
        finally
        {
            TryTerminate(process);
        }
    }

    private static Process StartHelper(
        string dshHome,
        string userProfile,
        string localAppData,
        string? executable = null,
        bool addLargeEnvironment = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable ?? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("echo READY& ping -n 30 127.0.0.1 > nul");
        startInfo.Environment["DSH_HOME"] = dshHome;
        startInfo.Environment["USERPROFILE"] = userProfile;
        startInfo.Environment["LOCALAPPDATA"] = localAppData;
        startInfo.Environment["DSH_TEST_UNRELATED"] = "dsh-test-unrelated-value";
        if (addLargeEnvironment)
        {
            for (var index = 0; index < 24; index++)
            {
                startInfo.Environment[$"A_DSH_READER_PADDING_{index:D2}"] = new string('p', 256);
            }
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("测试辅助进程未启动。");
        try
        {
            if (!string.Equals(process.StandardOutput.ReadLine(), "READY", StringComparison.Ordinal))
            {
                TryTerminate(process);
                throw new XunitException("测试辅助进程未报告 READY。");
            }

            return process;
        }
        catch
        {
            TryTerminate(process);
            process.Dispose();
            throw;
        }
    }

    private static ProcessDshHomeEnvironment ReadUntilReadable(Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        var result = new ProcessDshHomeEnvironment(false, null, null, null);
        while (DateTime.UtcNow < deadline)
        {
            result = ProcessDshHomeReader.Read(process);
            if (result.IsReadable)
            {
                return result;
            }

            Thread.Sleep(25);
        }

        return result;
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
