using System.Runtime.InteropServices;

namespace CuinProcessEase.Windows.Native;

/// <summary>
/// GetProcessInformation / SetProcessInformation 使用的公开信息类
/// （processthreadsapi.h 的 PROCESS_INFORMATION_CLASS）。
/// </summary>
/// <remarks>
/// 注意与 NtQueryInformationProcess 的内部 PROCESSINFOCLASS 编号无关
/// （后者的 ProcessProtectionLevelInfo 是 61，绝不可传给本 API）。
/// </remarks>
public enum PROCESS_INFORMATION_CLASS
{
    /// <summary>MEMORY_PRIORITY_INFORMATION</summary>
    ProcessMemoryPriority = 0,

    /// <summary>PROCESS_MEMORY_EXHAUSTION_INFO</summary>
    ProcessMemoryExhaustionInfo = 1,

    /// <summary>APP_MEMORY_INFORMATION</summary>
    ProcessAppMemoryInfo = 2,

    /// <summary>BOOLEAN</summary>
    ProcessInPrivateInfo = 3,

    /// <summary>PROCESS_POWER_THROTTLING_STATE</summary>
    ProcessPowerThrottling = 4,

    /// <summary>保留（曾为 ProcessActivityThrottlePolicyInfo）。</summary>
    ProcessReservedValue1 = 5,

    /// <summary>TELEMETRY_COVERAGE_POINT</summary>
    ProcessTelemetryCoverageInfo = 6,

    /// <summary>PROCESS_PROTECTION_LEVEL_INFORMATION（本项目使用）。</summary>
    ProcessProtectionLevelInfo = 7,

    /// <summary>PROCESS_LEAP_SECOND_INFO</summary>
    ProcessLeapSecondInfo = 8,

    /// <summary>PROCESS_MACHINE_INFORMATION</summary>
    ProcessMachineTypeInfo = 9,

    /// <summary>OVERRIDE_PREFETCH_PARAMETER</summary>
    ProcessOverrideSubsequentPrefetchParameter = 10,

    /// <summary>OVERRIDE_PREFETCH_PARAMETER</summary>
    ProcessMaxOverridePrefetchParameter = 11,

    /// <summary>枚举上限标记。</summary>
    ProcessInformationClassMax = 12,
}

/// <summary>
/// Windows 支持的进程保护级别（WinBase.h / winnt.h 原始定义，DWORD）。
/// </summary>
/// <remarks>
/// 0-8 为实际保护级别（0 也是有效级别：WinTcb-Light）；
/// PROTECTION_LEVEL_NONE（0xFFFFFFFE）才是"无保护"，且仅在查询时返回——绝不可假设 0=NONE。
/// </remarks>
public enum PROTECTION_LEVEL : uint
{
    /// <summary>PPL WinTcb-Light。</summary>
    PROTECTION_LEVEL_WINTCB_LIGHT = 0x00000000,

    /// <summary>PP Windows。</summary>
    PROTECTION_LEVEL_WINDOWS = 0x00000001,

    /// <summary>PPL Windows-Light。</summary>
    PROTECTION_LEVEL_WINDOWS_LIGHT = 0x00000002,

    /// <summary>PPL Antimalware-Light（如 MsMpEng.exe）。</summary>
    PROTECTION_LEVEL_ANTIMALWARE_LIGHT = 0x00000003,

    /// <summary>PPL LSA-Light。</summary>
    PROTECTION_LEVEL_LSA_LIGHT = 0x00000004,

    /// <summary>PP WinTcb（仅测试用途）。</summary>
    PROTECTION_LEVEL_WINTCB = 0x00000005,

    /// <summary>PPL CodeGen-Light（仅测试用途）。</summary>
    PROTECTION_LEVEL_CODEGEN_LIGHT = 0x00000006,

    /// <summary>PP Authenticode（仅测试用途）。</summary>
    PROTECTION_LEVEL_AUTHENTICODE = 0x00000007,

    /// <summary>PPL App（仅测试用途）。</summary>
    PROTECTION_LEVEL_PPL_APP = 0x00000008,

    /// <summary>保持与调用方相同（仅 SetProcessInformation 使用）。</summary>
    PROTECTION_LEVEL_SAME = 0xFFFFFFFF,

    /// <summary>无保护（普通进程）——仅在查询 ProcessProtectionLevelInfo 时返回。</summary>
    PROTECTION_LEVEL_NONE = 0xFFFFFFFE,
}

/// <summary>
/// Safety Engine 所需的 Win32 P/Invoke（与主文件 partial 合并）。
/// </summary>
internal static partial class NativeMethods
{
    // ---------- IsProcessCritical（公开文档 API） ----------

    /// <summary>
    /// 查询进程是否为 Critical（结束将触发 CRITICAL_PROCESS_DIED 蓝屏）。
    /// 仅需 PROCESS_QUERY_LIMITED_INFORMATION 句柄。
    /// 返回 false 时必须检查 GetLastWin32Error：失败≠false。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsProcessCritical(IntPtr hProcess, out bool bCritical);

    // ---------- GetProcessInformation / Protection Level（公开文档 API） ----------

    /// <summary>PROCESS_PROTECTION_LEVEL_INFORMATION：单个 DWORD 的原始保护级别。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_PROTECTION_LEVEL_INFORMATION
    {
        public uint ProtectionLevel;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessInformation(
        IntPtr hProcess,
        PROCESS_INFORMATION_CLASS processInformationClass,
        ref PROCESS_PROTECTION_LEVEL_INFORMATION processInformation,
        int processInformationSize);
}
