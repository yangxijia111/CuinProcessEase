using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Tree;

/// <summary>
/// 节点与其父节点之间父子关系的可信度。
/// </summary>
public enum ParentRelationConfidence
{
    /// <summary>无父关系（Root / Orphan）。</summary>
    None = 0,

    /// <summary>
    /// 低可信度：父子任意一方 StartTime 未知，
    /// 仅凭单次 Snapshot 的 PPID 建立连接。
    /// </summary>
    Unverified = 1,

    /// <summary>
    /// 已验证：父子双方 StartTime 均已知，且 Parent.StartTimeUtc &lt;= Child.StartTimeUtc。
    /// </summary>
    Verified = 2,
}

/// <summary>
/// 进程树节点：包装一份进程快照并维护父子链接。
/// </summary>
public sealed class ProcessNode
{
    private readonly List<ProcessNode> _children = new();

    /// <summary>节点对应的进程快照。</summary>
    public required ProcessSnapshot Process { get; init; }

    /// <summary>父节点；Root / Orphan 为 null。</summary>
    public ProcessNode? Parent { get; internal set; }

    /// <summary>本节点与 <see cref="Parent"/> 连接的可信度；Parent 为 null 时为 None。</summary>
    public ParentRelationConfidence ParentRelation { get; internal set; }
        = ParentRelationConfidence.None;

    /// <summary>子节点列表（按 PID 升序，由 Builder 构建完成后固化）。</summary>
    public IReadOnlyList<ProcessNode> Children => _children;

    /// <summary>便捷访问进程 PID。</summary>
    public int ProcessId => Process.ProcessId;

    internal void AddChild(ProcessNode child) => _children.Add(child);

    internal void SortChildren()
        => _children.Sort((a, b) => a.Process.ProcessId.CompareTo(b.Process.ProcessId));
}
