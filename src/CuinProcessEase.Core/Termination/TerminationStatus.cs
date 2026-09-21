namespace CuinProcessEase.Core.Termination;

/// <summary>应用级终止结果状态。</summary>
/// <remarks>
/// 优先级原则：宁可拒绝结束，也不能误杀。
/// 所有非 Success 状态都表示本次未对目标执行（或未完全执行）破坏性操作。
/// </remarks>
public enum TerminationStatus
{
    /// <summary>成功：全部目标成员已退出。</summary>
    Success = 0,

    /// <summary>部分成功：部分成员已退出，部分失败/残留。</summary>
    PartialSuccess = 1,

    /// <summary>目标成员已全部不存在（Fresh 快照中无任何预期身份）。</summary>
    AlreadyExited = 2,

    /// <summary>目标已变化（预期 PID 仍存在但身份不同，即 PID 已被复用）。</summary>
    TargetChanged = 3,

    /// <summary>Fresh 分组中多个组包含预期身份，无法唯一确定目标。</summary>
    AmbiguousTarget = 4,

    /// <summary>Fresh Safety 判定为 Blocked（系统保护），禁止结束。</summary>
    Blocked = 5,

    /// <summary>Fresh Safety 判定为 Indeterminate（状态未知），按 fail-closed 禁止结束。</summary>
    Indeterminate = 6,

    /// <summary>Fresh Safety 判定为 RequiresElevation；本阶段不提权、不执行。</summary>
    RequiresElevation = 7,

    /// <summary>身份校验失败（CreationTime 与 Fresh Snapshot StartTimeUtc 不匹配），在任何终止动作前取消。</summary>
    IdentityMismatch = 8,

    /// <summary>执行失败（含身份不可靠、执行期错误等，详见 FailureReason）。</summary>
    Failed = 9,
}
