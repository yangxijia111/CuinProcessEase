using System.Windows.Threading;
using CuinProcessEase.App.ViewModels;
using CuinProcessEase.Core.Interfaces;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Core.Termination;
using Xunit;

namespace CuinProcessEase.App.Tests;

/// <summary>
/// Phase 9 UX：F5 立即刷新命令 / 空状态文案 / 窗口设置持久化往返。
/// </summary>
public sealed class UxPolishTests : IDisposable
{
    private sealed class EmptySnapshotService : IProcessSnapshotService
    {
        public Task<ProcessSnapshotCollection> CaptureAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessSnapshotCollection
            {
                CapturedAtUtc = DateTime.UtcNow,
                Processes = Array.Empty<ProcessSnapshot>(),
            });
    }

    private sealed class AllowedSafetyService : IProcessSafetyService
    {
        public ProcessSafetyResult Assess(ProcessSnapshot process) => new()
        {
            Identity = process.Identity,
            ProcessName = process.Name,
            Decision = SafetyDecision.Allowed,
            RiskLevel = RiskLevel.Normal,
        };

        public ApplicationSafetyResult Assess(Core.Grouping.ApplicationGroup group) => new()
        {
            DisplayName = group.Identity.DisplayName,
            MemberResults = [],
            Decision = SafetyDecision.Allowed,
            OverallRiskLevel = RiskLevel.Normal,
        };
    }

    private sealed class NoopTerminationService : IProcessTerminationService
    {
        public Task<ApplicationTerminationResult> CloseApplicationGracefullyAsync(
            TerminationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApplicationTerminationResult { Status = TerminationStatus.Success, DisplayName = "noop" });

        public Task<ApplicationTerminationResult> ForceTerminateApplicationAsync(
            TerminationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApplicationTerminationResult { Status = TerminationStatus.Success, DisplayName = "noop" });

        public Task<ApplicationTerminationResult> ForceTerminateProcessAsync(
            TerminationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApplicationTerminationResult { Status = TerminationStatus.Success, DisplayName = "noop" });

        public Task<ApplicationTerminationResult> ForceTerminateViaHelperAsync(
            TerminationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApplicationTerminationResult { Status = TerminationStatus.Success, DisplayName = "noop" });
    }

    private static void Pump()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private readonly List<MainViewModel> _viewModels = [];

    private MainViewModel CreateEmpty()
    {
        var vm = new MainViewModel(new EmptySnapshotService(), new AllowedSafetyService(), new NoopTerminationService());
        _viewModels.Add(vm);
        return vm;
    }

    public void Dispose()
    {
        foreach (MainViewModel vm in _viewModels)
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void 空快照_空状态显示加载中文案()
    {
        MainViewModel vm = CreateEmpty();

        // 泵送刷新管线（空快照 → 无行 → IsListEmpty）
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Pump();
            if (vm.IsListEmpty)
            {
                break;
            }

            Thread.Sleep(50);
        }

        Assert.True(vm.IsListEmpty);
        Assert.Equal("正在扫描进程…", vm.EmptyStateText);
    }

    [Fact]
    public void 搜索无匹配_空状态显示搜索文案()
    {
        MainViewModel vm = CreateEmpty();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Pump();
            if (vm.IsListEmpty)
            {
                break;
            }

            Thread.Sleep(50);
        }

        vm.SearchText = "不存在的应用xyz";
        Pump();
        Thread.Sleep(300); // 防抖 200ms 后由 DispatcherTimer 应用
        Pump();

        Assert.Contains("不存在的应用xyz", vm.EmptyStateText, StringComparison.Ordinal);
    }

    [Fact]
    public void F5命令_可执行且不抛异常()
    {
        MainViewModel vm = CreateEmpty();

        Assert.True(vm.RefreshNowCommand.CanExecute(null));
        vm.RefreshNowCommand.Execute(null);
        Pump(); // 刷新在后台执行，任何异常会进 StatusText 而非抛出

        Assert.NotNull(vm.StatusText);
    }

    [Fact]
    public void CtrlF命令_触发焦点请求事件()
    {
        MainViewModel vm = CreateEmpty();
        int requested = 0;
        vm.FocusSearchRequested += () => requested++;

        vm.FocusSearchCommand.Execute(null);

        Assert.Equal(1, requested);
    }

    [Fact]
    public void 窗口设置_JSON往返无损()
    {
        var settings = new CuinProcessEase.App.Services.WindowSettings
        {
            Left = 100.5,
            Top = 50.25,
            Width = 1280,
            Height = 820,
            Maximized = true,
        };

        // 纯逻辑：序列化/反序列化字段无损（实际文件 IO 由窗口生命周期触发，不在此测试）
        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<CuinProcessEase.App.Services.WindowSettings>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal(settings.Left, roundTrip!.Left);
        Assert.Equal(settings.Top, roundTrip.Top);
        Assert.Equal(settings.Width, roundTrip.Width);
        Assert.Equal(settings.Height, roundTrip.Height);
        Assert.True(roundTrip.Maximized);
    }
}
