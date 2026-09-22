using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Interfaces;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Core.Termination;
using CuinProcessEase.Windows.Termination;
using Xunit;

namespace CuinProcessEase.Windows.Tests;

/// <summary>
/// 终止引擎安全不变量的 fake 测试（无真实进程）：
/// 通过注入 fake interop 精确控制 OpenProcess / GetProcessTimes / 等待结果，
/// 断言 Preflight 失败 → 0 破坏性动作、1-tick 身份不匹配拒绝、
/// TimedOut 后续成功不产生假 residual、Final Rescan 修正、单进程绝不伤及同组。
/// </summary>
public sealed class TerminationEngineFakeTests
{
    private const uint WaitObject0 = 0x0000;
    private const uint WaitTimeout = 0x0102;

    private const long BaseFileTime = 133_528_256_000_000_000;

    // ================= fake 设施 =================

    /// <summary>fake 进程状态：snapshot 视角与句柄视角可独立控制（用于构造身份不匹配）。</summary>
    private sealed class FakeProcess
    {
        public required long CreationFileTimeUtc { get; set; }

        /// <summary>句柄 GetProcessTimes 返回的创建时间；null 时与 Snapshot 一致（正常路径）。</summary>
        public long? HandleCreationFileTimeUtc { get; set; }

        public string Name { get; set; } = "app.exe";

        public string ExecutablePath { get; set; } = @"C:\FakeApps\app.exe";

        public int? ParentPid { get; set; }

        public bool OpenFails { get; set; }

        public int OpenWin32Error { get; set; } = 5; // ERROR_ACCESS_DENIED

        public bool TimesFails { get; set; }

        public bool Exited { get; set; }

        /// <summary>TerminateProcess 成功后进程是否真的退出（false = 挂死不退，测试 TimedOut / Residual）。</summary>
        public bool ExitsOnTerminate { get; set; } = true;

        /// <summary>长等待返回 WAIT_OBJECT_0 后是否标记退出（false = 报告 signaled 但 snapshot 仍存在）。</summary>
        public bool ExitsOnWaitConfirmed { get; set; } = true;

        /// <summary>长等待（&gt;0ms）结果脚本，按次序弹出；耗尽后按 Exited 决定。</summary>
        public List<uint> LongWaitResults { get; } = [];

        /// <summary>
        /// 已退出进程在快照中仍可见的剩余次数（模拟“枚举瞬间进程刚退出，旧快照还含它”的竞态；
        /// 每次 Capture 递减）。测试 Preflight 的 AlreadyExited 路径。
        /// </summary>
        public int ExitRendersRemaining { get; set; }

        /// <summary>
        /// 经过 N 次 Capture 后自行退出（模拟挂死进程在清理轮间被外部结束/自行退出）：
        /// 每次 Capture 递减，减到 0 时置 <see cref="Exited"/> 且当轮起从快照消失。
        /// </summary>
        public int SelfExitAfterCaptures { get; set; }

        /// <summary>
        /// 经过 N 次 Capture 后才出现在快照中（模拟清理轮浮现的 helper / PID 复用者）：
        /// 计数耗尽前每次 Capture 递减且当轮跳过。
        /// </summary>
        public int AppearAfterCaptures { get; set; }

        /// <summary>快照中的 StartTime 置为 null（模拟权限不足读不到；不影响句柄视角）。</summary>
        public bool SnapshotStartTimeNull { get; set; }

        /// <summary>
        /// PID 复用模拟：进程退出后的下一次 Capture 以该创建时间“复活”为新实例
        /// （新身份与原 identity 无关，用于 Final Rescan 的 PidReused / Uncertain 场景）。
        /// </summary>
        public long? ResurrectFileTimeUtc { get; set; }

        /// <summary>复活后的新实例：快照 StartTime 读不到（仅复活后生效，不影响原实例）。</summary>
        public bool ResurrectSnapshotStartTimeNull { get; set; }

        /// <summary>复活后的新实例：句柄 GetProcessTimes 失败（仅复活后生效）。</summary>
        public bool ResurrectTimesFails { get; set; }

        public int WindowCount { get; set; } = 1;
    }

    /// <summary>
    /// fake interop：句柄值直接用 PID；记录全部破坏性调用（TerminateProcess / WM_CLOSE），
    /// 供“整组取消必须 0 破坏性动作”断言使用。
    /// </summary>
    private sealed class FakeTerminationInterop : ITerminationInterop
    {
        public Dictionary<uint, FakeProcess> Processes { get; } = [];

        public List<string> DestructiveCalls { get; } = [];

        public int TerminateCount => DestructiveCalls.Count(c => c.StartsWith("Terminate:", StringComparison.Ordinal));

        public int CloseMessageCount => DestructiveCalls.Count(c => c.StartsWith("WM_CLOSE:", StringComparison.Ordinal));

        public IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId, out int win32Error)
        {
            win32Error = 0;
            if (Processes.TryGetValue(processId, out FakeProcess? process) && !process.OpenFails)
            {
                return new IntPtr(processId); // 句柄值 = PID（测试 PID 均非零）
            }

            win32Error = process?.OpenWin32Error ?? 87; // ERROR_INVALID_PARAMETER
            return IntPtr.Zero;
        }

        public bool TryGetCreationFileTime(IntPtr processHandle, out long creationFileTimeUtc)
        {
            creationFileTimeUtc = 0;
            if (Processes.TryGetValue((uint)processHandle, out FakeProcess? process)
                && !process.TimesFails)
            {
                creationFileTimeUtc = process.HandleCreationFileTimeUtc ?? process.CreationFileTimeUtc;
                return creationFileTimeUtc > 0;
            }

            return false;
        }

        public bool TerminateProcess(IntPtr processHandle, uint exitCode, out int win32Error)
        {
            DestructiveCalls.Add($"Terminate:{(uint)processHandle}");
            win32Error = 0;
            if (Processes.TryGetValue((uint)processHandle, out FakeProcess? process)
                && process.ExitsOnTerminate)
            {
                process.Exited = true;
                return true;
            }

            if (Processes.ContainsKey((uint)processHandle))
            {
                return true; // 成功但进程挂死不退（ExitsOnTerminate=false）
            }

            win32Error = 87;
            return false;
        }

        public uint WaitForSingleObject(IntPtr processHandle, uint milliseconds)
        {
            if (!Processes.TryGetValue((uint)processHandle, out FakeProcess? process))
            {
                return 0xFFFFFFFF; // WAIT_FAILED
            }

            if (milliseconds == 0)
            {
                return process.Exited ? WaitObject0 : WaitTimeout;
            }

            if (process.LongWaitResults.Count > 0)
            {
                uint result = process.LongWaitResults[0];
                process.LongWaitResults.RemoveAt(0);
                if (result == WaitObject0 && process.ExitsOnWaitConfirmed)
                {
                    process.Exited = true;
                }

                return result;
            }

            return process.Exited ? WaitObject0 : WaitTimeout;
        }

        public bool PostCloseMessage(IntPtr windowHandle)
        {
            DestructiveCalls.Add($"WM_CLOSE:{windowHandle}");
            return true;
        }

        public IReadOnlyList<IntPtr> FindTopLevelWindows(int processId)
            => Processes.TryGetValue((uint)processId, out FakeProcess? process)
                ? Enumerable.Range(1, Math.Max(0, process.WindowCount))
                    .Select(i => new IntPtr((processId << 8) | i))
                    .ToList()
                : [];

        public bool CloseHandle(IntPtr handle) => true;
    }

    /// <summary>fake 快照服务：直接从 fake interop 的进程状态派生（Exited 的进程不出现在快照）。</summary>
    private sealed class FakeSnapshotService : IProcessSnapshotService
    {
        public required FakeTerminationInterop Interop { get; init; }

        public Task<ProcessSnapshotCollection> CaptureAsync(CancellationToken cancellationToken = default)
        {
            var snapshots = new List<ProcessSnapshot>();
            foreach (KeyValuePair<uint, FakeProcess> kv in Interop.Processes)
            {
                FakeProcess process = kv.Value;

                // PID 复用：旧实例退出后的下一次 Capture 以新创建时间复活为新实例
                if (process.Exited && process.ResurrectFileTimeUtc is { } reusedFileTime)
                {
                    process.CreationFileTimeUtc = reusedFileTime;
                    process.ResurrectFileTimeUtc = null;
                    process.Exited = false;
                    process.SnapshotStartTimeNull = process.ResurrectSnapshotStartTimeNull;
                    process.TimesFails = process.ResurrectTimesFails;
                }

                // 自行退出：计数耗尽当轮起从快照消失
                if (!process.Exited && process.SelfExitAfterCaptures > 0)
                {
                    process.SelfExitAfterCaptures--;
                    if (process.SelfExitAfterCaptures == 0)
                    {
                        process.Exited = true;
                    }
                }

                // 延迟浮现：计数耗尽前不出现在快照
                if (process.AppearAfterCaptures > 0)
                {
                    process.AppearAfterCaptures--;
                    continue;
                }

                bool exitedStillRenders = process.Exited && process.ExitRendersRemaining > 0;
                if (!process.Exited || exitedStillRenders)
                {
                    if (exitedStillRenders)
                    {
                        process.ExitRendersRemaining--;
                    }

                    snapshots.Add(new ProcessSnapshot
                    {
                        Identity = new ProcessIdentity(
                            (int)kv.Key,
                            process.SnapshotStartTimeNull
                                ? null
                                : DateTime.FromFileTimeUtc(process.CreationFileTimeUtc)),
                        ParentProcessId = process.ParentPid,
                        Name = process.Name,
                        ExecutablePath = process.ExecutablePath,
                        SessionId = 1,
                    });
                }
            }

            return Task.FromResult(new ProcessSnapshotCollection
            {
                CapturedAtUtc = DateTime.UtcNow,
                Processes = snapshots,
            });
        }
    }

    /// <summary>fake Fresh Safety：决策可配置，默认 Allowed。</summary>
    private sealed class FakeSafetyService : IProcessSafetyService
    {
        public SafetyDecision Decision { get; set; } = SafetyDecision.Allowed;

        public ProcessSafetyResult Assess(ProcessSnapshot process) => new()
        {
            Identity = process.Identity,
            ProcessName = process.Name,
            Decision = Decision,
            RiskLevel = RiskLevel.Normal,
        };

        public ApplicationSafetyResult Assess(ApplicationGroup group) => new()
        {
            DisplayName = group.Identity.DisplayName,
            MemberResults = [],
            Decision = Decision,
            OverallRiskLevel = RiskLevel.Normal,
        };
    }

    private static (FakeTerminationInterop Interop, ProcessTerminationService Service) CreateService()
    {
        var interop = new FakeTerminationInterop();
        var service = new ProcessTerminationService(
            new FakeSnapshotService { Interop = interop },
            new FakeSafetyService(),
            interop);
        return (interop, service);
    }

    private static FakeProcess AddProcess(
        FakeTerminationInterop interop, uint pid, long creationFileTime, int? parentPid = null) 
    {
        var process = new FakeProcess { CreationFileTimeUtc = creationFileTime, ParentPid = parentPid };
        interop.Processes[pid] = process;
        return process;
    }

    private static ProcessIdentity IdentityOf(uint pid, long creationFileTime)
        => new((int)pid, DateTime.FromFileTimeUtc(creationFileTime));

    private static TerminationRequest GroupRequest(params ProcessIdentity[] identities) => new(
        "Fake App", identities, DateTimeOffset.UtcNow);

    /// <summary>携带 ExplicitWeakGroup 弱组范围授权的请求（P6.4）。</summary>
    private static TerminationRequest WeakConsentGroupRequest(params ProcessIdentity[] identities) => new(
        "Fake App", identities, DateTimeOffset.UtcNow)
    {
        ScopeConsent = TerminationScopeConsent.ExplicitWeakGroup,
    };

    // ================= Preflight fail-all =================

    [Fact]
    public async Task 终止_Preflight句柄打开失败_整组取消_零破坏性动作()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();
        FakeProcess parent = AddProcess(interop, 100, BaseFileTime);
        AddProcess(interop, 200, BaseFileTime + 1_000, parentPid: 100).OpenFails = true;

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime), IdentityOf(200, BaseFileTime + 1_000)));

        // B 句柄打不开 → 身份无法验证 → 整组取消：A 一个都不杀
        Assert.Equal(TerminationStatus.Failed, result.Status);
        Assert.Equal(TerminationFailureReason.ExecutionError, result.FailureReason);
        Assert.Empty(interop.DestructiveCalls);
        Assert.False(parent.Exited);
        Assert.Contains("取消整组", result.Message);
        // 取消结果保留 B 的 AccessDenied 诊断
        Assert.Contains(result.ProcessResults, r => r.Pid == 200 && r.Result == ProcessTerminationStatus.AccessDenied);
    }

    [Fact]
    public async Task 终止_句柄CreationTime差1个tick_IdentityMismatch_零破坏性动作()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();
        // Snapshot 视角创建时间 = T；句柄查询返回 T + 1 FILETIME tick（100ns）
        FakeProcess target = AddProcess(interop, 100, BaseFileTime);
        target.HandleCreationFileTimeUtc = BaseFileTime + 1;

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        Assert.Equal(TerminationStatus.IdentityMismatch, result.Status);
        Assert.Equal(TerminationFailureReason.IdentityVerificationFailed, result.FailureReason);
        Assert.Empty(interop.DestructiveCalls); // 0 TerminateProcess、0 WM_CLOSE
        Assert.False(target.Exited);
    }

    [Fact]
    public async Task 终止_GetProcessTimes失败_整组取消_零破坏性动作()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();
        FakeProcess target = AddProcess(interop, 100, BaseFileTime);
        target.TimesFails = true;

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        Assert.Equal(TerminationStatus.Failed, result.Status);
        Assert.Equal(TerminationFailureReason.UnreliableIdentity, result.FailureReason);
        Assert.Empty(interop.DestructiveCalls);
        Assert.False(target.Exited);
    }

    [Fact]
    public async Task 终止_组内成员Preflight时已退出_AlreadyExited不算失败_其余正常执行()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();
        FakeProcess survivor = AddProcess(interop, 100, BaseFileTime);
        // B 仍在首次快照中，但句柄已 signaled（执行前刚退出）→ Preflight 记 AlreadyExited，不取消整组
        FakeProcess exited = AddProcess(interop, 200, BaseFileTime + 1_000, parentPid: 100);
        exited.Exited = true;
        exited.ExitRendersRemaining = 1;

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime), IdentityOf(200, BaseFileTime + 1_000)));

        Assert.Equal(TerminationStatus.Success, result.Status);
        Assert.Equal(1, interop.TerminateCount); // 只终止存活成员
        Assert.True(survivor.Exited);
        Assert.Contains(result.ProcessResults, r => r.Pid == 200
            && r.Result == ProcessTerminationStatus.AlreadyExited);
        Assert.Contains(result.ProcessResults, r => r.Pid == 100
            && r.Result == ProcessTerminationStatus.Terminated);
    }

    // ================= 残留结果归并 =================

    [Fact]
    public async Task 终止_TimedOut后续残留轮Terminated_最终Success且无假residual()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();
        // 第 0 轮 TerminateProcess 成功但有限等待超时（挂死）；第 1 轮等待确认退出
        FakeProcess target = AddProcess(interop, 100, BaseFileTime);
        target.ExitsOnTerminate = false;
        target.LongWaitResults.AddRange([WaitTimeout, WaitObject0]);

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        Assert.Equal(2, interop.TerminateCount); // 第 0 轮 + 残留清理轮
        Assert.Equal(TerminationStatus.Success, result.Status);
        Assert.Equal(0, result.ResidualCount);
        // 最终逐进程状态只剩最后一次尝试（Terminated），不保留第 0 轮的 TimedOut 假残留
        var final = Assert.Single(result.ProcessResults);
        Assert.Equal(ProcessTerminationStatus.Terminated, final.Result);
        Assert.True(final.ConfirmedExited);
    }

    [Fact]
    public async Task 终止_Terminated但FinalRescan身份仍存在_修正为Residual()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();
        // TerminateProcess 成功、等待确认 signaled，但进程身份仍在 Fresh 快照中
        FakeProcess target = AddProcess(interop, 100, BaseFileTime);
        target.ExitsOnTerminate = false;
        target.ExitsOnWaitConfirmed = false;
        target.LongWaitResults.Add(WaitObject0);

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        Assert.Equal(1, interop.TerminateCount); // Final Rescan 只验证，绝不再终止
        // 唯一目标身份仍存在：无一确认退出 → Failed（Residual），绝不虚报 Success
        Assert.Equal(TerminationStatus.Failed, result.Status);
        Assert.Equal(TerminationFailureReason.ResidualRemain, result.FailureReason);
        Assert.Equal(1, result.ResidualCount);
        Assert.Equal(ProcessTerminationStatus.Residual, Assert.Single(result.ProcessResults).Result);
    }

    // ================= 单进程管线 =================

    [Fact]
    public async Task 单进程Force_同组其他成员绝不纳入候选()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();
        // 父子同 exe 路径：组模式会把两个都杀；单进程模式只允许目标本身
        FakeProcess parent = AddProcess(interop, 100, BaseFileTime);
        FakeProcess child = AddProcess(interop, 200, BaseFileTime + 1_000, parentPid: 100);

        ApplicationTerminationResult result = await service.ForceTerminateProcessAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        Assert.Equal(TerminationStatus.Success, result.Status);
        Assert.True(parent.Exited);
        Assert.False(child.Exited); // 同组 child 必须保持存活
        Assert.Equal(1, interop.TerminateCount);
        Assert.Equal(100, Assert.Single(result.ProcessResults).Pid);
    }

    [Fact]
    public async Task 单进程Force_句柄打开失败_取消且零破坏性动作()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();
        FakeProcess target = AddProcess(interop, 100, BaseFileTime);
        target.OpenFails = true;

        ApplicationTerminationResult result = await service.ForceTerminateProcessAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        Assert.Equal(TerminationStatus.Failed, result.Status);
        Assert.Equal(TerminationFailureReason.ExecutionError, result.FailureReason);
        Assert.Empty(interop.DestructiveCalls);
        Assert.False(target.Exited);
    }

    [Fact]
    public async Task 单进程Force_请求含多个身份_拒绝执行()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();
        AddProcess(interop, 100, BaseFileTime);
        AddProcess(interop, 200, BaseFileTime + 1_000);

        ApplicationTerminationResult result = await service.ForceTerminateProcessAsync(
            GroupRequest(IdentityOf(100, BaseFileTime), IdentityOf(200, BaseFileTime + 1_000)));

        Assert.Equal(TerminationStatus.Failed, result.Status);
        Assert.Equal(TerminationFailureReason.UnreliableIdentity, result.FailureReason);
        Assert.Empty(interop.DestructiveCalls);
    }

    [Fact]
    public async Task 单进程Force_句柄CreationTime差1个tick_拒绝且零破坏性动作()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();
        FakeProcess target = AddProcess(interop, 100, BaseFileTime);
        target.HandleCreationFileTimeUtc = BaseFileTime + 1;

        ApplicationTerminationResult result = await service.ForceTerminateProcessAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        Assert.Equal(TerminationStatus.IdentityMismatch, result.Status);
        Assert.Empty(interop.DestructiveCalls);
        Assert.False(target.Exited);
    }

    // ================= P6.2：Final Reconciliation Hardening =================

    [Fact]
    public async Task P62_Preflight失败候选留下结构化UnreliableIdentity结果()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();
        FakeProcess target = AddProcess(interop, 100, BaseFileTime);
        target.TimesFails = true;

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        Assert.Equal(TerminationStatus.Failed, result.Status);
        Assert.Equal(TerminationFailureReason.UnreliableIdentity, result.FailureReason);
        Assert.Empty(interop.DestructiveCalls);
        // 不用 Message 代替结构化状态：失败候选必须留下 ProcessTerminationResult
        Assert.Contains(result.ProcessResults, r => r.Pid == 100
            && r.Result == ProcessTerminationStatus.UnreliableIdentity);
    }

    [Fact]
    public async Task P62_CaseA_残留轮新增helper的Preflight失败_不虚报Success且helper计入Residual()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();

        // A：第 0 轮 TerminateProcess 成功但有限等待超时（TimedOut 挂死），Final Rescan 前自行退出
        FakeProcess a = AddProcess(interop, 100, BaseFileTime);
        a.ExitsOnTerminate = false;
        a.SelfExitAfterCaptures = 3; // #1 计划快照、#2 残留轮快照中仍出现；#3（Final）时已消失

        // B：残留轮浮现的新 helper，GetProcessTimes 失败 → 残留轮 Preflight 整组取消
        FakeProcess b = AddProcess(interop, 200, BaseFileTime + 1_000, parentPid: 100);
        b.TimesFails = true;
        b.AppearAfterCaptures = 1; // #1 不出现，#2 残留轮浮现

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        // B 仍存活且身份读不到：绝不能虚报 Success / ResidualCount = 0
        Assert.NotEqual(TerminationStatus.Success, result.Status);
        Assert.Equal(1, result.ResidualCount);
        Assert.False(b.Exited);
        Assert.Equal(1, interop.TerminateCount); // 仅第 0 轮对 A 执行过 TerminateProcess
        // B：Preflight 的 UnreliableIdentity 诊断保留 + Final Rescan fail-closed → Residual
        Assert.Contains(result.ProcessResults, r => r.Pid == 200
            && r.Result == ProcessTerminationStatus.Residual);
        // A：TimedOut + Final 确认 Gone → Terminated
        Assert.Contains(result.ProcessResults, r => r.Pid == 100
            && r.Result == ProcessTerminationStatus.Terminated);
    }

    [Fact]
    public async Task P62_CaseB_残留轮Preflight的AlreadyExited记录保留为最终状态()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();

        // A：第 0 轮 TimedOut（TerminateProcess 成功但挂死不退）；残留轮快照仍渲染它（竞态），
        // 但 Preflight 打开句柄时进程对象已 signaled → 记 AlreadyExited
        FakeProcess a = AddProcess(interop, 100, BaseFileTime);
        a.ExitsOnTerminate = false;
        a.SelfExitAfterCaptures = 2;
        a.ExitRendersRemaining = 1;
        // B：残留轮浮现的新 helper，正常被终止
        FakeProcess b = AddProcess(interop, 200, BaseFileTime + 1_000, parentPid: 100);
        b.AppearAfterCaptures = 1;

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        Assert.Equal(TerminationStatus.Success, result.Status);
        Assert.Equal(0, result.ResidualCount);
        // A 的最终状态是残留轮 Preflight 记录的 AlreadyExited（而非 TimedOut 被 Final 修正的
        // Terminated），证明 passPreflight.Results 已无条件进入最终归并
        Assert.Contains(result.ProcessResults, r => r.Pid == 100
            && r.Result == ProcessTerminationStatus.AlreadyExited);
        Assert.Contains(result.ProcessResults, r => r.Pid == 200
            && r.Result == ProcessTerminationStatus.Terminated);
    }

    [Fact]
    public async Task P62_CaseC_Final快照PID存在但身份读不到_failClosed计入Residual()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();

        // A：第 0 轮正常终止（TerminateProcess 并确认退出）；随后同 PID 被“复用者”接管，
        // 复用者的快照 StartTime 与句柄创建时间均读不到（权限受限视角）
        FakeProcess a = AddProcess(interop, 100, BaseFileTime);
        a.ResurrectFileTimeUtc = BaseFileTime + 5_000_000;
        a.ResurrectSnapshotStartTimeNull = true;
        a.ResurrectTimesFails = true;

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        // PID 100 存在但身份无法读取（StartTime == null 且句柄复核失败）：
        // 绝不能视为 Gone → fail-closed Residual，绝不虚报 Success
        Assert.NotEqual(TerminationStatus.Success, result.Status);
        Assert.Equal(1, result.ResidualCount);
        Assert.Equal(ProcessTerminationStatus.Residual, Assert.Single(result.ProcessResults).Result);
        // Final Rescan 只验证：绝不终止读不到身份的 PID 占用者
        Assert.False(a.Exited);
        Assert.Equal(1, interop.TerminateCount);
    }

    [Fact]
    public async Task P62_CaseD_Final快照PID被复用_原identity视为已退出且绝不伤及新实例()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();

        // A（PID 100 + T1）：正常终止；随后同 PID 被 T2 的新实例复用（StartTime 可读）
        FakeProcess a = AddProcess(interop, 100, BaseFileTime);
        a.ResurrectFileTimeUtc = BaseFileTime + 5_000_000;

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        // PID 100 + T2 ≠ 原身份（PID 100 + T1）：原 identity 已退出，本次操作本身是成功的
        Assert.Equal(TerminationStatus.Success, result.Status);
        Assert.Equal(0, result.ResidualCount);
        Assert.Equal(ProcessTerminationStatus.Terminated, Assert.Single(result.ProcessResults).Result);
        // 绝不把 T2 当 residual 处理，也绝不终止 T2
        Assert.False(a.Exited);
        Assert.Equal(1, interop.TerminateCount);
    }

    [Fact]
    public async Task P62_CaseE_从未产生attempt的targeted身份_最终仍出现在ProcessResults()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();

        // A：第 0 轮 TimedOut（挂死）
        FakeProcess a = AddProcess(interop, 100, BaseFileTime);
        a.ExitsOnTerminate = false;
        // C：残留轮浮现的新 root（PID 最小 → Preflight 排最前），GetProcessTimes 失败
        FakeProcess c = AddProcess(interop, 50, BaseFileTime + 2_000);
        c.TimesFails = true;
        c.AppearAfterCaptures = 1;
        // D：残留轮浮现的另一个新 root，排在失败的 C 之后 → Preflight 提前取消，从未被处理（无任何 attempt）
        FakeProcess d = AddProcess(interop, 60, BaseFileTime + 3_000);
        d.AppearAfterCaptures = 1;

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime)));

        // 三个 targeted identity（A / C / D）全部出现在最终 ProcessResults，D 不被 Reconciler 静默遗漏
        Assert.Equal(3, result.ProcessResults.Count);
        Assert.Equal(3, result.ProcessResults.Select(r => r.Pid).Distinct().Count());
        // D：无历史 attempt + Final 仍存活 → 必须生成 Residual（绝不 Success / ResidualCount = 0）
        Assert.Contains(result.ProcessResults, r => r.Pid == 60
            && r.Result == ProcessTerminationStatus.Residual);
        Assert.Equal(3, result.ResidualCount);
        Assert.NotEqual(TerminationStatus.Success, result.Status);
        Assert.Equal(1, interop.TerminateCount); // 仅第 0 轮 A；C 失败整组取消后 A/D 未再执行
        Assert.False(d.Exited);
    }

    // ================= P6.3：Medium 组破坏性范围门禁 + Preflight 取消全候选结构化结果 =================

    [Fact]
    public async Task P63_Medium置信度组_破坏范围收窄到请求锚点_弱证据成员绝不纳入()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();

        // A：请求锚点（root），exe 位于三层具体目录
        FakeProcess a = AddProcess(interop, 100, BaseFileTime);
        a.ExecutablePath = @"C:\FakeApps\Medium\app.exe";
        // B：与 A 同目录但不同 exe、无父子关系 → 真实分组引擎只给 SameInstallDirectory（Medium）
        FakeProcess b = AddProcess(interop, 200, BaseFileTime + 1_000, parentPid: 9999);
        b.Name = "helper.exe";
        b.ExecutablePath = @"C:\FakeApps\Medium\helper.exe";

        // 守护前提：真实分组引擎确实把 A、B 聚成 Medium 置信度组
        //（若分组规则变化导致前提失效，本测试必须失败而不是静默改测别的语义）
        ProcessSnapshotCollection premise = await new FakeSnapshotService { Interop = interop }.CaptureAsync();
        ApplicationGroup premiseGroup = ApplicationGroupingEngine.Group(premise)
            .Single(g => g.Processes.Any(p => p.ProcessId == 100));
        Assert.Equal(2, premiseGroup.ProcessCount);
        Assert.Equal(GroupingConfidence.Medium, premiseGroup.Confidence);

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            WeakConsentGroupRequest(IdentityOf(100, BaseFileTime)));

        // 已授权的弱组：只终止用户确认的锚点 A，弱证据成员 B 绝不纳入
        Assert.Equal(TerminationStatus.Success, result.Status);
        Assert.Equal(0, result.ResidualCount);
        Assert.True(a.Exited);
        Assert.False(b.Exited); // B 从未成为候选
        Assert.Equal(1, interop.TerminateCount);
        Assert.Equal(100, Assert.Single(result.ProcessResults).Pid);
        Assert.Contains("授权范围", result.Message); // 用户可感知范围收窄原因
    }

    [Fact]
    public async Task P64_Medium多进程组_Default未授权_ScopeConfirmationRequired零破坏()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();

        // 同 P63 配方：A、B 同目录不同 exe → Medium 弱证据组
        FakeProcess a = AddProcess(interop, 100, BaseFileTime);
        a.ExecutablePath = @"C:\FakeApps\Medium\app.exe";
        FakeProcess b = AddProcess(interop, 200, BaseFileTime + 1_000, parentPid: 9999);
        b.Name = "helper.exe";
        b.ExecutablePath = @"C:\FakeApps\Medium\helper.exe";

        // Default 请求（未经专门弱组确认，即使 UI 把整行成员都列为 anchors）
        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime), IdentityOf(200, BaseFileTime + 1_000)));

        Assert.Equal(TerminationStatus.ScopeConfirmationRequired, result.Status);
        Assert.Equal(TerminationFailureReason.ScopeConfirmationRequired, result.FailureReason);
        Assert.Empty(interop.DestructiveCalls); // 0 WM_CLOSE、0 TerminateProcess
        Assert.False(a.Exited);
        Assert.False(b.Exited);
    }

    [Fact]
    public async Task P63_初始Preflight取消_全部候选都有结构化结果()
    {
        (FakeTerminationInterop interop, ProcessTerminationService service) = CreateService();

        // 同 exe + verified 父子 → High 组，A/B/C 全体进候选；B 的 GetProcessTimes 失败 → 整组取消
        FakeProcess a = AddProcess(interop, 100, BaseFileTime);
        FakeProcess b = AddProcess(interop, 200, BaseFileTime + 1_000, parentPid: 100);
        b.TimesFails = true;
        FakeProcess c = AddProcess(interop, 300, BaseFileTime + 2_000, parentPid: 100);

        ApplicationTerminationResult result = await service.ForceTerminateApplicationAsync(
            GroupRequest(IdentityOf(100, BaseFileTime), IdentityOf(200, BaseFileTime + 1_000),
                IdentityOf(300, BaseFileTime + 2_000)));

        Assert.Equal(TerminationStatus.Failed, result.Status);
        Assert.Equal(TerminationFailureReason.UnreliableIdentity, result.FailureReason);
        Assert.Empty(interop.DestructiveCalls); // 整组取消：0 TerminateProcess、0 WM_CLOSE
        // P6.3：cancel 时全部 candidate 都有结构化结果——
        // A（已通过验证但未执行）与 C（排在失败候选之后从未验证）= Skipped，
        // B（失败者本身）= UnreliableIdentity
        Assert.Equal(3, result.ProcessResults.Count);
        Assert.Contains(result.ProcessResults, r => r.Pid == 100
            && r.Result == ProcessTerminationStatus.Skipped);
        Assert.Contains(result.ProcessResults, r => r.Pid == 200
            && r.Result == ProcessTerminationStatus.UnreliableIdentity);
        Assert.Contains(result.ProcessResults, r => r.Pid == 300
            && r.Result == ProcessTerminationStatus.Skipped);
        // 三者都无法确认退出 → 全部计入 ResidualIdentities
        Assert.Equal(3, result.ResidualCount);
    }
}
