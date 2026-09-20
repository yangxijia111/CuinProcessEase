using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Safety;

/// <summary>
/// 单个进程的安全评估结果。
/// </summary>
/// <remarks>
/// 信息读取失败时不伪造结果：IsCritical / ProtectionLevel 用可空类型表达"未知"，
/// 判定层绝不把查询失败当作 false / NONE。
/// </remarks>
public sealed class ProcessSafetyResult
{
    /// <summary>目标进程身份（PID + StartTime，防 PID 重用）。</summary>
    public required ProcessIdentity Identity { get; init; }

    /// <summary>进程名（便于展示与日志）。</summary>
    public required string ProcessName { get; init; }

    /// <summary>风险等级。</summary>
    public RiskLevel RiskLevel { get; init; }

    /// <summary>安全决策。</summary>
    public SafetyDecision Decision { get; init; }

    /// <summary>判定依据（可多个）。</summary>
    public SafetyReason Reasons { get; init; }

    /// <summary>IsProcessCritical 实测结果；查询失败为 null（不伪造 false）。</summary>
    public bool? IsCritical { get; init; }

    /// <summary>
    /// 原始 Windows Protection Level（0=NONE，1-6 为各类 PP/PPL）；查询失败为 null。
    /// 保留原始值供日志与高级详情使用。
    /// </summary>
    public int? ProtectionLevel { get; init; }

    /// <summary>便捷访问：决策是否为需要管理员权限。</summary>
    public bool RequiresElevation => Decision == SafetyDecision.RequiresElevation;
}
