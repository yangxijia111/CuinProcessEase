using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Termination;

namespace CuinProcessEase.Core.Elevated;

/// <summary>
/// Elevated Helper 协议结果 → 主程序终止引擎结果模型的映射（纯逻辑）。
/// </summary>
/// <remarks>
/// 映射保持语义等价：Helper 端的 IdentityMismatch / AccessDenied / TimedOut
/// 与主程序 Preflight / Force 阶段的同名状态含义一致，
/// Final Rescan 归并时按既有规则处理（TimedOut → 后续确认退出可覆盖等）。
/// </remarks>
public static class ElevatedOutcomeMapper
{
    /// <summary>
    /// 将单个 Helper 目标结果映射为 <see cref="ProcessTerminationResult"/>。
    /// </summary>
    /// <param name="result">Helper 返回的逐目标结果。</param>
    /// <param name="expectedIdentity">该 PID 对应的 exact ProcessIdentity（主程序侧 Fresh 候选）。</param>
    /// <param name="processName">进程名（Fresh 快照）。</param>
    public static ProcessTerminationResult ToProcessResult(
        ElevatedKillTargetResult result,
        ProcessIdentity expectedIdentity,
        string processName)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(expectedIdentity);

        (ProcessTerminationStatus status, string message) = result.Outcome switch
        {
            ElevatedKillOutcome.Terminated => (ProcessTerminationStatus.Terminated, "已通过管理员 Helper 强制终止并确认退出。"),
            ElevatedKillOutcome.TimedOut => (ProcessTerminationStatus.TimedOut, "管理员 Helper 已发出 TerminateProcess，但未在有限等待内确认退出。"),
            ElevatedKillOutcome.AlreadyExited => (ProcessTerminationStatus.AlreadyExited, "执行前已退出。"),
            ElevatedKillOutcome.IdentityMismatch => (ProcessTerminationStatus.IdentityMismatch, "管理员 Helper 身份重验证失败：PID 已被复用，已拒绝终止。"),
            ElevatedKillOutcome.AccessDenied => (ProcessTerminationStatus.AccessDenied, "管理员 Helper 操作被拒绝。" + (result.Detail ?? string.Empty)),
            ElevatedKillOutcome.Failed => (ProcessTerminationStatus.Failed, "管理员 Helper 执行失败：" + (result.Detail ?? "未知原因")),
            ElevatedKillOutcome.InvalidRequest => (ProcessTerminationStatus.Failed, "请求被 Helper 协议校验拒绝：" + (result.Detail ?? string.Empty)),
            _ => (ProcessTerminationStatus.Failed, "未知 Helper 结果。"),
        };

        return new ProcessTerminationResult
        {
            Pid = result.ProcessId,
            ExpectedIdentity = expectedIdentity,
            ProcessName = processName,
            Result = status,
            Message = message,
        };
    }
}
