using CuinProcessEase.Core.Grouping;

namespace CuinProcessEase.Core.Gui;

/// <summary>
/// 两次快照之间应用组的 Diff 计算结果。
/// </summary>
public sealed class ApplicationDiffResult
{
    /// <summary>新增应用（上次不存在）。</summary>
    public required IReadOnlyList<ApplicationGroup> Added { get; init; }

    /// <summary>仍然存在的应用（新快照的组对象，用于原地更新对应行）。</summary>
    public required IReadOnlyList<ApplicationGroup> Updated { get; init; }

    /// <summary>已退出应用的稳定 Key（对应行应被移除）。</summary>
    public required IReadOnlyList<string> RemovedKeys { get; init; }

    /// <summary>本次没有任何变化。</summary>
    public bool IsEmpty => Added.Count == 0 && RemovedKeys.Count == 0 && Updated.Count == 0;
}

/// <summary>
/// 应用组 Diff 引擎：按 <see cref="ApplicationStableKey"/> 比较旧/新两组应用，
/// 输出"新增 / 原地更新 / 移除"三类操作。
/// </summary>
/// <remarks>
/// 这是稳定列表的核心：UI 层对已存在应用只更新原行对象，绝不 Clear + 全量重建，
/// 从而保持选择、展开状态与列表顺序。纯逻辑，无 UI 依赖，可直接单元测试。
/// 同一帧内出现重复 StableKey 属于身份系统故障：立即抛出并指明冲突双方，
/// 绝不允许"后出现覆盖前一个"的静默吞没。
/// </remarks>
public static class ApplicationDiffEngine
{
    public static ApplicationDiffResult ComputeDiff(
        IReadOnlyList<ApplicationGroup> oldGroups,
        IReadOnlyList<ApplicationGroup> newGroups)
    {
        ArgumentNullException.ThrowIfNull(oldGroups);
        ArgumentNullException.ThrowIfNull(newGroups);

        IReadOnlyDictionary<string, ApplicationGroup> oldByKey = BuildKeyMap(oldGroups);
        IReadOnlyDictionary<string, ApplicationGroup> newByKey = BuildKeyMap(newGroups);

        var added = new List<ApplicationGroup>();
        var updated = new List<ApplicationGroup>();

        foreach (KeyValuePair<string, ApplicationGroup> pair in newByKey)
        {
            if (oldByKey.ContainsKey(pair.Key))
            {
                updated.Add(pair.Value);
            }
            else
            {
                added.Add(pair.Value);
            }
        }

        var removedKeys = oldByKey.Keys
            .Where(key => !newByKey.ContainsKey(key))
            .ToList();

        return new ApplicationDiffResult
        {
            Added = added,
            Updated = updated,
            RemovedKeys = removedKeys,
        };
    }

    /// <summary>
    /// 按稳定 Key 建立索引；同一帧出现重复 Key 立即抛出
    /// <see cref="InvalidOperationException"/>（消息包含冲突双方的显示名），绝不静默覆盖。
    /// </summary>
    /// <remarks>
    /// 正确的 StableKey 规则下真实运行永不触发；此防护保证身份系统的任何未来回归
    /// 都会立刻显式失败，而不是悄悄丢失一个应用组。
    /// </remarks>
    public static IReadOnlyDictionary<string, ApplicationGroup> BuildKeyMap(
        IReadOnlyList<ApplicationGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var map = new Dictionary<string, ApplicationGroup>(groups.Count, StringComparer.Ordinal);
        foreach (ApplicationGroup group in groups)
        {
            string key = ApplicationStableKey.Compute(group);
            if (!map.TryAdd(key, group))
            {
                throw new InvalidOperationException(
                    $"StableKey 冲突：'{key}' 同时属于 '{map[key].Identity.DisplayName}' 与 " +
                    $"'{group.Identity.DisplayName}'。同帧出现重复身份，拒绝静默覆盖。");
            }
        }

        return map;
    }
}
