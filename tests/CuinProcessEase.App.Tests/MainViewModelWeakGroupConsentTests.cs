using System.Windows.Threading;
using CuinProcessEase.App.ViewModels;
using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Interfaces;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Core.Termination;
using Xunit;

namespace CuinProcessEase.App.Tests;

/// <summary>
/// P6.4 Weak Group Scope Consent 的真实 MainViewModel 路径集成测试：
/// fake 快照/安全/终止服务注入 + 真实刷新管线（Snapshot→Grouping→Safety→行生成），
/// 验证弱证据多进程组必须经过显著不同的专门确认后才携带 ExplicitWeakGroup 发起操作。
/// </summary>
public sealed class MainViewModelWeakGroupConsentTests : IDisposable
{
    private const long BaseFileTime = 133_528_256_000_000_000;

    // ================= fake 设施 =================

    /// <summary>固定快照服务：始终返回同一组进程（真实刷新管线由此生成分组与行）。</summary>
    private sealed class FixedSnapshotService : IProcessSnapshotService
    {
        public required IReadOnlyList<ProcessSnapshot> Processes { get; init; }

        public Task<ProcessSnapshotCollection> CaptureAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessSnapshotCollection
            {
                CapturedAtUtc = DateTime.UtcNow,
                Processes = Processes,
            });
    }

    /// <summary>fake Fresh Safety：恒 Allowed（安全门禁不是本测试的变量）。</summary>
    private sealed class AllowedSafetyService : IProcessSafetyService
    {
        public ProcessSafetyResult Assess(ProcessSnapshot process) => new()
        {
            Identity = process.Identity,
            ProcessName = process.Name,
            Decision = SafetyDecision.Allowed,
            RiskLevel = RiskLevel.Normal,
        };

        public ApplicationSafetyResult Assess(ApplicationGroup group) => new()
        {
            DisplayName = group.Identity.DisplayName,
            MemberResults = [],
            Decision = SafetyDecision.Allowed,
            OverallRiskLevel = RiskLevel.Normal,
        };
    }

    /// <summary>记录型终止服务：记录全部请求（含 ScopeConsent），按脚本返回结果。</summary>
    private sealed class RecordingTerminationService : IProcessTerminationService
    {
        public List<TerminationRequest> GracefulRequests { get; } = [];

        public List<TerminationRequest> ForceRequests { get; } = [];

        /// <summary>Graceful 返回的残留身份数量（>0 触发 UI 的 Force 确认链）。</summary>
        public int GracefulResidualCount { get; set; }

        private static ApplicationTerminationResult Result(
            TerminationRequest request, TerminationStatus status, int residualCount) => new()
        {
            Status = status,
            DisplayName = request.ExpectedDisplayName,
            ProcessResults = request.ExpectedMemberIdentities
                .Take(Math.Max(0, request.ExpectedMemberIdentities.Count - residualCount))
                .Select(i => new ProcessTerminationResult
                {
                    Pid = i.ProcessId,
                    ExpectedIdentity = i,
                    ProcessName = i.ToString(),
                    Result = ProcessTerminationStatus.Terminated,
                })
                .ToList(),
            ResidualIdentities = request.ExpectedMemberIdentities.Take(residualCount).ToList(),
            Message = "fake",
        };

        public Task<ApplicationTerminationResult> CloseApplicationGracefullyAsync(
            TerminationRequest request, CancellationToken cancellationToken = default)
        {
            GracefulRequests.Add(request);
            return Task.FromResult(Result(
                request,
                GracefulResidualCount == 0 ? TerminationStatus.Success : TerminationStatus.PartialSuccess,
                GracefulResidualCount));
        }

        public Task<ApplicationTerminationResult> ForceTerminateApplicationAsync(
            TerminationRequest request, CancellationToken cancellationToken = default)
        {
            ForceRequests.Add(request);
            return Task.FromResult(Result(request, TerminationStatus.Success, 0));
        }

        public Task<ApplicationTerminationResult> ForceTerminateProcessAsync(
            TerminationRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("弱组 consent 测试不应触达单进程管线。");
    }

    // ================= 设施 =================

    private static ProcessSnapshot Snap(int pid, long fileTime, string name, string path, int? parentPid) => new()
    {
        Identity = new ProcessIdentity(pid, DateTime.FromFileTimeUtc(fileTime)),
        Name = name,
        ExecutablePath = path,
        ParentProcessId = parentPid,
        SessionId = 1,
    };

    /// <summary>同目录不同 exe、无父子 → 真实分组引擎产出 Medium 弱证据组（与引擎集成测试同配方）。</summary>
    private static ProcessSnapshot[] WeakGroupProcesses()
        =>
        [
            Snap(100, BaseFileTime, "app.exe", @"C:\FakeApps\Medium\app.exe", null),
            Snap(200, BaseFileTime + 1_000, "helper.exe", @"C:\FakeApps\Medium\helper.exe", 9999),
        ];

    /// <summary>同 exe 完整路径 → High 强证据组（对照场景）。</summary>
    private static ProcessSnapshot[] HighGroupProcesses()
        =>
        [
            Snap(100, BaseFileTime, "app.exe", @"C:\FakeApps\app.exe", null),
            Snap(200, BaseFileTime + 1_000, "app.exe", @"C:\FakeApps\app.exe", 100),
        ];

    /// <summary>泵送当前线程 Dispatcher（处理刷新管线 InvokeAsync 的 UI 应用 + 图标回调）。</summary>
    private static void Pump()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static ApplicationRowViewModel AwaitSingleRow(MainViewModel viewModel)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            Pump();
            if (viewModel.Applications.Count == 1)
            {
                return viewModel.Applications[0];
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException("刷新管线未在限定时间内生成应用行。");
    }

    private readonly List<(string Message, string Title)> _prompts = [];

    private readonly List<MainViewModel> _viewModels = [];

    private (MainViewModel Vm, RecordingTerminationService Recorder) Create(
        ProcessSnapshot[] processes, Func<string, string, bool> confirm)
    {
        var recorder = new RecordingTerminationService();
        MainViewModel vm = new(
            new FixedSnapshotService { Processes = processes },
            new AllowedSafetyService(),
            recorder);
        vm.Confirm = (message, title) =>
        {
            _prompts.Add((message, title));
            return confirm(message, title);
        };
        _viewModels.Add(vm);
        return (vm, recorder);
    }

    public void Dispose()
    {
        foreach (MainViewModel vm in _viewModels)
        {
            vm.Dispose();
        }
    }

    // ================= 弱组 consent：拒绝路径 =================

    [Fact]
    public async Task 弱组未通过专门确认_绝不调用终止引擎()
    {
        (MainViewModel vm, RecordingTerminationService recorder) = Create(WeakGroupProcesses(), (_, _) => false);

        ApplicationRowViewModel row = AwaitSingleRow(vm);
        // 前提守护：真实管线确实产出弱证据多进程行
        Assert.True(row.Confidence < GroupingConfidence.High);
        Assert.Equal(2, row.ProcessCount);

        vm.SelectedRow = row;
        await vm.KillCommand.ExecuteAsync();

        // 未确认范围 → 0 引擎调用（0 destructive 的 UI 层等价）
        Assert.Empty(recorder.GracefulRequests);
        Assert.Empty(recorder.ForceRequests);
        // 第一弹必须是显著不同的弱组范围确认（列出进程清单），不是普通"结束应用"确认
        (string message, string title) = Assert.Single(_prompts);
        Assert.Equal("确认弱关联应用组", title);
        Assert.Contains("弱证据", message);
        Assert.Contains("app.exe", message);
        Assert.Contains("helper.exe", message);
    }

    // ================= 弱组 consent：确认路径 =================

    [Fact]
    public async Task 弱组明确确认后_请求携带ExplicitWeakGroup且范围仅限确认成员()
    {
        (MainViewModel vm, RecordingTerminationService recorder) = Create(WeakGroupProcesses(), (_, _) => true);

        ApplicationRowViewModel row = AwaitSingleRow(vm);
        vm.SelectedRow = row;
        await vm.KillCommand.ExecuteAsync();

        TerminationRequest request = Assert.Single(recorder.GracefulRequests);
        Assert.Equal(TerminationScopeConsent.ExplicitWeakGroup, request.ScopeConsent);
        Assert.Equal(2, request.ExpectedMemberIdentities.Count);
        // 确认顺序：先弱组范围确认，再普通"结束应用"确认（弱组确认绝不与普通确认等价替代）
        Assert.Equal("确认弱关联应用组", _prompts[0].Title);
        Assert.Equal("确认结束应用", _prompts[1].Title);
        Assert.Empty(recorder.ForceRequests); // Graceful 无残留 → 不进入 Force
    }

    [Fact]
    public async Task 弱组Graceful残留后Force_继承同一ScopeConsent()
    {
        (MainViewModel vm, RecordingTerminationService recorder) =
            Create(WeakGroupProcesses(), (_, _) => true);
        recorder.GracefulResidualCount = 1; // Graceful 后仍剩 1 个 → 触发 Force 确认链

        ApplicationRowViewModel row = AwaitSingleRow(vm);
        vm.SelectedRow = row;
        await vm.KillCommand.ExecuteAsync();

        TerminationRequest forceRequest = Assert.Single(recorder.ForceRequests);
        // Force 必须复用同一 request（携带同一 ScopeConsent），绝不重新构造无授权请求
        Assert.Equal(TerminationScopeConsent.ExplicitWeakGroup, forceRequest.ScopeConsent);
        Assert.Equal(2, forceRequest.ExpectedMemberIdentities.Count);
    }

    // ================= 对照：High 组不受影响 =================

    [Fact]
    public async Task High组_不走弱组确认_请求保持Default()
    {
        (MainViewModel vm, RecordingTerminationService recorder) = Create(HighGroupProcesses(), (_, _) => true);

        ApplicationRowViewModel row = AwaitSingleRow(vm);
        Assert.Equal(GroupingConfidence.High, row.Confidence); // 前提守护

        vm.SelectedRow = row;
        await vm.KillCommand.ExecuteAsync();

        TerminationRequest request = Assert.Single(recorder.GracefulRequests);
        Assert.Equal(TerminationScopeConsent.Default, request.ScopeConsent);
        // 第一弹就是普通确认，从未出现弱组确认
        Assert.DoesNotContain(_prompts, p => p.Title == "确认弱关联应用组");
        Assert.Equal("确认结束应用", _prompts[0].Title);
    }
}
