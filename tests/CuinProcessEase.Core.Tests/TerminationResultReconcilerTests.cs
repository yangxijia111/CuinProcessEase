using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Termination;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// TerminationResultReconciler 归并器纯逻辑测试：
/// 历史失败被后续成功覆盖、Final Rescan 存活事实修正、最终状态汇总。
/// 这些安全规则全部脱离 Windows 集成环境即可验证。
/// </summary>
public sealed class TerminationResultReconcilerTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ProcessIdentity Id(int pid, int offsetSeconds = 0)
        => new(pid, BaseTime.AddSeconds(offsetSeconds));

    private static ProcessTerminationResult Attempt(
        ProcessIdentity identity, ProcessTerminationStatus status, string? message = null) => new()
    {
        Pid = identity.ProcessId,
        ExpectedIdentity = identity,
        ProcessName = "app.exe",
        Result = status,
        Message = message ?? status.ToString(),
    };

    // ================= LatestResultByIdentity =================

    [Theory]
    [InlineData(ProcessTerminationStatus.Failed)]
    [InlineData(ProcessTerminationStatus.TimedOut)]
    [InlineData(ProcessTerminationStatus.Residual)]
    [InlineData(ProcessTerminationStatus.AccessDenied)]
    public void 归并_历史失败被后续Terminated覆盖_最终状态为Terminated(ProcessTerminationStatus oldStatus)
    {
        var identity = Id(100);
        IReadOnlyList<ProcessTerminationResult> latest = TerminationResultReconciler.LatestResultByIdentity(
            [Attempt(identity, oldStatus), Attempt(identity, ProcessTerminationStatus.Terminated)]);

        var result = Assert.Single(latest);
        Assert.Equal(ProcessTerminationStatus.Terminated, result.Result);
        Assert.True(result.ConfirmedExited);
    }

    [Fact]
    public void 归并_不同身份互不覆盖_各自保留最后一次()
    {
        var a = Id(100);
        var b = Id(200);

        IReadOnlyList<ProcessTerminationResult> latest = TerminationResultReconciler.LatestResultByIdentity(
        [
            Attempt(a, ProcessTerminationStatus.TimedOut),
            Attempt(b, ProcessTerminationStatus.AccessDenied),
            Attempt(a, ProcessTerminationStatus.Terminated),
        ]);

        Assert.Equal(2, latest.Count);
        Assert.Contains(latest, r => r.ExpectedIdentity == a && r.Result == ProcessTerminationStatus.Terminated);
        Assert.Contains(latest, r => r.ExpectedIdentity == b && r.Result == ProcessTerminationStatus.AccessDenied);
    }

    // ================= ApplyFinalRescan =================

    private static IReadOnlySet<ProcessIdentity> SetOf(params ProcessIdentity[] identities)
        => new HashSet<ProcessIdentity>(identities);

    [Fact]
    public void 归并_Terminated且FinalRescan已消失_保持Terminated()
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [Attempt(identity, ProcessTerminationStatus.Terminated)], SetOf());

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.Terminated, result.Result);
        Assert.True(result.ConfirmedExited);
    }

    [Fact]
    public void 归并_Terminated但FinalRescan身份仍存在_修正为Residual()
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [Attempt(identity, ProcessTerminationStatus.Terminated)], SetOf(identity));

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.Residual, result.Result);
        Assert.False(result.ConfirmedExited);
        Assert.Contains("仍存在", result.Message);
    }

    [Fact]
    public void 归并_TimedOut且FinalRescan已消失_修正为Terminated_不产生假residual()
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [Attempt(identity, ProcessTerminationStatus.TimedOut)], SetOf());

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.Terminated, result.Result);
        Assert.True(result.ConfirmedExited);
    }

    [Theory]
    [InlineData(ProcessTerminationStatus.AccessDenied)]
    [InlineData(ProcessTerminationStatus.Failed)]
    [InlineData(ProcessTerminationStatus.Residual)]
    [InlineData(ProcessTerminationStatus.NoWindow)]
    public void 归并_未确认失败但FinalRescan已消失_修正为AlreadyExited不计residual(ProcessTerminationStatus status)
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [Attempt(identity, status)], SetOf());

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.AlreadyExited, result.Result);
        Assert.True(result.ConfirmedExited);
    }

    [Fact]
    public void 归并_已确认退出且FinalRescan已消失_保持不变()
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [Attempt(identity, ProcessTerminationStatus.AlreadyExited)], SetOf());

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.AlreadyExited, result.Result);
        Assert.Equal("AlreadyExited", result.Message);
    }

    [Fact]
    public void 归并_同PID不同StartTime_仅匹配的exact身份被修正()
    {
        var original = Id(100, offsetSeconds: 0);
        var reused = Id(100, offsetSeconds: 30); // 同 PID、不同 StartTime（复用后实例）

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
        [
            Attempt(original, ProcessTerminationStatus.Terminated),
            Attempt(reused, ProcessTerminationStatus.TimedOut),
        ], SetOf(reused));

        Assert.Equal(2, final.Count);
        Assert.Contains(final, r => r.ExpectedIdentity == original
            && r.Result == ProcessTerminationStatus.Terminated);
        Assert.Contains(final, r => r.ExpectedIdentity == reused
            && r.Result == ProcessTerminationStatus.Residual);
    }

    // ================= Summarize =================

    [Fact]
    public void 汇总_全部确认退出_Success()
    {
        var (status, reason) = TerminationResultReconciler.Summarize(
        [
            Attempt(Id(100), ProcessTerminationStatus.Terminated),
            Attempt(Id(200), ProcessTerminationStatus.AlreadyExited),
        ]);

        Assert.Equal(TerminationStatus.Success, status);
        Assert.Equal(TerminationFailureReason.None, reason);
    }

    [Fact]
    public void 汇总_全部执行前已退出_AlreadyExited()
    {
        var (status, _) = TerminationResultReconciler.Summarize(
        [
            Attempt(Id(100), ProcessTerminationStatus.AlreadyExited),
            Attempt(Id(200), ProcessTerminationStatus.AlreadyExited),
        ]);

        Assert.Equal(TerminationStatus.AlreadyExited, status);
    }

    [Fact]
    public void 汇总_部分退出部分残留_PartialSuccess且原因为残留()
    {
        var (status, reason) = TerminationResultReconciler.Summarize(
        [
            Attempt(Id(100), ProcessTerminationStatus.Terminated),
            Attempt(Id(200), ProcessTerminationStatus.Residual),
        ]);

        Assert.Equal(TerminationStatus.PartialSuccess, status);
        Assert.Equal(TerminationFailureReason.ResidualRemain, reason);
    }

    [Fact]
    public void 汇总_无一退出且无残留状态_Failed执行错误()
    {
        var (status, reason) = TerminationResultReconciler.Summarize(
        [
            Attempt(Id(100), ProcessTerminationStatus.AccessDenied),
            Attempt(Id(200), ProcessTerminationStatus.Failed),
        ]);

        Assert.Equal(TerminationStatus.Failed, status);
        Assert.Equal(TerminationFailureReason.ExecutionError, reason);
    }

    [Fact]
    public void 汇总_结果为空_Failed()
    {
        var (status, _) = TerminationResultReconciler.Summarize([]);

        Assert.Equal(TerminationStatus.Failed, status);
    }

    // ================= 端到端组合：TimedOut → 下一轮 Terminated =================

    [Fact]
    public void 归并组合_TimedOut后重试Terminated加FinalRescan消失_最终Success无假residual()
    {
        var identity = Id(100);
        IReadOnlyList<ProcessTerminationResult> latest = TerminationResultReconciler.LatestResultByIdentity(
        [
            Attempt(identity, ProcessTerminationStatus.TimedOut),   // 第 0 轮
            Attempt(identity, ProcessTerminationStatus.Terminated), // 残留清理轮
        ]);
        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            latest, SetOf());

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.Terminated, result.Result);
        Assert.Equal(TerminationStatus.Success, TerminationResultReconciler.Summarize(final).Status);
        Assert.Equal(0, final.Count(r => !r.ConfirmedExited));
    }
}
