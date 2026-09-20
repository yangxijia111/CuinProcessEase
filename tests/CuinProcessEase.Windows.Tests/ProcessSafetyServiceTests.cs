using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Windows.Safety;
using Xunit;

namespace CuinProcessEase.Windows.Tests;

/// <summary>
/// ProcessSafetyService 真实环境测试：验证公开 Windows API 采集与决策降级。
/// 注意：本测试类不结束任何进程，只做判断。
/// </summary>
public sealed class ProcessSafetyServiceTests
{
    private static readonly ProcessSafetyService Service = new();

    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ProcessSnapshot Snap(
        int pid,
        string name,
        string? path = @"C:\Program Files\App\app.exe",
        string? user = @"DESKTOP\user",
        int? session = 1,
        bool? elevated = false) => new()
    {
        Identity = new ProcessIdentity(pid, BaseTime),
        Name = name,
        ExecutablePath = path,
        UserName = user,
        SessionId = session,
        IsElevated = elevated,
    };

    // ================= 纯决策引擎：抽象状态组合（不依赖真实权限） =================

    [Fact]
    public void 决策引擎_IsProcessCritical为true_Protected_Blocked()
    {
        ProcessSafetyResult result = SafetyDecisionEngine.Evaluate(
            Snap(100, "someapp.exe"), isSelf: false, isCritical: true, protectionLevel: 0);

        Assert.Equal(RiskLevel.Protected, result.RiskLevel);
        Assert.Equal(SafetyDecision.Blocked, result.Decision);
        Assert.True(result.Reasons.HasFlag(SafetyReason.CriticalWindowsProcess));
        Assert.True(result.IsCritical);
    }

    [Fact]
    public void 决策引擎_PPL保护级别_Protected_Blocked_区分PP与PPL()
    {
        // PPL (Lite=1)
        ProcessSafetyResult ppl = SafetyDecisionEngine.Evaluate(
            Snap(100, "MsMpEng.exe"), isSelf: false, isCritical: false, protectionLevel: 1);
        Assert.Equal(RiskLevel.Protected, ppl.RiskLevel);
        Assert.True(ppl.Reasons.HasFlag(SafetyReason.ProtectedProcessLight));

        // PP (Windows=2)
        ProcessSafetyResult pp = SafetyDecisionEngine.Evaluate(
            Snap(100, "protected.exe"), isSelf: false, isCritical: false, protectionLevel: 2);
        Assert.Equal(RiskLevel.Protected, pp.RiskLevel);
        Assert.True(pp.Reasons.HasFlag(SafetyReason.ProtectedProcess));
    }

    [Fact]
    public void 决策引擎_ProtectionLevel查询失败为null_不得当作NONE()
    {
        // 查询失败（null）+ 身份信息充足（有路径有用户名）：不因查询失败而 Protected，
        // 但也不能证明有保护 → 按身份走 Normal（信息充分时可判断）
        ProcessSafetyResult result = SafetyDecisionEngine.Evaluate(
            Snap(100, "normal.exe"), isSelf: false, isCritical: null, protectionLevel: null);

        Assert.Equal(RiskLevel.Normal, result.RiskLevel);
        Assert.Equal(SafetyDecision.Allowed, result.Decision);
        Assert.Null(result.IsCritical);
        Assert.Null(result.ProtectionLevel);
    }

    [Fact]
    public void 决策引擎_查询失败且身份不足_Unknown_Indeterminate()
    {
        ProcessSafetyResult result = SafetyDecisionEngine.Evaluate(
            Snap(100, "mystery.exe", path: null, user: null, session: null),
            isSelf: false, isCritical: null, protectionLevel: null);

        Assert.Equal(RiskLevel.Unknown, result.RiskLevel);
        Assert.Equal(SafetyDecision.Indeterminate, result.Decision);
        Assert.True(result.Reasons.HasFlag(SafetyReason.InsufficientIdentity));
        Assert.True(result.Reasons.HasFlag(SafetyReason.UnknownSafetyState));
    }

    [Fact]
    public void 决策引擎_管理员普通应用_Elevated_RequiresElevation而非Blocked()
    {
        ProcessSafetyResult result = SafetyDecisionEngine.Evaluate(
            Snap(100, "notepad.exe", elevated: true), isSelf: false, isCritical: false, protectionLevel: 0);

        Assert.Equal(RiskLevel.Elevated, result.RiskLevel);
        Assert.Equal(SafetyDecision.RequiresElevation, result.Decision);
        Assert.True(result.Reasons.HasFlag(SafetyReason.ElevatedProcess));
    }

    [Fact]
    public void 决策引擎_SYSTEM账户_Session0_System_Blocked()
    {
        ProcessSafetyResult result = SafetyDecisionEngine.Evaluate(
            Snap(100, "somesvc.exe", user: @"NT AUTHORITY\SYSTEM", session: 0),
            isSelf: false, isCritical: false, protectionLevel: 0);

        Assert.Equal(RiskLevel.System, result.RiskLevel);
        Assert.Equal(SafetyDecision.Blocked, result.Decision);
        Assert.True(result.Reasons.HasFlag(SafetyReason.SystemAccount));
        Assert.True(result.Reasons.HasFlag(SafetyReason.SessionZero));
    }

    [Fact]
    public void 决策引擎_仅Windows路径不构成System_普通用户程序Allowed()
    {
        // 用户在会话 1 运行 System32\notepad.exe：路径在 C:\Windows 下但不是系统进程
        ProcessSafetyResult result = SafetyDecisionEngine.Evaluate(
            Snap(100, "notepad.exe", path: @"C:\Windows\System32\notepad.exe", session: 1),
            isSelf: false, isCritical: false, protectionLevel: 0);

        Assert.Equal(RiskLevel.Normal, result.RiskLevel);
        Assert.Equal(SafetyDecision.Allowed, result.Decision);
    }

    [Fact]
    public void 决策引擎_自身进程_永远Blocked()
    {
        ProcessSafetyResult byName = SafetyDecisionEngine.Evaluate(
            Snap(100, "CuinProcessEase.exe"), isSelf: false, isCritical: false, protectionLevel: 0);
        Assert.Equal(SafetyDecision.Blocked, byName.Decision);
        Assert.True(byName.Reasons.HasFlag(SafetyReason.SelfProcess));

        ProcessSafetyResult byPid = SafetyDecisionEngine.Evaluate(
            Snap(100, "anything.exe"), isSelf: true, isCritical: false, protectionLevel: 0);
        Assert.Equal(SafetyDecision.Blocked, byPid.Decision);
        Assert.True(byPid.Reasons.HasFlag(SafetyReason.SelfProcess));
    }

    // ================= 真实系统进程 =================

    [Fact]
    public async Task 自身进程_SelfProcess_Blocked()
    {
        // 用真实快照评估 testhost 自身（= 服务进程自身 PID）
        var snapshot = await new Services.ProcessSnapshotService().CaptureAsync();
        ProcessSnapshot self = snapshot.Processes.Single(p => p.ProcessId == Environment.ProcessId);

        ProcessSafetyResult result = Service.Assess(self);

        Assert.Equal(SafetyDecision.Blocked, result.Decision);
        Assert.True(result.Reasons.HasFlag(SafetyReason.SelfProcess));
    }

    [Fact]
    public async Task 关键系统进程_全部Blocked绝不Allowed()
    {
        var snapshot = await new Services.ProcessSnapshotService().CaptureAsync();

        string[] mustBlock = { "System", "Registry", "csrss.exe", "lsass.exe", "services.exe", "wininit.exe", "winlogon.exe", "smss.exe" };

        foreach (string name in mustBlock)
        {
            ProcessSnapshot? process = snapshot.Processes.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (process is null)
            {
                continue; // 当前系统不存在则跳过（csrss 每会话一个，至少有一个）
            }

            ProcessSafetyResult result = Service.Assess(process);

            Assert.NotEqual(SafetyDecision.Allowed, result.Decision);
            Assert.NotEqual(SafetyDecision.RequiresElevation, result.Decision);
            Assert.Equal(SafetyDecision.Blocked, result.Decision);
            Assert.True(result.RiskLevel is RiskLevel.Protected or RiskLevel.System,
                $"{name} 应为 Protected/System，实际 {result.RiskLevel}");
        }
    }

    [Fact]
    public async Task PID零_关键名单Blocked()
    {
        var snapshot = await new Services.ProcessSnapshotService().CaptureAsync();
        ProcessSnapshot idle = snapshot.Processes.Single(p => p.ProcessId == 0);

        ProcessSafetyResult result = Service.Assess(idle);

        Assert.Equal(SafetyDecision.Blocked, result.Decision);
        Assert.Equal(RiskLevel.Protected, result.RiskLevel);
        Assert.True(result.Reasons.HasFlag(SafetyReason.KnownCriticalProcess));
    }

    [Fact]
    public async Task 普通用户应用_Allowed_Normal()
    {
        var snapshot = await new Services.ProcessSnapshotService().CaptureAsync();

        // 当前用户会话中的自身之外进程：找一个路径可读、非系统路径的用户进程
        ProcessSnapshot? normalApp = snapshot.Processes.FirstOrDefault(p =>
            p.ProcessId != Environment.ProcessId
            && p.ExecutablePath is not null
            && !p.ExecutablePath.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase)
            && p.UserName is not null
            && !p.UserName.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase)
            && p.SessionId > 0
            && p.IsElevated == false);

        Assert.NotNull(normalApp); // 普通用户环境必有此进程（如 chrome/资源管理器扩展等）
        ProcessSafetyResult result = Service.Assess(normalApp!);

        Assert.Equal(RiskLevel.Normal, result.RiskLevel);
        Assert.Equal(SafetyDecision.Allowed, result.Decision);
    }

    [Fact]
    public async Task explorer与dwm_用户会话组件_Allowed或System但不Protected误判()
    {
        var snapshot = await new Services.ProcessSnapshotService().CaptureAsync();

        ProcessSnapshot? explorer = snapshot.Processes.FirstOrDefault(
            p => string.Equals(p.Name, "explorer.exe", StringComparison.OrdinalIgnoreCase));

        if (explorer is not null)
        {
            ProcessSafetyResult result = Service.Assess(explorer);
            // explorer 由用户启动、会话非 0：普通应用（可能 Elevated）
            Assert.NotEqual(SafetyDecision.Blocked, result.Decision);
            Assert.NotEqual(RiskLevel.Protected, result.RiskLevel);
        }
    }

    [Fact]
    public async Task MsMpEng若存在_PPL_Protected()
    {
        var snapshot = await new Services.ProcessSnapshotService().CaptureAsync();

        ProcessSnapshot? defender = snapshot.Processes.FirstOrDefault(
            p => string.Equals(p.Name, "MsMpEng.exe", StringComparison.OrdinalIgnoreCase));

        if (defender is null)
        {
            return; // 未开启 Defender 则跳过
        }

        ProcessSafetyResult result = Service.Assess(defender);

        Assert.Equal(RiskLevel.Protected, result.RiskLevel);
        Assert.Equal(SafetyDecision.Blocked, result.Decision);
        // PPL-AM：实测 ProtectionLevel 应为 6（Antimalware）；查询不到也应被名单外的
        // SYSTEM 账户兜底为 System/Blocked，绝不允许 Allowed
        Assert.NotEqual(SafetyDecision.Allowed, result.Decision);
    }

    [Fact]
    public async Task 真实服务进程_Session0_System_Blocked()
    {
        var snapshot = await new Services.ProcessSnapshotService().CaptureAsync();

        // 普通权限下 SYSTEM 进程的 UserName（令牌）通常读不到，
        // 用 SessionId==0 定位服务会话进程（如 svchost），Session 0 是充分的 System 证据
        ProcessSnapshot? serviceProcess = snapshot.Processes.FirstOrDefault(p =>
            p.SessionId == 0
            && !KnownCriticalProcesses.IsKnownCritical(p.ProcessId, p.Name));

        Assert.NotNull(serviceProcess); // 任何 Windows 系统必有 Session 0 服务进程
        ProcessSafetyResult result = Service.Assess(serviceProcess!);

        Assert.Equal(SafetyDecision.Blocked, result.Decision);
        Assert.True(result.Reasons.HasFlag(SafetyReason.SessionZero));
        Assert.True(result.RiskLevel is RiskLevel.System or RiskLevel.Protected or RiskLevel.Unknown);
        Assert.NotEqual(SafetyDecision.Allowed, result.Decision);
    }
}
