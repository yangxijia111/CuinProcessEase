namespace CuinProcessEase.Core.Termination;

/// <summary>
/// 进程终止服务接口：结束一个应用组（或单进程）。
/// </summary>
/// <remarks>
/// 安全原则（优先级从高到低）：Safety &gt; Kill Ability；Fresh State &gt; UI Cached State；
/// Exact Process Identity &gt; StableKey；宁可拒绝结束，也不能误杀。
/// 实现必须：
/// 1. 执行前重新获取 Fresh Snapshot / Fresh Grouping / Fresh Safety（禁止 GUI Safety 缓存）；
/// 2. 仅以 ProcessIdentity（PID + StartTimeUtc）完全匹配定位目标，绝不使用 StableKey；
/// 3. 严格两阶段 Preflight：全部候选用真实句柄以 CreationTime 原始 FILETIME 逐位比对
///    （无任何时间容差），全部通过前绝不执行任何终止动作；任一候选失败即整组取消；
/// 4. Graceful 绝不自动升级为 TerminateProcess；
/// 5. Force 最终必须 Final Fresh Rescan 验证全部 exact identity（只验证，不扩大范围）。
/// </remarks>
public interface IProcessTerminationService
{
    /// <summary>
    /// 优雅关闭：仅向目标进程的顶层窗口发送 WM_CLOSE，等待有限时间；
    /// 有残留时返回 PartialSuccess（由调用方决定是否强制结束），绝不自动升级强杀。
    /// </summary>
    Task<ApplicationTerminationResult> CloseApplicationGracefullyAsync(
        TerminationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 强制终止：对 Fresh Safety 为 Allowed 的组，用 Preflight 验证过的句柄 TerminateProcess，
    /// 有限等待确认退出，并执行最多 2 轮残留清理（仅限仍有 exact identity 锚点的组）。
    /// </summary>
    Task<ApplicationTerminationResult> ForceTerminateApplicationAsync(
        TerminationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 强制终止单个进程：Fresh Snapshot → exact ProcessIdentity 定位 → Fresh Safety（仅该进程）→
    /// HANDLE Preflight → exact CreationTime（无任何容差）→ TerminateProcess → 有限等待 → Fresh Rescan。
    /// <see cref="TerminationRequest.ExpectedMemberIdentities"/> 必须恰好包含一个身份；
    /// 绝不因目标属于某个应用组而把组内其他成员纳入候选。同样 fail-closed。
    /// </summary>
    Task<ApplicationTerminationResult> ForceTerminateProcessAsync(
        TerminationRequest request, CancellationToken cancellationToken = default);
}
