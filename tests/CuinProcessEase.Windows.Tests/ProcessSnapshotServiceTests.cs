using System.Diagnostics;
using System.Runtime.InteropServices;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Windows.Services;
using Xunit;

namespace CuinProcessEase.Windows.Tests;

/// <summary>
/// ProcessSnapshotService 集成测试：在真实 Windows 环境上验证 Phase 1 验收项。
/// </summary>
public sealed class ProcessSnapshotServiceTests
{
    private const string CmdPath = @"C:\Windows\System32\cmd.exe";

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static Task<ProcessSnapshotCollection> CaptureAsync()
        => new ProcessSnapshotService().CaptureAsync();

    [Fact]
    public async Task 快照不为空_且包含当前进程自身()
    {
        ProcessSnapshotCollection snapshot = await CaptureAsync();

        Assert.NotEmpty(snapshot.Processes);
        Assert.Contains(snapshot.Processes, p => p.ProcessId == Environment.ProcessId);
        Assert.All(snapshot.Processes, p => Assert.False(string.IsNullOrWhiteSpace(p.Name), "Name 绝不允许为空"));
    }

    [Fact]
    public async Task 当前进程的基础字段可读取()
    {
        ProcessSnapshotCollection snapshot = await CaptureAsync();

        ProcessSnapshot self = snapshot.Processes.Single(p => p.ProcessId == Environment.ProcessId);

        // 测试进程自身信息全部可读
        Assert.NotNull(self.ParentProcessId);
        Assert.NotNull(self.ExecutablePath);
        Assert.NotNull(self.StartTimeUtc);
        Assert.NotNull(self.SessionId);
        Assert.NotNull(self.UserName);
        Assert.NotNull(self.WorkingSetBytes);
        Assert.NotNull(self.PrivateMemoryBytes);
        Assert.Equal(ProcessArchitecture.X64, self.Architecture); // 本解决方案固定 x64
        Assert.Equal(Environment.ProcessId, self.Identity.ProcessId);
    }

    [Fact]
    public async Task PPID来自Toolhelp_系统空闲进程PPID为零()
    {
        ProcessSnapshotCollection snapshot = await CaptureAsync();

        // PID 0（System Idle Process）在任何 Windows 上都存在，其 PPID 固定为 0
        ProcessSnapshot idle = snapshot.Processes.Single(p => p.ProcessId == 0);
        Assert.Equal(0, idle.ParentProcessId);
    }

    [Fact]
    public async Task 同名进程的多个实例都能读取()
    {
        ProcessSnapshotCollection snapshot = await CaptureAsync();

        // 任何 Windows 系统都有多个 svchost.exe 实例（等价于 chrome.exe 多实例场景）
        var svchosts = snapshot.Processes.Where(p => p.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(svchosts.Count > 1, "系统应存在多个 svchost.exe 实例");
        Assert.Equal(svchosts.Count, svchosts.Select(p => p.ProcessId).Distinct().Count());
    }

    [Fact]
    public async Task 进程退出后_下一次快照不再包含该PID()
    {
        // 启动一个存活约 30 秒的探测进程，验证出现→退出→消失的完整生命周期
        using Process probe = Process.Start(new ProcessStartInfo(CmdPath, "/c timeout /t 30 /nobreak")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        int probePid = probe.Id;

        ProcessSnapshotCollection first = await CaptureAsync();
        Assert.Contains(first.Processes, p => p.ProcessId == probePid);

        try
        {
            probe.Kill();
        }
        catch (InvalidOperationException)
        {
            // 进程恰好在 Kill 前自行退出，不影响断言
        }

        probe.WaitForExit();

        ProcessSnapshotCollection second = await CaptureAsync();
        Assert.DoesNotContain(second.Processes, p => p.ProcessId == probePid);
    }

    [Fact]
    public async Task 连续多次捕获_全部成功且字段稳定()
    {
        ProcessSnapshotCollection first = await CaptureAsync();

        for (int i = 0; i < 4; i++)
        {
            ProcessSnapshotCollection next = await CaptureAsync();
            Assert.NotEmpty(next.Processes);
        }

        // 存活进程的两次快照中，PID + StartTime 身份保持一致（防 PID 重用的基础）
        ProcessSnapshotCollection last = await CaptureAsync();
        ProcessSnapshot selfInFirst = first.Processes.Single(p => p.ProcessId == Environment.ProcessId);
        ProcessSnapshot selfInLast = last.Processes.Single(p => p.ProcessId == Environment.ProcessId);
        Assert.Equal(selfInFirst.Identity, selfInLast.Identity);
    }

    [Fact]
    public async Task 扫描期间进程快速启动退出_不导致任何异常()
    {
        // 并发压力：一个任务连续启停短命进程，另一任务连续扫描
        Task churn = Task.Run(async () =>
        {
            for (int i = 0; i < 25; i++)
            {
                try
                {
                    using Process? shortLived = Process.Start(new ProcessStartInfo(CmdPath, "/c exit")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    await Task.Delay(20);
                }
                catch
                {
                    // 启动失败（资源紧张等）不影响本测试目的
                }
            }
        });

        Exception? scanError = null;
        try
        {
            for (int i = 0; i < 8; i++)
            {
                _ = await CaptureAsync();
                await Task.Delay(30);
            }
        }
        catch (Exception ex)
        {
            scanError = ex;
        }

        await churn;
        Assert.Null(scanError);
    }

    [Fact]
    public async Task 快照字段值合法()
    {
        ProcessSnapshotCollection snapshot = await CaptureAsync();

        Assert.All(snapshot.Processes, p =>
        {
            Assert.True(p.ProcessId >= 0);
            if (p.ParentProcessId.HasValue)
            {
                Assert.True(p.ParentProcessId.Value >= 0);
            }
            if (p.WorkingSetBytes.HasValue)
            {
                // 工作集可能为 0（如刚被清空工作集的进程），但不得为负
                Assert.True(p.WorkingSetBytes.Value >= 0);
            }
            Assert.True(Enum.IsDefined(p.Architecture));
        });
    }

    [Fact]
    public void GetProcessTimes备用路径_返回与NET一致的创建时间()
    {
        // 直接验证 StartTime 第二获取路径：用低权限句柄调用 internal 的
        // ReadCreationTimeUtc（GetProcessTimes），结果应与 .NET Process.StartTime 一致
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)Environment.ProcessId);
        Assert.NotEqual(IntPtr.Zero, handle);

        try
        {
            DateTime? creationTimeUtc = ProcessSnapshotService.ReadCreationTimeUtc(handle);

            Assert.NotNull(creationTimeUtc);

            DateTime netStartTimeUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            Assert.True(
                Math.Abs((creationTimeUtc.Value - netStartTimeUtc).TotalSeconds) < 2,
                $"GetProcessTimes={creationTimeUtc:O} 与 .NET StartTime={netStartTimeUtc:O} 不一致");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [Fact]
    public void GetProcessTimes备用路径_无效句柄返回null不抛异常()
    {
        // 非法句柄（0）必须安全返回 null，不得抛出异常影响扫描
        Assert.Null(ProcessSnapshotService.ReadCreationTimeUtc(IntPtr.Zero));
    }

    [Fact]
    public async Task 并发调用CaptureAsync_全部成功且结果一致()
    {
        // 同时发起 3 路捕获（模拟自动刷新重叠触发的服务端场景），
        // 每一路都应独立成功完成
        Task<ProcessSnapshotCollection>[] captures =
            [CaptureAsync(), CaptureAsync(), CaptureAsync()];

        ProcessSnapshotCollection[] results = await Task.WhenAll(captures);

        Assert.All(results, r =>
        {
            Assert.NotEmpty(r.Processes);
            Assert.Contains(r.Processes, p => p.ProcessId == Environment.ProcessId);
        });
    }
}
