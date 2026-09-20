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
        var relations = new List<Relation>(nodes.Count * 2);

        // ---- 阶段 1：树边关系（Verified 强依据 / Unverified 仅辅助） ----
        foreach (ProcessNode node in nodes)
        {
            if (node.Parent is null)
            {
                continue;
            }

            GenerateTreeEdgeRelation(node, nodeIndexByPid, relations);
        }

        // ---- 阶段 2：相同 exe 完整路径（同一软件多实例） ----
        CollectSameExecutablePaths(nodes, nodeIndexByPid, relations);

        // ---- 阶段 3：相同的具体安装目录 ----
        CollectSameInstallDirectories(nodes, nodeIndexByPid, relations);

        // ---- 阶段 4：相同有效产品名 + 相同公司名 ----
        CollectSameProductAndCompany(nodes, nodeIndexByPid, relations);

        // ---- 阶段 5：应用关系（最大生成树语义：按置信度降序应用）。
        // 强桥优先生效；弱边仅在连接不同 component（成为必要桥）时才降低组置信度，
        // 因此组的 Confidence 与关系应用顺序无关 ----
        relations.Sort((x, y) => y.Confidence.CompareTo(x.Confidence));
        foreach (Relation relation in relations)
        {
            union.Union(relation.A, relation.B, relation.Confidence, relation.Reasons);
        }

        // ---- 阶段 6：组装应用组 ----
        return AssembleGroups(snapshot, tree, nodes, nodeIndexByPid, union);
    }

    /// <summary>一条候选合并关系。</summary>
    private readonly record struct Relation(int A, int B, GroupingConfidence Confidence, GroupingReason Reasons);

    // ================= 关系规则（独立、可测） =================

    /// <summary>
    /// 规则 A/B：进程树父子边。真实父子关系不能等价为同一软件，
    /// 任何树边（含 Verified）都必须有附加证据才允许合并：
    /// - 附加证据 = SameExecutable / SameInstallDirectory / SameProduct+SameCompany；
    /// - Verified 边 + 强证据（同 exe / 同目录 / 同产品公司）→ High；
    /// - Unverified 边 + 证据，或含歧义进程名（runtime/宿主/WebView2 等）→ 恒 Medium；
    /// - 无任何证据（包括仅公司相同）→ 不生成关系（宁拆不合）。
    ///   CompanyName 单独永远不能触发合并，即使父子关系已 Verified；
    ///   公司名只作为已有强证据的补充 Reasons 与显示解释信息。
    /// </summary>
    private static void GenerateTreeEdgeRelation(
        ProcessNode child,
        Dictionary<int, int> nodeIndexByPid,
        List<Relation> relations)
    {
        ProcessNode parent = child.Parent!;
        ProcessSnapshot parentProcess = parent.Process;
        ProcessSnapshot childProcess = child.Process;

        bool verifiedEdge = child.ParentRelation == ParentRelationConfidence.Verified;
        bool ambiguous = ProcessNameRules.IsAmbiguousProcessName(parentProcess.Name)
                         || ProcessNameRules.IsAmbiguousProcessName(childProcess.Name);

        GroupingReason evidenceReasons = GetCommonEvidence(parentProcess, childProcess);

        if (evidenceReasons == GroupingReason.None)
        {
            return; // 证据不足（含仅公司相同的情形），宁可拆开
        }

        GroupingReason edgeReason = verifiedEdge
            ? GroupingReason.VerifiedParentChild
            : GroupingReason.UnverifiedParentChild;

        // Verified 边 + 强证据（非歧义名）→ High；其余（Unverified 或歧义名）恒 Medium
        GroupingConfidence confidence = verifiedEdge && !ambiguous
            ? GroupingConfidence.High
            : GroupingConfidence.Medium;

        relations.Add(new Relation(
            nodeIndexByPid[parent.ProcessId],
            nodeIndexByPid[child.ProcessId],
            confidence,
            edgeReason | evidenceReasons));
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
    private static void CollectSameExecutablePaths(
        List<ProcessNode> nodes,
        Dictionary<int, int> nodeIndexByPid,
        List<Relation> relations)
    {
        foreach (IGrouping<string, ProcessNode> group in nodes
                     .Where(n => !string.IsNullOrWhiteSpace(n.Process.ExecutablePath))
                     .Where(n => !ProcessNameRules.IsAmbiguousProcessName(n.Process.Name))
                     .GroupBy(n => n.Process.ExecutablePath!, StringComparer.OrdinalIgnoreCase))
        {
            ChainCollect(group, nodeIndexByPid, relations, GroupingConfidence.High, GroupingReason.SameExecutable);
        }
    }

    /// <summary>
    /// 相同的具体安装目录（黑名单目录不算）→ Medium。
    /// 歧义进程名同样不参与。
    /// </summary>
    private static void CollectSameInstallDirectories(
        List<ProcessNode> nodes,
        Dictionary<int, int> nodeIndexByPid,
        List<Relation> relations)
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

            ChainCollect(members, nodeIndexByPid, relations, GroupingConfidence.Medium, GroupingReason.SameInstallDirectory);
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
    private static void CollectSameProductAndCompany(
        List<ProcessNode> nodes,
        Dictionary<int, int> nodeIndexByPid,
        List<Relation> relations)
    {
        foreach (IGrouping<(string Product, string Company), ProcessNode> group in nodes
                     .Where(n => ProcessNameRules.IsValidProductName(n.Process.ProductName,
                                 ProcessNameRules.GetFileName(n.Process.ExecutablePath))
                                 && !string.IsNullOrWhiteSpace(n.Process.CompanyName)
                                 && !ProcessNameRules.IsAmbiguousProcessName(n.Process.Name))
                     .GroupBy(n => (n.Process.ProductName!.Trim().ToLowerInvariant(),
                                    n.Process.CompanyName!.Trim().ToLowerInvariant())))
        {
            ChainCollect(group, nodeIndexByPid, relations, GroupingConfidence.Medium,
                GroupingReason.SameProduct | GroupingReason.SameCompany);
        }
    }

    /// <summary>把同组节点链式收集为相邻关系（第 i 个与第 i-1 个），保持 O(n)。</summary>
    private static void ChainCollect(
        IEnumerable<ProcessNode> groupNodes,
        Dictionary<int, int> nodeIndexByPid,
        List<Relation> relations,
        GroupingConfidence confidence,
        GroupingReason reason)
    {
        int? previous = null;
        foreach (ProcessNode node in groupNodes)
        {
            int index = nodeIndexByPid[node.ProcessId];
            if (previous is { } prev)
            {
                relations.Add(new Relation(prev, index, confidence, reason));
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

    /// <summary>
    /// 构建应用身份：以 Root 为中心，避免大量 helper 的元数据覆盖宿主真实身份。
    /// DisplayName 优先级：Root ProductName → Root FileDescription → Root 进程名
    /// → 组内多数 ProductName → Unknown。
    /// InstallDirectory 优先取 primary Root / MainExecutable 所在目录。
    /// </summary>
    private static ApplicationIdentity BuildIdentity(
        IReadOnlyList<ProcessSnapshot> processes,
        IReadOnlyList<ProcessSnapshot> rootProcesses)
    {
        ProcessSnapshot primary = rootProcesses.Count > 0 ? rootProcesses[0] : processes[0];

        // ---- Root 优先的身份解析（多 Root 时取第一个可用的 Root 身份） ----
        string? rootProductName = null;
        string? rootFileDescription = null;
        foreach (ProcessSnapshot root in rootProcesses.Count > 0 ? rootProcesses : new[] { primary })
        {
            string? exeName = ProcessNameRules.GetFileName(root.ExecutablePath);
            if (rootProductName is null
                && ProcessNameRules.IsValidProductName(root.ProductName, exeName))
            {
                rootProductName = root.ProductName!.Trim();
            }

            if (rootFileDescription is null
                && ProcessNameRules.IsValidProductName(root.FileDescription, exeName))
            {
                rootFileDescription = root.FileDescription!.Trim();
            }
        }

        // 组内多数产品名（仅作 Root 无身份时的第 4 级 fallback）
        string? groupProductName = MajorityValue(
            processes,
            p => ProcessNameRules.IsValidProductName(p.ProductName, ProcessNameRules.GetFileName(p.ExecutablePath))
                ? p.ProductName!.Trim()
                : null);

        string displayName =
            rootProductName
            ?? rootFileDescription
            ?? (string.Equals(primary.Name, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? groupProductName
                : primary.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? primary.Name[..^4]
                    : primary.Name)
            ?? groupProductName
            ?? "Unknown";

        string? productName = rootProductName ?? groupProductName;
        string? company = rootProcesses.Count > 0 && !string.IsNullOrWhiteSpace(primary.CompanyName)
            ? primary.CompanyName.Trim()
            : MajorityValue(processes, p => p.CompanyName);

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

        // 安装目录：直接取 primary Root（或 MainExecutable）所在目录，
        // 绝不让大量 helper 的目录以多数票覆盖宿主目录；Root 无路径时为 null
        string? installDirectory = InstallDirectoryRules.GetApplicationDirectory(
            mainExecutable ?? primary.ExecutablePath);

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

    /// <summary>
    /// 带组元数据的并查集。组 Confidence 语义为"把整个组连接起来的最弱有效合并关系"
    ///（生成树最弱边）：A-B(High) + B-C(Medium) → 组为 Medium。
    /// Unknown（单节点、尚无任何关系）不参与降低；同 component 内的额外弱边只是冗余
    /// 路径（不改变生成树），只累计 Reasons、不降级已有 Confidence。
    /// </summary>
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
                // 冗余边（已成环）：只累计依据，不降低已有组置信度
                _reasons[rootA] |= reasons;
                return;
            }

            // 按小树挂大树，合并组元数据
            if (rootB < rootA)
            {
                (rootA, rootB) = (rootB, rootA);
            }

            _parent[rootB] = rootA;
            _confidence[rootA] = WeakestBridge(_confidence[rootA], _confidence[rootB], confidence);
            _reasons[rootA] |= _reasons[rootB] | reasons;
        }

        public GroupingConfidence GetConfidence(int root) => _confidence[root];

        public GroupingReason GetReasons(int root) => _reasons[root];

        /// <summary>
        /// 新 component 的最弱桥 = min(两侧既有有效置信度, 本次边)。
        /// Unknown（单节点）视为"无既有关系"，不参与降低。
        /// </summary>
        private static GroupingConfidence WeakestBridge(
            GroupingConfidence left,
            GroupingConfidence right,
            GroupingConfidence bridge)
        {
            GroupingConfidence result = bridge;
            if (left != GroupingConfidence.Unknown && left < result)
            {
                result = left;
            }

            if (right != GroupingConfidence.Unknown && right < result)
            {
                result = right;
            }

            return result;
        }
    }
}
