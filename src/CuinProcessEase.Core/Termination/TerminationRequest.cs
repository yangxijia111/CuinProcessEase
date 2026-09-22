using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Termination;

/// <summary>
/// 一次终止请求：用户点击应用行时创建的不可变目标描述。
/// </summary>
/// <remarks>
/// 核心身份必须是 ProcessIdentity（PID + StartTimeUtc）。
/// StableKey 绝不出现在终止链路中——它只用于 GUI Diff / 行连续性。
/// StartTime 不可靠的身份在执行引擎中 fail-closed。
/// <see cref="ScopeConsent"/> 默认 <see cref="TerminationScopeConsent.Default"/>，
/// 绝不自动获得弱组授权（P6.4）。
/// </remarks>
public sealed record TerminationRequest(
    string ExpectedDisplayName,
    IReadOnlyList<ProcessIdentity> ExpectedMemberIdentities,
    DateTimeOffset RequestedAtUtc)
{
    /// <summary>
    /// 范围授权：仅当用户在专门的弱组确认对话框明确确认后才为
    /// <see cref="TerminationScopeConsent.ExplicitWeakGroup"/>；
    /// 授权范围只覆盖 <see cref="ExpectedMemberIdentities"/>，绝不自动扩展。
    /// </summary>
    public TerminationScopeConsent ScopeConsent { get; init; } = TerminationScopeConsent.Default;

    /// <summary>校验请求基本合法（非空身份列表）。</summary>
    public bool IsValid
        => !string.IsNullOrWhiteSpace(ExpectedDisplayName)
           && ExpectedMemberIdentities.Count > 0;
}
