using System.Diagnostics;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Termination;
using CuinProcessEase.TerminationTestApp;
using CuinProcessEase.Windows.Termination;
using Xunit;

namespace CuinProcessEase.Windows.Tests;

/// <summary>
/// 终止引擎集成测试：只结束测试自己启动的 TerminationTestApp 实例，
/// 绝不结束系统进程或用户现有程序。
/// 覆盖：优雅 WM_CLOSE / IgnoreClose 残留 / Force / 父子同组 / 旧 Request 不杀新实例 / 已退出安全处理。
/// </summary>
public sealed class ProcessTerminationServiceTests
{
    private static readonly ProcessTerminationService Service = new();

    private static readonly string TestAppExe = Path.Combine(
        Path.GetDirectoryName(typeof(Program).Assembly.Location)!,
        "CuinProcessEase.TerminationTestApp.exe");

    /// <summary>启动一个 TestTarget 并读取其 PID。</summary>
    private static (Process Proc, int Pid) StartTarget(
        string title, bool ignoreClose = false, bool noWindow = false)
    {
        var arguments = $"--window \"{title}\" --hidden";
        if (ignoreClose)
        {
            arguments += " --ignore-close";
        }

        if (noWindow)
        {
            arguments += " --no-window";
        }

        var psi = new ProcessStartInfo(TestAppExe)
        {
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        Process proc = Process.Start(psi)!;
        string? line = proc.StandardOutput.ReadLine();
        Assert.NotNull(line);
        Assert.StartsWith("PID=", line);
        int pid = int.Parse(line["PID=".Length..]);
        Thread.Sleep(600); // 等窗口/进程状态稳定
        return (proc, pid);
    }

    /// <summary>读取带 spawn-child 的父进程两行输出（CHILD + PID）。</summary>
    private static (Process Proc, int ParentPid, int ChildPid) StartParentWithChild(string title, string childTitle)
    {
        var psi = new ProcessStartInfo(TestAppExe)
        {
            Arguments = $"--window \"{title}\" --hidden --spawn-child \"{childTitle}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        Process proc = Process.Start(psi)!;
        string? childLine = proc.StandardOutput.ReadLine();
        string? pidLine = proc.StandardOutput.ReadLine();
        Assert.NotNull(childLine);
        Assert.NotNull(pidLine);
        Assert.StartsWith("CHILD=", childLine);
        Assert.StartsWith("PID=", pidLine);
        int childPid = int.Parse(childLine["CHILD=".Length..]);
        int parentPid = int.Parse(pidLine["PID=".Length..]);
        Thread.Sleep(600);
        return (proc, parentPid, childPid);
    }

    private static ProcessIdentity GetIdentity(int pid)
    {
        using var p = Process.GetProcessById(pid);
        return new ProcessIdentity(pid, p.StartTime.ToUniversalTime());
    }

    private static TerminationRequest BuildRequest(string displayName, params int[] pids) => new(
        displayName,
        pids.Select(GetIdentity).ToList(),
        DateTimeOffset.UtcNow);

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillQuietly(Process? proc)
    {
        try
        {
            if (proc is { HasExited: false })
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        catch
        {
            // 清理失败忽略（进程可能已退出）
        }
    }

    [Fact]
    public async Task 终止_优雅关闭_响应WM_CLOSE的目标正常退出()
    {
        var (proc, pid) = StartTarget("CuinT-Graceful");
        try
        {
            var request = BuildRequest("TerminationTestApp", pid);

            var result = await Service.CloseApplicationGracefullyAsync(request);

            Assert.Equal(TerminationStatus.Success, result.Status);
            var pr = Assert.Single(result.ProcessResults);
            Assert.Equal(ProcessTerminationStatus.ClosedGracefully, pr.Result);
            Assert.True(pr.ConfirmedExited);
            Assert.Equal(0, result.ResidualCount);
            Assert.True(proc.WaitForExit(5000));
        }
        finally
        {
            KillQuietly(proc);
        }
    }

    [Fact]
    public async Task 终止_优雅关闭_IgnoreClose残留_绝不自动升级强杀_随后Force可清除()
    {
        var (proc, pid) = StartTarget("CuinT-IgnoreClose", ignoreClose: true);
        try
        {
            var request = BuildRequest("TerminationTestApp", pid);

            ApplicationTerminationResult graceful = await Service.CloseApplicationGracefullyAsync(request);

            // 优雅阶段：目标吞掉 WM_CLOSE，引擎绝不升级 TerminateProcess
            Assert.NotEqual(TerminationStatus.Success, graceful.Status);
            Assert.Equal(1, graceful.ResidualCount);
            Assert.Contains(graceful.ProcessResults, r => r.Result is ProcessTerminationStatus.Residual or ProcessTerminationStatus.NoWindow);
            Assert.True(IsAlive(pid), "Graceful 阶段后目标必须仍然存活（绝不自动强杀）");

            // 用户选择强制结束（同一 Request 重放，全部重新走 Fresh 管线）
            var forced = await Service.ForceTerminateApplicationAsync(request);

            Assert.Equal(TerminationStatus.Success, forced.Status);
            Assert.Contains(forced.ProcessResults, r => r.Result == ProcessTerminationStatus.Terminated);
            Assert.True(proc.WaitForExit(5000));
        }
        finally
        {
            KillQuietly(proc);
        }
    }

    [Fact]
    public async Task 终止_优雅关闭_部分成员退出部分残留_PartialSuccess()
    {
        // 同 exe 路径的两个目标会被分组引擎聚成一组：一个响应 WM_CLOSE、一个忽略
        var (normal, normalPid) = StartTarget("CuinT-Part-Normal");
        var (stubborn, stubbornPid) = StartTarget("CuinT-Part-Stubborn", ignoreClose: true);
        try
        {
            var request = BuildRequest("TerminationTestApp", normalPid, stubbornPid);

            var result = await Service.CloseApplicationGracefullyAsync(request);

            Assert.Equal(TerminationStatus.PartialSuccess, result.Status);
            Assert.Equal(1, result.ResidualCount);
            Assert.Contains(result.ProcessResults, r => r.Pid == normalPid && r.Result == ProcessTerminationStatus.ClosedGracefully);
            Assert.Contains(result.ProcessResults, r => r.Pid == stubbornPid && !r.ConfirmedExited);
            Assert.True(IsAlive(stubbornPid));
            Assert.False(IsAlive(normalPid));
        }
        finally
        {
            KillQuietly(normal);
            KillQuietly(stubborn);
        }
    }

    [Fact]
    public async Task 终止_Force单目标_Terminated并有限等待确认退出()
    {
        var (proc, pid) = StartTarget("CuinT-Force");
        try
        {
            var request = BuildRequest("TerminationTestApp", pid);

            var result = await Service.ForceTerminateApplicationAsync(request);

            Assert.Equal(TerminationStatus.Success, result.Status);
            var pr = Assert.Single(result.ProcessResults);
            Assert.Equal(ProcessTerminationStatus.Terminated, pr.Result);
            Assert.True(proc.WaitForExit(5000));
        }
        finally
        {
            KillQuietly(proc);
        }
    }

    [Fact]
    public async Task 终止_Force父子同组_仅凭父身份_子进程被一并纳入并退出()
    {
        var (proc, parentPid, childPid) = StartParentWithChild("CuinT-Parent", "CuinT-Child");
        try
        {
            // 请求只含父进程身份：Fresh 分组按同 exe 路径聚组，child 作为组成员被纳入
            var request = BuildRequest("TerminationTestApp", parentPid);

            var result = await Service.ForceTerminateApplicationAsync(request);

            Assert.Equal(TerminationStatus.Success, result.Status);
            Assert.Equal(2, result.ProcessResults.Count);
            // Root 优先：父进程的结果在先
            Assert.Equal(parentPid, result.ProcessResults[0].Pid);
            Assert.All(result.ProcessResults, r => Assert.Equal(ProcessTerminationStatus.Terminated, r.Result));
            Assert.True(proc.WaitForExit(5000));
            Assert.False(IsAlive(childPid));
        }
        finally
        {
            KillQuietly(proc);
        }
    }

    [Fact]
    public async Task 终止_关键安全_旧Request绝不能结束同路径新实例()
    {
        // Phase 6 最重要的防误杀测试：
        // 结束 A 后启动 B（exe path / DisplayName 相同），重放旧 Request 必须拒绝结束 B
        var (procA, pidA) = StartTarget("CuinT-Old-A");
        var request = BuildRequest("TerminationTestApp", pidA);
        var service = new ProcessTerminationService();

        var first = await service.ForceTerminateApplicationAsync(request);
        Assert.Equal(TerminationStatus.Success, first.Status);
        Assert.True(procA.WaitForExit(5000));

        var (procB, pidB) = StartTarget("CuinT-Old-B");
        try
        {
            Assert.NotEqual(pidA, pidB);

            var replay = await service.ForceTerminateApplicationAsync(request);

            Assert.True(replay.Status is TerminationStatus.AlreadyExited or TerminationStatus.TargetChanged,
                $"旧 Request 重放应拒绝，实际 {replay.Status}");
            Assert.DoesNotContain(replay.ProcessResults, r => r.Pid == pidB && r.ConfirmedExited);
            Assert.True(IsAlive(pidB), "新实例绝不能被旧 Request 结束");
        }
        finally
        {
            KillQuietly(procB);
        }
    }

    [Fact]
    public async Task 终止_已退出目标_安全返回AlreadyExited()
    {
        var (proc, pid) = StartTarget("CuinT-Exited");
        var identity = GetIdentity(pid);
        proc.Kill(entireProcessTree: true);
        Assert.True(proc.WaitForExit(5000));

        var request = new TerminationRequest("TerminationTestApp", [identity], DateTimeOffset.UtcNow);
        var result = await Service.ForceTerminateApplicationAsync(request);

        Assert.Equal(TerminationStatus.AlreadyExited, result.Status);
        var pr = Assert.Single(result.ProcessResults);
        Assert.Equal(ProcessTerminationStatus.AlreadyExited, pr.Result);
    }

    [Fact]
    public async Task 终止_无窗口目标_优雅关闭报NoWindow_不误杀_Force可清除()
    {
        var (proc, pid) = StartTarget("CuinT-NoWindow", noWindow: true);
        try
        {
            var request = BuildRequest("TerminationTestApp", pid);

            var graceful = await Service.CloseApplicationGracefullyAsync(request);

            Assert.Equal(1, graceful.ResidualCount);
            Assert.Contains(graceful.ProcessResults, r => r.Result == ProcessTerminationStatus.NoWindow);
            Assert.True(IsAlive(pid));

            var forced = await Service.ForceTerminateApplicationAsync(request);

            Assert.Equal(TerminationStatus.Success, forced.Status);
            Assert.True(proc.WaitForExit(5000));
        }
        finally
        {
            KillQuietly(proc);
        }
    }

    [Fact]
    public async Task 终止_单进程Force_与组终止同一Fresh管线()
    {
        var (proc, pid) = StartTarget("CuinT-SingleForce");
        try
        {
            var request = BuildRequest("TerminationTestApp", pid);

            var result = await Service.ForceTerminateProcessAsync(request);

            Assert.Equal(TerminationStatus.Success, result.Status);
            Assert.Equal(ProcessTerminationStatus.Terminated, Assert.Single(result.ProcessResults).Result);
            Assert.True(proc.WaitForExit(5000));
        }
        finally
        {
            KillQuietly(proc);
        }
    }

    [Fact]
    public async Task 终止_无效请求_拒绝执行()
    {
        var empty = new TerminationRequest("Nothing", [], DateTimeOffset.UtcNow);

        var result = await Service.ForceTerminateApplicationAsync(empty);

        Assert.Equal(TerminationStatus.Failed, result.Status);
        Assert.Equal(TerminationFailureReason.UnreliableIdentity, result.FailureReason);
    }
}
