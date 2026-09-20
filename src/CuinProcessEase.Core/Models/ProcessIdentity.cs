namespace CuinProcessEase.Core.Models;

/// <summary>
/// 进程的唯一身份标识：PID + 进程启动时间。
/// </summary>
/// <remarks>
/// Windows 会复用已退出进程的 PID，因此 PID 不能单独作为进程身份。
/// PID + StartTime 组合在系统运行期间唯一，
/// 可用于判断“还是不是原来那个进程”，避免 PID 重用导致的误判。
/// 若 StartTime 因权限不足无法读取，则退化为仅 PID（此时唯一性减弱）。
/// </remarks>
public sealed record ProcessIdentity(
    int ProcessId,
    DateTime? StartTimeUtc)
{
    /// <inheritdoc />
    public override string ToString()
        => StartTimeUtc is { } start
            ? $"PID {ProcessId} @ {start:O}"
            : $"PID {ProcessId} @ ?";
}
