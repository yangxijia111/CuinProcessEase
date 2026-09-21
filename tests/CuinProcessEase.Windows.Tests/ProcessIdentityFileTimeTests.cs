using System.Diagnostics;
using CuinProcessEase.Core.Termination;
using CuinProcessEase.TerminationTestApp;
using CuinProcessEase.Windows.Native;
using CuinProcessEase.Windows.Services;
using Xunit;

namespace CuinProcessEase.Windows.Tests;

/// <summary>
/// 真实系统身份一致性验证：快照引擎的 StartTime 获取路径与同 PID 句柄
/// GetProcessTimes CreationTime 必须在原始 FILETIME 上逐位一致——
/// 这是“无容差精确身份匹配”在真实系统上成立的前提。
/// </summary>
public sealed class ProcessIdentityFileTimeTests
{
    /// <summary>OpenProcess（最低权限）+ GetProcessTimes：返回 CreationTime 原始 FILETIME。</summary>
    private static long ReadHandleCreationFileTime(uint pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, bInheritHandle: false, pid);
        Assert.NotEqual(IntPtr.Zero, handle);
        try
        {
            Assert.True(NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _));
            long raw = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
            Assert.True(raw > 0);
            return raw;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    [Fact]
    public void 身份_testhost自身_ProcessStartTime与句柄CreationTime逐位一致()
    {
        // 快照引擎 StartTime 第一路径：System.Diagnostics.Process.StartTime
        using Process current = Process.GetCurrentProcess();
        long viaProcessStart = current.StartTime.ToUniversalTime().ToFileTimeUtc();

        Assert.Equal(viaProcessStart, ReadHandleCreationFileTime((uint)current.Id));
    }

    [Fact]
    public async Task 身份_真实TestTarget_快照引擎StartTime与句柄CreationTime逐位一致()
    {
        string testAppExe = Path.Combine(
            Path.GetDirectoryName(typeof(Program).Assembly.Location)!,
            "CuinProcessEase.TerminationTestApp.exe");
        using var target = Process.Start(new ProcessStartInfo(testAppExe)
        {
            Arguments = "--window CuinT-FileTime --hidden",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        try
        {
            Assert.NotNull(target.StandardOutput.ReadLine()); // PID=...

            // 与终止引擎相同的 Fresh Snapshot 路径采集目标进程
            var snapshotService = new ProcessSnapshotService();
            var snapshot = await snapshotService.CaptureAsync();
            var entry = snapshot.Processes.Single(p => p.ProcessId == target.Id);
            Assert.NotNull(entry.StartTimeUtc);

            long viaSnapshot = entry.StartTimeUtc.Value.ToFileTimeUtc();
            Assert.Equal(viaSnapshot, ReadHandleCreationFileTime((uint)target.Id));
        }
        finally
        {
            try
            {
                if (!target.HasExited)
                {
                    target.Kill(entireProcessTree: true);
                    target.WaitForExit(3000);
                }
            }
            catch
            {
                // 清理失败忽略（进程可能已退出）
            }
        }
    }
}
