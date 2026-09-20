using System.Diagnostics;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Tree;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// ProcessTreeBuilder 单元测试：全部使用人工构造数据，不依赖真实 Windows 进程。
/// </summary>
public sealed class ProcessTreeBuilderTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>构造一条快照数据（仅填充树构建关心的字段）。</summary>
    private static ProcessSnapshot Snap(int pid, int? ppid, DateTime? startTimeUtc = null, string name = "test.exe")
        => new()
        {
            Identity = new ProcessIdentity(pid, startTimeUtc),
            ParentProcessId = ppid,
            Name = name,
        };

    private static ProcessSnapshotCollection Collection(params ProcessSnapshot[] processes)
        => new()
        {
            CapturedAtUtc = DateTime.UtcNow,
            Processes = processes,
        };

    // 1. 单 root
    [Fact]
    public void 单root_无PPID的进程构成唯一根()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(Snap(100, null, BaseTime)));

        ProcessNode root = Assert.Single(tree.Roots);
        Assert.Equal(100, root.ProcessId);
        Assert.Null(root.Parent);
        Assert.Equal(ParentRelationConfidence.None, root.ParentRelation);
        Assert.Empty(root.Children);
        Assert.Equal(1, tree.TotalCount);
    }

    // 2. Parent → Child
    [Fact]
    public void 父子连接_时间已知且父早于子_Verified()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(100, null, BaseTime),
            Snap(200, 100, BaseTime.AddSeconds(10))));

        ProcessNode root = Assert.Single(tree.Roots);
        Assert.Equal(100, root.ProcessId);

        ProcessNode child = Assert.Single(root.Children);
        Assert.Equal(200, child.ProcessId);
        Assert.Same(root, child.Parent);
        Assert.Equal(ParentRelationConfidence.Verified, child.ParentRelation);
    }

    [Fact]
    public void 父子连接_时间相等同样允许_Verified()
    {
        // 父子 StartTime 完全相等（精度内）仍视为可信
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(100, null, BaseTime),
            Snap(200, 100, BaseTime)));

        Assert.Single(tree.Roots);
        Assert.Single(tree.Roots[0].Children);
    }

    // 3. Parent → Child → Grandchild
    [Fact]
    public void 三代链_多层结构正确()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(100, null, BaseTime),
            Snap(200, 100, BaseTime.AddSeconds(1)),
            Snap(300, 200, BaseTime.AddSeconds(2))));

        ProcessNode root = Assert.Single(tree.Roots);
        ProcessNode child = Assert.Single(root.Children);
        ProcessNode grandchild = Assert.Single(child.Children);

        Assert.Equal(100, root.ProcessId);
        Assert.Equal(200, child.ProcessId);
        Assert.Equal(300, grandchild.ProcessId);
        Assert.Equal(3, tree.EnumerateAll().Count());
    }

    // 4. 多个独立 root
    [Fact]
    public void 多个独立root_按PID升序()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(500, null, BaseTime),
            Snap(100, null, BaseTime),
            Snap(300, null, BaseTime),
            Snap(310, 300, BaseTime.AddSeconds(1))));

        Assert.Equal(3, tree.Roots.Count);
        Assert.Equal([100, 300, 500], tree.Roots.Select(r => r.ProcessId));
        Assert.Equal(4, tree.EnumerateAll().Count());
    }

    // 5. Parent 不存在（父已退出 / PPID 不存在）
    [Fact]
    public void 父进程不存在_Child成为Orphan根()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(100, 9999, BaseTime)));

        ProcessNode orphan = Assert.Single(tree.Roots);
        Assert.Equal(100, orphan.ProcessId);
        Assert.Null(orphan.Parent);
        Assert.Equal(ParentRelationConfidence.None, orphan.ParentRelation);
    }

    // 6. 自引用 PPID
    [Fact]
    public void 自引用PPID_不连接_成为根()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(100, 100, BaseTime),
            Snap(200, null, BaseTime)));

        Assert.Equal(2, tree.Roots.Count);
        Assert.All(tree.Roots, r => Assert.Null(r.Parent));
    }

    // PID 0：自引用的空闲进程为根；指向 PID 0 的进程正常连接
    [Fact]
    public void PID零_自身为根_其他进程可挂载()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(0, 0, null, "[System Process]"),
            Snap(4, 0, null, "System")));

        Assert.Equal(2, tree.EnumerateAll().Count());

        ProcessNode idle = tree.Roots.Single(r => r.ProcessId == 0);
        Assert.Null(idle.Parent); // PID 0 的 PPID=0 是自引用，不连

        ProcessNode system = tree.Roots.SelectMany(r => r.Children).Single(c => c.ProcessId == 4);
        Assert.Same(idle, system.Parent);
        // 双方 StartTime 均为 null → 低可信度连接
        Assert.Equal(ParentRelationConfidence.Unverified, system.ParentRelation);
    }

    // 7. 人工循环数据：A ↔ B（相等 StartTime 可通过时间校验形成环）
    [Fact]
    public void 人工循环数据_检测并断开_两节点均可达()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(100, 200, BaseTime),
            Snap(200, 100, BaseTime)));

        // 环被打破：一个根 + 一个子节点，无死循环、无丢失
        Assert.Equal(2, tree.EnumerateAll().Count());
        Assert.Single(tree.Roots);

        ProcessNode root = tree.Roots[0];
        ProcessNode child = Assert.Single(root.Children);
        Assert.Same(root, child.Parent);
    }

    [Fact]
    public void 三节点循环_检测并断开()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(100, 300, BaseTime),
            Snap(200, 100, BaseTime),
            Snap(300, 200, BaseTime)));

        Assert.Equal(3, tree.EnumerateAll().Count());
        Assert.Single(tree.Roots);
    }

    // 8. Parent 时间晚于 Child → 不连接（PID 复用防护）
    [Fact]
    public void 父启动晚于子_PPID视为复用_不连接()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(100, null, BaseTime.AddMinutes(5)),   // "父"晚启动
            Snap(200, 100, BaseTime)));                  // "子"早启动

        // Child 保持 Root/Orphan，绝不错误连接
        ProcessNode child = tree.Roots.Single(r => r.ProcessId == 200);
        Assert.Null(child.Parent);
        Assert.Equal(ParentRelationConfidence.None, child.ParentRelation);

        ProcessNode parent = tree.Roots.Single(r => r.ProcessId == 100);
        Assert.Empty(parent.Children);
        Assert.Equal(2, tree.Roots.Count);
    }

    // 9. StartTime 为 null → 低可信度连接
    [Fact]
    public void 任一方StartTime为null_Unverified连接()
    {
        // 子时间未知
        ProcessTree childUnknown = ProcessTreeBuilder.Build(Collection(
            Snap(100, null, BaseTime),
            Snap(200, 100, null)));
        Assert.Equal(
            ParentRelationConfidence.Unverified,
            childUnknown.Roots[0].Children.Single().ParentRelation);

        // 父时间未知
        ProcessTree parentUnknown = ProcessTreeBuilder.Build(Collection(
            Snap(100, null, null),
            Snap(200, 100, BaseTime)));
        Assert.Equal(
            ParentRelationConfidence.Unverified,
            parentUnknown.Roots[0].Children.Single().ParentRelation);

        // 双方时间未知
        ProcessTree bothUnknown = ProcessTreeBuilder.Build(Collection(
            Snap(100, null, null),
            Snap(200, 100, null)));
        Assert.Equal(
            ParentRelationConfidence.Unverified,
            bothUnknown.Roots[0].Children.Single().ParentRelation);
    }

    // 10. 空 Snapshot
    [Fact]
    public void 空集合_返回空树()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection());

        Assert.Empty(tree.Roots);
        Assert.Equal(0, tree.TotalCount);
        Assert.Empty(tree.EnumerateAll());
    }

    // 11. 500+ 节点性能测试（500 层深链是最苛刻形态：遍历也须不爆栈）
    [Fact]
    public void 五百零一节点深链_构建耗时低于50毫秒()
    {
        const int count = 501;
        List<ProcessSnapshot> chain = Enumerable.Range(1, count)
            .Select(i => Snap(i, i == 1 ? null : i - 1, BaseTime.AddSeconds(i)))
            .ToList();

        ProcessSnapshotCollection snapshot = Collection(chain.ToArray());

        var stopwatch = Stopwatch.StartNew();
        ProcessTree tree = ProcessTreeBuilder.Build(snapshot);
        stopwatch.Stop();

        Assert.Equal(count, tree.TotalCount);
        Assert.Single(tree.Roots);
        Assert.Equal(count, tree.EnumerateAll().Count()); // 深链遍历不递归、不爆栈
        Assert.True(
            stopwatch.ElapsedMilliseconds < 50,
            $"构建 {count} 节点耗时 {stopwatch.ElapsedMilliseconds} ms，超过 50 ms 目标");
    }

    [Fact]
    public void 一千节点混合树_构建耗时低于50毫秒()
    {
        const int count = 1000;
        // 20 条独立链，每链 50 节点，时间全部已知（Verified）
        List<ProcessSnapshot> processes = Enumerable.Range(1, count)
            .Select(i => i % 50 == 1
                ? Snap(i, null, BaseTime.AddSeconds(i))
                : Snap(i, i - 1, BaseTime.AddSeconds(i)))
            .ToList();

        ProcessSnapshotCollection snapshot = Collection(processes.ToArray());

        var stopwatch = Stopwatch.StartNew();
        ProcessTree tree = ProcessTreeBuilder.Build(snapshot);
        stopwatch.Stop();

        Assert.Equal(count, tree.TotalCount);
        Assert.Equal(20, tree.Roots.Count);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 50,
            $"构建 {count} 节点耗时 {stopwatch.ElapsedMilliseconds} ms，超过 50 ms 目标");
    }

    // 12. 所有节点最终只出现一次，不重复、不丢失
    [Fact]
    public void 混合异常数据_所有节点恰好出现一次()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(100, null, BaseTime),                    // 正常 root
            Snap(200, 100, BaseTime.AddSeconds(1)),       // 正常 child
            Snap(300, 200, BaseTime.AddSeconds(2)),       // grandchild
            Snap(400, 9999, BaseTime),                    // orphan
            Snap(500, 500, BaseTime),                     // 自引用
            Snap(600, 100, BaseTime.AddSeconds(1)),       // 第二个 child
            Snap(700, 8888, null),                        // orphan（时间未知）
            Snap(800, 100, BaseTime.AddSeconds(9))));

        List<ProcessNode> all = tree.EnumerateAll().ToList();

        Assert.Equal(8, all.Count);                                            // 不丢失
        Assert.Equal(8, all.Select(n => n.ProcessId).Distinct().Count());      // 不重复
        Assert.Equal(tree.TotalCount, all.Count);

        // 双向一致性：children 中的节点其 Parent 必回指该父节点
        foreach (ProcessNode node in all)
        {
            foreach (ProcessNode child in node.Children)
            {
                Assert.Same(node, child.Parent);
            }
        }

        // 除根外的所有节点 Parent 均非空
        Assert.All(all.Where(n => !tree.Roots.Contains(n)), n => Assert.NotNull(n.Parent));
    }

    [Fact]
    public void 快照内重复PID_保留第一个_不产生重复节点()
    {
        ProcessTree tree = ProcessTreeBuilder.Build(Collection(
            Snap(100, null, BaseTime),
            Snap(100, 9999, BaseTime)));

        Assert.Single(tree.Roots);
        Assert.Equal(1, tree.TotalCount);
    }
}
