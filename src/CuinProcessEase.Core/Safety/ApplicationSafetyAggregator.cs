using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Safety;

/// <summary>
/// 应用组安全聚合器：组级结果取最严格成员（纯逻辑，便于测试）。
/// </summary>
public static class ApplicationSafetyAggregator
{
    /// <summary>
    /// 聚合全部成员评估结果：决策按 Blocked &gt; Indeterminate &gt; RequiresElevation &gt; Allowed，
    /// 风险按 Protected &gt; System &gt; Unknown &gt; Elevated &gt; Normal，
    /// 并保留导致 Blocked 的具体成员。
    /// </summary>
    public static ApplicationSafetyResult Aggregate(
        string displayName,
        IReadOnlyList<ProcessSafetyResult> memberResults)
    {
        ArgumentNullException.ThrowIfNull(memberResults);
        if (memberResults.Count == 0)
        {
            throw new ArgumentException("成员结果不能为空", nameof(memberResults));
        }

        SafetyDecision decision = memberResults.Max(m => m.Decision);
        RiskLevel overallRisk = memberResults
            .OrderByDescending(m => RiskSeverity(m.RiskLevel))
            .First().RiskLevel;

        SafetyReason reasons = memberResults.Aggregate(SafetyReason.None, (acc, m) => acc | m.Reasons);

        List<ProcessSafetyResult> blocking = memberResults
            .Where(m => m.Decision == SafetyDecision.Blocked)
            .ToList();

        return new ApplicationSafetyResult
        {
            DisplayName = displayName,
            MemberResults = memberResults,
            Decision = decision,
            OverallRiskLevel = overallRisk,
            Reasons = reasons,
            BlockingMembers = blocking,
        };
    }

    /// <summary>风险严重度排序（Protected &gt; System &gt; Unknown &gt; Elevated &gt; Normal）。</summary>
    /// <remarks>与枚举数值不同：Unknown 排在 Elevated 之前，对齐决策优先级（不知道 ≠ 安全，但低于绝对禁止）。</remarks>
    public static int RiskSeverity(RiskLevel level) => level switch
    {
        RiskLevel.Protected => 4,
        RiskLevel.System => 3,
        RiskLevel.Unknown => 2,
        RiskLevel.Elevated => 1,
        _ => 0,
    };
}
