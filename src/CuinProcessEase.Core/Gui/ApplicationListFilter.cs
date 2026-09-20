using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Safety;

namespace CuinProcessEase.Core.Gui;

/// <summary>主列表筛选页。</summary>
public enum ApplicationListTab
{
    /// <summary>全部应用（默认；隐藏系统保护/系统进程行）。</summary>
    All = 0,

    /// <summary>高资源（CPU 或内存达到阈值）。</summary>
    HighResource = 1,

    /// <summary>后台运行（组内没有任何进程属于当前交互会话）。</summary>
    Background = 2,

    /// <summary>系统进程（仅查看被默认隐藏的系统保护 / 系统行）。</summary>
    System = 3,
}

/// <summary>
/// 应用列表筛选规则（纯逻辑）：哪些行进入当前筛选页、系统行如何在默认视图隐藏。
/// </summary>
/// <remarks>
/// 系统行默认隐藏但数据绝不删除——切到"系统进程"页即可查看。
/// Unknown / Indeterminate 不属于系统行，永远可见，不会被偷偷隐藏。
/// </remarks>
public static class ApplicationListFilter
{
    /// <summary>高资源页的 CPU 阈值（占总 CPU 百分比）。</summary>
    public const double HighResourceCpuPercent = 1.0;

    /// <summary>高资源页的内存阈值（字节）。</summary>
    public const long HighResourceMemoryBytes = 200L * 1024 * 1024;

    /// <summary>
    /// 是否为"系统行"：组级决策为 Blocked 且风险为 System / Protected。
    /// 此类行默认不在"全部应用"中出现。
    /// </summary>
    public static bool IsSystemRow(SafetyDecision decision, RiskLevel riskLevel)
        => decision == SafetyDecision.Blocked
           && riskLevel is RiskLevel.System or RiskLevel.Protected;

    /// <summary>
    /// 判断应用组是否属于指定筛选页。
    /// </summary>
    /// <param name="tab">目标筛选页。</param>
    /// <param name="group">应用组（后台页需要组内进程的会话信息）。</param>
    /// <param name="isSystemRow">该组是否为系统行（见 <see cref="IsSystemRow"/>）。</param>
    /// <param name="cpuPercent">组 CPU（未知传 null）。</param>
    /// <param name="memoryBytes">组内存（未知传 null）。</param>
    /// <param name="currentSessionId">当前用户交互会话 ID。</param>
    public static bool MatchesTab(
        ApplicationListTab tab,
        ApplicationGroup group,
        bool isSystemRow,
        double? cpuPercent,
        long? memoryBytes,
        int currentSessionId)
    {
        ArgumentNullException.ThrowIfNull(group);

        return tab switch
        {
            ApplicationListTab.All => !isSystemRow,
            ApplicationListTab.System => isSystemRow,
            ApplicationListTab.HighResource => !isSystemRow && IsHighResource(cpuPercent, memoryBytes),
            ApplicationListTab.Background => !isSystemRow && IsBackground(group, currentSessionId),
            _ => !isSystemRow,
        };
    }

    /// <summary>是否达到高资源阈值：CPU 达标或内存达标（任一即可）。</summary>
    public static bool IsHighResource(double? cpuPercent, long? memoryBytes)
        => (cpuPercent.HasValue && cpuPercent.Value >= HighResourceCpuPercent)
           || (memoryBytes.HasValue && memoryBytes.Value >= HighResourceMemoryBytes);

    /// <summary>
    /// 是否为后台应用：组内没有任何进程属于当前交互会话。
    /// 会话未知的进程不视为当前会话（典型：Session 0 服务组、权限不足读不到会话的组）。
    /// </summary>
    public static bool IsBackground(ApplicationGroup group, int currentSessionId)
        => group.Processes.All(p => p.SessionId != currentSessionId);
}
