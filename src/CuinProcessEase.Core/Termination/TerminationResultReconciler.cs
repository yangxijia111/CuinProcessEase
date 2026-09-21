using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Termination;

/// <summary>
/// 终止结果归并器（纯逻辑，可完整单元测试，避免安全规则埋在 Windows 集成代码里）。
/// </summary>
/// <remarks>
/// 三步归并语义：
/// 1. <see cref="LatestResultByIdentity"/>——每个 ProcessIdentity 的最终状态取最后一次尝试，
///    历史失败（TimedOut / AccessDenied 等）被后续成功覆盖后不得继续产生假 residual；
/// 2. <see cref="ApplyFinalRescan"/>——用 Final Fresh Rescan 的 exact identity 存活事实
///    修正最终状态（只验证，绝不扩大终止范围）；
/// 3. <see cref="Summarize"/>——基于修正后的最终状态汇总应用级状态。
/// </remarks>
public static class TerminationResultReconciler
{
    /// <summary>
    /// 每个 ProcessIdentity 的最终状态 = 最后一次尝试的结果（按输入顺序，后出现者覆盖）。
    /// </summary>
    public static IReadOnlyList<ProcessTerminationResult> LatestResultByIdentity(
        IEnumerable<ProcessTerminationResult> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        var latest = new Dictionary<ProcessIdentity, ProcessTerminationResult>();
        foreach (ProcessTerminationResult attempt in attempts)
        {
            latest[attempt.ExpectedIdentity] = attempt;
        }

        return latest.Values.ToList();
    }

    /// <summary>
    /// 用 Final Fresh Rescan 的存活事实修正每个身份的最终状态：
    /// - exact identity 仍存在 → 一律修正为 <see cref="ProcessTerminationStatus.Residual"/>
    ///   （即使此前记录为 Terminated，也不能声称成功）；
    /// - exact identity 已消失：
    ///   · 已确认退出（Terminated / ClosedGracefully / AlreadyExited）→ 保持不变；
    ///   · TimedOut（动作已执行但有限等待内未确认）→ 修正为 Terminated；
    ///   · AccessDenied / Failed / Residual / NoWindow（本次未确认退出）→ 修正为
    ///     AlreadyExited（本次操作未能终止，但 Final Rescan 证明该身份已不存在，
    ///     不产生假 residual，也不声称是本次操作的功劳）。
    /// </summary>
    public static IReadOnlyList<ProcessTerminationResult> ApplyFinalRescan(
        IReadOnlyList<ProcessTerminationResult> latestResults,
        IReadOnlySet<ProcessIdentity> survivingExactIdentities)
    {
        ArgumentNullException.ThrowIfNull(latestResults);
        ArgumentNullException.ThrowIfNull(survivingExactIdentities);

        var final = new List<ProcessTerminationResult>(latestResults.Count);
        foreach (ProcessTerminationResult result in latestResults)
        {
            if (survivingExactIdentities.Contains(result.ExpectedIdentity))
            {
                final.Add(result.Result == ProcessTerminationStatus.Residual
                    ? result
                    : Rewrite(result, ProcessTerminationStatus.Residual,
                        "Final Rescan 确认该进程身份（PID + 启动时间）仍存在。"));
                continue;
            }

            final.Add(result.Result switch
            {
                ProcessTerminationStatus.TimedOut => Rewrite(
                    result, ProcessTerminationStatus.Terminated,
                    "Final Rescan 确认该身份已退出。"),
                ProcessTerminationStatus.AccessDenied
                    or ProcessTerminationStatus.Failed
                    or ProcessTerminationStatus.Residual
                    or ProcessTerminationStatus.NoWindow => Rewrite(
                    result, ProcessTerminationStatus.AlreadyExited,
                    "本次操作未能终止该进程；Final Rescan 确认该身份已不存在（可能已被其他方式结束）。"),
                _ => result,
            });
        }

        return final;
    }

    /// <summary>
    /// 基于最终状态汇总应用级状态：
    /// 全部确认退出 → Success（或全部执行前已退出 → AlreadyExited）；
    /// 部分确认退出 → PartialSuccess；无一退出 → Failed。
    /// </summary>
    public static (TerminationStatus Status, TerminationFailureReason Reason) Summarize(
        IReadOnlyList<ProcessTerminationResult> finalResults)
    {
        ArgumentNullException.ThrowIfNull(finalResults);

        if (finalResults.Count == 0)
        {
            return (TerminationStatus.Failed, TerminationFailureReason.ExecutionError);
        }

        bool allExited = finalResults.All(r => r.ConfirmedExited);
        if (allExited)
        {
            return finalResults.All(r => r.Result == ProcessTerminationStatus.AlreadyExited)
                ? (TerminationStatus.AlreadyExited, TerminationFailureReason.None)
                : (TerminationStatus.Success, TerminationFailureReason.None);
        }

        if (finalResults.Any(r => r.ConfirmedExited))
        {
            return (TerminationStatus.PartialSuccess, PartialReason(finalResults));
        }

        return (TerminationStatus.Failed, PartialReason(finalResults));
    }

    private static TerminationFailureReason PartialReason(IReadOnlyList<ProcessTerminationResult> results)
        => results.Any(r => r.Result is ProcessTerminationStatus.Residual or ProcessTerminationStatus.NoWindow)
            ? TerminationFailureReason.ResidualRemain
            : TerminationFailureReason.ExecutionError;

    private static ProcessTerminationResult Rewrite(
        ProcessTerminationResult result, ProcessTerminationStatus status, string message) => new()
    {
        Pid = result.Pid,
        ExpectedIdentity = result.ExpectedIdentity,
        ProcessName = result.ProcessName,
        Result = status,
        Win32Error = result.Win32Error,
        Message = message,
    };
}
