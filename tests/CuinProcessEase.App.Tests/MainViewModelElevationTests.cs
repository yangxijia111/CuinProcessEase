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
/// P7 提权终止的 MainViewModel 真实路径测试：
/// RequiresElevation 行必须先经专门的提权确认（说明 UAC 与强杀语义），
/// 确认后请求携带显式 AllowElevation 走 Helper 管线；拒绝则 0 引擎调用；
/// 普通 Allowed 行 Graceful 返回 RequiresElevation 时同样先询问再提权重试。
/// </summary>
public sealed class MainViewModelElevationTests : IDisposable
{
    private const long BaseFileTime = 133_528_256_000_000_000;

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

    /// <summary>fake Fresh Safety：决策可配置（默认 RequiresElevation）。</summary>
    private sealed class ScriptableSafetyService : IProcessSafetyService
    {
        public SafetyDecision Decision { get; set; } = SafetyDecision.RequiresElevation;

        public ProcessSafetyResult Assess(ProcessSnapshot process) => new()
        {
            Identity = process.Identity,
            ProcessName = process.Name,
            Decision = Decision,
            RiskLevel = Decision == SafetyDecision.Allowed ? RiskLevel.Normal : RiskLevel.Elevated,
        };

        public ApplicationSafetyResult Assess(ApplicationGroup group) => new()
        {
            DisplayName = group.Identity.DisplayName,
            MemberResults = [],
            Decision = Decision,
            OverallRiskLevel = Decision == SafetyDecision.Allowed ? RiskLevel.Normal : RiskLevel.Elevated,
        };
    }

    /// <summary>记录型终止服务：Graceful 可脚本返回 RequiresElevation；Helper 调用全部记录。</summary>
    private sealed class RecordingTerminationService : IProcessTerminationService
    {
        public List<TerminationRequest> GracefulRequests { get; } = [];

        public List<TerminationRequest> HelperRequests { get; } = [];

        public bool GracefulReturnsElevation { get; set; }

        public Task<ApplicationTerminationResult> CloseApplicationGracefullyAsync(
            TerminationRequest request, CancellationToken cancellationToken = default)
        {
            GracefulRequests.Add(request);
            return Task.FromResult(new ApplicationTerminationResult
            {
                Status = GracefulReturnsElevation
                    ? TerminationStatus.RequiresElevation
                    : TerminationStatus.Success,
                DisplayName = request.ExpectedDisplayName,
                ProcessResults = [],
                ResidualIdentities = [],
                Message = "fake",
            });
        }

        public Task<ApplicationTerminationResult> ForceTerminateApplicationAsync(
            TerminationRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("提权测试不应触达普通 Force 管线。");

        public Task<ApplicationTerminationResult> ForceTerminateProcessAsync(
            TerminationRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("提权测试不应触达单进程管线。");

        public Task<ApplicationTerminationResult> ForceTerminateViaHelperAsync(
            TerminationRequest request, CancellationToken cancellationToken = default)
        {
            HelperRequests.Add(request);
            return Task.FromResult(new ApplicationTerminationResult
            {
                Status = TerminationStatus.Success,
                DisplayName = request.ExpectedDisplayName,
                ProcessResults = [],
                ResidualIdentities = [],
                Message = "fake helper",
            });
        }
    }

    private static ProcessSnapshot Snap(int pid, long fileTime, int? parentPid = null) => new()
    {
        Identity = new ProcessIdentity(pid, DateTime.FromFileTimeUtc(fileTime)),
        Name = "adminapp.exe",
        ExecutablePath = @"C:\FakeApps\adminapp.exe",
        ParentProcessId = parentPid,
        SessionId = 1,
    };

    /// <summary>同 exe 路径两进程 → High 强证据组（避免弱组确认干扰提权流程测试）。</summary>
    private static ProcessSnapshot[] HighGroupProcesses()
    {
        ProcessSnapshot root = Snap(100, BaseFileTime);
        ProcessSnapshot helper = Snap(200, BaseFileTime + 1_000, parentPid: 100);
        return [root, helper];
    }

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

    private (MainViewModel Vm, RecordingTerminationService Recorder, ScriptableSafetyService Safety) Create(
        SafetyDecision decision, Func<string, string, bool> confirm)
    {
        var recorder = new RecordingTerminationService();
        var safety = new ScriptableSafetyService { Decision = decision };
        MainViewModel vm = new(
            new FixedSnapshotService { Processes = HighGroupProcesses() },
            safety,
            recorder);
        vm.Confirm = (message, title) =>
        {
            _prompts.Add((message, title));
            return confirm(message, title);
        };
        _viewModels.Add(vm);
        return (vm, recorder, safety);
    }

    public void Dispose()
    {
        foreach (MainViewModel vm in _viewModels)
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task RequiresElevation行_按钮可用_专门确认后携带AllowElevation调用Helper()
    {
        (MainViewModel vm, RecordingTerminationService recorder, _) =
            Create(SafetyDecision.RequiresElevation, (_, _) => true);

        ApplicationRowViewModel row = AwaitSingleRow(vm);
        Assert.Equal(SafetyDecision.RequiresElevation, row.SafetyDecision); // 前提守护
        vm.SelectedRow = row;
        Assert.True(vm.KillButtonEnabled); // 提权行可点（P7 放开）

        await vm.KillCommand.ExecuteAsync();

        TerminationRequest helperRequest = Assert.Single(recorder.HelperRequests);
        Assert.True(helperRequest.AllowElevation); // 显式提权授权
        Assert.Equal(TerminationScopeConsent.Default, helperRequest.ScopeConsent); // High 组无需弱组确认
        Assert.Empty(recorder.GracefulRequests); // 提权行绝不先走 Graceful
        // 第一弹必须是专门的提权确认（显著区别于普通"结束应用"确认）
        Assert.Equal("以管理员身份强制结束", _prompts[0].Title);
        Assert.Contains("UAC", _prompts[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiresElevation行_拒绝提权确认_0引擎调用()
    {
        (MainViewModel vm, RecordingTerminationService recorder, _) =
            Create(SafetyDecision.RequiresElevation, (_, _) => false);

        ApplicationRowViewModel row = AwaitSingleRow(vm);
        vm.SelectedRow = row;

        await vm.KillCommand.ExecuteAsync();

        Assert.Empty(recorder.HelperRequests);
        Assert.Empty(recorder.GracefulRequests);
        Assert.Single(_prompts); // 只有提权确认这一弹
    }

    [Fact]
    public async Task Allowed行Graceful返回RequiresElevation_先询问再提权重试()
    {
        (MainViewModel vm, RecordingTerminationService recorder, _) =
            Create(SafetyDecision.Allowed, (_, _) => true);
        recorder.GracefulReturnsElevation = true; // Fresh Safety 与 UI 缓存不一致（应用中途提权）

        ApplicationRowViewModel row = AwaitSingleRow(vm);
        vm.SelectedRow = row;

        await vm.KillCommand.ExecuteAsync();

        // 顺序：普通确认 → Graceful（无授权）→ 提权重试确认 → Helper（带授权）
        Assert.Single(recorder.GracefulRequests);
        Assert.False(recorder.GracefulRequests[0].AllowElevation);
        TerminationRequest helperRequest = Assert.Single(recorder.HelperRequests);
        Assert.True(helperRequest.AllowElevation);
        Assert.Equal("确认结束应用", _prompts[0].Title);
        Assert.Equal("以管理员身份强制结束", _prompts[^1].Title);
    }

    [Fact]
    public async Task Allowed行Graceful返回RequiresElevation_拒绝重试_不调用Helper()
    {
        var answers = new Stack<bool>([false, true]); // 第一弹（普通确认）true，第二弹（提权重试）false
        (MainViewModel vm, RecordingTerminationService recorder, _) =
            Create(SafetyDecision.Allowed, (_, _) => answers.Pop());
        recorder.GracefulReturnsElevation = true;

        ApplicationRowViewModel row = AwaitSingleRow(vm);
        vm.SelectedRow = row;

        await vm.KillCommand.ExecuteAsync();

        Assert.Single(recorder.GracefulRequests);
        Assert.Empty(recorder.HelperRequests); // 拒绝提权重试 → 0 Helper 调用
    }
}
