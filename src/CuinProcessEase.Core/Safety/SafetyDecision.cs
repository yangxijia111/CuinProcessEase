namespace CuinProcessEase.Core.Safety;

/// <summary>
/// 安全决策：该目标是否允许未来执行破坏性操作（如结束进程）。
/// </summary>
/// <remarks>
/// 不使用 bool："需要管理员权限"与"绝对禁止"不是同一回事。
/// 严格度：Blocked &gt; Indeterminate &gt; RequiresElevation &gt; Allowed。
/// </remarks>
public enum SafetyDecision
{
    /// <summary>允许（普通用户应用）。</summary>
    Allowed = 0,

    /// <summary>需要管理员权限（提权后的普通应用）。</summary>
    RequiresElevation = 1,

    /// <summary>身份信息不足，无法判断（不知道 ≠ 安全）。</summary>
    Indeterminate = 2,

    /// <summary>绝对禁止（关键系统进程 / PPL / Critical / 自身）。</summary>
    Blocked = 3,
}
