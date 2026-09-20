using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Gui;

/// <summary>
/// 应用组的稳定标识：跨刷新识别"还是同一个应用"，是稳定列表 / Diff 更新的基础。
/// </summary>
/// <remarks>
/// 优先级（尽可能稳定）：
/// 1. 主程序完整路径（应用重启换 PID 也不变）；
/// 2. 根进程身份 PID + StartTime（无可靠主程序路径的多 Root 组）；
/// 3. 显示名（连根身份都拿不到时的最后兜底）。
/// 同一应用在两次刷新中必须得到相同 Key；不同应用绝不允许相同 Key。
/// </remarks>
public static class ApplicationStableKey
{
    /// <summary>主程序路径前缀。</summary>
    public const string ExePrefix = "exe:";

    /// <summary>根进程身份前缀。</summary>
    public const string RootPrefix = "root:";

    /// <summary>显示名前缀。</summary>
    public const string NamePrefix = "name:";

    public static string Compute(ApplicationGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // 1) 主程序完整路径：同一路径不会属于两个应用（同 exe 路径是 High 合并依据），
        //    且应用重启（PID/StartTime 全变）后路径不变，是最稳定的标识
        if (!string.IsNullOrWhiteSpace(group.Identity.MainExecutable))
        {
            return ExePrefix + group.Identity.MainExecutable.Trim().ToLowerInvariant();
        }

        // 2) 根进程身份：取 PID 最小的 Root（决定性，不依赖枚举顺序）
        ProcessSnapshot? root = group.RootProcesses
            .OrderBy(p => p.ProcessId)
            .FirstOrDefault();
        if (root is not null)
        {
            long startTicks = root.StartTimeUtc?.Ticks ?? -1;
            return $"{RootPrefix}{root.ProcessId}:{startTicks}";
        }

        // 3) 显示名兜底（无 Root 的构造场景）
        return NamePrefix + group.Identity.DisplayName.Trim().ToLowerInvariant();
    }
}
