using System.Diagnostics;
using System.IO.Pipes;
using CuinProcessEase.Core.Elevated;
using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Interfaces;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Core.Termination;
using CuinProcessEase.TerminationTestApp;
using CuinProcessEase.Windows.Elevation;
using CuinProcessEase.Windows.Termination;
using Xunit;

namespace CuinProcessEase.Windows.Tests;

/// <summary>
/// P7 Elevated Helper 管线测试：
/// - fake 全链路（无真实进程）：授权门禁 / 请求构造（PID + FILETIME 精确传递）/
///   UAC 取消 / Helper 身份不匹配 / Final Rescan 归并；
/// - 真实端到端：Helper 协议服务循环 + 真实 Win32 interop 结束测试自己启动的 TestApp
///   （Helper 进程本身带 requireAdministrator manifest，UAC 交互属人工验收，此处验证协议栈组合）。
/// </summary>
/// <remarks>
/// 与 ProcessTerminationServiceTests 同 Collection 串行：全部测试共用同一个 TestApp exe，
/// 并行时组级终止（同 exe 路径 = High 组，产品正确行为）会波及对方测试的无辜目标进程。
/// </remarks>
[Collection("TerminationIntegration")]
public sealed class ElevatedHelperPipelineTests
{
    private const long BaseFileTime = 133_528_256_000_000_000;

    // ================= fake 设施 =================

    /// <summary>fake interop：同时实现主程序引擎接口（构造注入需要）与 Helper 引擎接口。</summary>
    private sealed class FakeElevatedInterop : ITerminationInterop, IElevatedKillInterop
    {
        public Dictionary<uint, long> CreationTimes { get; } = [];

        public List<uint> TerminatedPids { get; } = [];

        public Dictionary<uint, bool> Exited { get; } = [];

        public IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId, out int win32Error)
        {
            win32Error = 0;
            return CreationTimes.ContainsKey(processId) || Exited.ContainsKey(processId)
                ? new IntPtr(processId)
                : IntPtr.Zero;
        }

        public bool TryGetCreationFileTime(IntPtr processHandle, out long creationFileTimeUtc)
        {
            creationFileTimeUtc = CreationTimes.GetValueOrDefault((uint)processHandle, 0);
            return creationFileTimeUtc > 0;
        }

        public bool TerminateProcess(IntPtr processHandle, uint exitCode, out int win32Error)
        {
            TerminatedPids.Add((uint)processHandle);
            win32Error = 0;
            Exited[(uint)processHandle] = true;
            CreationTimes.Remove((uint)processHandle);
            return true;
        }

        public uint WaitForSingleObject(IntPtr processHandle, uint milliseconds)
            => Exited.GetValueOrDefault((uint)processHandle) ? 0x0000u : 0x0102u;

        public bool PostCloseMessage(IntPtr windowHandle) => false; // Helper 管线绝不发 WM_CLOSE

        public IReadOnlyList<IntPtr> FindTopLevelWindows(int processId) => [];

        public bool CloseHandle(IntPtr handle) => true;
    }

    /// <summary>fake 快照：直接给出固定进程列表（可变以模拟 Final Rescan 后消失）。</summary>
    private sealed class FakeSnapshotService : IProcessSnapshotService
    {
        public List<ProcessSnapshot> Processes { get; set; } = [];

        public Task<ProcessSnapshotCollection> CaptureAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessSnapshotCollection
            {
                CapturedAtUtc = DateTime.UtcNow,
                Processes = Processes,
            });
    }

    private sealed class FakeSafetyService : IProcessSafetyService
    {
        public SafetyDecision Decision { get; set; } = SafetyDecision.RequiresElevation;

        public ProcessSafetyResult Assess(ProcessSnapshot process) => new()
        {
            Identity = process.Identity,
            ProcessName = process.Name,
            Decision = Decision,
            RiskLevel = RiskLevel.Elevated,
        };

        public ApplicationSafetyResult Assess(ApplicationGroup group) => new()
        {
            DisplayName = group.Identity.DisplayName,
            MemberResults = [],
            Decision = Decision,
            OverallRiskLevel = RiskLevel.Elevated,
        };
    }

    /// <summary>fake Helper 客户端：记录请求 / 可脚本化响应或异常。</summary>
    private sealed class FakeHelperClient : IElevatedHelperClient
    {
        public List<ElevatedKillRequest> Requests { get; } = [];

        public Func<ElevatedKillRequest, ElevatedKillResponse>? ResponseScript { get; set; }

        public ElevatedHelperException? ThrowScript { get; set; }

        public Task<ElevatedKillResponse> KillAsync(
            ElevatedKillRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (ThrowScript is not null)
            {
                throw ThrowScript;
            }

            return Task.FromResult(ResponseScript!(request));
        }
    }

    private static ProcessSnapshot Snap(int pid, long fileTime) => new()
    {
        Identity = new ProcessIdentity(pid, DateTime.FromFileTimeUtc(fileTime)),
        Name = "elevated.exe",
        ExecutablePath = @"C:\FakeApps\elevated.exe",
        SessionId = 1,
    };

    private static (FakeSnapshotService Snapshot, FakeSafetyService Safety, FakeHelperClient Helper,
        FakeElevatedInterop Interop, ProcessTerminationService Service) Create(
        SafetyDecision decision = SafetyDecision.RequiresElevation)
    {
        var interop = new FakeElevatedInterop();
        var snapshot = new FakeSnapshotService();
        var safety = new FakeSafetyService { Decision = decision };
        var helper = new FakeHelperClient();
        var service = new ProcessTerminationService(snapshot, safety, interop, helper);
        return (snapshot, safety, helper, interop, service);
    }

    private static TerminationRequest ElevatedRequest(params ProcessSnapshot[] members) => new(
        "Elevated App", members.Select(p => p.Identity).ToList(), DateTimeOffset.UtcNow)
    {
        AllowElevation = true,
    };

    // ================= 授权门禁 =================

    [Fact]
    public async Task Helper管线_未携带AllowElevation_绝不调用Helper()
    {
        ProcessSnapshot target = Snap(100, BaseFileTime);
        (FakeSnapshotService snapshot, _, FakeHelperClient helper, _, ProcessTerminationService service) =
            Create();
        snapshot.Processes = [target];

        var unauthorized = new TerminationRequest(
            "Elevated App", [target.Identity], DateTimeOffset.UtcNow);

        ApplicationTerminationResult result = await service.ForceTerminateViaHelperAsync(unauthorized);

        Assert.Equal(TerminationStatus.RequiresElevation, result.Status);
        Assert.Empty(helper.Requests); // 0 Helper 调用 = 0 破坏性动作
    }

    // ================= 请求构造与全链路 =================

    [Fact]
    public async Task Helper管线_授权请求_PID与FILETIME精确传递_结果归并Success()
    {
        ProcessSnapshot root = Snap(100, BaseFileTime);
        ProcessSnapshot helperProc = Snap(200, BaseFileTime + 5_000);
        (FakeSnapshotService snapshot, _, FakeHelperClient helper, FakeElevatedInterop interop,
            ProcessTerminationService service) = Create();
        snapshot.Processes = [root, helperProc];
        helper.ResponseScript = request =>
        {
            // 模拟 Helper 已真实终止：后续 Fresh 快照（Final Rescan）不再包含这些进程
            snapshot.Processes.RemoveAll(
                p => request.Targets.Any(t => t.ProcessId == p.ProcessId));
            return new ElevatedKillResponse(
                request.OperationId,
                request.Targets.Select(t => new ElevatedKillTargetResult(
                    t.ProcessId, ElevatedKillOutcome.Terminated, "ok")).ToList());
        };

        ApplicationTerminationResult result = await service.ForceTerminateViaHelperAsync(
            ElevatedRequest(root, helperProc));

        Assert.Equal(TerminationStatus.Success, result.Status);

        // Helper 请求必须携带与 Fresh 快照逐位一致的 PID + CreationTime FILETIME
        ElevatedKillRequest helperRequest = Assert.Single(helper.Requests);
        Assert.Equal(ElevatedKillRequest.KillOperation, helperRequest.Operation);
        Assert.Equal(2, helperRequest.Targets.Count);
        Assert.Contains(helperRequest.Targets, t => t.ProcessId == 100 && t.ExpectedStartTimeUtcFileTime == BaseFileTime);
        Assert.Contains(helperRequest.Targets, t => t.ProcessId == 200 && t.ExpectedStartTimeUtcFileTime == BaseFileTime + 5_000);
    }

    [Fact]
    public async Task Helper管线_Helper身份不匹配_进程存活_结果如实报告残留()
    {
        ProcessSnapshot target = Snap(100, BaseFileTime);
        (FakeSnapshotService snapshot, _, FakeHelperClient helper, FakeElevatedInterop interop,
            ProcessTerminationService service) = Create();
        snapshot.Processes = [target];
        helper.ResponseScript = request => new ElevatedKillResponse(
            request.OperationId,
            [new ElevatedKillTargetResult(100, ElevatedKillOutcome.IdentityMismatch, "PID 已被复用")]);

        ApplicationTerminationResult result = await service.ForceTerminateViaHelperAsync(ElevatedRequest(target));

        // Helper 拒绝终止 → 进程仍存活 → Final Rescan 判 Surviving → 历史尝试修正为 Residual
        Assert.Equal(ProcessTerminationStatus.Residual,
            Assert.Single(result.ProcessResults).Result);
        // 唯一目标全部残留 → 整体 Failed（Summarize 语义：全部失败）
        Assert.Equal(TerminationStatus.Failed, result.Status);
        Assert.Equal(1, result.ResidualCount);
        Assert.Empty(interop.TerminatedPids); // 主程序端 0 破坏性动作
    }

    [Fact]
    public async Task Helper管线_UAC取消_用户可读失败_未执行终止()
    {
        ProcessSnapshot target = Snap(100, BaseFileTime);
        (FakeSnapshotService snapshot, _, FakeHelperClient helper, FakeElevatedInterop interop,
            ProcessTerminationService service) = Create();
        snapshot.Processes = [target];
        helper.ThrowScript = new ElevatedHelperException(
            ElevatedHelperFailure.UacCancelled, "已取消管理员授权（UAC）。");

        ApplicationTerminationResult result = await service.ForceTerminateViaHelperAsync(ElevatedRequest(target));

        Assert.Equal(TerminationStatus.Failed, result.Status);
        Assert.Contains("取消", result.Message, StringComparison.Ordinal);
        Assert.Empty(interop.TerminatedPids);
    }

    [Fact]
    public async Task Helper管线_弱组未授权_ScopeConfirmationRequired_0Helper调用()
    {
        // 弱证据多进程组即使携带 AllowElevation 也必须先过弱组专门确认（P6.4 门禁不因提权放松）
        var weakA = new ProcessSnapshot
        {
            Identity = new ProcessIdentity(100, DateTime.FromFileTimeUtc(BaseFileTime)),
            Name = "elevated.exe",
            ExecutablePath = @"C:\FakeApps\Medium\elevated.exe",
            SessionId = 1,
        };
        var weakB = new ProcessSnapshot
        {
            Identity = new ProcessIdentity(200, DateTime.FromFileTimeUtc(BaseFileTime + 5_000)),
            Name = "helper2.exe",
            ExecutablePath = @"C:\FakeApps\Medium\helper2.exe",
            ParentProcessId = 9999, // 无父子关系 → 同目录 Medium 弱证据
            SessionId = 1,
        };
        (FakeSnapshotService snapshot, _, FakeHelperClient helper, _, ProcessTerminationService service) = Create();
        snapshot.Processes = [weakA, weakB];

        ApplicationTerminationResult result = await service.ForceTerminateViaHelperAsync(
            ElevatedRequest(weakA, weakB));

        Assert.Equal(TerminationStatus.ScopeConfirmationRequired, result.Status);
        Assert.Empty(helper.Requests);
    }

    // ================= 真实端到端：协议栈 + 真实 Win32 interop + TestApp =================

    private static readonly string TestAppExe = Path.Combine(
        Path.GetDirectoryName(typeof(Program).Assembly.Location)!,
        "CuinProcessEase.TerminationTestApp.exe");

    [Fact]
    public async Task 真实端到端_Helper服务循环_结束自己启动的TestApp()
    {
        // 启动真实测试目标（只杀自己启动的进程）
        var psi = new ProcessStartInfo(TestAppExe)
        {
            Arguments = "--window CuinT-Helper --hidden --no-window",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using Process proc = Process.Start(psi)!;
        string? line = proc.StandardOutput.ReadLine();
        Assert.NotNull(line);
        int pid = int.Parse(line!["PID=".Length..]);
        Thread.Sleep(600);

        // 真实快照读取 exact identity（CreationTime FILETIME 与句柄视角逐位一致）
        var snapshotService = new Services.ProcessSnapshotService();
        ProcessSnapshotCollection snapshot = await snapshotService.CaptureAsync();
        ProcessSnapshot target = Assert.Single(snapshot.Processes, p => p.ProcessId == pid);
        Assert.NotNull(target.StartTimeUtc);

        // 双工队列流 + 真实 Win32 interop 驱动 Helper 协议服务循环（Helper 进程壳/UAC 属人工验收）
        (Stream clientSide, Stream serverSide) = TestDuplexQueueStream.CreatePair();
        using Stream client = clientSide, server = serverSide;
        Task<bool> serveTask = Task.Run(() => ElevatedHelperServer.ServeOnceAsync(
            server, new Win32TerminationInterop(), 5000));

        var request = new ElevatedKillRequest(
            ElevatedKillRequest.KillOperation, "op-e2e",
            [new ElevatedKillTarget(pid, target.StartTimeUtc!.Value.ToFileTimeUtc())]);
        await WriteFramedAsync(client, ElevatedKillProtocol.SerializeRequest(request));

        Assert.True(await serveTask);
        byte[]? responsePayload = await ElevatedHelperServer.ReadFrameAsync(client, 5000, CancellationToken.None);
        Assert.NotNull(responsePayload);
        ElevatedKillResponse? response = ElevatedKillProtocol.DeserializeResponse(responsePayload);

        Assert.NotNull(response);
        Assert.Equal("op-e2e", response.OperationId);
        Assert.Equal(ElevatedKillOutcome.Terminated, Assert.Single(response.Results).Outcome);
        Assert.True(proc.WaitForExit(5000), "TestApp 应已被 Helper 协议栈真实终止");
    }

    [Fact]
    public async Task 真实端到端_差1tick请求_IdentityMismatch绝不终止()
    {
        var psi = new ProcessStartInfo(TestAppExe)
        {
            Arguments = "--window CuinT-Helper-Mismatch --hidden --no-window",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using Process proc = Process.Start(psi)!;
        string? line = proc.StandardOutput.ReadLine();
        Assert.NotNull(line);
        int pid = int.Parse(line!["PID=".Length..]);
        Thread.Sleep(600);

        var snapshotService = new Services.ProcessSnapshotService();
        ProcessSnapshotCollection snapshot = await snapshotService.CaptureAsync();
        ProcessSnapshot target = Assert.Single(snapshot.Processes, p => p.ProcessId == pid);

        (Stream clientSide, Stream serverSide) = TestDuplexQueueStream.CreatePair();
        using Stream client = clientSide, server = serverSide;
        Task<bool> serveTask = Task.Run(() => ElevatedHelperServer.ServeOnceAsync(
            server, new Win32TerminationInterop(), 5000));

        // 预期 FILETIME 偏差 1 tick（100ns）→ Helper 端身份重验证必须拒绝
        var request = new ElevatedKillRequest(
            ElevatedKillRequest.KillOperation, "op-mismatch",
            [new ElevatedKillTarget(pid, target.StartTimeUtc!.Value.ToFileTimeUtc() + 1)]);
        await WriteFramedAsync(client, ElevatedKillProtocol.SerializeRequest(request));

        Assert.True(await serveTask);
        byte[]? responsePayload = await ElevatedHelperServer.ReadFrameAsync(client, 5000, CancellationToken.None);
        ElevatedKillResponse? response = ElevatedKillProtocol.DeserializeResponse(responsePayload!);

        Assert.Equal(ElevatedKillOutcome.IdentityMismatch, Assert.Single(response!.Results).Outcome);
        Assert.False(proc.HasExited); // 绝不终止仍存活的原目标
        if (!proc.HasExited)
        {
            proc.Kill(entireProcessTree: true);
        }
    }

    private static async Task WriteFramedAsync(Stream stream, byte[] payload)
    {
        byte[] frame = new byte[4 + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        payload.AsSpan().CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }
}
