namespace CuinProcessEase.Core.Termination;

/// <summary>
/// 精确进程身份匹配纯逻辑：CreationTime 以原始 FILETIME（100ns tick）逐位比较。
/// </summary>
/// <remarks>
/// P6.1 安全原则：绝无任何时间容差——即使仅差 1 个 FILETIME tick（100ns）
/// 也视为身份不匹配（PID 已被复用），必须在任何 WM_CLOSE / TerminateProcess 之前
/// 取消整组操作。宁可本次操作失败让用户重试，也不能因为模糊身份继续执行。
/// </remarks>
public static class ProcessIdentityMatcher
{
    /// <summary>
    /// 判断期望创建时间与句柄实测创建时间是否指向同一进程。
    /// </summary>
    /// <param name="expectedCreationFileTimeUtc">
    /// 期望创建时间：Fresh Snapshot 的 StartTimeUtc.ToFileTimeUtc()（原始 FILETIME 值）。
    /// </param>
    /// <param name="actualCreationFileTimeUtc">
    /// 实测创建时间：OpenProcess 句柄上 GetProcessTimes 返回的 CreationTime 原始 FILETIME 值。
    /// </param>
    /// <returns>完全相同返回 true；差 1 tick（100ns）即返回 false；非法值（≤0）同样返回 false。</returns>
    public static bool IsExactProcessIdentityMatch(long expectedCreationFileTimeUtc, long actualCreationFileTimeUtc)
        => expectedCreationFileTimeUtc > 0
           && expectedCreationFileTimeUtc == actualCreationFileTimeUtc;
}
