using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Termination;

/// <summary>
/// 终止结果归并器（纯逻辑，可完整单元测试，避免安全规则埋在 Windows 集成代码里）。
/// </summary>
/// <remarks>
/// 三步归并语义：
/// 1. <see cref="LatestResultByIdentity"/>——每个 ProcessIdentity 的最终状态取最后一次尝试，
///    历史失败（TimedOut / AccessDenied 等）被后续成功覆盖后不得继续产生假 residual；
/// 2. <see cref="ApplyFinalRescan"/>——用 Final Fresh Rescan 的四态验证结论
///    （<see cref="FinalIdentityVerification"/>）修正最终状态，并保证输出覆盖全部
///    targeted identities（只验证，绝不扩大终止范围）；
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
    /// 用 Final Rescan 的四态验证结论归并出最终逐进程结果（P6.2：必须覆盖全部 targeted identities）。
    /// 最终 ProcessResults 的身份集合 == 本次曾纳入候选的 targeted identities（至少对 Force 操作）。
    /// <para>逐状态语义：</para>
    /// <list type="bullet">
    /// <item><see cref="FinalIdentityState.Surviving"/>——exact identity 仍存在 → 一律
    ///   <see cref="ProcessTerminationStatus.Residual"/>（即使此前记录为 Terminated，也不能声称成功）；
    ///   无历史结果（纳入候选但未执行终止动作）→ 新建 Residual；</item>
    /// <item><see cref="FinalIdentityState.Uncertain"/>——身份无法读取，不能判定原身份已退出 →
    ///   fail-closed 一律 Residual，绝不能因此虚报成功；</item>
    /// <item><see cref="FinalIdentityState.Gone"/> / <see cref="FinalIdentityState.PidReused"/>——原
    ///   identity 已不存在：
    ///   · 已确认退出（Terminated / ClosedGracefully / AlreadyExited）→ 保持不变；
    ///   · TimedOut（动作已执行但有限等待内未确认）→ 修正为 Terminated；
    ///   · AccessDenied / Failed / Residual / NoWindow / UnreliableIdentity / IdentityMismatch
    ///     （本次未确认退出）→ 修正为 AlreadyExited（本次操作未能终止，但 Final Rescan 证明该
    ///     身份已不存在，不产生假 residual，也不声称是本次操作的功劳）；
    ///   · 无历史结果 → 新建 AlreadyExited（未执行终止动作，但 Final Rescan 确认该 exact
    ///     identity 已不存在）。</item>
    /// </list>
    /// 验证结论缺失的 targeted identity 按 fail-closed 视为 Uncertain（Residual）。
    /// </summary>
    /// <param name="targetedIdentities">本次操作曾纳入终止候选的全部 exact identity（输出覆盖范围）。</param>
    /// <param name="latestResults">按身份去重后的最后一次尝试结果。</param>
    /// <param name="verifications">Final Rescan 对每个 targeted identity 的四态判定。</param>
    public static IReadOnlyList<ProcessTerminationResult> ApplyFinalRescan(
        IReadOnlyCollection<ProcessIdentity> targetedIdentities,
        IReadOnlyList<ProcessTerminationResult> latestResults,
        IReadOnlyList<FinalIdentityVerification> verifications)
    {
        ArgumentNullException.ThrowIfNull(targetedIdentities);
        ArgumentNullException.ThrowIfNull(latestResults);
        ArgumentNullException.ThrowIfNull(verifications);

        var latestByIdentity = new Dictionary<ProcessIdentity, ProcessTerminationResult>();
        foreach (ProcessTerminationResult result in latestResults)
        {
            latestByIdentity[result.ExpectedIdentity] = result;
        }

        var stateByIdentity = new Dictionary<ProcessIdentity, FinalIdentityState>();
        foreach (FinalIdentityVerification verification in verifications)
        {
            stateByIdentity[verification.Identity] = verification.State;
        }

        var final = new List<ProcessTerminationResult>(targetedIdentities.Count);
        foreach (ProcessIdentity identity in targetedIdentities)
        {
            // 契约：调用方为每个 targeted identity 提供 verification；缺失时 fail-closed 视为 Uncertain
            FinalIdentityState state = stateByIdentity.TryGetValue(identity, out FinalIdentityState verified)
                ? verified
                : FinalIdentityState.Uncertain;
            latestByIdentity.TryGetValue(identity, out ProcessTerminationResult? result);

            final.Add(state switch
            {
                FinalIdentityState.Surviving => result is null
                    ? Missing(identity, ProcessTerminationStatus.Residual,
                        "该身份曾纳入终止候选但未执行终止动作；Final Rescan 确认该 exact 身份仍存在。")
                    : Residualize(result, "Final Rescan 确认该进程身份（PID + 启动时间）仍存在。"),
                FinalIdentityState.Uncertain => result is null
                    ? Missing(identity, ProcessTerminationStatus.Residual,
                        "该身份曾纳入终止候选但未执行终止动作；Final Rescan 无法确认其是否已退出，按 fail-closed 视为残留。")
                    : Residualize(result, "Final Rescan 无法确认该进程身份是否已退出（快照与句柄均不可读），按 fail-closed 视为残留。"),
                FinalIdentityState.Gone or FinalIdentityState.PidReused => result is null
                    ? Missing(identity, ProcessTerminationStatus.AlreadyExited,
                        "未执行终止动作，但 Final Rescan 确认该 exact identity 已不存在。")
                    : ConfirmedAbsent(result),
                _ => Missing(identity, ProcessTerminationStatus.Residual,
                    "未知的 Final Rescan 验证状态，按 fail-closed 视为残留。"),
            });
        }

        // 防御：latest 中存在但未被 targeted 覆盖的记录（正常管线不可能出现）原样保留，绝不静默丢弃
        var targetedSet = new HashSet<ProcessIdentity>(targetedIdentities);
        foreach (ProcessTerminationResult orphan in latestResults)
        {
            if (!targetedSet.Contains(orphan.ExpectedIdentity))
            {
                final.Add(orphan);
            }
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

    /// <summary>Final Rescan 确认仍存在 / 无法判定的身份统一改写为 Residual（已是 Residual 则保持）。</summary>
    private static ProcessTerminationResult Residualize(ProcessTerminationResult result, string message)
        => result.Result == ProcessTerminationStatus.Residual
            ? result
            : Rewrite(result, ProcessTerminationStatus.Residual, message);

    /// <summary>Final Rescan 确认 identity 已不存在（Gone / PidReused）时的历史结果修正。</summary>
    private static ProcessTerminationResult ConfirmedAbsent(ProcessTerminationResult result)
        => result.Result switch
        {
            ProcessTerminationStatus.TimedOut => Rewrite(
                result, ProcessTerminationStatus.Terminated,
                "Final Rescan 确认该身份已退出。"),
            ProcessTerminationStatus.AccessDenied
                or ProcessTerminationStatus.Failed
                or ProcessTerminationStatus.Residual
                or ProcessTerminationStatus.NoWindow
                or ProcessTerminationStatus.UnreliableIdentity
                or ProcessTerminationStatus.IdentityMismatch => Rewrite(
                result, ProcessTerminationStatus.AlreadyExited,
                "本次操作未能终止该进程；Final Rescan 确认该身份已不存在（可能已被其他方式结束）。"),
            _ => result,
        };

    /// <summary>targeted identity 从未产生 attempt 结果时的兜底构造（绝不静默遗漏）。</summary>
    private static ProcessTerminationResult Missing(
        ProcessIdentity identity, ProcessTerminationStatus status, string message) => new()
    {
        Pid = identity.ProcessId,
        ExpectedIdentity = identity,
        ProcessName = identity.ToString(),
        Result = status,
        Message = message,
    };

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
