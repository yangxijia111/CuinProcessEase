using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Termination;

/// <summary>
/// Final Rescan 单身份四态判定纯逻辑（只验证，绝不终止）。
/// </summary>
/// <remarks>
/// 输入：targeted identity、Final 快照中同 PID 的条目（无则 null）、
/// 以及快照 StartTime 不可读时的句柄 CreationTime 复核结果（未复核或失败为 null）。
/// fail-closed 原则：任何无法精确比对的情形一律 <see cref="FinalIdentityState.Uncertain"/>，
/// 绝不视为 Gone——宁可多报残留让用户重试，也不能虚报成功。
/// </remarks>
public static class FinalIdentityVerifier
{
    /// <summary>
    /// 判定 targeted identity 在 Final 快照时刻的状态。
    /// </summary>
    /// <param name="targeted">本次操作曾纳入候选的 exact identity。</param>
    /// <param name="snapshotEntry">Final 快照中同 PID 的进程条目；PID 不存在时为 null。</param>
    /// <param name="handleCreationFileTimeUtc">
    /// 快照条目 StartTime 不可读时，通过 OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)
    /// 加 GetProcessTimes 复核到的原始 CreationTime FILETIME；未复核或复核失败（含句柄打开被拒绝）为 null。
    /// </param>
    public static FinalIdentityState Determine(
        ProcessIdentity targeted,
        ProcessSnapshot? snapshotEntry,
        long? handleCreationFileTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(targeted);

        // 预期身份本身缺 StartTime：无法做任何精确比对（fail-closed，不可判定）
        if (targeted.StartTimeUtc is null)
        {
            return FinalIdentityState.Uncertain;
        }

        // PID 已不存在：原 identity 确认退出
        if (snapshotEntry is null)
        {
            return FinalIdentityState.Gone;
        }

        // PID 存在且 exact 身份完全一致（含 CreationTime 逐位相同）：仍存活
        if (snapshotEntry.Identity == targeted)
        {
            return FinalIdentityState.Surviving;
        }

        // PID 存在、快照 StartTime 已知但不同：PID 已被复用，原 identity 已退出
        if (snapshotEntry.StartTimeUtc is not null)
        {
            return FinalIdentityState.PidReused;
        }

        // PID 存在但快照读不到 StartTime：句柄级复核裁决；复核失败或返回非法值 → fail-closed Uncertain
        if (handleCreationFileTimeUtc is not { } handleFileTime || handleFileTime <= 0)
        {
            return FinalIdentityState.Uncertain;
        }

        return ProcessIdentityMatcher.IsExactProcessIdentityMatch(
            targeted.StartTimeUtc.Value.ToFileTimeUtc(), handleFileTime)
            ? FinalIdentityState.Surviving
            : FinalIdentityState.PidReused;
    }
}
