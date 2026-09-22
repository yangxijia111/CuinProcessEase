using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Core.Termination;
using Xunit;

namespace CuinProcessEase.Core.Tests;

/// <summary>
/// TerminationPlanner 决策矩阵测试（纯 fake，无任何真实进程/终止动作）。
/// 覆盖：目标仍存在 / 已退出 / PID 复用 / 多组歧义 / Safety 拒绝 / 身份不可靠 / 候选排序。
/// </summary>
public sealed class TerminationPlannerTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ProcessSnapshot Snap(int pid, string name, string? path = null, int? session = 1) => new()
    {
        Identity = new ProcessIdentity(pid, BaseTime.AddSeconds(pid)),
        Name = name,
        ExecutablePath = path,
        SessionId = session,
    };

    private static ApplicationGroup Group(
        string displayName,
        ProcessSnapshot[] processes,
        ProcessSnapshot[]? roots = null,
        GroupingConfidence confidence = GroupingConfidence.High) => new()
    {
        Identity = new ApplicationIdentity { DisplayName = displayName },
        Processes = processes,
        RootProcesses = roots ?? processes[..1],
        Confidence = confidence,
    };

    private static TerminationRequest Request(params ProcessSnapshot[] members) => new(
        "Test App",
        members.Select(p => p.Identity).ToList(),
        DateTimeOffset.UtcNow);

    private static ApplicationSafetyResult SafetyOf(SafetyDecision decision) => new()
    {
        DisplayName = "Test App",
        MemberResults = [],
        Decision = decision,
        OverallRiskLevel = decision switch
        {
            SafetyDecision.Blocked => RiskLevel.Protected,
            SafetyDecision.Indeterminate => RiskLevel.Unknown,
            SafetyDecision.RequiresElevation => RiskLevel.Elevated,
            _ => RiskLevel.Normal,
        },
    };

    private static readonly Func<ApplicationGroup, ApplicationSafetyResult> Allowed =
        _ => SafetyOf(SafetyDecision.Allowed);

    [Fact]
    public void 规划_目标仍存在_Priority候选Root优先且其余按PID()
    {
        var root = Snap(300, "app.exe");
        var helperB = Snap(200, "helper.exe");
        var helperA = Snap(150, "helper.exe");
        var group = Group("Test App", [root, helperB, helperA], [root]);

        var plan = TerminationPlanner.Plan(Request(root, helperA, helperB), [group], [root, helperA, helperB], Allowed);

        Assert.True(plan.Proceed);
        Assert.Same(group, plan.TargetGroup);
        // Root 优先，其余按 PID 升序
        Assert.Equal([300, 150, 200], plan.Candidates.Select(p => p.ProcessId));
    }

    [Fact]
    public void 规划_原进程全部退出_AlreadyExited_新实例组绝不匹配()
    {
        // 旧应用退出；同路径、同显示名的新实例启动（PID 999）
        var oldInstance = Snap(100, "app.exe", @"C:\Apps\app.exe");
        var newInstance = Snap(999, "app.exe", @"C:\Apps\app.exe");
        var newGroup = Group("Test App", [newInstance], [newInstance]);

        var plan = TerminationPlanner.Plan(Request(oldInstance), [newGroup], [newInstance], Allowed);

        Assert.False(plan.Proceed);
        Assert.Equal(TerminationStatus.AlreadyExited, plan.Status);
        Assert.Null(plan.TargetGroup); // 绝不把新实例当目标
    }

    [Fact]
    public void 规划_PID被复用_TargetChanged()
    {
        var oldProcess = Snap(100, "app.exe");
        // Fresh 快照中 PID 100 仍在，但 StartTime 已不同（复用）
        var reused = new ProcessSnapshot
        {
            Identity = new ProcessIdentity(100, BaseTime.AddHours(1)),
            Name = "other.exe",
        };

        var plan = TerminationPlanner.Plan(Request(oldProcess), [], [reused], Allowed);

        Assert.False(plan.Proceed);
        Assert.Equal(TerminationStatus.TargetChanged, plan.Status);
    }

    [Fact]
    public void 规划_身份分散在多个组_AmbiguousTarget()
    {
        var memberA = Snap(100, "app.exe");
        var memberB = Snap(200, "helper.exe");
        var groupA = Group("App A", [memberA], [memberA]);
        var groupB = Group("App B", [memberB], [memberB]);

        var plan = TerminationPlanner.Plan(Request(memberA, memberB), [groupA, groupB], [memberA, memberB], Allowed);

        Assert.False(plan.Proceed);
        Assert.Equal(TerminationStatus.AmbiguousTarget, plan.Status);
        Assert.Equal(TerminationFailureReason.AmbiguousTarget, plan.FailureReason);
    }

    [Theory]
    [InlineData(SafetyDecision.Blocked, TerminationStatus.Blocked)]
    [InlineData(SafetyDecision.Indeterminate, TerminationStatus.Indeterminate)]
    [InlineData(SafetyDecision.RequiresElevation, TerminationStatus.RequiresElevation)]
    public void 规划_FreshSafety拒绝_一律不执行(SafetyDecision decision, TerminationStatus expected)
    {
        var target = Snap(100, "app.exe");
        var group = Group("Test App", [target], [target]);

        var plan = TerminationPlanner.Plan(
            Request(target), [group], [target], _ => SafetyOf(decision));

        Assert.False(plan.Proceed);
        Assert.Equal(expected, plan.Status);
        Assert.Equal(TerminationFailureReason.SafetyRejected, plan.FailureReason);
    }

    [Fact]
    public void 规划_全部预期身份StartTime不可用_FailClosed拒绝()
    {
        var unreliable = new ProcessSnapshot
        {
            Identity = new ProcessIdentity(100, null),
            Name = "app.exe",
        };
        var group = Group("Test App", [unreliable], [unreliable]);

        var plan = TerminationPlanner.Plan(Request(unreliable), [group], [unreliable], Allowed);

        Assert.False(plan.Proceed);
        Assert.Equal(TerminationStatus.Failed, plan.Status);
        Assert.Equal(TerminationFailureReason.UnreliableIdentity, plan.FailureReason);
    }

    [Fact]
    public void 规划_候选中存在SnapshotStartTime不可靠的成员_FailClosed取消整个操作()
    {
        var root = Snap(100, "app.exe");
        var protectedHelper = new ProcessSnapshot
        {
            Identity = new ProcessIdentity(200, null), // 无法读取启动时间
            Name = "helper.exe",
        };
        var group = Group("Test App", [root, protectedHelper], [root]);

        var plan = TerminationPlanner.Plan(Request(root), [group], [root, protectedHelper], Allowed);

        Assert.False(plan.Proceed);
        Assert.Equal(TerminationStatus.Failed, plan.Status);
        Assert.Equal(TerminationFailureReason.UnreliableIdentity, plan.FailureReason);
    }

    [Fact]
    public void 规划_部分原成员已退出_仍定位到含存活成员的组()
    {
        var exited = Snap(100, "app.exe");
        var alive = Snap(200, "helper.exe");
        var group = Group("Test App", [alive], [alive]);

        var plan = TerminationPlanner.Plan(Request(exited, alive), [group], [alive], Allowed);

        Assert.True(plan.Proceed);
        Assert.Same(group, plan.TargetGroup);
        Assert.Equal([200], plan.Candidates.Select(p => p.ProcessId));
    }

    [Fact]
    public void 规划_无效请求_拒绝()
    {
        var empty = new TerminationRequest("App", [], DateTimeOffset.UtcNow);

        var plan = TerminationPlanner.Plan(empty, [], [], Allowed);

        Assert.False(plan.Proceed);
        Assert.Equal(TerminationStatus.Failed, plan.Status);
    }

    [Fact]
    public void 规划_StableKey同路径新组_不因路径相同被匹配()
    {
        // 目标：旧 python 任务（PID 100）。Fresh 分组里是同路径新 python 任务（PID 900）。
        // 即使 exe path / DisplayName / StableKey 相似，也绝不匹配。
        var oldTask = Snap(100, "python.exe", @"C:\Python312\python.exe");
        var newTask = Snap(900, "python.exe", @"C:\Python312\python.exe");
        var newGroup = Group("Python", [newTask], [newTask]);

        var plan = TerminationPlanner.Plan(Request(oldTask), [newGroup], [newTask], Allowed);

        Assert.False(plan.Proceed);
        Assert.Equal(TerminationStatus.AlreadyExited, plan.Status);
        Assert.Null(plan.TargetGroup);
    }

    // ================= P6.3：Medium 组破坏性范围门禁 =================

    [Fact]
    public void 规划_High置信度组_候选扩展到全组含新增成员()
    {
        // 强证据组（Verified 父子 + 同 exe）：请求只锚定 root，Fresh 组新纳入的 helper 也进候选
        var root = Snap(100, "app.exe");
        var newHelper = Snap(200, "app.exe");
        var group = Group("Test App", [root, newHelper], [root], GroupingConfidence.High);

        var plan = TerminationPlanner.Plan(Request(root), [group], [root, newHelper], Allowed);

        Assert.True(plan.Proceed);
        Assert.Equal([100, 200], plan.Candidates.Select(p => p.ProcessId));
    }

    [Fact]
    public void 规划_Medium置信度组_候选收窄为请求锚点_新增成员绝不纳入()
    {
        // 弱证据组（如同安装目录聚进来的无关进程）：即使 Fresh 分组把它和锚点并成一组，
        // 也不授权自动扩展杀伤范围——候选仅限请求确认的 exact identity
        var root = Snap(100, "app.exe");
        var weakMember = Snap(200, "other.exe");
        var group = Group("Test App", [root, weakMember], [root], GroupingConfidence.Medium);

        var plan = TerminationPlanner.Plan(Request(root), [group], [root, weakMember], Allowed);

        Assert.True(plan.Proceed);
        Assert.Equal([100], plan.Candidates.Select(p => p.ProcessId));
        Assert.Contains("弱证据", plan.Message);
    }

    [Fact]
    public void 规划_Medium置信度组_全部成员均为锚点时候选不变()
    {
        // Medium 组但请求锚点覆盖全组（UI 常态：请求就是该行全部成员）→ 收窄不损失任何成员
        var root = Snap(100, "app.exe");
        var member = Snap(200, "other.exe");
        var group = Group("Test App", [root, member], [root], GroupingConfidence.Medium);

        var plan = TerminationPlanner.Plan(Request(root, member), [group], [root, member], Allowed);

        Assert.True(plan.Proceed);
        Assert.Equal([100, 200], plan.Candidates.Select(p => p.ProcessId));
    }

    [Fact]
    public void 规划_Medium组非锚点成员StartTime不可靠_不触发取消()
    {
        // 门禁后非锚点成员不进候选，其 StartTime 不可靠与本操作无关，不得放大为整组取消
        var root = Snap(100, "app.exe");
        var unreliable = new ProcessSnapshot
        {
            Identity = new ProcessIdentity(200, null),
            Name = "other.exe",
        };
        var group = Group("Test App", [root, unreliable], [root], GroupingConfidence.Medium);

        var plan = TerminationPlanner.Plan(Request(root), [group], [root, unreliable], Allowed);

        Assert.True(plan.Proceed);
        Assert.Equal([100], plan.Candidates.Select(p => p.ProcessId));
    }

    [Fact]
    public void 规划_High组候选StartTime不可靠_仍FailClosed取消()
    {
        // 强证据组全组候选时，组内 StartTime 不可靠成员仍触发 fail-closed（原防线保留）
        var root = Snap(100, "app.exe");
        var protectedHelper = new ProcessSnapshot
        {
            Identity = new ProcessIdentity(200, null),
            Name = "helper.exe",
        };
        var group = Group("Test App", [root, protectedHelper], [root], GroupingConfidence.High);

        var plan = TerminationPlanner.Plan(Request(root), [group], [root, protectedHelper], Allowed);

        Assert.False(plan.Proceed);
        Assert.Equal(TerminationStatus.Failed, plan.Status);
        Assert.Equal(TerminationFailureReason.UnreliableIdentity, plan.FailureReason);
    }
}
