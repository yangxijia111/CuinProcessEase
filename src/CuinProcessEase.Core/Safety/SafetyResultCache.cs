using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Safety;

/// <summary>
/// 安全评估缓存：以 ProcessIdentity（PID + StartTime）为键缓存评估结果。
/// </summary>
/// <remarks>
/// 保护状态（Critical / ProtectionLevel）在进程生命周期内基本不变，
/// 以 PID+StartTime 识别"还是不是同一个进程"：同一身份直接复用结果，
/// 新身份才重新调用 Safety API，避免每秒对全部 500+ 进程重复昂贵查询。
/// PID 被复用（StartTime 变化）即为新身份，绝不误用旧结果。
/// 非线程安全：由单一刷新循环顺序使用。
/// 未来真正执行 Kill 前仍必须绕过缓存重新验证。
/// </remarks>
public sealed class SafetyResultCache
{
    private readonly Dictionary<ProcessIdentity, ProcessSafetyResult> _cache = new();

    /// <summary>当前缓存条目数。</summary>
    public int Count => _cache.Count;

    /// <summary>是否命中缓存。</summary>
    public bool TryGet(ProcessIdentity identity, out ProcessSafetyResult result)
        => _cache.TryGetValue(identity, out result!);

    /// <summary>取缓存的评估结果；新身份才调用 <paramref name="assess"/> 并缓存。</summary>
    public ProcessSafetyResult GetOrAdd(
        ProcessIdentity identity,
        Func<ProcessIdentity, ProcessSafetyResult> assess)
    {
        ArgumentNullException.ThrowIfNull(assess);

        if (_cache.TryGetValue(identity, out ProcessSafetyResult? cached))
        {
            return cached;
        }

        ProcessSafetyResult fresh = assess(identity);
        _cache[identity] = fresh;
        return fresh;
    }

    /// <summary>
    /// 只保留仍然存活的身份（本次快照中出现的），清除已退出进程的条目，防止缓存无限增长。
    /// </summary>
    public void Retain(IEnumerable<ProcessIdentity> liveIdentities)
    {
        ArgumentNullException.ThrowIfNull(liveIdentities);

        var live = new HashSet<ProcessIdentity>(liveIdentities);
        List<ProcessIdentity> stale = _cache.Keys.Where(key => !live.Contains(key)).ToList();
        foreach (ProcessIdentity key in stale)
        {
            _cache.Remove(key);
        }
    }

    /// <summary>清空缓存（测试用）。</summary>
    public void Clear() => _cache.Clear();
}
