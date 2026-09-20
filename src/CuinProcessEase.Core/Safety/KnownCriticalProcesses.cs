namespace CuinProcessEase.Core.Safety;

/// <summary>
/// 关键进程兜底名单：API 查询失败也绝不能把这些进程当作安全。
/// </summary>
/// <remarks>
/// 名单刻意保持小型、明确：只收录"结束会立即导致系统不可用"的核心进程。
/// 结束这些进程通常直接蓝屏（CRITICAL_PROCESS_DIED）。
/// </remarks>
public static class KnownCriticalProcesses
{
    /// <summary>关键进程名（不区分大小写；不含 .exe 也兼容）。</summary>
    public static readonly IReadOnlySet<string> CriticalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "Registry",
        "Secure System",
        "[System Process]",
        "smss.exe",
        "csrss.exe",
        "wininit.exe",
        "services.exe",
        "lsass.exe",
        "winlogon.exe",
    };

    /// <summary>关键 PID：0（空闲/调度器）与 4（内核 System 进程）。</summary>
    public static bool IsKnownCriticalPid(int processId)
        => processId is 0 or 4;

    /// <summary>按 PID 或进程名判断是否命中关键名单。</summary>
    public static bool IsKnownCritical(int processId, string? processName)
        => IsKnownCriticalPid(processId)
           || (processName is not null && CriticalNames.Contains(processName.Trim()));
}
