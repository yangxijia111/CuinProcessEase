using CuinProcessEase.Core.Models;
using CuinProcessEase.Core.Resources;
using CuinProcessEase.Windows.Native;

namespace CuinProcessEase.Windows.Resources;

/// <summary>
/// 进程资源采样器：每次刷新对快照内全部进程读取 CPU 时间与内存，
/// 结合上次采样计算 CPU 百分比（占整机，已按核心数归一化）。
/// </summary>
/// <remarks>
/// - 首次采样 CPU 为 null（UI 显示 "--"），第二次起才有可靠百分比；
/// - 采样历史身份 = PID + GetProcessTimes CreationTime（同一次调用里一并读取），
///   不依赖 Snapshot.StartTime：即使快照 StartTime 为空，只要 CPU 句柄可查询，
///   PID 重用（CreationTime 变化）仍能被正确识别为"新进程"（CPU = null，重建历史）；
/// - 句柄打不开 / 已退出 / 时间读取失败：该进程 CPU 为 null，绝不伪造；
/// - 内存直接取自快照（WorkingSetBytes），不重复开句柄查询；
/// - 非线程安全：由单一刷新循环顺序调用。
/// </remarks>
public sealed class ProcessResourceSampler
{
    /// <summary>上次采样：PID → （CreationTime FILETIME，内核+用户时间 100ns tick 累计值）。</summary>
    private readonly Dictionary<int, (long CreationTime, long TotalTicks)> _previous = new();

    private long _previousWallTicks;
    private bool _hasPrevious;

    /// <summary>逻辑核心数（多核归一化用）。</summary>
    public int CoreCount { get; } = Environment.ProcessorCount;

    /// <summary>
    /// 对本次快照做一次采样，返回 PID → 资源采样。
    /// </summary>
    public IReadOnlyDictionary<int, ProcessResourceSample> Sample(ProcessSnapshotCollection snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // 墙钟用 TickCount64（毫秒分辨率，永不回退）换算为 100ns tick，与 FILETIME 单位一致
        long currentWallTicks = Environment.TickCount64 * 10_000L;
        var result = new Dictionary<int, ProcessResourceSample>(snapshot.Count);
        var alivePids = new HashSet<int>(snapshot.Count);

        foreach (ProcessSnapshot process in snapshot.Processes)
        {
            int pid = process.ProcessId;
            alivePids.Add(pid);

            (long CreationTime, long TotalTicks)? times = TryReadCpuTimes(pid);
            double? cpuPercent = null;

            if (times.HasValue
                && _hasPrevious
                && _previous.TryGetValue(pid, out (long CreationTime, long TotalTicks) previous))
            {
                cpuPercent = ComputeCpuDeltaOrNull(
                    previous.CreationTime, times.Value.CreationTime,
                    previous.TotalTicks, times.Value.TotalTicks,
                    _previousWallTicks, currentWallTicks,
                    CoreCount);
            }

            if (times.HasValue)
            {
                _previous[pid] = times.Value;
            }
            else
            {
                _previous.Remove(pid);
            }

            result[pid] = new ProcessResourceSample(cpuPercent, process.WorkingSetBytes);
        }

        // 清理已退出进程，防止采样状态随时间无限增长
        if (_previous.Count > 0)
        {
            List<int> stale = _previous.Keys.Where(pid => !alivePids.Contains(pid)).ToList();
            foreach (int pid in stale)
            {
                _previous.Remove(pid);
            }
        }

        _previousWallTicks = currentWallTicks;
        _hasPrevious = true;
        return result;
    }

    /// <summary>
    /// 由上次采样计算 CPU 百分比；CreationTime 不同（PID 重用）视为全新进程返回 null。
    /// internal 仅供测试直接验证 PID 重用分支。
    /// </summary>
    internal static double? ComputeCpuDeltaOrNull(
        long previousCreationTime,
        long currentCreationTime,
        long previousTotalTicks,
        long currentTotalTicks,
        long previousWallTicks,
        long currentWallTicks,
        int coreCount)
    {
        if (previousCreationTime != currentCreationTime)
        {
            // PID 相同但 CreationTime 不同：复用 PID 的新进程，历史不可用
            return null;
        }

        return CpuUsageCalculator.ComputePercent(
            previousTotalTicks, currentTotalTicks,
            previousWallTicks, currentWallTicks,
            coreCount);
    }

    /// <summary>
    /// 一次调用同时读取进程 CreationTime 与 内核+用户 CPU 时间（FILETIME 100ns tick）。
    /// 句柄打不开 / 已退出 / 读取失败 / CreationTime 非法（0）返回 null。
    /// </summary>
    private static (long CreationTime, long TotalTicks)? TryReadCpuTimes(int pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, bInheritHandle: false, (uint)pid);
        if (handle == IntPtr.Zero || handle == NativeMethods.INVALID_HANDLE_VALUE)
        {
            return null;
        }

        try
        {
            if (!NativeMethods.GetProcessTimes(
                    handle,
                    out System.Runtime.InteropServices.ComTypes.FILETIME creation,
                    out _,
                    out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
                    out System.Runtime.InteropServices.ComTypes.FILETIME user))
            {
                return null;
            }

            long creationTicks = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
            if (creationTicks <= 0)
            {
                // 无效创建时间：无法作为可靠身份，宁缺毋假
                return null;
            }

            long kernelTicks = ((long)kernel.dwHighDateTime << 32) | (uint)kernel.dwLowDateTime;
            long userTicks = ((long)user.dwHighDateTime << 32) | (uint)user.dwLowDateTime;
            long total = kernelTicks + userTicks;
            return total >= 0 ? (creationTicks, total) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
