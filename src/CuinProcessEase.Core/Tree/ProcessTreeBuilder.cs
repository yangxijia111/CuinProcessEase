using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Tree;

/// <summary>
/// 进程树构建器：由一次进程快照集合构建完整进程森林。
/// </summary>
/// <remarks>
/// 纯 Core 逻辑，不依赖 WPF / Win32。
/// 算法为 O(n)：PID 字典索引 + 单次连接 + 三色环检测，不引入并行化。
/// 原则：宁可成为 Root / Orphan，绝不错误连接。
/// </remarks>
public static class ProcessTreeBuilder
{
    /// <summary>
    /// 根据快照中的 ParentProcessId 建立父子关系，输出进程森林。
    /// </summary>
    /// <exception cref="ArgumentNullException">snapshot 为 null。</exception>
    public static ProcessTree Build(ProcessSnapshotCollection snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var nodes = new List<ProcessNode>(snapshot.Processes.Count);
        var nodesByPid = new Dictionary<int, ProcessNode>(snapshot.Processes.Count);

        // 第一遍：建立节点与 PID 索引（快照内出现重复 PID 时保留第一个，防御异常数据）
        foreach (ProcessSnapshot process in snapshot.Processes)
        {
            if (nodesByPid.ContainsKey(process.ProcessId))
            {
                continue;
            }

            var node = new ProcessNode { Process = process };
            nodesByPid[process.ProcessId] = node;
            nodes.Add(node);
        }

        // 第二遍：按 PPID 建立父链接（含 PID 重用防护与自引用检测）
        foreach (ProcessNode node in nodes)
        {
            ConnectParent(node, nodesByPid);
        }

        // 第三遍：异常循环数据检测与解除（人工构造或时间相等产生的环）
        BreakCycles(nodes);

        // 第四遍：父子关系固化后建立 Children 索引（按 PID 升序，输出稳定）
        foreach (ProcessNode node in nodes)
        {
            node.Parent?.AddChild(node);
        }

        foreach (ProcessNode node in nodes)
        {
            node.SortChildren();
        }

        List<ProcessNode> roots = nodes
            .Where(n => n.Parent is null)
            .OrderBy(n => n.Process.ProcessId)
            .ToList();

        return new ProcessTree
        {
            Roots = roots,
            TotalCount = nodes.Count,
        };
    }

    /// <summary>
    /// 尝试把节点连接到其 PPID 指向的父节点。
    /// 以下情况一律保持 Root / Orphan，不建立连接：
    /// - PPID 为 null（快照间隙未能读取）；
    /// - PPID 等于自身 PID（自引用）；
    /// - PPID 不在当前快照中（父进程已退出 / PPID 不存在）；
    /// - 双方 StartTime 已知但 Parent 晚于 Child（PPID 已被复用或数据不可信）。
    /// </summary>
    private static void ConnectParent(ProcessNode node, Dictionary<int, ProcessNode> nodesByPid)
    {
        int? parentPid = node.Process.ParentProcessId;
        if (parentPid is null)
        {
            return;
        }

        if (parentPid.Value == node.Process.ProcessId)
        {
            return;
        }

        if (!nodesByPid.TryGetValue(parentPid.Value, out ProcessNode? parent))
        {
            return;
        }

        // PID 重用防护：绝不伪造时间，只依据双方已知的 StartTime 判定
        if (parent.Process.StartTimeUtc is { } parentStart && node.Process.StartTimeUtc is { } childStart)
        {
            if (parentStart > childStart)
            {
                // 父比子还晚启动：PPID 已被复用或数据不可信，宁可不连
                return;
            }

            node.Parent = parent;
            node.ParentRelation = ParentRelationConfidence.Verified;
        }
        else
        {
            // 任一方 StartTime 未知：允许基于单次快照 PPID 的低可信度连接
            node.Parent = parent;
            node.ParentRelation = ParentRelationConfidence.Unverified;
        }
    }

    /// <summary>
    /// 三色标记环检测：沿 Parent 指针是函数图（每节点至多一个父），
    /// 整体 O(n)。检测到环时断开环上可信度最低的一条边（全 Verified 时断任一条），
    /// 保证任何输入都不会形成无限递归。
    /// </summary>
    private static void BreakCycles(List<ProcessNode> nodes)
    {
        const int white = 0, gray = 1, black = 2;
        var state = new Dictionary<ProcessNode, int>(nodes.Count);

        foreach (ProcessNode start in nodes)
        {
            if (state.TryGetValue(start, out int startState) && startState != white)
            {
                continue;
            }

            var path = new List<ProcessNode>();
            ProcessNode? current = start;

            while (current is not null
                   && (!state.TryGetValue(current, out int currentState) || currentState == white))
            {
                state[current] = gray;
                path.Add(current);
                current = current.Parent;
            }

            if (current is not null && state[current] == gray)
            {
                // current 出现在本次路径上：从它开始到路径末尾构成环
                int cycleStart = path.IndexOf(current);
                List<ProcessNode> cycle = path.GetRange(cycleStart, path.Count - cycleStart);

                // 断开可信度最低的成员（None 不会出现在环上，Unverified 优先牺牲）
                ProcessNode victim = cycle.OrderBy(n => n.ParentRelation).First();
                victim.Parent = null;
                victim.ParentRelation = ParentRelationConfidence.None;
            }

            foreach (ProcessNode node in path)
            {
                state[node] = black;
            }
        }
    }
}
