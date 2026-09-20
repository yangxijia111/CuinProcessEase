using CuinProcessEase.Core.Gui;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// 排序测试：名称排序稳定、CPU/内存/进程数降序、未知值排最后、并列完全确定。
/// </summary>
public sealed class ApplicationListSorterTests
{
    private static ApplicationSortKey Key(
        string name,
        double? cpu = null,
        long? memory = null,
        int count = 1,
        string? stableKey = null) => new(name, cpu, memory, count, stableKey ?? name);

    [Fact]
    public void 排序_名称_不区分大小写升序且完全确定()
    {
        var comparison = ApplicationListSorter.GetComparison(ApplicationSortMode.Name);
        var keys = new List<ApplicationSortKey>
        {
            Key("zcore", stableKey: "exe:z"),
            Key("Alpha", stableKey: "exe:a"),
            Key("alpha", stableKey: "exe:b"), // 与 Alpha 同名：靠 StableKey 决胜
            Key("beta", stableKey: "exe:c"),
        };

        keys.Sort(comparison);

        Assert.Equal(["Alpha", "alpha", "beta", "zcore"], keys.Select(k => k.DisplayName).ToList());
    }

    [Fact]
    public void 排序_名称_多次排序结果一致_刷新间不跳动()
    {
        var comparison = ApplicationListSorter.GetComparison(ApplicationSortMode.Name);
        var keys = new List<ApplicationSortKey>
        {
            Key("c"), Key("a"), Key("b"),
        };

        var first = new List<ApplicationSortKey>(keys);
        first.Sort(comparison);
        var second = new List<ApplicationSortKey>(keys);
        second.Sort(comparison);

        Assert.Equal(first, second);
    }

    [Fact]
    public void 排序_CPU_降序且未知值排最后()
    {
        var comparison = ApplicationListSorter.GetComparison(ApplicationSortMode.Cpu);
        var keys = new List<ApplicationSortKey>
        {
            Key("idle", cpu: null),          // 未知 → 最后
            Key("low", cpu: 0.5),
            Key("high", cpu: 12.0),
            Key("mid", cpu: 4.2),
        };

        keys.Sort(comparison);

        Assert.Equal(["high", "mid", "low", "idle"], keys.Select(k => k.DisplayName).ToList());
    }

    [Fact]
    public void 排序_内存_降序且未知值排最后()
    {
        var comparison = ApplicationListSorter.GetComparison(ApplicationSortMode.Memory);
        var keys = new List<ApplicationSortKey>
        {
            Key("small", memory: 100L * 1024 * 1024),
            Key("big", memory: 2L * 1024 * 1024 * 1024),
            Key("unknown", memory: null),
        };

        keys.Sort(comparison);

        Assert.Equal(["big", "small", "unknown"], keys.Select(k => k.DisplayName).ToList());
    }

    [Fact]
    public void 排序_进程数量_降序()
    {
        var comparison = ApplicationListSorter.GetComparison(ApplicationSortMode.ProcessCount);
        var keys = new List<ApplicationSortKey>
        {
            Key("single", count: 1),
            Key("many", count: 16),
            Key("some", count: 5),
        };

        keys.Sort(comparison);

        Assert.Equal(["many", "some", "single"], keys.Select(k => k.DisplayName).ToList());
    }

    [Fact]
    public void 排序_数值并列时回退名称_避免每秒抖动()
    {
        var comparison = ApplicationListSorter.GetComparison(ApplicationSortMode.Cpu);
        var a = Key("aaa", cpu: 5.0, stableKey: "exe:a");
        var b = Key("bbb", cpu: 5.0, stableKey: "exe:b");

        Assert.True(comparison(a, b) < 0);
        Assert.True(comparison(b, a) > 0);
        Assert.Equal(0, comparison(a, Key("aaa", cpu: 5.0, stableKey: "exe:a")));
    }
}
