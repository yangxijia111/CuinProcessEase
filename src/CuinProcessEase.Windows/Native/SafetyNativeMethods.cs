using System.Runtime.InteropServices;

namespace CuinProcessEase.Windows.Native;

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

    /// <summary>PROCESSINFOCLASS.ProcessProtectionLevelInfo。</summary>
    public const int ProcessProtectionLevelInfo = 61;

    /// <summary>PROCESS_PROTECTION_LEVEL_INFORMATION：单个 DWORD 的原始保护级别。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_PROTECTION_LEVEL_INFORMATION
    {
        public uint ProtectionLevel;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref PROCESS_PROTECTION_LEVEL_INFORMATION processInformation,
        int processInformationSize);

    // ---------- Protection Level 常量（winnt.h） ----------

    /// <summary>无保护（普通进程）。</summary>
    public const uint PROTECTION_LEVEL_NONE = 0;

    /// <summary>Protected Process Light。</summary>
    public const uint PROTECTION_LEVEL_LITE = 1;

    /// <summary>Protected Process (Windows)。</summary>
    public const uint PROTECTION_LEVEL_WINDOWS = 2;

    /// <summary>Protected Process Light (Windows)。</summary>
    public const uint PROTECTION_LEVEL_WINDOWS_LITE = 3;

    /// <summary>Protected Process (App)。</summary>
    public const uint PROTECTION_LEVEL_APP = 4;

    /// <summary>Protected Process Light (App)。</summary>
    public const uint PROTECTION_LEVEL_APP_LITE = 5;

    /// <summary>Protected Process Light (Antimalware)，如 MsMpEng.exe。</summary>
    public const uint PROTECTION_LEVEL_ANTIMALWARE = 6;
}
