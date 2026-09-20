using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Gui;

/// <summary>
/// 应用组的稳定标识：跨刷新识别"还是同一个应用"，是稳定列表 / Diff 更新的基础。
/// </summary>
/// <remarks>
/// 优先级（尽可能稳定）：
/// 1. 主程序完整路径——仅限非歧义应用。Chrome 重启（PID/StartTime 全变）仍保持同一
///    app-level Key；
/// 2. 歧义运行时 / 宿主（python/node/java/svchost 等，
///    <see cref="ProcessNameRules.IsAmbiguousProcessName"/>）：禁止 path-only Key——
///    同一路径可以承载任意多个独立任务，必须使用任务根身份
///    （normalized exe + 排序后的全部 Root identities）；
/// 3. 无可靠主程序路径的多 Root 组：全部 Root identities（排序后，确定性）；
/// 4. 显示名（连根身份都拿不到时的最后兜底）。
/// 同一应用在两次刷新中必须得到相同 Key；不同应用绝不允许相同 Key。
/// </remarks>
public static class ApplicationStableKey
{
    /// <summary>主程序路径前缀（非歧义应用，app-level 稳定）。</summary>
    public const string ExePrefix = "exe:";

    /// <summary>任务根身份前缀（歧义运行时 / 宿主，task-level 身份）。</summary>
    public const string RuntimeRootPrefix = "runtime-root:";

    /// <summary>根进程身份前缀（无主程序路径的多 Root 组）。</summary>
    public const string RootPrefix = "root:";

    /// <summary>显示名前缀。</summary>
    public const string NamePrefix = "name:";

    public static string Compute(ApplicationGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        List<ProcessSnapshot> roots = group.RootProcesses
            .OrderBy(p => p.ProcessId)
            .ThenBy(p => p.StartTimeUtc ?? DateTime.MinValue)
            .ToList();

        // 歧义运行时 / 宿主：同一 exe 路径可承载任意多个独立任务（两个 python、
        // 多组 svchost），path-only Key 会确定性冲突——必须用任务根身份。
        // 多 Root 时使用排序后的全部 Root identities，绝不随意取第一个。
        bool ambiguousRoot = roots.Any(p => ProcessNameRules.IsAmbiguousProcessName(p.Name));
        if (ambiguousRoot)
        {
            string exe = NormalizePath(group.Identity.MainExecutable);
            string rootIdentities = string.Join("|", roots.Select(p =>
                $"{p.ProcessId}:{p.StartTimeUtc?.Ticks ?? -1}"));
            return $"{RuntimeRootPrefix}{exe}|{rootIdentities}";
        }

        // 1) 主程序完整路径：非歧义应用重启（PID/StartTime 全变）后路径不变，最稳定
        if (!string.IsNullOrWhiteSpace(group.Identity.MainExecutable))
        {
            return ExePrefix + NormalizePath(group.Identity.MainExecutable);
        }

        // 2) 根进程身份（无可靠主程序路径）：排序后的全部 Root identities
        if (roots.Count > 0)
        {
            string rootIdentities = string.Join("|", roots.Select(p =>
                $"{p.ProcessId}:{p.StartTimeUtc?.Ticks ?? -1}"));
            return $"{RootPrefix}{NormalizePath(group.Identity.MainExecutable)}|{rootIdentities}";
        }

        // 3) 显示名兜底（无 Root 的构造场景）
        return NamePrefix + group.Identity.DisplayName.Trim().ToLowerInvariant();
    }

    /// <summary>路径归一化：去除首尾空白 + 固定小写（Windows 路径大小写不敏感）。</summary>
    private static string NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? "" : path.Trim().ToLowerInvariant();
}
