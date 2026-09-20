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
/// </remarks>
public static class ApplicationDiffEngine
{
    public static ApplicationDiffResult ComputeDiff(
        IReadOnlyList<ApplicationGroup> oldGroups,
        IReadOnlyList<ApplicationGroup> newGroups)
    {
        ArgumentNullException.ThrowIfNull(oldGroups);
        ArgumentNullException.ThrowIfNull(newGroups);

        var oldByKey = new Dictionary<string, ApplicationGroup>(oldGroups.Count, StringComparer.Ordinal);
        foreach (ApplicationGroup oldGroup in oldGroups)
        {
            oldByKey[ApplicationStableKey.Compute(oldGroup)] = oldGroup;
        }

        var added = new List<ApplicationGroup>();
        var updated = new List<ApplicationGroup>();
        var matchedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (ApplicationGroup newGroup in newGroups)
        {
            string key = ApplicationStableKey.Compute(newGroup);
            if (oldByKey.ContainsKey(key))
            {
                updated.Add(newGroup);
                matchedKeys.Add(key);
            }
            else
            {
                added.Add(newGroup);
            }
        }

        var removedKeys = oldByKey.Keys
            .Where(key => !matchedKeys.Contains(key))
            .ToList();

        return new ApplicationDiffResult
        {
            Added = added,
            Updated = updated,
            RemovedKeys = removedKeys,
        };
    }
}
