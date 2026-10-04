namespace CuinProcessEase.Core.Resources;

/// <summary>
/// 全部应用行的资源历史存储：StableKey → 环形缓冲。
/// </summary>
/// <remarks>
/// StableKey 只用于 GUI 展示连续性（P8 图表历史跨刷新关联同一应用），
/// 与终止链路无关（终止链路只认 exact ProcessIdentity，见 P5/P6 原则）。
/// 行消失时由 <see cref="Retain"/> 清理对应历史，保证总量随应用数线性、不泄漏。
/// </remarks>
public sealed class ResourceHistoryStore
{
    private readonly Dictionary<string, ResourceHistoryRing> _rings = new(StringComparer.Ordinal);

    private readonly int _capacity;

    public ResourceHistoryStore(int capacity = ResourceHistoryRing.DefaultCapacity)
    {
        _capacity = capacity;
    }

    /// <summary>当前跟踪的应用数。</summary>
    public int TrackedCount => _rings.Count;

    /// <summary>追加一个应用本秒的采样（应用首次出现时自动创建缓冲）。</summary>
    public void Append(string stableKey, ResourceSample sample)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        if (!_rings.TryGetValue(stableKey, out ResourceHistoryRing? ring))
        {
            ring = new ResourceHistoryRing(_capacity);
            _rings[stableKey] = ring;
        }

        ring.Append(sample);
    }

    /// <summary>读取指定应用的历史（不存在返回 null）。</summary>
    public ResourceHistoryRing? TryGet(string stableKey)
        => _rings.GetValueOrDefault(stableKey);

    /// <summary>只保留仍在的 StableKey（行已消失的应用历史随之释放，防长期泄漏）。</summary>
    public void Retain(IReadOnlySet<string> liveStableKeys)
    {
        ArgumentNullException.ThrowIfNull(liveStableKeys);
        List<string> stale = _rings.Keys.Where(k => !liveStableKeys.Contains(k)).ToList();
        foreach (string key in stale)
        {
            _rings.Remove(key);
        }
    }
}
