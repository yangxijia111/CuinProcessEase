namespace CuinProcessEase.Core.Safety;

/// <summary>
/// 安全判定的具体依据，一个结果可包含多个（Flags）。
/// </summary>
[Flags]
public enum SafetyReason
{
    /// <summary>无。</summary>
    None = 0,

    /// <summary>目标就是 ProcessEase 自身进程（或其 ElevatedHelper）。</summary>
    SelfProcess = 1 << 0,

    /// <summary>IsProcessCritical 实测为 true（结束会直接蓝屏/致系统不可用）。</summary>
    CriticalWindowsProcess = 1 << 1,

    /// <summary>受保护进程（PP，ProtectionLevel 非 NONE 且非 Lite）。</summary>
    ProtectedProcess = 1 << 2,

    /// <summary>受保护进程 Light（PPL）。</summary>
    ProtectedProcessLight = 1 << 3,

    /// <summary>命中关键进程兜底名单（API 查询失败也不得视为安全）。</summary>
    KnownCriticalProcess = 1 << 4,

    /// <summary>Windows 系统路径（C:\Windows 下）的系统组件证据。</summary>
    WindowsSystemProcess = 1 << 5,

    /// <summary>以系统账户运行（NT AUTHORITY\SYSTEM 等）。</summary>
    SystemAccount = 1 << 6,

    /// <summary>Session 0（服务会话）。</summary>
    SessionZero = 1 << 7,

    /// <summary>以管理员权限运行。</summary>
    ElevatedProcess = 1 << 8,

    /// <summary>安全查询被拒绝（OpenProcess / IsProcessCritical / PPL 查询 AccessDenied）。</summary>
    AccessDenied = 1 << 9,

    /// <summary>身份信息不足以证明目标为普通安全应用。</summary>
    InsufficientIdentity = 1 << 10,

    /// <summary>安全状态未知（无法可靠判断）。</summary>
    UnknownSafetyState = 1 << 11,
}
