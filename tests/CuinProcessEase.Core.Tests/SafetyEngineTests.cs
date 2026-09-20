using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// Safety Engine 纯逻辑测试：决策聚合、关键名单、严格度排序。
/// 全部人工构造数据，不依赖真实 Windows 进程。
/// </summary>
public sealed class SafetyEngineTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ProcessSafetyResult Result(
        int pid,
        string name,
        RiskLevel risk,
        SafetyDecision decision,
        SafetyReason reasons = SafetyReason.None) => new()
    {
        Identity = new ProcessIdentity(pid, BaseTime),
        ProcessName = name,
        RiskLevel = risk,
        Decision = decision,
        Reasons = reasons,
    };

    // ================= 关键名单 =================

    [Theory]
    [InlineData(4, "System")]
    [InlineData(0, "[System Process]")]
    [InlineData(100, "Registry")]
    [InlineData(100, "Secure System")]
    [InlineData(100, "smss.exe")]
    [InlineData(100, "csrss.exe")]
    [InlineData(100, "wininit.exe")]
    [InlineData(100, "services.exe")]
    [InlineData(100, "lsass.exe")]
    [InlineData(100, "winlogon.exe")]
    [InlineData(100, "LSASS.EXE")] // 大小写不敏感
    public void 关键名单_系统核心进程全部命中(int pid, string name)
    {
        Assert.True(KnownCriticalProcesses.IsKnownCritical(pid, name));
    }

    [Theory]
    [InlineData(100, "notepad.exe")]
    [InlineData(100, "explorer.exe")]
    [InlineData(100, "chrome.exe")]
    [InlineData(100, "svchost.exe")] // 系统进程但非关键名单（结束单个不蓝屏）
    public void 关键名单_普通进程不命中(int pid, string name)
    {
        Assert.False(KnownCriticalProcesses.IsKnownCritical(pid, name));
    }

    // ================= 聚合器 =================

    [Fact]
    public void 组聚合_含Blocked成员_组决策为Blocked并保留阻断成员()
    {
        var members = new List<ProcessSafetyResult>
        {
            Result(100, "chrome.exe", RiskLevel.Normal, SafetyDecision.Allowed),
            Result(200, "chrome.exe", RiskLevel.Normal, SafetyDecision.Allowed),
            Result(300, "lsass.exe", RiskLevel.Protected, SafetyDecision.Blocked, SafetyReason.KnownCriticalProcess),
        };

        ApplicationSafetyResult aggregate = ApplicationSafetyAggregator.Aggregate("Chrome", members);

        Assert.Equal(SafetyDecision.Blocked, aggregate.Decision);
        Assert.Equal(RiskLevel.Protected, aggregate.OverallRiskLevel);
        ProcessSafetyResult blocker = Assert.Single(aggregate.BlockingMembers);
        Assert.Equal("lsass.exe", blocker.ProcessName);
    }

    [Fact]
    public void 组聚合_含RequiresElevation成员_组决策为RequiresElevation()
    {
        var members = new List<ProcessSafetyResult>
        {
            Result(100, "chrome.exe", RiskLevel.Normal, SafetyDecision.Allowed),
            Result(200, "admin-helper.exe", RiskLevel.Elevated, SafetyDecision.RequiresElevation, SafetyReason.ElevatedProcess),
        };

        ApplicationSafetyResult aggregate = ApplicationSafetyAggregator.Aggregate("Chrome", members);

        Assert.Equal(SafetyDecision.RequiresElevation, aggregate.Decision);
        Assert.Equal(RiskLevel.Elevated, aggregate.OverallRiskLevel);
        Assert.True(aggregate.RequiresElevation);
        Assert.Empty(aggregate.BlockingMembers);
    }

    [Fact]
    public void 组聚合_含Unknown成员_组决策为Indeterminate()
    {
        var members = new List<ProcessSafetyResult>
        {
            Result(100, "chrome.exe", RiskLevel.Normal, SafetyDecision.Allowed),
            Result(200, "mystery.exe", RiskLevel.Unknown, SafetyDecision.Indeterminate,
                SafetyReason.InsufficientIdentity | SafetyReason.UnknownSafetyState),
        };

        ApplicationSafetyResult aggregate = ApplicationSafetyAggregator.Aggregate("Chrome", members);

        // Unknown 比 RequiresElevation 严格（不知道 ≠ 安全）
        Assert.Equal(SafetyDecision.Indeterminate, aggregate.Decision);
        Assert.Equal(RiskLevel.Unknown, aggregate.OverallRiskLevel);
    }

    [Fact]
    public void 组聚合_全Allowed_组Allowed()
    {
        var members = new List<ProcessSafetyResult>
        {
            Result(100, "chrome.exe", RiskLevel.Normal, SafetyDecision.Allowed),
            Result(200, "chrome.exe", RiskLevel.Normal, SafetyDecision.Allowed),
        };

        ApplicationSafetyResult aggregate = ApplicationSafetyAggregator.Aggregate("Chrome", members);

        Assert.Equal(SafetyDecision.Allowed, aggregate.Decision);
        Assert.Equal(RiskLevel.Normal, aggregate.OverallRiskLevel);
    }

    [Fact]
    public void 组聚合_决策严格度排序_Blocked最高()
    {
        // Blocked > Indeterminate > RequiresElevation > Allowed 逐对验证
        Assert.True(SafetyDecision.Blocked > SafetyDecision.Indeterminate);
        Assert.True(SafetyDecision.Indeterminate > SafetyDecision.RequiresElevation);
        Assert.True(SafetyDecision.RequiresElevation > SafetyDecision.Allowed);
    }

    [Fact]
    public void 风险严重度排序_Protected最高_Unknown高于Elevated()
    {
        Assert.True(ApplicationSafetyAggregator.RiskSeverity(RiskLevel.Protected)
                    > ApplicationSafetyAggregator.RiskSeverity(RiskLevel.System));
        Assert.True(ApplicationSafetyAggregator.RiskSeverity(RiskLevel.System)
                    > ApplicationSafetyAggregator.RiskSeverity(RiskLevel.Unknown));
        Assert.True(ApplicationSafetyAggregator.RiskSeverity(RiskLevel.Unknown)
                    > ApplicationSafetyAggregator.RiskSeverity(RiskLevel.Elevated));
        Assert.True(ApplicationSafetyAggregator.RiskSeverity(RiskLevel.Elevated)
                    > ApplicationSafetyAggregator.RiskSeverity(RiskLevel.Normal));
    }

    [Fact]
    public void 组聚合_Reasons取并集()
    {
        var members = new List<ProcessSafetyResult>
        {
            Result(100, "a.exe", RiskLevel.Normal, SafetyDecision.Allowed, SafetyReason.ElevatedProcess),
            Result(200, "b.exe", RiskLevel.System, SafetyDecision.Blocked, SafetyReason.SystemAccount | SafetyReason.SessionZero),
        };

        ApplicationSafetyResult aggregate = ApplicationSafetyAggregator.Aggregate("App", members);

        Assert.True(aggregate.Reasons.HasFlag(SafetyReason.ElevatedProcess));
        Assert.True(aggregate.Reasons.HasFlag(SafetyReason.SystemAccount));
        Assert.True(aggregate.Reasons.HasFlag(SafetyReason.SessionZero));
    }

    [Fact]
    public void 组聚合_空成员列表抛出异常()
    {
        Assert.Throws<ArgumentException>(
            () => ApplicationSafetyAggregator.Aggregate("App", Array.Empty<ProcessSafetyResult>()));
    }
}
