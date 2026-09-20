namespace CuinProcessEase.Core.Tree;

/// <summary>
/// 进程森林（多个 Root 的集合）。
/// </summary>
public sealed class ProcessTree
{
    /// <summary>全部根节点（含 Orphan），按 PID 升序。</summary>
    public required IReadOnlyList<ProcessNode> Roots { get; init; }

    /// <summary>树中节点总数：即输入快照去重后的“唯一 PID 节点数”（重复 PID 只保留第一个）。</summary>
    public int TotalCount { get; internal set; }

    /// <summary>
    /// 广度优先遍历全部节点。迭代式实现，深链不产生递归栈溢出。
    /// </summary>
    public IEnumerable<ProcessNode> EnumerateAll()
    {
        var queue = new Queue<ProcessNode>(Roots);
        while (queue.Count > 0)
        {
            ProcessNode node = queue.Dequeue();
            yield return node;
            foreach (ProcessNode child in node.Children)
            {
                queue.Enqueue(child);
            }
        }
    }
}
