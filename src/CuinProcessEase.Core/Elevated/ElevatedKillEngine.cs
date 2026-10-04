using CuinProcessEase.Core.Termination;

namespace CuinProcessEase.Core.Elevated;

/// <summary>
/// Elevated Helper 的单目标执行引擎（纯逻辑）：OpenProcess → 已退出判定 →
/// CreationTime 原始 FILETIME 逐位比对 → TerminateProcess → 有限等待确认。
/// </summary>
/// <remarks>
/// 安全设计（与主程序终止引擎同源的原则）：
/// - Helper 绝不盲目信任主程序传入的 PID：必须重新打开句柄并以 CreationTime
///   与请求预期值逐位比对（差 1 tick 也拒绝），不匹配一律 IdentityMismatch 绝不终止；
/// - 已 signaled 的目标记 AlreadyExited，不算失败、不执行终止动作；
/// - TerminateProcess 返回 true ≠ 已退出：WaitForSingleObject 有限等待确认；
/// - 所有句柄全路径释放（含失败路径）；
/// - PID 0 / 4 等关键进程由主程序 Safety 门禁拦截，此处再做 PID 范围防御（&gt; 0）。
/// </remarks>
public static class ElevatedKillEngine
{
    /// <summary>执行单个终止目标。</summary>
    public static ElevatedKillTargetResult Execute(ElevatedKillTarget target, IElevatedKillInterop interop)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(interop);
        if (target.ProcessId <= 0 || target.ExpectedStartTimeUtcFileTime <= 0)
        {
            return new ElevatedKillTargetResult(target.ProcessId, ElevatedKillOutcome.InvalidRequest,
                "目标 PID 或预期启动时间非法。");
        }

        // 终止 + 同步等待 + 受限查询：Helper 端验证与执行使用同一句柄，绝不按 PID 重开
        uint access = ElevatedInteropConstants.ProcessTerminate
                      | ElevatedInteropConstants.Synchronize
                      | ElevatedInteropConstants.ProcessQueryLimitedInformation;

        IntPtr handle = interop.OpenProcess(access, inheritHandle: false, (uint)target.ProcessId, out int openError);
        if (handle == IntPtr.Zero)
        {
            return new ElevatedKillTargetResult(
                target.ProcessId,
                openError == ElevatedInteropConstants.ErrorAccessDenied
                    ? ElevatedKillOutcome.AccessDenied
                    : ElevatedKillOutcome.Failed,
                $"OpenProcess 失败（Win32 错误 {openError}）。");
        }

        try
        {
            // 身份重验证：句柄 CreationTime 与请求预期值逐位比对，差 1 tick 也拒绝
            if (!interop.TryGetCreationFileTime(handle, out long actualFileTime))
            {
                return new ElevatedKillTargetResult(target.ProcessId, ElevatedKillOutcome.Failed,
                    "无法读取进程创建时间，无法验证身份。");
            }

            if (!ProcessIdentityMatcher.IsExactProcessIdentityMatch(
                    target.ExpectedStartTimeUtcFileTime, actualFileTime))
            {
                return new ElevatedKillTargetResult(target.ProcessId, ElevatedKillOutcome.IdentityMismatch,
                    "句柄创建时间与预期不一致（PID 已被复用），已拒绝终止。");
            }

            // 执行前已退出：不算失败，不执行终止动作
            if (interop.WaitForSingleObject(handle, 0) == ElevatedInteropConstants.WaitObject0)
            {
                return new ElevatedKillTargetResult(target.ProcessId, ElevatedKillOutcome.AlreadyExited,
                    "执行前已退出。");
            }

            if (!interop.TerminateProcess(handle, 1, out int terminateError))
            {
                return new ElevatedKillTargetResult(
                    target.ProcessId,
                    terminateError == ElevatedInteropConstants.ErrorAccessDenied
                        ? ElevatedKillOutcome.AccessDenied
                        : ElevatedKillOutcome.Failed,
                    $"TerminateProcess 失败（Win32 错误 {terminateError}）。");
            }

            uint wait = interop.WaitForSingleObject(handle, ElevatedInteropConstants.ForceWaitMs);
            return wait switch
            {
                ElevatedInteropConstants.WaitObject0 => new ElevatedKillTargetResult(
                    target.ProcessId, ElevatedKillOutcome.Terminated, "已强制终止并确认退出。"),
                ElevatedInteropConstants.WaitTimeout => new ElevatedKillTargetResult(
                    target.ProcessId, ElevatedKillOutcome.TimedOut,
                    "TerminateProcess 已发出，但未在有限等待内确认退出。"),
                _ => new ElevatedKillTargetResult(target.ProcessId, ElevatedKillOutcome.Failed,
                    $"等待进程对象失败（Wait 结果 {wait}）。"),
            };
        }
        finally
        {
            interop.CloseHandle(handle);
        }
    }
}
