using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Grouping;

/// <summary>
/// 应用组：被判定属于同一个软件的全部进程。
/// </summary>
/// <remarks>本阶段不含 RiskLevel（属 Phase 4 Safety Engine）。</remarks>
public sealed class ApplicationGroup
{
    /// <summary>应用身份信息。</summary>
    public required ApplicationIdentity Identity { get; init; }

    /// <summary>组内全部进程快照（按 PID 升序）。</summary>
    public required IReadOnlyList<ProcessSnapshot> Processes { get; init; }

    /// <summary>组内根进程：进程树中无父、或父不在本组内的进程。</summary>
    public required IReadOnlyList<ProcessSnapshot> RootProcesses { get; init; }

    /// <summary>本组的最高分组可信度；单进程组为 Unknown。</summary>
    public GroupingConfidence Confidence { get; init; }

    /// <summary>分组依据（组内全部合并依据的并集）。</summary>
    public GroupingReason Reasons { get; init; }

    /// <summary>进程数量。</summary>
    public int ProcessCount => Processes.Count;
}
