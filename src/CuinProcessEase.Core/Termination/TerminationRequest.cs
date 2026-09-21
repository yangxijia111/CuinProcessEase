using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Termination;

/// <summary>
/// 一次终止请求：用户点击应用行时创建的不可变目标描述。
/// </summary>
/// <remarks>
/// 核心身份必须是 ProcessIdentity（PID + StartTimeUtc）。
/// StableKey 绝不出现在终止链路中——它只用于 GUI Diff / 行连续性。
/// StartTime 不可靠的身份在执行引擎中 fail-closed。
/// </remarks>
public sealed record TerminationRequest(
    string ExpectedDisplayName,
    IReadOnlyList<ProcessIdentity> ExpectedMemberIdentities,
    DateTimeOffset RequestedAtUtc)
{
    /// <summary>校验请求基本合法（非空身份列表）。</summary>
    public bool IsValid
        => !string.IsNullOrWhiteSpace(ExpectedDisplayName)
           && ExpectedMemberIdentities.Count > 0;
}
