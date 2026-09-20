using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;

namespace CuinProcessEase.Windows.Safety;

/// <summary>
/// 带缓存的 Safety 服务：以 ProcessIdentity（PID + StartTime）为键复用评估结果，
/// 避免每秒对全部进程重复执行 OpenProcess / IsProcessCritical / GetProcessInformation。
/// </summary>
/// <remarks>
/// 保护状态在进程生命周期内基本静态；只有新出现的进程身份才真正调用 Safety API。
/// 刷新循环每次快照后调用 <see cref="RetainLive"/> 清理已退出身份，防止缓存增长。
/// 缓存只服务于"展示"；未来真正执行 Kill 前必须绕过缓存重新验证。
/// 非线程安全：由单一刷新循环顺序使用。
/// </remarks>
public sealed class CachedProcessSafetyService : IProcessSafetyService
{
    private readonly IProcessSafetyService _inner;
    private readonly SafetyResultCache _cache = new();

    public CachedProcessSafetyService(IProcessSafetyService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>底层缓存（供刷新循环 Retain 与测试检查）。</summary>
    public SafetyResultCache Cache => _cache;

    /// <inheritdoc />
    public ProcessSafetyResult Assess(ProcessSnapshot process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return _cache.GetOrAdd(process.Identity, _ => _inner.Assess(process));
    }

    /// <inheritdoc />
    public ApplicationSafetyResult Assess(ApplicationGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        List<ProcessSafetyResult> memberResults = group.Processes
            .Select(Assess)
            .ToList();

        return ApplicationSafetyAggregator.Aggregate(group.Identity.DisplayName, memberResults);
    }

    /// <summary>只保留仍存活的进程身份（每次快照后调用）。</summary>
    public void RetainLive(IEnumerable<ProcessIdentity> liveIdentities)
        => _cache.Retain(liveIdentities);
}
