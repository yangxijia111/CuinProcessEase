using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Windows.Native;

namespace CuinProcessEase.Windows.Safety;

/// <summary>
/// 安全决策纯逻辑：输入进程快照 + 实测保护状态（Critical / ProtectionLevel），
/// 输出安全评估结果。无任何 API 调用，全部状态组合可直接单元测试。
/// </summary>
/// <remarks>
/// 判定优先级（从绝对禁止到允许）：
/// 1. Self（自身 / CuinProcessEase 程序）→ Protected / Blocked；
/// 2. 关键名单（System/lsass/csrss/...）→ Protected / Blocked；
/// 3. IsProcessCritical 实测 true → Protected / Blocked；
/// 4. ProtectionLevel != NONE(0xFFFFFFFE) → Protected / Blocked（区分 PP / PPL，
///    注意 0 是有效保护级别 WinTcb-Light，NONE 才是无保护）；
/// 5. 系统证据（SYSTEM 账户 / Session 0 / Windows 路径组合）→ System / Blocked；
/// 6. IsElevated == true → Elevated / RequiresElevation；
/// 7. 身份信息不足（无路径且无用户名）→ Unknown / Indeterminate；
/// 8. 其余 → Normal / Allowed。
/// 查询失败（isCritical / protectionLevel 为 null）绝不当作 false / NONE：
/// 若同时身份不足则落入 Unknown，绝不落入 Normal。
/// </remarks>
internal static class SafetyDecisionEngine
{
    /// <summary>ProcessEase 自身及未来 ElevatedHelper 的程序名，永久自我保护。</summary>
    private static readonly HashSet<string> SelfProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CuinProcessEase.exe",
        "CuinProcessEase.App.exe",
        "CuinProcessEase.ElevatedHelper.exe",
    };

    /// <summary>系统账户前缀（含 LOCAL/NETWORK SERVICE 服务账户）。</summary>
    private const string SystemAccountPrefix = @"NT AUTHORITY\";

    public static ProcessSafetyResult Evaluate(
        ProcessSnapshot process,
        bool isSelf,
        bool? isCritical,
        PROTECTION_LEVEL? protectionLevel)
    {
        SafetyReason reasons = SafetyReason.None;

        // 1. 自身：永远 Blocked
        if (isSelf || SelfProcessNames.Contains(process.Name))
        {
            return Result(process, RiskLevel.Protected, SafetyDecision.Blocked,
                SafetyReason.SelfProcess, isCritical, protectionLevel);
        }

        // 2. 关键名单兜底：API 查询失败也不得视为安全
        if (KnownCriticalProcesses.IsKnownCritical(process.ProcessId, process.Name))
        {
            return Result(process, RiskLevel.Protected, SafetyDecision.Blocked,
                SafetyReason.KnownCriticalProcess, isCritical, protectionLevel);
        }

        // 3. IsProcessCritical 实测
        if (isCritical == true)
        {
            return Result(process, RiskLevel.Protected, SafetyDecision.Blocked,
                SafetyReason.CriticalWindowsProcess, isCritical, protectionLevel);
        }

        // 4. PP / PPL：仅 NONE（0xFFFFFFFE）代表无保护；未知值按受保护处理（fail-closed）
        if (protectionLevel is not null and not PROTECTION_LEVEL.PROTECTION_LEVEL_NONE)
        {
            SafetyReason protectionReason = IsProtectionLightLevel(protectionLevel.Value)
                ? SafetyReason.ProtectedProcessLight
                : SafetyReason.ProtectedProcess;
            return Result(process, RiskLevel.Protected, SafetyDecision.Blocked,
                protectionReason, isCritical, protectionLevel);
        }

        // ---- 以下判定需要结合快照元数据 ----
        bool isSystemAccount = process.UserName is not null
                               && process.UserName.StartsWith(SystemAccountPrefix, StringComparison.OrdinalIgnoreCase);
        bool isSessionZero = process.SessionId == 0;
        bool isWindowsPath = process.ExecutablePath is not null
                              && process.ExecutablePath.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase);

        // 5. 系统进程证据（System 与 Protected 严格区分）
        //    注意：仅有 Windows 路径（如用户运行的 System32\notepad.exe）不构成 System 证据
        if (isSystemAccount)
        {
            reasons |= SafetyReason.SystemAccount;
        }

        if (isSessionZero)
        {
            reasons |= SafetyReason.SessionZero;
        }

        if (isWindowsPath && (isSystemAccount || isSessionZero))
        {
            reasons |= SafetyReason.WindowsSystemProcess;
        }

        if (isSystemAccount || isSessionZero)
        {
            return Result(process, RiskLevel.System, SafetyDecision.Blocked,
                reasons, isCritical, protectionLevel);
        }

        // 6. 管理员进程：不把管理员程序当 Protected
        if (process.IsElevated == true)
        {
            return Result(process, RiskLevel.Elevated, SafetyDecision.RequiresElevation,
                SafetyReason.ElevatedProcess, isCritical, protectionLevel);
        }

        // 7. 身份不足：不知道 ≠ 安全，绝不为了减少 Unknown 而猜测
        bool identityInsufficient = string.IsNullOrWhiteSpace(process.ExecutablePath)
                                    && string.IsNullOrWhiteSpace(process.UserName);

        if (identityInsufficient)
        {
            reasons |= SafetyReason.InsufficientIdentity | SafetyReason.UnknownSafetyState;

            // 查询曾被拒绝时补充 AccessDenied 依据（由调用方传入的 null 状态体现）
            return Result(process, RiskLevel.Unknown, SafetyDecision.Indeterminate,
                reasons, isCritical, protectionLevel);
        }

        // 8. 普通应用
        return Result(process, RiskLevel.Normal, SafetyDecision.Allowed,
            reasons, isCritical, protectionLevel);
    }

    /// <summary>
    /// PP（完整保护）级别：WINDOWS / WINTCB / AUTHENTICODE；
    /// 其余（WINTCB_LIGHT / WINDOWS_LIGHT / ANTIMALWARE_LIGHT / LSA_LIGHT /
    /// CODEGEN_LIGHT / PPL_APP 及未知级别）归入 PPL（Light）系列。
    /// </summary>
    private static bool IsProtectionLightLevel(PROTECTION_LEVEL level) => level switch
    {
        PROTECTION_LEVEL.PROTECTION_LEVEL_WINDOWS
            or PROTECTION_LEVEL.PROTECTION_LEVEL_WINTCB
            or PROTECTION_LEVEL.PROTECTION_LEVEL_AUTHENTICODE => false,
        _ => true,
    };

    private static ProcessSafetyResult Result(
        ProcessSnapshot process,
        RiskLevel risk,
        SafetyDecision decision,
        SafetyReason reasons,
        bool? isCritical,
        PROTECTION_LEVEL? protectionLevel) => new()
    {
        Identity = process.Identity,
        ProcessName = process.Name,
        RiskLevel = risk,
        Decision = decision,
        Reasons = reasons,
        IsCritical = isCritical,
        ProtectionLevel = protectionLevel is null ? null : (uint)protectionLevel.Value,
    };
}
