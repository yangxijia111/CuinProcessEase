using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Resources;

/// <summary>
/// 应用组资源聚合器（纯逻辑）：组内存 = Σ成员工作集，组 CPU = Σ成员 CPU。
/// </summary>
/// <remarks>
/// 部分成员读取失败（权限不足/瞬间退出）时不让整组变未知：
/// 已知成员求和，未知成员按缺失跳过；全部成员都未知才输出 null（UI 显示 "--"）。
/// </remarks>
public static class ApplicationResourceAggregator
{
    /// <summary>组资源聚合结果。</summary>
    public readonly record struct GroupResource(double? CpuPercent, long? MemoryBytes);

    /// <summary>
    /// 聚合一个应用组的 CPU 与内存。
    /// </summary>
    /// <param name="group">应用组。</param>
    /// <param name="samplesByPid">PID → 单进程采样。</param>
    public static GroupResource Aggregate(
        ApplicationGroup group,
        IReadOnlyDictionary<int, ProcessResourceSample> samplesByPid)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(samplesByPid);

        double? cpuSum = null;
        long? memorySum = null;

        foreach (ProcessSnapshot process in group.Processes)
        {
            if (!samplesByPid.TryGetValue(process.ProcessId, out ProcessResourceSample sample))
            {
                continue;
            }

            if (sample.CpuPercent.HasValue)
            {
                cpuSum = (cpuSum ?? 0) + sample.CpuPercent.Value;
            }

            if (sample.WorkingSetBytes.HasValue)
            {
                memorySum = (memorySum ?? 0) + sample.WorkingSetBytes.Value;
            }
        }

        return new GroupResource(cpuSum, memorySum);
    }
}
