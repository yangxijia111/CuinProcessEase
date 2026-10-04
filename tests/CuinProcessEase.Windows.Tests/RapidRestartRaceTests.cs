using System.Diagnostics;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Termination;
using CuinProcessEase.TerminationTestApp;
using CuinProcessEase.Windows.Termination;
using Xunit;

namespace CuinProcessEase.Windows.Tests;

/// <summary>
/// Phase 10 Race Condition：快速"启动 → 结束 → 再启动"循环下的 PID 复用 / 扫描中退出 /
/// 终止中退出竞态。每轮都走完整单进程 Force 管线（Fresh 快照 → exact 定位 → Preflight →
/// Terminate → Final Rescan），绝不允许误杀、异常或虚报成功。
/// </summary>
[Collection("TerminationIntegration")]
public sealed class RapidRestartRaceTests
{
    private static readonly ProcessTerminationService Service = new();

    private static readonly string TestAppExe = Path.Combine(
        Path.GetDirectoryName(typeof(Program).Assembly.Location)!,
        "CuinProcessEase.TerminationTestApp.exe");

    private static (Process Proc, int Pid) StartTarget(string title)
    {
        var psi = new ProcessStartInfo(TestAppExe)
        {
            Arguments = $"--window \"{title}\" --hidden --no-window",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        Process proc = Process.Start(psi)!;
        string? line = proc.StandardOutput.ReadLine();
        Assert.NotNull(line);
        return (proc, int.Parse(line!["PID=".Length..]));
    }

    private static ProcessIdentity GetIdentity(int pid)
    {
        using Process probe = Process.GetProcessById(pid);
        return new ProcessIdentity(pid, probe.StartTime.ToUniversalTime());
    }

    [Fact]
    public async Task 快速启停循环_五轮全部安全终结_绝不误杀或异常()
    {
        for (int round = 1; round <= 5; round++)
        {
            (Process proc, int pid) = StartTarget($"CuinT-Race-{round}");
            Thread.Sleep(300); // 让进程稳定可见

            ProcessIdentity identity = GetIdentity(pid);
            var request = new TerminationRequest(
                $"Race-{round}", [identity], DateTimeOffset.UtcNow);

            // 单进程 Force 全管线：Fresh 定位 → Preflight（exact FILETIME）→ Terminate → Final Rescan
            ApplicationTerminationResult result = await Service.ForceTerminateProcessAsync(request);

            // 快速启停下前一轮 PID 可能已被复用到本轮——exact identity 保证要么成功要么明确拒绝
            Assert.True(
                result.Status is TerminationStatus.Success or TerminationStatus.AlreadyExited,
                $"第 {round} 轮结果异常：{result.Status} - {result.Message}");

            Assert.True(proc.WaitForExit(5000), $"第 {round} 轮目标未退出");
            proc.Dispose();
        }
    }

    [Fact]
    public async Task 目标在快照与终止间隙退出_结果如实报告_绝不虚报成功()
    {
        // 构造竞态：拿到 identity 后立刻让目标自行退出，再发起终止
        (Process proc, int pid) = StartTarget("CuinT-Race-Exit");
        Thread.Sleep(300);
        ProcessIdentity identity = GetIdentity(pid);

        // 在请求构造后、引擎快照前杀掉目标（模拟"扫描间隙退出"）
        if (!proc.HasExited)
        {
            proc.Kill();
        }

        var request = new TerminationRequest("Race-Exit", [identity], DateTimeOffset.UtcNow);
        ApplicationTerminationResult result = await Service.ForceTerminateProcessAsync(request);

        // 目标已死：Fresh 定位失败 → AlreadyExited（PID 未复用）或 IdentityMismatch（恰好复用）；
        // 两者都是安全终态，绝不出现 Success（虚报）或异常
        Assert.True(
            result.Status is TerminationStatus.AlreadyExited or TerminationStatus.TargetChanged,
            $"竞态退出场景结果异常：{result.Status} - {result.Message}");
        proc.Dispose();
    }
}
