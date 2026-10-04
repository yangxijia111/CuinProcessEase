namespace CuinProcessEase.Core.Resources;

/// <summary>单个资源采样点（CPU 百分比 / 内存字节，均可空 = 该秒读不到）。</summary>
public readonly record struct ResourceSample(DateTimeOffset Timestamp, double? CpuPercent, long? MemoryBytes);

/// <summary>
/// 单应用资源历史环形缓冲：固定容量（默认 60 = 最近 60 秒），满后覆盖最旧样本。
/// </summary>
/// <remarks>
/// Phase 8 验收要求"历史数据内存不得无限增长"：容量硬上限由结构保证，
/// 任何调用频率下每个应用的内存占用恒定。
/// </remarks>
public sealed class ResourceHistoryRing
{
    private readonly ResourceSample[] _samples;

    private int _head; // 下一个写入位置

    private int _count;

    public ResourceHistoryRing(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 10_000);
        Capacity = capacity;
        _samples = new ResourceSample[capacity];
    }

    /// <summary>默认容量：60 个样本（1 秒一个 = 最近 60 秒）。</summary>
    public const int DefaultCapacity = 60;

    public int Capacity { get; }

    /// <summary>当前样本数（≤ 容量）。</summary>
    public int Count => _count;

    public void Append(ResourceSample sample)
    {
        _samples[_head] = sample;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }
    }

    /// <summary>按时间顺序输出全部样本（最旧 → 最新）。</summary>
    public IReadOnlyList<ResourceSample> ToListTimeOrdered()
    {
        var result = new ResourceSample[_count];
        int start = (_head - _count + Capacity) % Capacity;
        for (int i = 0; i < _count; i++)
        {
            result[i] = _samples[(start + i) % Capacity];
        }

        return result;
    }

    /// <summary>窗口内 CPU 峰值（所有非空样本的最大值；全空/无样本返回 null）。</summary>
    public double? PeakCpuPercent()
    {
        double? peak = null;
        foreach (ResourceSample sample in ToListTimeOrdered())
        {
            if (sample.CpuPercent is { } cpu && (peak is null || cpu > peak))
            {
                peak = cpu;
            }
        }

        return peak;
    }

    /// <summary>窗口内内存峰值（字节）。</summary>
    public long? PeakMemoryBytes()
    {
        long? peak = null;
        foreach (ResourceSample sample in ToListTimeOrdered())
        {
            if (sample.MemoryBytes is { } mem && (peak is null || mem > peak))
            {
                peak = mem;
            }
        }

        return peak;
    }

    /// <summary>最新一个样本（无样本返回 null）。</summary>
    public ResourceSample? Latest()
        => _count == 0 ? null : _samples[(_head - 1 + Capacity) % Capacity];
}
