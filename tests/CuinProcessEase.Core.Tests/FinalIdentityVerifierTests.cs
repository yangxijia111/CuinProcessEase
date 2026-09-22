using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Termination;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// FinalIdentityVerifier 四态判定纯逻辑测试（P6.2）：
/// Gone / Surviving / PidReused / Uncertain 的完整决策矩阵，
/// 核心不变量：身份读不到 → fail-closed Uncertain，绝不视为 Gone。
/// </summary>
public sealed class FinalIdentityVerifierTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const long BaseFileTime = 133_528_256_000_000_000;

    private static ProcessIdentity Id(int pid, long creationFileTimeUtc)
        => new(pid, DateTime.FromFileTimeUtc(creationFileTimeUtc));

    private static ProcessSnapshot Entry(int pid, long creationFileTimeUtc, bool startTimeUnknown = false)
        => new()
        {
            Identity = new ProcessIdentity(
                pid, startTimeUnknown ? null : DateTime.FromFileTimeUtc(creationFileTimeUtc)),
            Name = "app.exe",
            ExecutablePath = @"C:\FakeApps\app.exe",
            SessionId = 1,
        };

    // ================= Gone：PID 已不存在 =================

    [Fact]
    public void 判定_PID不存在_Gone()
    {
        Assert.Equal(FinalIdentityState.Gone,
            FinalIdentityVerifier.Determine(Id(100, BaseFileTime), snapshotEntry: null, handleCreationFileTimeUtc: null));
    }

    // ================= Surviving：exact 身份一致 =================

    [Fact]
    public void 判定_快照身份精确一致_Surviving()
    {
        Assert.Equal(FinalIdentityState.Surviving,
            FinalIdentityVerifier.Determine(
                Id(100, BaseFileTime), Entry(100, BaseFileTime), handleCreationFileTimeUtc: null));
    }

    [Fact]
    public void 判定_快照StartTime不可读但句柄复核精确一致_Surviving()
    {
        Assert.Equal(FinalIdentityState.Surviving,
            FinalIdentityVerifier.Determine(
                Id(100, BaseFileTime),
                Entry(100, BaseFileTime + 9_000, startTimeUnknown: true),
                handleCreationFileTimeUtc: BaseFileTime));
    }

    // ================= PidReused：PID 存在但身份已知不同 =================

    [Fact]
    public void 判定_快照StartTime已知且不同_PidReused()
    {
        Assert.Equal(FinalIdentityState.PidReused,
            FinalIdentityVerifier.Determine(
                Id(100, BaseFileTime),
                Entry(100, BaseFileTime + 5_000_000), // 新实例占用同一 PID
                handleCreationFileTimeUtc: null));
    }

    [Fact]
    public void 判定_快照StartTime不可读且句柄复核不同_PidReused()
    {
        Assert.Equal(FinalIdentityState.PidReused,
            FinalIdentityVerifier.Determine(
                Id(100, BaseFileTime),
                Entry(100, BaseFileTime + 5_000_000, startTimeUnknown: true),
                handleCreationFileTimeUtc: BaseFileTime + 1)); // 差 1 tick 也判定为不同实例
    }

    // ================= Uncertain：fail-closed =================

    [Fact]
    public void 判定_快照StartTime不可读且句柄复核失败_Uncertain_绝不视为Gone()
    {
        // OpenProcess 被拒绝 / GetProcessTimes 失败 → handleCreationFileTimeUtc == null
        Assert.Equal(FinalIdentityState.Uncertain,
            FinalIdentityVerifier.Determine(
                Id(100, BaseFileTime),
                Entry(100, BaseFileTime, startTimeUnknown: true),
                handleCreationFileTimeUtc: null));
    }

    [Fact]
    public void 判定_目标身份本身缺StartTime_Uncertain()
    {
        var targeted = new ProcessIdentity(100, StartTimeUtc: null);

        Assert.Equal(FinalIdentityState.Uncertain,
            FinalIdentityVerifier.Determine(targeted, Entry(100, BaseFileTime), handleCreationFileTimeUtc: null));
    }

    [Fact]
    public void 判定_句柄复核返回非法值_Uncertain()
    {
        // TryGetCreationFileTime 契约失败时返回 0：与 null 同等对待（fail-closed）
        Assert.Equal(FinalIdentityState.Uncertain,
            FinalIdentityVerifier.Determine(
                Id(100, BaseFileTime),
                Entry(100, BaseFileTime, startTimeUnknown: true),
                handleCreationFileTimeUtc: 0));
    }

    // ================= 参数校验 =================

    [Fact]
    public void 判定_目标身份为null_抛出ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => FinalIdentityVerifier.Determine(null!, snapshotEntry: null, handleCreationFileTimeUtc: null));
    }
}
