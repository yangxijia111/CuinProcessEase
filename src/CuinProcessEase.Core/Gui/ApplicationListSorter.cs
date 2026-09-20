using System.Globalization;

namespace CuinProcessEase.Core.Gui;

/// <summary>列表排序方式。</summary>
public enum ApplicationSortMode
{
    /// <summary>按应用名称（默认；稳定，不随 CPU/RAM 刷新跳动）。</summary>
    Name = 0,

    /// <summary>按 CPU 降序（仅用户明确选择后动态排序）。</summary>
    Cpu = 1,

    /// <summary>按内存降序（仅用户明确选择后动态排序）。</summary>
    Memory = 2,

    /// <summary>按进程数量降序。</summary>
    ProcessCount = 3,
}

/// <summary>
/// 排序所需的行数据快照（纯数据，与 UI 解耦，便于直接单元测试）。
/// </summary>
/// <param name="DisplayName">应用显示名。</param>
/// <param name="CpuPercent">组 CPU 百分比；未知为 null（排在已知值之后）。</param>
/// <param name="MemoryBytes">组内存字节；未知为 null（排在已知值之后）。</param>
/// <param name="ProcessCount">组内进程数。</param>
/// <param name="StableKey">稳定 Key（并列时的最终决胜，保证排序完全确定）。</param>
public readonly record struct ApplicationSortKey(
    string DisplayName,
    double? CpuPercent,
    long? MemoryBytes,
    int ProcessCount,
    string StableKey);

/// <summary>
/// 应用列表排序比较器（纯逻辑）。
/// </summary>
/// <remarks>
/// 名称排序完全确定（显示名并列时按 StableKey 决胜），刷新之间不会跳位；
/// CPU/内存/进程数排序为降序、未知值排最后，同样以名称/StableKey 决胜避免抖动。
/// </remarks>
public static class ApplicationListSorter
{
    /// <summary>获取指定模式的排序比较（返回负数表示 a 排在 b 前）。</summary>
    public static Comparison<ApplicationSortKey> GetComparison(ApplicationSortMode mode) => mode switch
    {
        ApplicationSortMode.Cpu => CompareByCpu,
        ApplicationSortMode.Memory => CompareByMemory,
        ApplicationSortMode.ProcessCount => CompareByProcessCount,
        _ => CompareByName,
    };

    /// <summary>名称排序：当前文化不区分大小写；并列时 StableKey 决胜（完全稳定）。</summary>
    public static int CompareByName(ApplicationSortKey a, ApplicationSortKey b)
    {
        int byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        if (byName != 0)
        {
            return byName;
        }

        return string.CompareOrdinal(a.StableKey, b.StableKey);
    }

    /// <summary>CPU 降序：未知（null）排最后；并列回退名称排序。</summary>
    public static int CompareByCpu(ApplicationSortKey a, ApplicationSortKey b)
    {
        int byValue = CompareNullableDescending(a.CpuPercent, b.CpuPercent);
        return byValue != 0 ? byValue : CompareByName(a, b);
    }

    /// <summary>内存降序：未知（null）排最后；并列回退名称排序。</summary>
    public static int CompareByMemory(ApplicationSortKey a, ApplicationSortKey b)
    {
        int byValue = CompareNullableDescending(a.MemoryBytes, b.MemoryBytes);
        return byValue != 0 ? byValue : CompareByName(a, b);
    }

    /// <summary>进程数量降序；并列回退名称排序。</summary>
    public static int CompareByProcessCount(ApplicationSortKey a, ApplicationSortKey b)
    {
        int byCount = b.ProcessCount.CompareTo(a.ProcessCount);
        return byCount != 0 ? byCount : CompareByName(a, b);
    }

    private static int CompareNullableDescending(double? a, double? b)
    {
        if (a.HasValue && b.HasValue)
        {
            return b.Value.CompareTo(a.Value);
        }

        // 已知值在前，未知值在后；两者都未知则并列
        return (a.HasValue, b.HasValue) switch
        {
            (true, false) => -1,
            (false, true) => 1,
            _ => 0,
        };
    }

    private static int CompareNullableDescending(long? a, long? b)
    {
        if (a.HasValue && b.HasValue)
        {
            return b.Value.CompareTo(a.Value);
        }

        return (a.HasValue, b.HasValue) switch
        {
            (true, false) => -1,
            (false, true) => 1,
            _ => 0,
        };
    }
}
