using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Termination;

/// <summary>一个应用组的终止结果。</summary>
public sealed class ApplicationTerminationResult
{
    /// <summary>应用级状态。</summary>
    public required TerminationStatus Status { get; init; }

    /// <summary>失败/取消补充原因。</summary>
    public TerminationFailureReason FailureReason { get; init; } = TerminationFailureReason.None;

    /// <summary>请求的显示名（便于 UI 展示与日志）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>逐进程结果。</summary>
    public IReadOnlyList<ProcessTerminationResult> ProcessResults { get; init; }
        = Array.Empty<ProcessTerminationResult>();

    /// <summary>操作结束后仍存活的预期身份（exact identity，PID + StartTimeUtc）。</summary>
    public IReadOnlyList<ProcessIdentity> ResidualIdentities { get; init; }
        = Array.Empty<ProcessIdentity>();

    /// <summary>人类可读说明（UI / 日志展示）。</summary>
    public string? Message { get; init; }

    /// <summary>仍存活的预期成员数量（UI 提示"仍有 N 个相关进程未退出"）。</summary>
    public int ResidualCount => ResidualIdentities.Count;

    /// <summary>是否存在任何被确认退出的成员。</summary>
    public bool AnyConfirmedExited => ProcessResults.Any(r => r.ConfirmedExited);
}
