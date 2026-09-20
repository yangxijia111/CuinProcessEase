namespace CuinProcessEase.Core.Safety;

/// <summary>
/// 应用组的安全评估结果：先分别评估全部成员，再取最严格成员。
/// </summary>
public sealed class ApplicationSafetyResult
{
    /// <summary>应用显示名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>全部成员的逐进程评估结果。</summary>
    public required IReadOnlyList<ProcessSafetyResult> MemberResults { get; init; }

    /// <summary>组级决策 = 最严格成员决策（Blocked &gt; Indeterminate &gt; RequiresElevation &gt; Allowed）。</summary>
    public SafetyDecision Decision { get; init; }

    /// <summary>组级风险等级 = 最严重成员风险（Protected &gt; System &gt; Unknown &gt; Elevated &gt; Normal）。</summary>
    public RiskLevel OverallRiskLevel { get; init; }

    /// <summary>组内全部成员依据的并集。</summary>
    public SafetyReason Reasons { get; init; }

    /// <summary>导致组被 Blocked 的具体成员（保留追溯能力）。</summary>
    public IReadOnlyList<ProcessSafetyResult> BlockingMembers { get; init; } = Array.Empty<ProcessSafetyResult>();

    /// <summary>便捷访问：组级决策是否需要管理员权限。</summary>
    public bool RequiresElevation => Decision == SafetyDecision.RequiresElevation;
}
