namespace CuinProcessEase.Core.Elevated;

/// <summary>
/// Elevated Helper 对单个终止目标的执行结果（跨进程协议枚举）。
/// </summary>
public enum ElevatedKillOutcome
{
    /// <summary>已 TerminateProcess 并在有限等待内确认退出。</summary>
    Terminated,

    /// <summary>TerminateProcess 已发出，但未在有限等待内确认进程对象 signaled。</summary>
    TimedOut,

    /// <summary>执行前进程对象已 signaled（已退出），未执行终止动作。</summary>
    AlreadyExited,

    /// <summary>句柄 CreationTime 与请求预期不一致（PID 已被复用）——绝不终止。</summary>
    IdentityMismatch,

    /// <summary>打开进程句柄 / TerminateProcess 被拒绝（Win32 ERROR_ACCESS_DENIED）。</summary>
    AccessDenied,

    /// <summary>其他失败（读创建时间失败、等待失败等），Detail 携带细节。</summary>
    Failed,

    /// <summary>请求未通过协议校验，Helper 未执行任何终止动作（逐目标结果共用此值）。</summary>
    InvalidRequest,
}
