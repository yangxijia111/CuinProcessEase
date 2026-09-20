using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Windows.Resources;
using CuinProcessEase.Windows.Safety;
using CuinProcessEase.Windows.Services;
using Xunit;

namespace CuinProcessEase.Windows.Tests;

/// <summary>
/// 资源采样与缓存安全服务测试（真实环境）：以 testhost 自身为目标，只读不写。
/// </summary>
public sealed class ResourceSamplingTests
{
    private static async Task<ProcessSnapshot> GetSelfSnapshotAsync()
    {
        var snapshot = await new ProcessSnapshotService().CaptureAsync();
        return snapshot.Processes.Single(p => p.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public async Task 进程采样器_首次采样CPU为null_第二次起得到百分比()
    {
        var sampler = new ProcessResourceSampler();
        var snapshotService = new ProcessSnapshotService();

        var firstSnapshot = await snapshotService.CaptureAsync();
        var first = sampler.Sample(firstSnapshot);
        var selfFirst = first[Environment.ProcessId];

        // 首次采样：CPU 必须未知（"--"），绝不伪造；内存来自快照
        Assert.Null(selfFirst.CpuPercent);

        await Task.Delay(200); // 留出采样间隔

        var secondSnapshot = await snapshotService.CaptureAsync();
        var second = sampler.Sample(secondSnapshot);
        var selfSecond = second[Environment.ProcessId];

        // 第二次起：CPU 为 0-100 的确定值（testhost 自身可打开句柄，不允许 null）
        Assert.NotNull(selfSecond.CpuPercent);
        Assert.InRange(selfSecond.CpuPercent!.Value, 0.0, 100.0);

        // 快照里全部进程都有采样条目（含 CPU 未知的）
        Assert.Equal(secondSnapshot.Count, second.Count);
    }

    [Fact]
    public async Task 进程采样器_内存来自快照工作集()
    {
        var sampler = new ProcessResourceSampler();
        var snapshotService = new ProcessSnapshotService();

        var firstSnapshot = await snapshotService.CaptureAsync();
        sampler.Sample(firstSnapshot);

        await Task.Delay(150);

        var secondSnapshot = await snapshotService.CaptureAsync();
        var second = sampler.Sample(secondSnapshot);

        ProcessSnapshot self = secondSnapshot.Processes.Single(p => p.ProcessId == Environment.ProcessId);
        Assert.Equal(self.WorkingSetBytes, second[Environment.ProcessId].WorkingSetBytes);
    }

    [Fact]
    public void 系统监控_内存百分比与总量_首次即有效()
    {
        var monitor = new SystemResourceMonitor();

        var first = monitor.Sample();

        Assert.InRange(first.MemoryPercent, 0.0, 100.0);
        Assert.True(first.TotalPhysicalBytes > 0);
        Assert.True(first.AvailablePhysicalBytes < first.TotalPhysicalBytes);

        // CPU 首次采样为 null（需要差分）
        Assert.Null(first.CpuPercent);
    }

    [Fact]
    public async Task 系统监控_CPU第二次起_0到100()
    {
        var monitor = new SystemResourceMonitor();
        monitor.Sample();

        await Task.Delay(200);

        var second = monitor.Sample();

        Assert.NotNull(second.CpuPercent);
        Assert.InRange(second.CpuPercent!.Value, 0.0, 100.0);
    }

    [Fact]
    public async Task 缓存安全服务_同一进程身份_复用同一结果实例()
    {
        var cached = new CachedProcessSafetyService(new ProcessSafetyService());
        ProcessSnapshot self = await GetSelfSnapshotAsync();

        ProcessSafetyResult first = cached.Assess(self);
        ProcessSafetyResult second = cached.Assess(self);

        Assert.Same(first, second);
        Assert.Equal(1, cached.Cache.Count);
        Assert.Equal(SafetyDecision.Blocked, first.Decision); // Self=Blocked
    }

    [Fact]
    public async Task 缓存安全服务_RetainLive_清除已退出身份()
    {
        var cached = new CachedProcessSafetyService(new ProcessSafetyService());
        ProcessSnapshot self = await GetSelfSnapshotAsync();
        cached.Assess(self);
        Assert.Equal(1, cached.Cache.Count);

        // 假装系统里只剩一个不可能的 PID：自身条目被清除
        var ghost = new ProcessIdentity(int.MaxValue, self.StartTimeUtc);
        cached.RetainLive([ghost]);

        Assert.Equal(0, cached.Cache.Count);
    }

    // ================= CPU 采样身份：PID + CreationTime（P5.1） =================

    [Fact]
    public void CPU采样_同一进程两次采样_CreationTime相同_输出百分比()
    {
        double? cpu = ProcessResourceSampler.ComputeCpuDeltaOrNull(
            previousCreationTime: 133_000_000_000_000_000,
            currentCreationTime: 133_000_000_000_000_000,
            previousTotalTicks: 1_000_000,
            currentTotalTicks: 2_000_000,
            previousWallTicks: 0,
            currentWallTicks: 10_000_000,
            coreCount: 4);

        Assert.NotNull(cpu);
        Assert.Equal(2.5, cpu);
    }

    [Fact]
    public void CPU采样_PID相同但CreationTime不同_视为新进程_CPU为null()
    {
        // PID 重用场景：绝不把复用 PID 的旧历史用在新进程上
        double? cpu = ProcessResourceSampler.ComputeCpuDeltaOrNull(
            previousCreationTime: 133_000_000_000_000_000,
            currentCreationTime: 133_500_000_000_000_000,
            previousTotalTicks: 1_000_000,
            currentTotalTicks: 2_000_000,
            previousWallTicks: 0,
            currentWallTicks: 10_000_000,
            coreCount: 4);

        Assert.Null(cpu);
    }

    [Fact]
    public void CPU采样_CreationTime不同时_即使墙钟非法也返回null而非异常()
    {
        double? cpu = ProcessResourceSampler.ComputeCpuDeltaOrNull(
            previousCreationTime: 100,
            currentCreationTime: 200,
            previousTotalTicks: 0,
            currentTotalTicks: 0,
            previousWallTicks: 0,
            currentWallTicks: 0,
            coreCount: 0);

        Assert.Null(cpu);
    }
}
