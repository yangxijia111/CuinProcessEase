using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Interfaces;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Core.Termination;
using CuinProcessEase.Windows.Native;

namespace CuinProcessEase.Windows.Termination;

/// <summary>
/// 终止引擎：Fresh Snapshot → Fresh Grouping → Fresh Safety → Identity Preflight → 执行 → 残留清理 → Final Rescan。
/// </summary>
/// <remarks>
/// 安全设计（不可违背，最高原则：宁可本次操作失败让用户重试，也不能因为模糊身份继续执行）：
/// - 仅以 ProcessIdentity（PID + StartTimeUtc）完全匹配定位目标，StableKey 绝不参与终止链路；
/// - 每次执行前重新获取 Fresh 数据，禁止使用 GUI Safety 缓存；
/// - 严格两阶段事务式 Preflight：全部候选先打开真实 HANDLE 并以 CreationTime 原始 FILETIME
///   逐位比对（绝无任何时间容差，差 1 tick 也拒绝），全部得到 Validated / AlreadyExited 之后
///   才允许第一个破坏性动作；任一仍存活候选 OpenProcess / GetProcessTimes / 身份比对失败
///   → 整组取消（0 WM_CLOSE、0 TerminateProcess）；
/// - Force 直接复用 Preflight 验证过的 HANDLE，绝不按 PID 重新打开；
/// - Graceful 仅向目标进程顶层窗口投递 WM_CLOSE（禁止广播），有限等待，绝不自动升级强杀；
/// - 单进程 Force 只重新定位这一个 exact identity，绝不因同组身份扩展候选；
/// - Force 最终始终执行 Final Fresh Rescan：仅验证本次所有曾纳入候选的 exact ProcessIdentity
///   是否仍存在（只验证，绝不扩大终止范围），结果经 TerminationResultReconciler 归并；
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

    /// <summary>残留清理轮开始前的等待（给潜在 helper 浮现留时间）。</summary>
    private const int ResidualPassDelayMs = 700;

    private const uint WaitObject0 = 0x0000;
    private const uint WaitTimeout = 0x0102;

    private readonly IProcessSnapshotService _snapshotService;
    private readonly IProcessSafetyService _freshSafety;
    private readonly ITerminationInterop _interop;

    public ProcessTerminationService()
        : this(
            new Services.ProcessSnapshotService(),
            new Safety.ProcessSafetyService(),
            new Win32TerminationInterop())
    {
    }

    internal ProcessTerminationService(
        IProcessSnapshotService snapshotService,
        IProcessSafetyService freshSafety,
        ITerminationInterop interop)
    {
        _snapshotService = snapshotService;
        _freshSafety = freshSafety;
        _interop = interop;
    }

    /// <inheritdoc />
    public Task<ApplicationTerminationResult> CloseApplicationGracefullyAsync(
        TerminationRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => ExecuteGroupAsync(request, TerminationMode.Graceful, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<ApplicationTerminationResult> ForceTerminateApplicationAsync(
        TerminationRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => ExecuteGroupAsync(request, TerminationMode.Force, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<ApplicationTerminationResult> ForceTerminateProcessAsync(
        TerminationRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => ExecuteSingleProcessAsync(request, cancellationToken), cancellationToken);

    // ================= 应用组执行管线 =================

    private async Task<ApplicationTerminationResult> ExecuteGroupAsync(
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

            // 本次操作曾纳入终止候选的全部 exact identity（Final Rescan 的验证范围）
            var targetedIdentities = new HashSet<ProcessIdentity>(plan.Candidates.Select(p => p.Identity));

            // ---- Phase 2：Identity Preflight（严格两阶段：全部候选验证通过前，0 破坏性动作） ----
            PreflightOutcome preflight = Preflight(plan.Candidates, mode, allHandles, cancellationToken);
            if (preflight.CancelStatus is { } cancelStatus)
            {
                return Canceled(request, cancelStatus, preflight);
            }

            var attempts = new List<ProcessTerminationResult>(preflight.Results);

            // ---- Phase 3：执行（Graceful / Force） ----
            attempts.AddRange(mode == TerminationMode.Graceful
                ? ExecuteGraceful(preflight.Validated)
                : ExecuteForce(preflight.Validated));

            // ---- Phase 4：Force 残留清理（最多 2 轮，仅限仍有 exact identity 锚点的组） ----
            if (mode == TerminationMode.Force)
            {
                for (int pass = 1; pass <= MaxResidualPasses; pass++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // 残留判定基于每个身份的最终状态（最后一次尝试），历史失败不得产生假残留
                    List<ProcessIdentity> survivors = TerminationResultReconciler
                        .LatestResultByIdentity(attempts)
                        .Where(r => !r.ConfirmedExited)
                        .Select(r => r.ExpectedIdentity)
                        .ToList();
                    if (survivors.Count == 0)
                    {
                        break;
                    }

                    // 等待潜在 helper 浮现后再重扫
                    await Task.Delay(ResidualPassDelayMs, cancellationToken).ConfigureAwait(false);
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
                    targetedIdentities.UnionWith(subPlan.Candidates.Select(p => p.Identity));
                    PreflightOutcome passPreflight = Preflight(subPlan.Candidates, TerminationMode.Force, allHandles, cancellationToken);
                    if (passPreflight.CancelStatus is { } passCancel)
                    {
                        messages.Add($"残留清理第 {pass} 轮身份校验失败，已停止自动清理：{passPreflight.CancelMessage}");
                        break;
                    }

                    attempts.AddRange(ExecuteForce(passPreflight.Validated));
                }
            }

            // ---- Phase 5：结果归并（每身份最终状态 = 最后一次尝试） ----
            IReadOnlyList<ProcessTerminationResult> latest =
                TerminationResultReconciler.LatestResultByIdentity(attempts);
            IReadOnlyList<ProcessTerminationResult> finalResults = latest;

            // ---- Phase 6：Final exact-identity rescan（Force 专属；只验证，绝不终止） ----
            if (mode == TerminationMode.Force)
            {
                IReadOnlySet<ProcessIdentity> survivorsAfterFinal =
                    await FindSurvivingIdentitiesAsync(targetedIdentities, cancellationToken).ConfigureAwait(false);
                finalResults = TerminationResultReconciler.ApplyFinalRescan(latest, survivorsAfterFinal);
                messages.Add(survivorsAfterFinal.Count == 0
                    ? "所有已确认目标身份均已退出。"
                    : $"Final Rescan 确认仍有 {survivorsAfterFinal.Count} 个目标身份存在。");
            }

            // ---- Phase 7：汇总（存活的预期身份即残留） ----
            (TerminationStatus status, TerminationFailureReason reason) =
                TerminationResultReconciler.Summarize(finalResults);
            List<ProcessIdentity> residual = finalResults
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
                ProcessResults = finalResults,
                ResidualIdentities = residual,
                Message = string.Join(" ", messages.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct()),
            };
        }
        finally
        {
            // 所有 HANDLE（含取消路径中已打开的）最终释放
            foreach (IntPtr handle in allHandles)
            {
                _interop.CloseHandle(handle);
            }
        }
    }

    // ================= 单进程执行管线 =================

    /// <summary>
    /// 单进程强制终止：Fresh Snapshot → exact ProcessIdentity → Fresh Safety（该进程）→
    /// HANDLE Preflight → exact CreationTime → TerminateProcess → WaitForSingleObject → Fresh Rescan。
    /// 绝不因目标属于某个 ApplicationGroup 而把组内其他成员纳入候选。
    /// </summary>
    private async Task<ApplicationTerminationResult> ExecuteSingleProcessAsync(
        TerminationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid)
        {
            return Terminal(request, TerminationStatus.Failed, TerminationFailureReason.UnreliableIdentity,
                "终止请求无效（缺少预期身份）。");
        }

        if (request.ExpectedMemberIdentities.Count != 1)
        {
            return Terminal(request, TerminationStatus.Failed, TerminationFailureReason.UnreliableIdentity,
                $"单进程终止请求必须恰好包含一个预期身份（实际 {request.ExpectedMemberIdentities.Count} 个），已拒绝执行。");
        }

        ProcessIdentity target = request.ExpectedMemberIdentities[0];
        if (target.StartTimeUtc is null)
        {
            return Terminal(request, TerminationStatus.Failed, TerminationFailureReason.UnreliableIdentity,
                "预期身份缺少启动时间，无法安全定位目标，已拒绝操作。");
        }

        var messages = new List<string>();
        var allHandles = new List<IntPtr>();

        try
        {
            // ---- Fresh Snapshot + exact identity 定位（不经过分组，不扩展候选） ----
            cancellationToken.ThrowIfCancellationRequested();
            ProcessSnapshotCollection snapshot = await _snapshotService.CaptureAsync(cancellationToken).ConfigureAwait(false);
            ProcessSnapshot? match = snapshot.Processes.FirstOrDefault(p => p.Identity == target);
            if (match is null)
            {
                bool pidReused = snapshot.Processes.Any(p => p.ProcessId == target.ProcessId);
                if (!pidReused)
                {
                    return new ApplicationTerminationResult
                    {
                        Status = TerminationStatus.AlreadyExited,
                        FailureReason = TerminationFailureReason.None,
                        DisplayName = request.ExpectedDisplayName,
                        ProcessResults =
                        [
                            new ProcessTerminationResult
                            {
                                Pid = target.ProcessId,
                                ExpectedIdentity = target,
                                ProcessName = target.ToString(),
                                Result = ProcessTerminationStatus.AlreadyExited,
                                Message = "Fresh 快照中未发现该进程（已退出）。",
                            },
                        ],
                        ResidualIdentities = Array.Empty<ProcessIdentity>(),
                        Message = $"“{request.ExpectedDisplayName}”的相关进程均已退出。",
                    };
                }

                return Terminal(request, TerminationStatus.TargetChanged, TerminationFailureReason.None,
                    $"“{request.ExpectedDisplayName}”的原进程已退出，其 PID 已被其他进程复用，已拒绝操作。");
            }

            messages.Add($"目标进程 {match.Name}（PID {match.ProcessId}）。");

            // ---- Fresh Safety：仅评估该进程（单进程重载，不构造、不扩展组） ----
            ProcessSafetyResult safety = _freshSafety.Assess(match);
            switch (safety.Decision)
            {
                case SafetyDecision.Blocked:
                    return Terminal(request, TerminationStatus.Blocked, TerminationFailureReason.SafetyRejected,
                        $"“{match.Name}”受系统保护，禁止结束。");
                case SafetyDecision.Indeterminate:
                    return Terminal(request, TerminationStatus.Indeterminate, TerminationFailureReason.SafetyRejected,
                        $"“{match.Name}”安全状态未知，已阻止操作。");
                case SafetyDecision.RequiresElevation:
                    return Terminal(request, TerminationStatus.RequiresElevation, TerminationFailureReason.SafetyRejected,
                        $"“{match.Name}”需要管理员权限，当前阶段不提供提权。");
                case SafetyDecision.Allowed:
                    break;
                default:
                    return Terminal(request, TerminationStatus.Indeterminate, TerminationFailureReason.SafetyRejected,
                        $"“{match.Name}”安全状态未知，已阻止操作。");
            }

            // ---- Identity Preflight（同一严格管线，单候选） ----
            PreflightOutcome preflight = Preflight([match], TerminationMode.Force, allHandles, cancellationToken);
            if (preflight.CancelStatus is { } cancelStatus)
            {
                return Canceled(request, cancelStatus, preflight);
            }

            var attempts = new List<ProcessTerminationResult>(preflight.Results);

            // ---- TerminateProcess + 有限等待确认退出 ----
            attempts.AddRange(ExecuteForce(preflight.Validated));

            // ---- Final Fresh Rescan：仅验证该 exact identity；发现残留绝不自动扩大杀伤范围 ----
            IReadOnlySet<ProcessIdentity> survivors = await FindSurvivingIdentitiesAsync(
                [target], cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ProcessTerminationResult> finalResults =
                TerminationResultReconciler.ApplyFinalRescan(
                    TerminationResultReconciler.LatestResultByIdentity(attempts), survivors);
            messages.Add(survivors.Count == 0
                ? "所有已确认目标身份均已退出。"
                : "Final Rescan 确认目标身份仍存在。");

            (TerminationStatus status, TerminationFailureReason reason) =
                TerminationResultReconciler.Summarize(finalResults);
            List<ProcessIdentity> residual = finalResults
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
                ProcessResults = finalResults,
                ResidualIdentities = residual,
                Message = string.Join(" ", messages.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct()),
            };
        }
        finally
        {
            foreach (IntPtr handle in allHandles)
            {
                _interop.CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// Final Fresh Rescan：重新拍摄快照，返回 targeted 集合中仍存在的 exact ProcessIdentity。
    /// 只验证，绝不基于 exe/path/product/stableKey 自动扩大终止范围。
    /// </summary>
    private async Task<IReadOnlySet<ProcessIdentity>> FindSurvivingIdentitiesAsync(
        IReadOnlyCollection<ProcessIdentity> targeted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessSnapshotCollection snapshot = await _snapshotService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var present = new HashSet<ProcessIdentity>(snapshot.Processes.Select(p => p.Identity));
        return new HashSet<ProcessIdentity>(targeted.Where(present.Contains));
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
    /// 对全部候选打开真实 HANDLE，并以句柄 CreationTime 原始 FILETIME 与 Fresh Snapshot
    /// StartTimeUtc.ToFileTimeUtc() 逐位比对（绝无时间容差，差 1 tick 也拒绝）。
    /// 严格两阶段：任何候选身份不可靠 / 不匹配 / 句柄打开失败 → 在任何终止动作发生前取消整个操作。
    /// 已通过句柄确认 AlreadyExited 的候选不算失败。
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

            IntPtr handle = _interop.OpenProcess(access, inheritHandle: false, (uint)candidate.ProcessId, out int openError);
            if (handle == IntPtr.Zero)
            {
                // P6.1 严格 fail-all：仍存活候选的句柄打不开 → 身份无法验证 → 整组取消（0 破坏性动作）。
                // 已通过其他候选的 AlreadyExited 不受影响；该候选无法确认已退出，宁可让用户重试。
                outcome.Results.Add(new ProcessTerminationResult
                {
                    Pid = candidate.ProcessId,
                    ExpectedIdentity = candidate.Identity,
                    ProcessName = candidate.Name,
                    Result = openError == NativeMethods.ERROR_ACCESS_DENIED
                        ? ProcessTerminationStatus.AccessDenied
                        : ProcessTerminationStatus.Failed,
                    Win32Error = openError,
                    Message = openError == NativeMethods.ERROR_ACCESS_DENIED
                        ? "打开进程句柄被拒绝。"
                        : $"打开进程句柄失败（Win32 错误 {openError}）。",
                });
                outcome.CancelStatus = TerminationStatus.Failed;
                outcome.CancelReason = TerminationFailureReason.ExecutionError;
                outcome.CancelMessage = $"进程 {candidate.Name}（PID {candidate.ProcessId}）句柄打开失败"
                    + $"（Win32 错误 {openError}），无法验证身份，已取消整组操作（未执行任何终止动作）。";
                return outcome;
            }

            allHandles.Add(handle);

            if (!_interop.TryGetCreationFileTime(handle, out long actualCreationFileTime))
            {
                outcome.CancelStatus = TerminationStatus.Failed;
                outcome.CancelReason = TerminationFailureReason.UnreliableIdentity;
                outcome.CancelMessage = $"进程 {candidate.Name}（PID {candidate.ProcessId}）无法读取创建时间，无法校验身份，已取消操作。";
                return outcome;
            }

            long expectedCreationFileTime = candidate.StartTimeUtc.Value.ToFileTimeUtc();
            if (!ProcessIdentityMatcher.IsExactProcessIdentityMatch(expectedCreationFileTime, actualCreationFileTime))
            {
                // IdentityMismatch：PID 已变化（即使仅差 1 个 FILETIME tick 也拒绝）。
                // 在任何 TerminateProcess / WM_CLOSE 发生前取消整个操作。
                outcome.Results.Add(new ProcessTerminationResult
                {
                    Pid = candidate.ProcessId,
                    ExpectedIdentity = candidate.Identity,
                    ProcessName = candidate.Name,
                    Result = ProcessTerminationStatus.IdentityMismatch,
                    Message = "句柄创建时间与快照启动时间不一致（PID 已被复用），已取消整组操作。",
                });
                outcome.CancelStatus = TerminationStatus.IdentityMismatch;
                outcome.CancelReason = TerminationFailureReason.IdentityVerificationFailed;
                outcome.CancelMessage = $"“{candidate.Name}”（PID {candidate.ProcessId}）身份校验失败：进程已不是原目标。已在执行任何终止动作前取消。";
                return outcome;
            }

            // 执行前已退出的进程（进程对象已 signaled）：已确认退出，不算失败
            if (_interop.WaitForSingleObject(handle, 0) == WaitObject0)
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
            List<IntPtr> windows = [.. _interop.FindTopLevelWindows(proc.Snapshot.ProcessId)];
            proc.HadWindow = windows.Count > 0;
            foreach (IntPtr window in windows)
            {
                // 仅向属于目标 PID 的顶层窗口投递 WM_CLOSE；绝不 HWND_BROADCAST
                _interop.PostCloseMessage(window);
            }
        }

        // 有限等待（轮询各进程对象），绝不无限等待，绝不升级为 TerminateProcess
        long deadlineTicks = Environment.TickCount64 + GracefulWaitMs;
        while (true)
        {
            bool allExited = procs.All(p =>
                _interop.WaitForSingleObject(p.Handle, 0) == WaitObject0);
            if (allExited || Environment.TickCount64 >= deadlineTicks)
            {
                break;
            }

            Thread.Sleep(GracefulPollIntervalMs);
        }

        var results = new List<ProcessTerminationResult>(procs.Count);
        foreach (ValidatedProcess proc in procs)
        {
            bool exited = _interop.WaitForSingleObject(proc.Handle, 0) == WaitObject0;
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
            if (_interop.WaitForSingleObject(proc.Handle, 0) == WaitObject0)
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

            if (!_interop.TerminateProcess(proc.Handle, 1, out int terminateError))
            {
                results.Add(new ProcessTerminationResult
                {
                    Pid = proc.Snapshot.ProcessId,
                    ExpectedIdentity = proc.Snapshot.Identity,
                    ProcessName = proc.Snapshot.Name,
                    Result = terminateError == NativeMethods.ERROR_ACCESS_DENIED
                        ? ProcessTerminationStatus.AccessDenied
                        : ProcessTerminationStatus.Failed,
                    Win32Error = terminateError,
                    Message = terminateError == NativeMethods.ERROR_ACCESS_DENIED
                        ? "TerminateProcess 被拒绝。"
                        : $"TerminateProcess 失败（Win32 错误 {terminateError}）。",
                });
                continue;
            }

            // TerminateProcess 返回 true ≠ 进程已完全退出：有限等待确认 signaled
            uint wait = _interop.WaitForSingleObject(proc.Handle, ForceWaitMs);
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

    private static ApplicationTerminationResult Canceled(
        TerminationRequest request,
        TerminationStatus status,
        PreflightOutcome preflight)
        => new()
        {
            Status = status,
            FailureReason = preflight.CancelReason,
            DisplayName = request.ExpectedDisplayName,
            // 保留 Preflight 阶段已产生的诊断记录（如句柄打开失败的具体候选）
            ProcessResults = preflight.Results,
            ResidualIdentities = preflight.Results
                .Where(r => !r.ConfirmedExited)
                .Select(r => r.ExpectedIdentity)
                .ToList(),
            Message = preflight.CancelMessage,
        };

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
