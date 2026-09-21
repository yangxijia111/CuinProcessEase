using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Termination;

/// <summary>单个进程的终止结果。</summary>
/// <remarks>绝不只返回 bool：保留 PID、预期身份、进程名、结果状态、Win32 错误码与说明。</remarks>
public sealed class ProcessTerminationResult
{
    /// <summary>进程 ID（执行时刻）。</summary>
    public required int Pid { get; init; }

    /// <summary>该进程的预期身份（来自 Fresh Snapshot，PID + StartTimeUtc）。</summary>
    public required ProcessIdentity ExpectedIdentity { get; init; }

    /// <summary>进程名（来自 Fresh Snapshot）。</summary>
    public required string ProcessName { get; init; }

    /// <summary>结果状态。</summary>
    public required ProcessTerminationStatus Result { get; init; }

    /// <summary>Win32 错误码（无错误为 null）。</summary>
    public int? Win32Error { get; init; }

    /// <summary>人类可读说明。</summary>
    public string? Message { get; init; }

    /// <summary>该进程是否已确认退出（含执行前已退出）。</summary>
    public bool ConfirmedExited =>
        Result is ProcessTerminationStatus.ClosedGracefully
            or ProcessTerminationStatus.Terminated
            or ProcessTerminationStatus.AlreadyExited;
}
