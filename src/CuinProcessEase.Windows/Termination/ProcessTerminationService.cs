using System.Runtime.InteropServices;
using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Interfaces;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Core.Termination;
using CuinProcessEase.Windows.Native;

namespace CuinProcessEase.Windows.Termination;

/// <summary>
/// 终止引擎：Fresh Snapshot → Fresh Grouping → Fresh Safety → Identity Preflight → 执行 → 残留清理。
/// </summary>
/// <remarks>
/// 安全设计（不可违背）：
/// - 仅以 ProcessIdentity（PID + StartTimeUtc）完全匹配定位目标，StableKey 绝不参与终止链路；
/// - 每次执行前重新获取 Fresh 数据，禁止使用 GUI Safety 缓存；
/// - 全部候选先完成 Preflight（真实 HANDLE + GetProcessTimes CreationTime 与 Fresh Snapshot 匹配），
///   再执行任何终止动作；身份不可靠 / 不匹配时在任何动作前取消（fail-closed）；
/// - Force 直接复用 Preflight 验证过的 HANDLE，绝不按 PID 重新打开；
/// - Graceful 仅向目标进程顶层窗口投递 WM_CLOSE（禁止广播），有限等待，绝不自动升级强杀；
/// - 所有 HANDLE 最终 CloseHandle。
/// </remarks>
public sealed class ProcessTerminationService : IProcessTerminationService
{
    /// <summary>优雅关闭后的有限等待（绝不无限等待，绝不自动升级强杀）。</summary>
    private const uint GracefulWaitMs = 3000;

    private const int GracefulPollIntervalMs = 150;

    /// <summary>TerminateProcess 成功后确认进程对象 signaled 的有限等待。</summary>
    private const uint ForceWaitMs = 5000;

    /// <summary>残留清理轮次上限。</summary>
    private const int MaxResidualPasses = 2;

    /// <summary>句柄 CreationTime 与 Fresh Snapshot StartTime 的匹配容差（时钟精度差异）。</summary>
    private static readonly TimeSpan IdentityTolerance = TimeSpan.FromSeconds(1);

    private const uint WaitObject0 = 0x0000;
    private const uint WaitTimeout = 0x0102;

    private readonly IProcessSnapshotService _snapshotService;
    private readonly IProcessSafetyService _freshSafety;

    public ProcessTerminationService()
        : this(new Services.ProcessSnapshotService(), new Safety.ProcessSafetyService())
    {
    }

    internal ProcessTerminationService(IProcessSnapshotService snapshotService, IProcessSafetyService freshSafety)
    {
        _snapshotService = snapshotService;
        _freshSafety = freshSafety;
    }

    /// <inheritdoc />
    public Task<ApplicationTerminationResult> CloseApplicationGracefullyAsync(
        TerminationRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => ExecuteAsync(request, TerminationMode.Graceful, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<ApplicationTerminationResult> ForceTerminateApplicationAsync(
        TerminationRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => ExecuteAsync(request, TerminationMode.Force, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<ApplicationTerminationResult> ForceTerminateProcessAsync(
        TerminationRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => ExecuteAsync(request, TerminationMode.Force, cancellationToken), cancellationToken);

    // ================= 执行管线 =================

    private async Task<ApplicationTerminationResult> ExecuteAsync(
        TerminationRequest request, TerminationMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid)
        {
            return Terminal(request, TerminationStatus.Failed, TerminationFailureReason.UnreliableIdentity,
                "终止请求无效（缺少预期身份）。");
        }

        var messages = new List<string>();
        var allHandles = new List<IntPtr>();

        try
        {
            // ---- Phase 1：Fresh Snapshot + Fresh Grouping + Fresh Safety 规划 ----
            cancellationToken.ThrowIfCancellationRequested();
            ProcessSnapshotCollection snapshot = await _snapshotService.CaptureAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ApplicationGroup> groups = ApplicationGroupingEngine.Group(snapshot);
            TerminationPlanner.TerminationPlan plan = TerminationPlanner.Plan(
                request, groups, snapshot.Processes, g => _freshSafety.Assess(g));

            if (!plan.Proceed)
            {
                // AlreadyExited 终态：为每个预期身份生成逐进程结果，便于 UI/日志追溯
                if (plan.Status == TerminationStatus.AlreadyExited)
                {
                    return new ApplicationTerminationResult
                    {
                        Status = plan.Status,
                        FailureReason = plan.FailureReason,
                        DisplayName = request.ExpectedDisplayName,
                        ProcessResults = request.ExpectedMemberIdentities.Select(i => new ProcessTerminationResult
                        {
                            Pid = i.ProcessId,
                            ExpectedIdentity = i,
                            ProcessName = i.ToString(),
                            Result = ProcessTerminationStatus.AlreadyExited,
                            Message = "Fresh 快照中未发现该进程（已退出）。",
                        }).ToList(),
                        ResidualIdentities = Array.Empty<ProcessIdentity>(),
                        Message = plan.Message,
                    };
                }

                return Terminal(request, plan.Status, plan.FailureReason, plan.Message);
            }

            messages.Add(plan.Message ?? string.Empty);

            // ---- Phase 2：Identity Preflight（全部候选先验证，再动一个） ----
            PreflightOutcome preflight = Preflight(plan.Candidates, mode, allHandles, cancellationToken);
            if (preflight.CancelStatus is { } cancelStatus)
            {
                return Terminal(request, cancelStatus, preflight.CancelReason, preflight.CancelMessage);
            }

            // ---- Phase 3：执行（Graceful / Force） ----
            var results = new List<ProcessTerminationResult>(preflight.Results);
            results.AddRange(mode == TerminationMode.Graceful
                ? ExecuteGraceful(preflight.Validated)
                : ExecuteForce(preflight.Validated));

            // ---- Phase 4：Force 残留清理（最多 2 轮，仅限仍有 exact identity 锚点的组） ----
            if (mode == TerminationMode.Force)
            {
                for (int pass = 1; pass <= MaxResidualPasses; pass++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    List<ProcessIdentity> survivors = results
                        .Where(r => !r.ConfirmedExited)
                        .Select(r => r.ExpectedIdentity)
                        .ToList();
                    if (survivors.Count == 0)
                    {
                        break;
                    }

                    // 等待潜在 helper 浮现后再重扫
                    await Task.Delay(700, cancellationToken).ConfigureAwait(false);
                    ProcessSnapshotCollection rescan = await _snapshotService.CaptureAsync(cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<ApplicationGroup> rescanGroups = ApplicationGroupingEngine.Group(rescan);
                    var followUp = new TerminationRequest(
                        request.ExpectedDisplayName, survivors, DateTimeOffset.UtcNow);
                    TerminationPlanner.TerminationPlan subPlan = TerminationPlanner.Plan(
                        followUp, rescanGroups, rescan.Processes, g => _freshSafety.Assess(g));

                    if (!subPlan.Proceed)
                    {
                        messages.Add($"残留清理第 {pass} 轮取消：{subPlan.Message}");
                        break;
                    }

                    messages.Add($"残留清理第 {pass} 轮：发现 {subPlan.Candidates.Count} 个残留/新增成员。");
                    PreflightOutcome passPreflight = Preflight(subPlan.Candidates, TerminationMode.Force, allHandles, cancellationToken);
                    if (passPreflight.CancelStatus is { } passCancel)
                    {
                        messages.Add($"残留清理第 {pass} 轮身份校验失败，已停止自动清理：{passPreflight.CancelMessage}");
                        break;
                    }

                    results.AddRange(ExecuteForce(passPreflight.Validated));
                }
            }

            // ---- Phase 5：汇总（存活的预期身份即残留） ----
            (TerminationStatus status, TerminationFailureReason reason) = Summarize(results);
            List<ProcessIdentity> residual = results
                .Where(r => !r.ConfirmedExited)
                .Select(r => r.ExpectedIdentity)
                .ToList();

            if (residual.Count > 0)
            {
                messages.Add($"仍有 {residual.Count} 个相关进程未退出。");
            }

            return new ApplicationTerminationResult
            {
                Status = status,
                FailureReason = reason,
                DisplayName = request.ExpectedDisplayName,
                ProcessResults = results,
                ResidualIdentities = residual,
                Message = string.Join(" ", messages.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct()),
            };
        }
        finally
        {
            // 所有 HANDLE（含取消路径中已打开的）最终释放
            foreach (IntPtr handle in allHandles)
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    // ================= Identity Preflight =================

    private sealed class ValidatedProcess
    {
        public required IntPtr Handle { get; init; }
        public required ProcessSnapshot Snapshot { get; init; }
        public bool HadWindow { get; set; }
    }

    private sealed class PreflightOutcome
    {
        public List<ValidatedProcess> Validated { get; } = [];
        public List<ProcessTerminationResult> Results { get; } = [];
        public TerminationStatus? CancelStatus { get; set; }
        public TerminationFailureReason CancelReason { get; set; }
        public string? CancelMessage { get; set; }
    }

    /// <summary>
    /// 对全部候选打开真实 HANDLE 并校验 CreationTime == Fresh Snapshot StartTimeUtc。
    /// 任何身份不可靠 / 不匹配 → 在任何终止动作发生前取消整个操作。
    /// </summary>
    private PreflightOutcome Preflight(
        IReadOnlyList<ProcessSnapshot> candidates,
        TerminationMode mode,
        List<IntPtr> allHandles,
        CancellationToken cancellationToken)
    {
        var outcome = new PreflightOutcome();
        uint access = mode == TerminationMode.Force
            ? NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_TERMINATE | NativeMethods.SYNCHRONIZE
            : NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.SYNCHRONIZE;

        foreach (ProcessSnapshot candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // fail-closed：Snapshot StartTime 缺失 → 无法校验身份 → 取消整个操作
            if (candidate.StartTimeUtc is null)
            {
                outcome.CancelStatus = TerminationStatus.Failed;
                outcome.CancelReason = TerminationFailureReason.UnreliableIdentity;
                outcome.CancelMessage = $"进程 {candidate.Name}（PID {candidate.ProcessId}）缺少启动时间，无法安全校验身份，已取消操作。";
                return outcome;
            }

            IntPtr handle = NativeMethods.OpenProcess(access, bInheritHandle: false, (uint)candidate.ProcessId);
            if (handle == IntPtr.Zero || handle == NativeMethods.INVALID_HANDLE_VALUE)
            {
                int error = Marshal.GetLastWin32Error();
                outcome.Results.Add(new ProcessTerminationResult
                {
                    Pid = candidate.ProcessId,
                    ExpectedIdentity = candidate.Identity,
                    ProcessName = candidate.Name,
                    Result = error == NativeMethods.ERROR_ACCESS_DENIED
                        ? ProcessTerminationStatus.AccessDenied
                        : ProcessTerminationStatus.Failed,
                    Win32Error = error,
                    Message = error == NativeMethods.ERROR_ACCESS_DENIED
                        ? "打开进程句柄被拒绝。"
                        : $"打开进程句柄失败（Win32 错误 {error}）。",
                });
                continue;
            }

            allHandles.Add(handle);

            if (!NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _) ||
                (((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime) <= 0)
            {
                outcome.CancelStatus = TerminationStatus.Failed;
                outcome.CancelReason = TerminationFailureReason.UnreliableIdentity;
                outcome.CancelMessage = $"进程 {candidate.Name}（PID {candidate.ProcessId}）无法读取创建时间，无法校验身份，已取消操作。";
                return outcome;
            }

            DateTime creationUtc = DateTime.FromFileTimeUtc(((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime);
            if ((creationUtc - candidate.StartTimeUtc.Value).Duration() > IdentityTolerance)
            {
                // IdentityMismatch：PID 已变化。在任何 TerminateProcess / WM_CLOSE 发生前取消整个操作。
                outcome.Results.Add(new ProcessTerminationResult
                {
                    Pid = candidate.ProcessId,
                    ExpectedIdentity = candidate.Identity,
                    ProcessName = candidate.Name,
                    Result = ProcessTerminationStatus.IdentityMismatch,
                    Message = "句柄创建时间与快照启动时间不一致（PID 已被复用）。",
                });
                outcome.CancelStatus = TerminationStatus.IdentityMismatch;
                outcome.CancelReason = TerminationFailureReason.IdentityVerificationFailed;
                outcome.CancelMessage = $"“{candidate.Name}”（PID {candidate.ProcessId}）身份校验失败：进程已不是原目标。已在执行任何终止动作前取消。";
                return outcome;
            }

            // 执行前已退出的进程（进程对象已 signaled）
            if (NativeMethods.WaitForSingleObject(handle, 0) == WaitObject0)
            {
                outcome.Results.Add(new ProcessTerminationResult
                {
                    Pid = candidate.ProcessId,
                    ExpectedIdentity = candidate.Identity,
                    ProcessName = candidate.Name,
                    Result = ProcessTerminationStatus.AlreadyExited,
                    Message = "执行前已退出。",
                });
                continue;
            }

            outcome.Validated.Add(new ValidatedProcess { Handle = handle, Snapshot = candidate });
        }

        return outcome;
    }

    // ================= Graceful（WM_CLOSE） =================

    private List<ProcessTerminationResult> ExecuteGraceful(List<ValidatedProcess> procs)
    {
        foreach (ValidatedProcess proc in procs)
        {
            List<IntPtr> windows = FindTopLevelWindows(proc.Snapshot.ProcessId);
            proc.HadWindow = windows.Count > 0;
            foreach (IntPtr window in windows)
            {
                // 仅向属于目标 PID 的顶层窗口投递 WM_CLOSE；绝不 HWND_BROADCAST
                NativeMethods.PostMessageW(window, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }

        // 有限等待（轮询各进程对象），绝不无限等待，绝不升级为 TerminateProcess
        long deadlineTicks = Environment.TickCount64 + GracefulWaitMs;
        while (true)
        {
            bool allExited = procs.All(p =>
                NativeMethods.WaitForSingleObject(p.Handle, 0) == WaitObject0);
            if (allExited || Environment.TickCount64 >= deadlineTicks)
            {
                break;
            }

            Thread.Sleep(GracefulPollIntervalMs);
        }

        var results = new List<ProcessTerminationResult>(procs.Count);
        foreach (ValidatedProcess proc in procs)
        {
            bool exited = NativeMethods.WaitForSingleObject(proc.Handle, 0) == WaitObject0;
            results.Add(new ProcessTerminationResult
            {
                Pid = proc.Snapshot.ProcessId,
                ExpectedIdentity = proc.Snapshot.Identity,
                ProcessName = proc.Snapshot.Name,
                Result = exited
                    ? ProcessTerminationStatus.ClosedGracefully
                    : proc.HadWindow
                        ? ProcessTerminationStatus.Residual
                        : ProcessTerminationStatus.NoWindow,
                Message = exited
                    ? (proc.HadWindow ? "已响应 WM_CLOSE 退出。" : "无窗口，等待期内自行退出。")
                    : proc.HadWindow
                        ? "已发送 WM_CLOSE 但未在等待窗口内退出。"
                        : "无顶层窗口，WM_CLOSE 不适用，等待期内未退出。",
            });
        }

        return results;
    }

    // ================= Force（TerminateProcess） =================

    private List<ProcessTerminationResult> ExecuteForce(List<ValidatedProcess> procs)
    {
        // 顺序沿用规划结果（Root 优先，先切断主要控制）
        var results = new List<ProcessTerminationResult>(procs.Count);
        foreach (ValidatedProcess proc in procs)
        {
            // 直接使用 Preflight 验证过的 HANDLE，绝不按 PID 重新打开
            if (NativeMethods.WaitForSingleObject(proc.Handle, 0) == WaitObject0)
            {
                results.Add(new ProcessTerminationResult
                {
                    Pid = proc.Snapshot.ProcessId,
                    ExpectedIdentity = proc.Snapshot.Identity,
                    ProcessName = proc.Snapshot.Name,
                    Result = ProcessTerminationStatus.AlreadyExited,
                    Message = "执行前已退出。",
                });
                continue;
            }

            if (!NativeMethods.TerminateProcess(proc.Handle, 1))
            {
                int error = Marshal.GetLastWin32Error();
                results.Add(new ProcessTerminationResult
                {
                    Pid = proc.Snapshot.ProcessId,
                    ExpectedIdentity = proc.Snapshot.Identity,
                    ProcessName = proc.Snapshot.Name,
                    Result = error == NativeMethods.ERROR_ACCESS_DENIED
                        ? ProcessTerminationStatus.AccessDenied
                        : ProcessTerminationStatus.Failed,
                    Win32Error = error,
                    Message = error == NativeMethods.ERROR_ACCESS_DENIED
                        ? "TerminateProcess 被拒绝。"
                        : $"TerminateProcess 失败（Win32 错误 {error}）。",
                });
                continue;
            }

            // TerminateProcess 返回 true ≠ 进程已完全退出：有限等待确认 signaled
            uint wait = NativeMethods.WaitForSingleObject(proc.Handle, ForceWaitMs);
            results.Add(new ProcessTerminationResult
            {
                Pid = proc.Snapshot.ProcessId,
                ExpectedIdentity = proc.Snapshot.Identity,
                ProcessName = proc.Snapshot.Name,
                Result = wait == WaitObject0
                    ? ProcessTerminationStatus.Terminated
                    : wait == WaitTimeout
                        ? ProcessTerminationStatus.TimedOut
                        : ProcessTerminationStatus.Failed,
                Win32Error = wait == WaitObject0 ? null : (int)wait,
                Message = wait == WaitObject0
                    ? "已强制终止并确认退出。"
                    : wait == WaitTimeout
                        ? "TerminateProcess 已返回，但未在有限等待内确认退出。"
                        : "等待进程对象失败。",
            });
        }

        return results;
    }

    // ================= 辅助 =================

    private static List<IntPtr> FindTopLevelWindows(int pid)
    {
        var windows = new List<IntPtr>();
        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            if (NativeMethods.GetWindowThreadProcessId(hWnd, out uint windowPid) != 0
                && windowPid == (uint)pid)
            {
                windows.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static (TerminationStatus Status, TerminationFailureReason Reason) Summarize(
        IReadOnlyList<ProcessTerminationResult> results)
    {
        if (results.Count == 0)
        {
            return (TerminationStatus.Failed, TerminationFailureReason.ExecutionError);
        }

        bool allExited = results.All(r => r.ConfirmedExited);
        if (allExited)
        {
            return results.All(r => r.Result == ProcessTerminationStatus.AlreadyExited)
                ? (TerminationStatus.AlreadyExited, TerminationFailureReason.None)
                : (TerminationStatus.Success, TerminationFailureReason.None);
        }

        bool anyExited = results.Any(r => r.ConfirmedExited);
        if (anyExited)
        {
            return (TerminationStatus.PartialSuccess, PartialReason(results));
        }

        // 无一退出
        return (TerminationStatus.Failed, PartialReason(results));
    }

    private static TerminationFailureReason PartialReason(IReadOnlyList<ProcessTerminationResult> results)
        => results.Any(r => r.Result is ProcessTerminationStatus.Residual or ProcessTerminationStatus.NoWindow)
            ? TerminationFailureReason.ResidualRemain
            : TerminationFailureReason.ExecutionError;

    private static ApplicationTerminationResult Terminal(
        TerminationRequest request,
        TerminationStatus status,
        TerminationFailureReason reason,
        string? message)
        => new()
        {
            Status = status,
            FailureReason = reason,
            DisplayName = request.ExpectedDisplayName,
            ProcessResults = Array.Empty<ProcessTerminationResult>(),
            ResidualIdentities = Array.Empty<ProcessIdentity>(),
            Message = message,
        };
}
