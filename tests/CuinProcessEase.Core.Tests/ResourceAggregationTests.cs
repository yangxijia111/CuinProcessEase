using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Resources;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// 资源采样纯逻辑测试：CPU 百分比计算（多核归一化）与应用组聚合。
/// </summary>
public sealed class ResourceAggregationTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ================= CPU 百分比 =================

    [Fact]
    public void CPU计算_单核打满_为100()
    {
        // 进程用时与墙钟时间完全相等（tick 单位一致）
        double? percent = CpuUsageCalculator.ComputePercent(0, 10_000_000, 0, 10_000_000, coreCount: 1);
        Assert.Equal(100.0, percent);
    }

    [Fact]
    public void CPU计算_多核归一化_占整机百分比()
    {
        // 4 核机器 1 秒墙钟内某进程用掉 0.5 核 → 占整机 12.5%
        double? percent = CpuUsageCalculator.ComputePercent(0, 5_000_000, 0, 10_000_000, coreCount: 4);
        Assert.Equal(12.5, percent);
    }

    [Fact]
    public void CPU计算_部分使用_按比例输出()
    {
        // 1 核机器：1 秒墙钟内用 0.1 秒 → 10%
        double? percent = CpuUsageCalculator.ComputePercent(1_000_000, 2_000_000, 0, 10_000_000, coreCount: 1);
        Assert.Equal(10.0, percent);
    }

    [Fact]
    public void CPU计算_零增量_为0而非null()
    {
        double? percent = CpuUsageCalculator.ComputePercent(1_000, 1_000, 0, 10_000_000, coreCount: 4);
        Assert.Equal(0.0, percent);
    }

    [Theory]
    [InlineData(0, 10_000_000, 0, 10_000_000, 0)]     // 核心数非法
    [InlineData(0, 10_000_000, 0, 0, 4)]              // 墙钟未前进
    [InlineData(10_000_000, 0, 0, 10_000_000, 4)]     // 进程时间倒退（PID 重用等异常）
    public void CPU计算_非法输入_返回null绝不伪造(int prevProc, int currProc, int prevWall, int currWall, int cores)
    {
        Assert.Null(CpuUsageCalculator.ComputePercent(prevProc, currProc, prevWall, currWall, cores));
    }

    [Fact]
    public void CPU计算_超出100时收敛_防测量抖动输出爆表值()
    {
        // 单核机器上进程时间增量超过墙钟（时钟源误差）→ 钳制到 100
        double? percent = CpuUsageCalculator.ComputePercent(0, 20_000_000, 0, 10_000_000, coreCount: 1);
        Assert.Equal(100.0, percent);
    }

    // ================= 应用组聚合 =================

    private static ProcessSnapshot Snap(int pid, string name) => new()
    {
        Identity = new ProcessIdentity(pid, BaseTime),
        Name = name,
    };

    private static ApplicationGroup Group(params ProcessSnapshot[] processes) => new()
    {
        Identity = new ApplicationIdentity { DisplayName = "App" },
        Processes = processes,
        RootProcesses = processes.Take(1).ToList(),
    };

    [Fact]
    public void 聚合_组CPU与内存为成员总和()
    {
        var group = Group(Snap(1, "a.exe"), Snap(2, "b.exe"), Snap(3, "c.exe"));
        var samples = new Dictionary<int, ProcessResourceSample>
        {
            [1] = new(4.0, 700L * 1024 * 1024),
            [2] = new(0.2, 500L * 1024 * 1024),
            [3] = new(0.0, 16L * 1024 * 1024),
        };

        var result = ApplicationResourceAggregator.Aggregate(group, samples);

        Assert.Equal(4.2, result.CpuPercent);
        Assert.Equal(1216L * 1024 * 1024, result.MemoryBytes);
    }

    [Fact]
    public void 聚合_部分成员未知_已知成员求和()
    {
        // 权限不足拿不到某成员的 CPU：不让整组变 "--"，也不伪造缺失成员的值
        var group = Group(Snap(1, "a.exe"), Snap(2, "b.exe"));
        var samples = new Dictionary<int, ProcessResourceSample>
        {
            [1] = new(2.5, 100L * 1024 * 1024),
            [2] = ProcessResourceSample.Unknown,
        };

        var result = ApplicationResourceAggregator.Aggregate(group, samples);

        Assert.Equal(2.5, result.CpuPercent);
        Assert.Equal(100L * 1024 * 1024, result.MemoryBytes);
    }

    [Fact]
    public void 聚合_全部成员未知_输出null()
    {
        var group = Group(Snap(1, "a.exe"), Snap(2, "b.exe"));
        var samples = new Dictionary<int, ProcessResourceSample>
        {
            [1] = new(null, null),
            [2] = new(null, null),
        };

        var result = ApplicationResourceAggregator.Aggregate(group, samples);

        Assert.Null(result.CpuPercent);
        Assert.Null(result.MemoryBytes);
    }

    [Fact]
    public void 聚合_快照里没有的PID_直接跳过()
    {
        var group = Group(Snap(1, "a.exe"));

        var result = ApplicationResourceAggregator.Aggregate(group, new Dictionary<int, ProcessResourceSample>());

        Assert.Null(result.CpuPercent);
        Assert.Null(result.MemoryBytes);
    }

    [Fact]
    public void 聚合_CPU已知但内存全部未知_两字段互不影响()
    {
        var group = Group(Snap(1, "a.exe"));
        var samples = new Dictionary<int, ProcessResourceSample> { [1] = new(3.0, null) };

        var result = ApplicationResourceAggregator.Aggregate(group, samples);

        Assert.Equal(3.0, result.CpuPercent);
        Assert.Null(result.MemoryBytes);
    }
}
