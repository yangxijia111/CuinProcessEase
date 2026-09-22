namespace CuinProcessEase.Core.Termination;

/// <summary>终止操作失败/取消的补充原因（与 TerminationStatus 配合提供细节）。</summary>
public enum TerminationFailureReason
{
    /// <summary>无（成功或状态本身已足够明确）。</summary>
    None = 0,

    /// <summary>预期身份的 StartTime 不可靠（PID+null 无法防止 PID 重用），fail-closed。</summary>
    UnreliableIdentity = 1,

    /// <summary>候选进程句柄 CreationTime 与 Fresh Snapshot 不匹配，在任何终止前取消。</summary>
    IdentityVerificationFailed = 2,

    /// <summary>目标解析到多个组，取消操作。</summary>
    AmbiguousTarget = 3,

    /// <summary>Fresh Safety 拒绝（Blocked / Indeterminate / RequiresElevation）。</summary>
    SafetyRejected = 4,

    /// <summary>残留进程未能清理（无 exact identity 锚点或清理轮次用尽）。</summary>
    ResidualRemain = 5,

    /// <summary>执行期错误（OpenProcess / Win32 调用失败等）。</summary>
    ExecutionError = 6,

    /// <summary>弱证据多进程组未获得用户明确范围确认（P6.4）。</summary>
    ScopeConfirmationRequired = 7,
}
