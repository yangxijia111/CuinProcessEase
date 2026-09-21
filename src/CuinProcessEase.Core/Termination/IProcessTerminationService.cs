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
/// 3. 全部候选先完成 Identity Preflight，再执行任何终止动作；
/// 4. Graceful 绝不自动升级为 TerminateProcess。
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
    /// 强制终止单个进程：与组终止走完全相同的 Fresh Snapshot / Fresh Safety / Identity 校验管线，
    /// 仅预期身份为单个进程。同样 fail-closed。
    /// </summary>
    Task<ApplicationTerminationResult> ForceTerminateProcessAsync(
        TerminationRequest request, CancellationToken cancellationToken = default);
}
