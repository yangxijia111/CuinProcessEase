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

    /// <summary>
    /// 提权授权（P7）：仅当用户在专门的提权确认对话框明确同意后才为 true；
    /// 默认 false，绝不自动提权。为 true 时目标组 Fresh Safety 为
    /// RequiresElevation 也允许规划继续（由 <see cref="Elevated"/> 命名空间下的
    /// Helper 管线执行，Helper 端必须重新验证每个 exact identity）。
    /// </summary>
    public bool AllowElevation { get; init; }

    /// <summary>校验请求基本合法（非空身份列表）。</summary>
    public bool IsValid
        => !string.IsNullOrWhiteSpace(ExpectedDisplayName)
           && ExpectedMemberIdentities.Count > 0;
}
