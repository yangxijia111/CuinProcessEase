using CuinProcessEase.Core.Resources;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// Phase 8 资源历史：固定容量环形缓冲（内存恒定）与 Store 的清理语义。
/// </summary>
public sealed class ResourceHistoryTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 环形缓冲_未满时按序输出()
    {
        var ring = new ResourceHistoryRing(capacity: 5);
        for (int i = 0; i < 3; i++)
        {
            ring.Append(new ResourceSample(Base.AddSeconds(i), i, 100L * i));
        }

        Assert.Equal(3, ring.Count);
        IReadOnlyList<ResourceSample> ordered = ring.ToListTimeOrdered();
        Assert.Equal(3, ordered.Count);
        Assert.Equal(0, ordered[0].CpuPercent);
        Assert.Equal(2, ordered[2].CpuPercent);
        Assert.Equal(200, ordered[2].MemoryBytes);
    }

    [Fact]
    public void 环形缓冲_满后覆盖最旧_容量恒定()
    {
        var ring = new ResourceHistoryRing(capacity: 4);
        for (int i = 0; i < 10; i++)
        {
            ring.Append(new ResourceSample(Base.AddSeconds(i), i, null));
        }

        // Phase 8 验收核心：任何追加次数下样本数恒 ≤ 容量（内存不无限增长）
        Assert.Equal(4, ring.Count);
        IReadOnlyList<ResourceSample> ordered = ring.ToListTimeOrdered();
        Assert.Equal([6.0, 7.0, 8.0, 9.0], ordered.Select(s => s.CpuPercent!.Value).ToArray());
        // 最新样本 = 最后追加的
        Assert.Equal(9, ring.Latest().GetValueOrDefault().CpuPercent);
    }

    [Fact]
    public void 环形缓冲_峰值统计_忽略空样本()
    {
        var ring = new ResourceHistoryRing(capacity: 10);
        ring.Append(new ResourceSample(Base, 3.0, 100));
        ring.Append(new ResourceSample(Base.AddSeconds(1), null, null));
        ring.Append(new ResourceSample(Base.AddSeconds(2), 12.5, 900));
        ring.Append(new ResourceSample(Base.AddSeconds(3), 4.0, 500));

        Assert.Equal(12.5, ring.PeakCpuPercent());
        Assert.Equal(900, ring.PeakMemoryBytes());
    }

    [Fact]
    public void 环形缓冲_全空或无样本_峰值为null()
    {
        var empty = new ResourceHistoryRing();
        Assert.Null(empty.PeakCpuPercent());
        Assert.Null(empty.PeakMemoryBytes());
        Assert.Null(empty.Latest());

        var allNull = new ResourceHistoryRing();
        allNull.Append(new ResourceSample(Base, null, null));
        Assert.Null(allNull.PeakCpuPercent());
        Assert.Null(allNull.PeakMemoryBytes());
    }

    [Fact]
    public void 环形缓冲_非法容量_拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceHistoryRing(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceHistoryRing(-1));
    }

    [Fact]
    public void Store_应用首次出现自动创建_按StableKey隔离()
    {
        var store = new ResourceHistoryStore();
        store.Append("app-a", new ResourceSample(Base, 1.0, 10));
        store.Append("app-a", new ResourceSample(Base.AddSeconds(1), 2.0, 20));
        store.Append("app-b", new ResourceSample(Base, 9.0, 99));

        Assert.Equal(2, store.TrackedCount);
        Assert.Equal(2.0, store.TryGet("app-a")!.Latest().GetValueOrDefault().CpuPercent);
        Assert.Equal(9.0, store.TryGet("app-b")!.Latest().GetValueOrDefault().CpuPercent);
        Assert.Null(store.TryGet("app-missing"));
    }

    [Fact]
    public void Store_Retain清理消失行_长期运行不泄漏()
    {
        var store = new ResourceHistoryStore();
        store.Append("app-a", new ResourceSample(Base, 1.0, 10));
        store.Append("app-b", new ResourceSample(Base, 2.0, 20));
        store.Append("app-c", new ResourceSample(Base, 3.0, 30));

        store.Retain(new HashSet<string>(["app-b", "app-c"]));

        Assert.Equal(2, store.TrackedCount);
        Assert.Null(store.TryGet("app-a"));
        Assert.NotNull(store.TryGet("app-b"));
    }

    [Fact]
    public void Store_容量上限跨应用一致()
    {
        var store = new ResourceHistoryStore(capacity: 3);
        for (int i = 0; i < 100; i++)
        {
            store.Append("app-a", new ResourceSample(Base.AddSeconds(i), i, null));
        }

        Assert.Equal(3, store.TryGet("app-a")!.Count);
        Assert.Equal(99, store.TryGet("app-a")!.Latest().GetValueOrDefault().CpuPercent);
    }
}
