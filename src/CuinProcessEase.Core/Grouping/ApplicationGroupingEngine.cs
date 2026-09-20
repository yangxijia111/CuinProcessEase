using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Tree;

namespace CuinProcessEase.Core.Grouping;

/// <summary>
/// 应用分组引擎：将进程快照聚合成用户可理解的 ApplicationGroup。
/// </summary>
/// <remarks>
/// 管道：Processes → ProcessTree → 候选关系生成（独立规则，各自可测）→
/// 关系置信度评估 → 仅合并达到阈值的关系（并查集）→ ApplicationGroup[]。
/// 原则：宁可拆开，不要错合。
/// 纯 Core 逻辑，不依赖 WPF / Win32。
/// </remarks>
public static class ApplicationGroupingEngine
{
    /// <summary>达到该可信度的关系才允许合并。</summary>
    public const GroupingConfidence MergeThreshold = GroupingConfidence.Medium;

    /// <summary>
    /// 对一次快照执行应用分组，返回应用组列表（按组内最小 PID 升序）。
    /// 每个输入进程恰好属于一个组，无丢失、无重复。
    /// </summary>
    public static IReadOnlyList<ApplicationGroup> Group(ProcessSnapshotCollection snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Processes.Count == 0)
        {
            return Array.Empty<ApplicationGroup>();
        }

        ProcessTree tree = ProcessTreeBuilder.Build(snapshot);

        // 节点索引（与 snapshot 顺序一致）
        List<ProcessNode> nodes = tree.EnumerateAll().OrderBy(n => n.ProcessId).ToList();
        var nodeIndexByPid = new Dictionary<int, int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            nodeIndexByPid[nodes[i].ProcessId] = i;
        }

        var union = new UnionFind(nodes.Count);

        // ---- 阶段 1：树边关系（Verified 强依据 / Unverified 仅辅助） ----
        foreach (ProcessNode node in nodes)
        {
            if (node.Parent is null)
            {
                continue;
            }

            GenerateTreeEdgeRelation(node, nodeIndexByPid, union);
        }

        // ---- 阶段 2：相同 exe 完整路径（同一软件多实例） ----
        MergeSameExecutablePaths(nodes, nodeIndexByPid, union);

        // ---- 阶段 3：相同的具体安装目录 ----
        MergeSameInstallDirectories(nodes, nodeIndexByPid, union);

        // ---- 阶段 4：相同有效产品名 + 相同公司名 ----
        MergeSameProductAndCompany(nodes, nodeIndexByPid, union);

        // ---- 阶段 5：组装应用组 ----
        return AssembleGroups(snapshot, tree, nodes, nodeIndexByPid, union);
    }

    // ================= 关系规则（独立、可测） =================

    /// <summary>
    /// 规则 A/B：进程树父子边。
    /// - 双方均非歧义进程名 且 Verified → High（真实父子的强依据）；
    /// - 其余情况（任一方歧义，或 Unverified）必须存在附加证据（同 exe / 同目录 / 同产品公司）
    ///   才生成 Medium 关系；
    /// - 无证据 → 不生成关系（宁拆不合）。
    /// </summary>
    private static void GenerateTreeEdgeRelation(
        ProcessNode child,
        Dictionary<int, int> nodeIndexByPid,
        UnionFind union)
    {
        ProcessNode parent = child.Parent!;
        ProcessSnapshot parentProcess = parent.Process;
        ProcessSnapshot childProcess = child.Process;

        bool verifiedEdge = child.ParentRelation == ParentRelationConfidence.Verified;
        bool ambiguous = ProcessNameRules.IsAmbiguousProcessName(parentProcess.Name)
                         || ProcessNameRules.IsAmbiguousProcessName(childProcess.Name);

        GroupingReason evidenceReasons = GetCommonEvidence(parentProcess, childProcess);

        if (!ambiguous && verifiedEdge)
        {
            union.Union(
                nodeIndexByPid[parent.ProcessId],
                nodeIndexByPid[child.ProcessId],
                GroupingConfidence.High,
                GroupingReason.VerifiedParentChild | evidenceReasons);
            return;
        }

        if (evidenceReasons != GroupingReason.None)
        {
            // 歧义进程或未验证的边：附加证据补强到 Medium
            GroupingReason edgeReason = verifiedEdge
                ? GroupingReason.VerifiedParentChild
                : GroupingReason.UnverifiedParentChild;
            union.Union(
                nodeIndexByPid[parent.ProcessId],
                nodeIndexByPid[child.ProcessId],
                GroupingConfidence.Medium,
                edgeReason | evidenceReasons);
        }
    }

    /// <summary>
    /// 计算两个进程之间的"附加共同证据"。
    /// 注意：公司名相同永远不单独构成证据（同公司可能运行大量不同软件）。
    /// </summary>
    private static GroupingReason GetCommonEvidence(ProcessSnapshot a, ProcessSnapshot b)
    {
        GroupingReason reasons = GroupingReason.None;

        // 相同 exe 完整路径
        if (!string.IsNullOrWhiteSpace(a.ExecutablePath)
            && string.Equals(a.ExecutablePath, b.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            reasons |= GroupingReason.SameExecutable;
        }

        // 相同的具体安装目录（产品名冲突时目录证据失效，见 MergeSameInstallDirectories）
        string? dirA = InstallDirectoryRules.GetApplicationDirectory(a.ExecutablePath);
        string? dirB = InstallDirectoryRules.GetApplicationDirectory(b.ExecutablePath);
        if (dirA is not null
            && string.Equals(dirA, dirB, StringComparison.OrdinalIgnoreCase)
            && InstallDirectoryRules.IsSpecificApplicationDirectory(dirA)
            && !ProductsConflict(a, b))
        {
            reasons |= GroupingReason.SameInstallDirectory;
        }

        // 相同有效产品名 + 相同公司（产品名单独相同不足 Medium 证据，公司单独相同永远无效）
        if (ProcessNameRules.IsValidProductName(a.ProductName)
            && string.Equals(a.ProductName, b.ProductName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(a.CompanyName)
            && string.Equals(a.CompanyName, b.CompanyName, StringComparison.OrdinalIgnoreCase))
        {
            reasons |= GroupingReason.SameProduct | GroupingReason.SameCompany;
        }

        return reasons;
    }

    /// <summary>
    /// 相同 exe 完整路径 → High。
    /// 歧义进程名（python.exe / svchost.exe 等通用运行时与宿主）不参与：
    /// 同一路径的多个 python/cmd 实例是不同任务，绝不因同路径合并。
    /// </summary>
    private static void MergeSameExecutablePaths(
        List<ProcessNode> nodes,
        Dictionary<int, int> nodeIndexByPid,
        UnionFind union)
    {
        foreach (IGrouping<string, ProcessNode> group in nodes
                     .Where(n => !string.IsNullOrWhiteSpace(n.Process.ExecutablePath))
                     .Where(n => !ProcessNameRules.IsAmbiguousProcessName(n.Process.Name))
                     .GroupBy(n => n.Process.ExecutablePath!, StringComparer.OrdinalIgnoreCase))
        {
            ChainUnion(group, nodeIndexByPid, union, GroupingConfidence.High, GroupingReason.SameExecutable);
        }
    }

    /// <summary>
    /// 相同的具体安装目录（黑名单目录不算）→ Medium。
    /// 歧义进程名同样不参与。
    /// </summary>
    private static void MergeSameInstallDirectories(
        List<ProcessNode> nodes,
        Dictionary<int, int> nodeIndexByPid,
        UnionFind union)
    {
        var nodesByDirectory = new Dictionary<string, List<ProcessNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (ProcessNode node in nodes)
        {
            string? directory = InstallDirectoryRules.GetApplicationDirectory(node.Process.ExecutablePath);
            if (directory is null
                || !InstallDirectoryRules.IsSpecificApplicationDirectory(directory)
                || ProcessNameRules.IsAmbiguousProcessName(node.Process.Name))
            {
                continue;
            }

            if (!nodesByDirectory.TryGetValue(directory, out List<ProcessNode>? members))
            {
                members = new List<ProcessNode>();
                nodesByDirectory[directory] = members;
            }

            members.Add(node);
        }

        foreach (List<ProcessNode> members in nodesByDirectory.Values)
        {
            // 套件共享目录防护：同一目录下出现多个不同的有效产品名
            //（如 Office16 同时含 Word / Excel）时，该目录不是任何单一软件的专属目录，
            // 整组放弃按目录合并（宁拆不合）
            int distinctProducts = members
                .Select(n => n.Process)
                .Select(p => ProcessNameRules.IsValidProductName(p.ProductName,
                    ProcessNameRules.GetFileName(p.ExecutablePath))
                    ? p.ProductName!.Trim().ToLowerInvariant()
                    : null)
                .Where(name => name is not null)
                .Distinct()
                .Count();
            if (distinctProducts > 1)
            {
                continue;
            }

            ChainUnion(members, nodeIndexByPid, union, GroupingConfidence.Medium, GroupingReason.SameInstallDirectory);
        }
    }

    /// <summary>
    /// 双方产品名均有效但互不相同：目录等辅助证据失效
    /// （不同产品即使同目录也不属于同一软件，如套件共享目录）。
    /// </summary>
    private static bool ProductsConflict(ProcessSnapshot a, ProcessSnapshot b)
    {
        return ProcessNameRules.IsValidProductName(a.ProductName, ProcessNameRules.GetFileName(a.ExecutablePath))
               && ProcessNameRules.IsValidProductName(b.ProductName, ProcessNameRules.GetFileName(b.ExecutablePath))
               && !string.Equals(a.ProductName, b.ProductName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 相同有效产品名 + 相同公司名 → Medium。
    /// 产品名单独相同仅 Low 不足以合并；公司名单独相同永远不足以合并。
    /// 歧义进程名不参与。
    /// </summary>
    private static void MergeSameProductAndCompany(
        List<ProcessNode> nodes,
        Dictionary<int, int> nodeIndexByPid,
        UnionFind union)
    {
        foreach (IGrouping<(string Product, string Company), ProcessNode> group in nodes
                     .Where(n => ProcessNameRules.IsValidProductName(n.Process.ProductName,
                                 ProcessNameRules.GetFileName(n.Process.ExecutablePath))
                                 && !string.IsNullOrWhiteSpace(n.Process.CompanyName)
                                 && !ProcessNameRules.IsAmbiguousProcessName(n.Process.Name))
                     .GroupBy(n => (n.Process.ProductName!.Trim().ToLowerInvariant(),
                                    n.Process.CompanyName!.Trim().ToLowerInvariant())))
        {
            ChainUnion(group, nodeIndexByPid, union, GroupingConfidence.Medium,
                GroupingReason.SameProduct | GroupingReason.SameCompany);
        }
    }

    /// <summary>把同组节点链式合并（第 i 个与第 i-1 个合并），保持 O(n)。</summary>
    private static void ChainUnion(
        IEnumerable<ProcessNode> groupNodes,
        Dictionary<int, int> nodeIndexByPid,
        UnionFind union,
        GroupingConfidence confidence,
        GroupingReason reason)
    {
        int? previous = null;
        foreach (ProcessNode node in groupNodes)
        {
            int index = nodeIndexByPid[node.ProcessId];
            if (previous is { } prev)
            {
                union.Union(prev, index, confidence, reason);
            }

            previous = index;
        }
    }

    // ================= 组装 =================

    private static IReadOnlyList<ApplicationGroup> AssembleGroups(
        ProcessSnapshotCollection snapshot,
        ProcessTree tree,
        List<ProcessNode> nodes,
        Dictionary<int, int> nodeIndexByPid,
        UnionFind union)
    {
        var rootToMembers = new Dictionary<int, List<ProcessNode>>();
        for (int i = 0; i < nodes.Count; i++)
        {
            int root = union.Find(i);
            if (!rootToMembers.TryGetValue(root, out List<ProcessNode>? members))
            {
                members = new List<ProcessNode>();
                rootToMembers[root] = members;
            }

            members.Add(nodes[i]);
        }

        var pidToGroupRoot = new Dictionary<int, int>(nodes.Count);
        foreach ((int root, List<ProcessNode> members) in rootToMembers)
        {
            foreach (ProcessNode member in members)
            {
                pidToGroupRoot[member.ProcessId] = root;
            }
        }

        var groups = new List<ApplicationGroup>(rootToMembers.Count);
        foreach ((int root, List<ProcessNode> members) in rootToMembers)
        {
            List<ProcessSnapshot> processes = members
                .Select(n => n.Process)
                .OrderBy(p => p.ProcessId)
                .ToList();

            // 组内根：树上无父，或父不在这个组里（父被拆开的场景）
            List<ProcessSnapshot> rootProcesses = members
                .Where(n => n.Parent is null
                            || pidToGroupRoot[n.Parent.ProcessId] != root)
                .Select(n => n.Process)
                .OrderBy(p => p.ProcessId)
                .ToList();

            GroupingConfidence confidence = union.GetConfidence(root);
            GroupingReason reasons = union.GetReasons(root);

            // 组级附加依据：组内全部进程公司名一致且非空 → 记录 SameCompany（仅解释用，不影响合并）
            if (members.Count > 1
                && members.All(m => !string.IsNullOrWhiteSpace(m.Process.CompanyName))
                && members.Select(m => m.Process.CompanyName!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                reasons |= GroupingReason.SameCompany;
            }

            groups.Add(new ApplicationGroup
            {
                Identity = BuildIdentity(processes, rootProcesses),
                Processes = processes,
                RootProcesses = rootProcesses,
                Confidence = confidence,
                Reasons = reasons,
            });
        }

        return groups
            .OrderBy(g => g.Processes[0].ProcessId)
            .ToList();
    }

    /// <summary>构建应用身份：显示名按 ProductName → FileDescription → 根进程名回退。</summary>
    private static ApplicationIdentity BuildIdentity(
        IReadOnlyList<ProcessSnapshot> processes,
        IReadOnlyList<ProcessSnapshot> rootProcesses)
    {
        ProcessSnapshot primary = rootProcesses.Count > 0 ? rootProcesses[0] : processes[0];

        // 产品名：组内多数一致的有效值
        string? productName = MajorityValue(
            processes,
            p => ProcessNameRules.IsValidProductName(p.ProductName, ProcessNameRules.GetFileName(p.ExecutablePath))
                ? p.ProductName!.Trim()
                : null);

        string? company = MajorityValue(processes, p => p.CompanyName);

        // 显示名：ProductName → 根进程 FileDescription → 根进程名（去 .exe）
        string displayName;
        if (productName is not null)
        {
            displayName = productName;
        }
        else if (ProcessNameRules.IsValidProductName(primary.FileDescription, ProcessNameRules.GetFileName(primary.ExecutablePath)))
        {
            displayName = primary.FileDescription!.Trim();
        }
        else if (!string.Equals(primary.Name, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            displayName = primary.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? primary.Name[..^4]
                : primary.Name;
        }
        else
        {
            displayName = "Unknown";
        }

        // 主 exe：全部根进程路径一致且非空时才确定，否则 null（宁缺毋错）
        string? mainExecutable = null;
        string?[] rootPaths = rootProcesses
            .Select(p => p.ExecutablePath)
            .ToArray();
        if (rootPaths.Length > 0
            && rootPaths.All(p => !string.IsNullOrWhiteSpace(p))
            && rootPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            mainExecutable = rootPaths[0];
        }

        // 安装目录：组内多数一致的具体目录
        string? installDirectory = MajorityValue(
            processes,
            p => InstallDirectoryRules.GetApplicationDirectory(p.ExecutablePath));

        return new ApplicationIdentity
        {
            DisplayName = displayName,
            MainExecutable = mainExecutable,
            InstallDirectory = installDirectory,
            ProductName = productName,
            CompanyName = company,
        };
    }

    /// <summary>取集合中出现次数最多的非空值；平票取字典序最小（保证稳定）；无非空值返回 null。</summary>
    private static string? MajorityValue(IEnumerable<ProcessSnapshot> processes, Func<ProcessSnapshot, string?> selector)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessSnapshot process in processes)
        {
            string? value = selector(process);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string key = value.Trim();
            counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        if (counts.Count == 0)
        {
            return null;
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .First().Key;
    }

    /// <summary>带组元数据（最高可信度 / 依据并集）的并查集。</summary>
    private sealed class UnionFind
    {
        private readonly int[] _parent;

        private readonly GroupingConfidence[] _confidence;
        private readonly GroupingReason[] _reasons;

        public UnionFind(int size)
        {
            _parent = new int[size];
            _confidence = new GroupingConfidence[size];
            _reasons = new GroupingReason[size];
            for (int i = 0; i < size; i++)
            {
                _parent[i] = i;
            }
        }

        public int Find(int i)
        {
            while (_parent[i] != i)
            {
                _parent[i] = _parent[_parent[i]]; // 路径减半
                i = _parent[i];
            }

            return i;
        }

        public void Union(int a, int b, GroupingConfidence confidence, GroupingReason reasons)
        {
            if (confidence < MergeThreshold)
            {
                return; // 未达阈值的关系绝不合并（宁拆不合）
            }

            int rootA = Find(a);
            int rootB = Find(b);
            if (rootA == rootB)
            {
                _confidence[rootA] = Max(_confidence[rootA], confidence);
                _reasons[rootA] |= reasons;
                return;
            }

            // 按小树挂大树，合并组元数据
            if (rootB < rootA)
            {
                (rootA, rootB) = (rootB, rootA);
            }

            _parent[rootB] = rootA;
            _confidence[rootA] = Max(_confidence[rootA], Max(_confidence[rootB], confidence));
            _reasons[rootA] |= _reasons[rootB] | reasons;
        }

        public GroupingConfidence GetConfidence(int root) => _confidence[root];

        public GroupingReason GetReasons(int root) => _reasons[root];

        private static GroupingConfidence Max(GroupingConfidence a, GroupingConfidence b)
            => a >= b ? a : b;
    }
}
