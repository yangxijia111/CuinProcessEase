using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;

namespace CuinProcessEase.Core.Termination;

/// <summary>
/// Fresh 目标解析 + Safety 门禁的纯逻辑规划器。
/// </summary>
/// <remarks>
/// 输入 Fresh Snapshot / Fresh Grouping / Fresh Safety（调用方注入、禁止用 GUI 缓存），
/// 输出"继续执行（含有序候选）"或"取消（终态 + 原因）"。
/// 决策矩阵全部可单元测试：
/// - 0 组匹配 → AlreadyExited / TargetChanged（PID 被复用）——绝不仅因 exe path 相同就匹配新实例；
/// - ≥2 组匹配 → AmbiguousTarget；
/// - Blocked / Indeterminate / RequiresElevation → 拒绝执行；
/// - 候选中出现 Snapshot StartTime 不可靠的进程 → fail-closed 取消；
/// - P6.3/P6.4 破坏性范围门禁：组 Confidence &lt; High（Medium 弱证据组）时候选收窄为请求锚点成员，
///   绝不自动扩展到组内新增成员；High / VeryHigh 组 + Default 请求才允许全组候选（含新 helper）；
/// - P6.4：弱证据多进程组未经 ExplicitWeakGroup 授权 → ScopeConfirmationRequired 拒绝执行；
///   携带 ExplicitWeakGroup 授权时（无论组置信度）候选一律限定为用户确认过的 exact identities；
/// - Proceed 时候选顺序：Root 优先，其余按 PID。
/// </remarks>
public static class TerminationPlanner
{
    /// <summary>规划结果。</summary>
    public sealed class TerminationPlan
    {
        /// <summary>是否继续进入执行阶段（Preflight / 终止动作）。</summary>
        public required bool Proceed { get; init; }

        /// <summary>非 Proceed 时的应用级终态。</summary>
        public TerminationStatus Status { get; init; } = TerminationStatus.Success;

        /// <summary>失败/取消补充原因。</summary>
        public TerminationFailureReason FailureReason { get; init; } = TerminationFailureReason.None;

        /// <summary>人类可读说明。</summary>
        public string? Message { get; init; }

        /// <summary>命中的 Fresh 目标组（Proceed 时非 null）。</summary>
        public ApplicationGroup? TargetGroup { get; init; }

        /// <summary>有序候选进程（Root 优先，其余按 PID；Proceed 时非空）。</summary>
        public IReadOnlyList<ProcessSnapshot> Candidates { get; init; } = Array.Empty<ProcessSnapshot>();
    }

    /// <summary>
    /// 在 Fresh 分组中解析终止目标并施加 Safety 门禁。
    /// </summary>
    /// <param name="request">终止请求（预期成员身份）。</param>
    /// <param name="freshGroups">Fresh 应用分组。</param>
    /// <param name="freshProcesses">Fresh 快照进程列表（用于 TargetChanged / PID 复用判定）。</param>
    /// <param name="assessFreshSafety">Fresh Safety 评估委托（实现方必须绕过任何 GUI 缓存）。</param>
    public static TerminationPlan Plan(
        TerminationRequest request,
        IReadOnlyList<ApplicationGroup> freshGroups,
        IReadOnlyList<ProcessSnapshot> freshProcesses,
        Func<ApplicationGroup, ApplicationSafetyResult> assessFreshSafety)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(freshGroups);
        ArgumentNullException.ThrowIfNull(freshProcesses);
        ArgumentNullException.ThrowIfNull(assessFreshSafety);

        if (!request.IsValid)
        {
            return Cancel(TerminationStatus.Failed, TerminationFailureReason.UnreliableIdentity,
                "终止请求无效（缺少预期身份）。");
        }

        // 可靠锚点：仅 StartTime 已知的身份可参与匹配（PID + null 不是可靠身份）
        List<ProcessIdentity> anchors = request.ExpectedMemberIdentities
            .Where(i => i.StartTimeUtc is not null)
            .ToList();

        if (anchors.Count == 0)
        {
            return Cancel(TerminationStatus.Failed, TerminationFailureReason.UnreliableIdentity,
                $"“{request.ExpectedDisplayName}”的成员身份缺少启动时间，无法安全定位目标，已拒绝操作。");
        }

        // Fresh 分组匹配：包含任一完全匹配身份的组
        List<ApplicationGroup> matched = freshGroups
            .Where(g => g.Processes.Any(p => anchors.Contains(p.Identity)))
            .ToList();

        if (matched.Count >= 2)
        {
            return Cancel(TerminationStatus.AmbiguousTarget, TerminationFailureReason.AmbiguousTarget,
                $"“{request.ExpectedDisplayName}”的身份分散在 {matched.Count} 个应用组中，无法唯一确定目标，已取消。");
        }

        if (matched.Count == 0)
        {
            // 无任何原成员：区分"已全部退出"与"PID 被复用（目标已变化）"。
            // 绝不因 exe path / StableKey 相同匹配新实例。
            bool pidReused = freshProcesses.Any(p =>
                anchors.Any(a => a.ProcessId == p.ProcessId));
            return pidReused
                ? Cancel(TerminationStatus.TargetChanged, TerminationFailureReason.None,
                    $"“{request.ExpectedDisplayName}”的原进程已退出，其 PID 已被其他进程复用，已拒绝操作。")
                : Cancel(TerminationStatus.AlreadyExited, TerminationFailureReason.None,
                    $"“{request.ExpectedDisplayName}”的相关进程均已退出。");
        }

        ApplicationGroup target = matched[0];

        // Fresh Safety 门禁（唯一执行入口；非 Allowed 一律拒绝）
        ApplicationSafetyResult safety = assessFreshSafety(target);
        switch (safety.Decision)
        {
            case SafetyDecision.Blocked:
                return Cancel(TerminationStatus.Blocked, TerminationFailureReason.SafetyRejected,
                    $"“{target.Identity.DisplayName}”受系统保护，禁止结束。");
            case SafetyDecision.Indeterminate:
                return Cancel(TerminationStatus.Indeterminate, TerminationFailureReason.SafetyRejected,
                    $"“{target.Identity.DisplayName}”安全状态未知，已阻止操作。");
            case SafetyDecision.RequiresElevation:
                return Cancel(TerminationStatus.RequiresElevation, TerminationFailureReason.SafetyRejected,
                    $"“{target.Identity.DisplayName}”需要管理员权限，当前阶段不提供提权。");
            case SafetyDecision.Allowed:
                break;
            default:
                return Cancel(TerminationStatus.Indeterminate, TerminationFailureReason.SafetyRejected,
                    $"“{target.Identity.DisplayName}”安全状态未知，已阻止操作。");
        }

        // P6.4 Weak Group Scope Consent：弱证据多进程组（Confidence < High 且成员 > 1）
        // 在用户未经过专门弱组确认对话框明确授权（ScopeConsent != ExplicitWeakGroup）时
        // 一律拒绝执行（0 WM_CLOSE、0 TerminateProcess）——默认构造绝不自动获得弱组授权。
        // 单进程组没有"错误扩大到其他成员"的 blast radius，无需额外确认。
        bool weakMultiProcessGroup = target.Confidence < GroupingConfidence.High && target.ProcessCount > 1;
        if (weakMultiProcessGroup && request.ScopeConsent != TerminationScopeConsent.ExplicitWeakGroup)
        {
            return Cancel(TerminationStatus.ScopeConfirmationRequired,
                TerminationFailureReason.ScopeConfirmationRequired,
                $"“{target.Identity.DisplayName}”为弱证据分组（可信度 {target.Confidence}，{target.ProcessCount} 个进程），"
                + "需在确认对话框中明确确认要操作的进程后才能结束。");
        }

        // P6.3/P6.4 破坏性范围门禁：以下两种情形候选收窄为请求锚点成员，绝不自动扩展：
        // 1) 组 Confidence < High（弱证据组，即使已 ExplicitWeakGroup 授权，
        //    也只操作用户确认过的 exact identities，组内新出现的弱证据成员绝不纳入）；
        // 2) 请求携带 ExplicitWeakGroup 授权（用户只授权了确认时列出的成员，
        //    即使 Fresh 组呈强证据也不得超出确认范围自动扩展新 helper）。
        // High / VeryHigh 组 + Default 请求：允许全组候选（含新浮现 helper，原设计保留）。
        bool anchoredOnly = target.Confidence < GroupingConfidence.High
                            || request.ScopeConsent == TerminationScopeConsent.ExplicitWeakGroup;
        var anchorSet = new HashSet<ProcessIdentity>(anchors);
        List<ProcessSnapshot> candidatePool = anchoredOnly
            ? target.Processes.Where(p => anchorSet.Contains(p.Identity)).ToList()
            : [.. target.Processes];

        // 候选顺序：Root 优先（先切断主要控制），其余按 PID。
        var rootIds = new HashSet<ProcessIdentity>(target.RootProcesses.Select(p => p.Identity));
        List<ProcessSnapshot> roots = candidatePool
            .Where(p => rootIds.Contains(p.Identity))
            .OrderBy(p => p.ProcessId)
            .ToList();
        List<ProcessSnapshot> others = candidatePool
            .Where(p => !rootIds.Contains(p.Identity))
            .OrderBy(p => p.ProcessId)
            .ToList();
        var candidates = roots.Concat(others).ToList();

        // fail-closed：任何候选的 Snapshot StartTime 不可靠 → 整个操作取消
        if (candidates.Any(p => p.StartTimeUtc is null))
        {
            return Cancel(TerminationStatus.Failed, TerminationFailureReason.UnreliableIdentity,
                $"“{target.Identity.DisplayName}”存在无法确认启动时间的成员，无法安全校验身份，已拒绝操作。");
        }

        string message;
        if (!anchoredOnly)
        {
            message = $"目标组“{target.Identity.DisplayName}”（{candidates.Count} 个进程）。";
        }
        else
        {
            int excluded = target.ProcessCount - candidates.Count;
            message = excluded > 0
                ? $"目标组“{target.Identity.DisplayName}”：仅终止请求确认的 {candidates.Count} 个成员"
                  + $"（组内另有 {excluded} 个成员未纳入授权范围，绝不自动扩展）。"
                : $"目标组“{target.Identity.DisplayName}”（请求确认的 {candidates.Count} 个成员）。";
        }

        return new TerminationPlan
        {
            Proceed = true,
            TargetGroup = target,
            Candidates = candidates,
            Message = message,
        };
    }

    private static TerminationPlan Cancel(TerminationStatus status, TerminationFailureReason reason, string message)
        => new()
        {
            Proceed = false,
            Status = status,
            FailureReason = reason,
            Message = message,
        };
}
