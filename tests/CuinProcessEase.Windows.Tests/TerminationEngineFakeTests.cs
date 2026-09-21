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
            var snapshots = Interop.Processes
                .Where(kv => !kv.Value.Exited || kv.Value.ExitRendersRemaining > 0)
                .Select(kv =>
                {
                    if (kv.Value.Exited && kv.Value.ExitRendersRemaining > 0)
                    {
                        kv.Value.ExitRendersRemaining--;
                    }

                    return new ProcessSnapshot
                    {
                        Identity = new ProcessIdentity(
                            (int)kv.Key, DateTime.FromFileTimeUtc(kv.Value.CreationFileTimeUtc)),
                        ParentProcessId = kv.Value.ParentPid,
                        Name = kv.Value.Name,
                        ExecutablePath = kv.Value.ExecutablePath,
                        SessionId = 1,
                    };
                })
                .ToList();

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
}
