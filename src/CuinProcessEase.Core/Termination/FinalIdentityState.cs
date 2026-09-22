namespace CuinProcessEase.Core.Termination;

/// <summary>Final Rescan 对单个 targeted identity 的判定状态（P6.2 四态模型）。</summary>
/// <remarks>
/// 严格区分"确认不存在"（<see cref="Gone"/> / <see cref="PidReused"/>）与
/// "读不到 / 无法判定"（<see cref="Uncertain"/>）：只有前者允许把未确认退出的
/// 历史结果修正为 AlreadyExited；后者必须 fail-closed 视为仍存活（Residual），
/// 绝不能因为"读不到身份"而虚报成功。
/// </remarks>
public enum FinalIdentityState
{
    /// <summary>PID 已不存在于 Final 快照中（原 identity 确认退出）。</summary>
    Gone = 0,

    /// <summary>PID 存在且 StartTime 与 targeted identity 精确相同（exact match，仍存活）。</summary>
    Surviving = 1,

    /// <summary>PID 存在但 StartTime 已知且不同：PID 已被复用，原 identity 已退出（新实例与本次操作无关）。</summary>
    PidReused = 2,

    /// <summary>PID 存在但身份无法读取（快照与句柄均拿不到可比对的 CreationTime），不能判定原身份已退出。</summary>
    Uncertain = 3,
}
