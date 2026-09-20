using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Safety;
using CuinProcessEase.Windows.Native;

namespace CuinProcessEase.Windows.Safety;

/// <summary>
/// 安全引擎 Windows 实现：使用公开文档 API（IsProcessCritical / GetProcessInformation）
/// 采集保护状态，交由纯逻辑 SafetyDecisionEngine 判定。
/// </summary>
/// <remarks>
/// - 只"判断"，绝不"执行"：本类没有任何 Kill / Terminate / CloseMainWindow 能力；
/// - 不尝试绕过 PPL、不开 DebugPrivilege、不做令牌操纵；
/// - 按需计算（用户准备执行操作前），不进入 Snapshot 每秒热路径；
/// - 查询失败（AccessDenied 等）以 null 状态进入决策层，绝不当成 false / NONE。
/// </remarks>
public sealed class ProcessSafetyService : IProcessSafetyService
{
    /// <inheritdoc />
    public ProcessSafetyResult Assess(ProcessSnapshot process)
    {
        ArgumentNullException.ThrowIfNull(process);

        bool isSelf = process.ProcessId == Environment.ProcessId;

        bool? isCritical;
        int? protectionLevel;

        IntPtr handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, bInheritHandle: false, (uint)process.ProcessId);

        if (handle != IntPtr.Zero && handle != NativeMethods.INVALID_HANDLE_VALUE)
        {
            try
            {
                isCritical = QueryIsProcessCritical(handle);
                protectionLevel = QueryProtectionLevel(handle);
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }
        else
        {
            // 句柄打不开：两项保护状态均未知（不伪造）
            isCritical = null;
            protectionLevel = null;
        }

        return SafetyDecisionEngine.Evaluate(process, isSelf, isCritical, protectionLevel);
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

    /// <summary>
    /// IsProcessCritical 查询：返回 false 且 GetLastError 非 0 时视为未知。
    /// </summary>
    private static bool? QueryIsProcessCritical(IntPtr processHandle)
    {
        try
        {
            if (NativeMethods.IsProcessCritical(processHandle, out bool isCritical))
            {
                return isCritical;
            }

            // 失败 ≠ false：返回未知
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ProtectionLevel 查询：成功返回 0-6 原始级别；失败返回未知（null，绝不当 NONE）。
    /// </summary>
    private static int? QueryProtectionLevel(IntPtr processHandle)
    {
        try
        {
            var info = new NativeMethods.PROCESS_PROTECTION_LEVEL_INFORMATION();
            if (NativeMethods.GetProcessInformation(
                    processHandle,
                    NativeMethods.ProcessProtectionLevelInfo,
                    ref info,
                    System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESS_PROTECTION_LEVEL_INFORMATION>()))
            {
                return unchecked((int)info.ProtectionLevel);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
