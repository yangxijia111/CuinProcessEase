namespace CuinProcessEase.Core.Safety;

/// <summary>
/// 进程/应用的风险等级。
/// </summary>
/// <remarks>
/// System 与 Protected 必须区分：System 是系统组件归属（无 PPL / 非 Critical），
/// Protected 是 Windows 强保护（Critical / PPL / 关键名单）。二者决策都为 Blocked，
/// 但语义与未来高级模式处理不同。
/// </remarks>
public enum RiskLevel
{
    /// <summary>普通用户应用，无提权。</summary>
    Normal = 0,

    /// <summary>以管理员权限运行，但不是系统/保护进程（如管理员启动的记事本）。</summary>
    Elevated = 1,

    /// <summary>Windows 系统进程（SYSTEM 账户 / Session 0 / 系统路径），非 PPL 非 Critical。</summary>
    System = 2,

    /// <summary>受 Windows 强保护：Critical 进程 / PPL / 关键系统名单 / 自身进程。</summary>
    Protected = 3,

    /// <summary>身份信息不足，无法可靠判断（不知道 ≠ 安全）。</summary>
    Unknown = 4,
}
