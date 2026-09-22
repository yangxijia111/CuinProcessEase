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
    public void 归并_Skipped表示该轮未执行_绝不覆盖已有历史结果()
    {
        // 残留清理轮整组取消时 validated 候选补 Skipped：该身份第 0 轮的 TimedOut
        // 信息量更大（确曾执行 TerminateProcess），不得被"未执行"掩盖
        var identity = Id(100);
        IReadOnlyList<ProcessTerminationResult> latest = TerminationResultReconciler.LatestResultByIdentity(
        [
            Attempt(identity, ProcessTerminationStatus.TimedOut),
            Attempt(identity, ProcessTerminationStatus.Skipped),
        ]);

        var result = Assert.Single(latest);
        Assert.Equal(ProcessTerminationStatus.TimedOut, result.Result);
    }

    [Fact]
    public void 归并_仅有Skipped时保留Skipped()
    {
        // 该身份从未真正执行（整组取消）→ Skipped 就是其全部历史，必须保留
        var identity = Id(100);
        IReadOnlyList<ProcessTerminationResult> latest = TerminationResultReconciler.LatestResultByIdentity(
            [Attempt(identity, ProcessTerminationStatus.Skipped)]);

        var result = Assert.Single(latest);
        Assert.Equal(ProcessTerminationStatus.Skipped, result.Result);
        Assert.False(result.ConfirmedExited);
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

    // ================= ApplyFinalRescan（P6.2：targeted 全覆盖 + 四态归并） =================

    private static IReadOnlyList<FinalIdentityVerification> Verifications(
        params (ProcessIdentity Identity, FinalIdentityState State)[] items)
        => items.Select(i => new FinalIdentityVerification(i.Identity, i.State)).ToList();

    [Fact]
    public void 归并_Terminated且FinalRescan已消失_保持Terminated()
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [identity], [Attempt(identity, ProcessTerminationStatus.Terminated)],
            Verifications((identity, FinalIdentityState.Gone)));

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.Terminated, result.Result);
        Assert.True(result.ConfirmedExited);
    }

    [Fact]
    public void 归并_Terminated但FinalRescan身份仍存在_修正为Residual()
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [identity], [Attempt(identity, ProcessTerminationStatus.Terminated)],
            Verifications((identity, FinalIdentityState.Surviving)));

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
            [identity], [Attempt(identity, ProcessTerminationStatus.TimedOut)],
            Verifications((identity, FinalIdentityState.Gone)));

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.Terminated, result.Result);
        Assert.True(result.ConfirmedExited);
    }

    [Theory]
    [InlineData(ProcessTerminationStatus.AccessDenied)]
    [InlineData(ProcessTerminationStatus.Failed)]
    [InlineData(ProcessTerminationStatus.Residual)]
    [InlineData(ProcessTerminationStatus.NoWindow)]
    [InlineData(ProcessTerminationStatus.UnreliableIdentity)]
    [InlineData(ProcessTerminationStatus.IdentityMismatch)]
    [InlineData(ProcessTerminationStatus.Skipped)]
    public void 归并_未确认失败但FinalRescan已消失_修正为AlreadyExited不计residual(ProcessTerminationStatus status)
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [identity], [Attempt(identity, status)],
            Verifications((identity, FinalIdentityState.Gone)));

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.AlreadyExited, result.Result);
        Assert.True(result.ConfirmedExited);
    }

    [Fact]
    public void 归并_已确认退出且FinalRescan已消失_保持不变()
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [identity], [Attempt(identity, ProcessTerminationStatus.AlreadyExited)],
            Verifications((identity, FinalIdentityState.Gone)));

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.AlreadyExited, result.Result);
        Assert.Equal("AlreadyExited", result.Message);
    }

    [Fact]
    public void 归并_PidReused与Gone同样视为原identity已退出()
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [identity], [Attempt(identity, ProcessTerminationStatus.TimedOut)],
            Verifications((identity, FinalIdentityState.PidReused)));

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.Terminated, result.Result);
        Assert.True(result.ConfirmedExited);
    }

    [Fact]
    public void 归并_同PID不同StartTime_仅匹配的exact身份被修正()
    {
        var original = Id(100, offsetSeconds: 0);
        var reused = Id(100, offsetSeconds: 30); // 同 PID、不同 StartTime（复用后实例）

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [original, reused],
        [
            Attempt(original, ProcessTerminationStatus.Terminated),
            Attempt(reused, ProcessTerminationStatus.TimedOut),
        ],
            Verifications((original, FinalIdentityState.Gone), (reused, FinalIdentityState.Surviving)));

        Assert.Equal(2, final.Count);
        Assert.Contains(final, r => r.ExpectedIdentity == original
            && r.Result == ProcessTerminationStatus.Terminated);
        Assert.Contains(final, r => r.ExpectedIdentity == reused
            && r.Result == ProcessTerminationStatus.Residual);
    }

    // ================= ApplyFinalRescan：fail-closed 与 targeted 全覆盖（P6.2） =================

    [Theory]
    [InlineData(ProcessTerminationStatus.Terminated)]
    [InlineData(ProcessTerminationStatus.ClosedGracefully)]
    [InlineData(ProcessTerminationStatus.AlreadyExited)]
    [InlineData(ProcessTerminationStatus.TimedOut)]
    [InlineData(ProcessTerminationStatus.AccessDenied)]
    public void 归并_Uncertain_无论历史状态一律Residual_failClosed(ProcessTerminationStatus history)
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [identity], [Attempt(identity, history)],
            Verifications((identity, FinalIdentityState.Uncertain)));

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.Residual, result.Result);
        Assert.False(result.ConfirmedExited);
    }

    [Fact]
    public void 归并_targeted无历史结果且Final确认仍存在_生成Residual()
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [identity], [], Verifications((identity, FinalIdentityState.Surviving)));

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.Residual, result.Result);
        Assert.False(result.ConfirmedExited);
        Assert.Contains("未执行终止动作", result.Message);
        Assert.Equal(identity, result.ExpectedIdentity);
    }

    [Fact]
    public void 归并_targeted无历史结果且Final确认不存在_生成AlreadyExited()
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [identity], [], Verifications((identity, FinalIdentityState.Gone)));

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.AlreadyExited, result.Result);
        Assert.True(result.ConfirmedExited);
        Assert.Contains("未执行终止动作", result.Message);
    }

    [Fact]
    public void 归并_验证结论缺失_按failClosed视为Residual()
    {
        var identity = Id(100);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [identity], [Attempt(identity, ProcessTerminationStatus.Terminated)], []);

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.Residual, result.Result);
        Assert.False(result.ConfirmedExited);
    }

    [Fact]
    public void 归并_全部targeted身份都出现在最终结果_绝不静默遗漏()
    {
        // 混合场景：A 有历史结果且已退出；B 有历史结果仍存活；C 从未产生 attempt 且仍存活；
        // D 从未产生 attempt 且已消失。最终结果必须恰好覆盖四个 targeted identity。
        var a = Id(100);
        var b = Id(200);
        var c = Id(300);
        var d = Id(400);

        IReadOnlyList<ProcessTerminationResult> final = TerminationResultReconciler.ApplyFinalRescan(
            [a, b, c, d],
        [
            Attempt(a, ProcessTerminationStatus.Terminated),
            Attempt(b, ProcessTerminationStatus.TimedOut),
        ],
            Verifications(
                (a, FinalIdentityState.Gone),
                (b, FinalIdentityState.Surviving),
                (c, FinalIdentityState.Surviving),
                (d, FinalIdentityState.PidReused)));

        Assert.Equal(4, final.Count);
        Assert.Equal(4, final.Select(r => r.ExpectedIdentity).Distinct().Count());
        Assert.Contains(final, r => r.ExpectedIdentity == a && r.Result == ProcessTerminationStatus.Terminated);
        Assert.Contains(final, r => r.ExpectedIdentity == b && r.Result == ProcessTerminationStatus.Residual);
        Assert.Contains(final, r => r.ExpectedIdentity == c && r.Result == ProcessTerminationStatus.Residual);
        Assert.Contains(final, r => r.ExpectedIdentity == d && r.Result == ProcessTerminationStatus.AlreadyExited);
        // Final survivor > 0 → 汇总绝不可能是 Success
        var (status, reason) = TerminationResultReconciler.Summarize(final);
        Assert.Equal(TerminationStatus.PartialSuccess, status);
        Assert.Equal(TerminationFailureReason.ResidualRemain, reason);
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
            [identity], latest, Verifications((identity, FinalIdentityState.Gone)));

        var result = Assert.Single(final);
        Assert.Equal(ProcessTerminationStatus.Terminated, result.Result);
        Assert.Equal(TerminationStatus.Success, TerminationResultReconciler.Summarize(final).Status);
        Assert.Equal(0, final.Count(r => !r.ConfirmedExited));
    }
}
