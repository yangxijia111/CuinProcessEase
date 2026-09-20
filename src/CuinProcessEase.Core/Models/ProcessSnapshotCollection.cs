namespace CuinProcessEase.Core.Models;

/// <summary>
/// 一次完整扫描得到的进程快照集合。
/// </summary>
public sealed class ProcessSnapshotCollection
{
    /// <summary>本次快照的采集时刻（UTC）。</summary>
    public required DateTime CapturedAtUtc { get; init; }

    /// <summary>按 PID 升序排列的进程快照列表。</summary>
    public required IReadOnlyList<ProcessSnapshot> Processes { get; init; }

    /// <summary>进程数量。</summary>
    public int Count => Processes.Count;
}
