namespace CuinProcessEase.Core.Resources;

/// <summary>单个进程一次资源采样的结果。</summary>
/// <param name="CpuPercent">
/// 本次采样区间的 CPU 百分比（占整机，0-100，已按核心数归一化）；
/// 首次采样或读取失败为 null（UI 显示 "--"）。
/// </param>
/// <param name="WorkingSetBytes">工作集内存（字节）；读取失败为 null。</param>
public readonly record struct ProcessResourceSample(
    double? CpuPercent,
    long? WorkingSetBytes)
{
    /// <summary>未知采样（CPU 与内存都不可用）。</summary>
    public static readonly ProcessResourceSample Unknown = new(null, null);
}

/// <summary>
/// CPU 百分比计算（纯逻辑）。
/// </summary>
/// <remarks>
/// CPU% = Δ进程内核+用户时间 / (Δ墙钟时间 × 核心数) × 100，
/// 即"占整机的百分比"（与任务管理器一致）：单进程打满 1 核 = 100%/核心数，
/// 全部进程之和不超过 100%。任何非法输入（时间倒退、除零、核数非正）返回 null，
/// 绝不输出负数或伪造值。
/// </remarks>
public static class CpuUsageCalculator
{
    /// <summary>
    /// 计算采样区间内的 CPU 百分比。
    /// </summary>
    /// <param name="previousProcessTicks">上一次采样的进程内核+用户时间（FILETIME 100ns tick）。</param>
    /// <param name="currentProcessTicks">本次采样的进程内核+用户时间（FILETIME 100ns tick）。</param>
    /// <param name="previousWallTicks">上一次采样墙钟时刻（同一 tick 单位）。</param>
    /// <param name="currentWallTicks">本次采样墙钟时刻。</param>
    /// <param name="coreCount">逻辑核心数（用于多核归一化）。</param>
    public static double? ComputePercent(
        long previousProcessTicks,
        long currentProcessTicks,
        long previousWallTicks,
        long currentWallTicks,
        int coreCount)
    {
        long processDelta = currentProcessTicks - previousProcessTicks;
        long wallDelta = currentWallTicks - previousWallTicks;

        if (coreCount <= 0 || wallDelta <= 0 || processDelta < 0)
        {
            // 核心数非法 / 墙钟未前进 / 进程时间倒退（不应发生）：宁缺毋假
            return null;
        }

        double percent = processDelta * 100.0 / (wallDelta * coreCount);
        return Math.Clamp(percent, 0.0, 100.0);
    }
}
