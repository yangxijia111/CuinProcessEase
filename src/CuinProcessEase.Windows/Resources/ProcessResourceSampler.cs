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
/// - 以 ProcessIdentity（PID + StartTime）比对两次采样，PID 重用视为新进程；
/// - 句柄打不开 / 已退出 / 时间读取失败：该进程 CPU 为 null，绝不伪造；
/// - 内存直接取自快照（WorkingSetBytes），不重复开句柄查询；
/// - 非线程安全：由单一刷新循环顺序调用。
/// </remarks>
public sealed class ProcessResourceSampler
{
    /// <summary>上次采样：PID → （进程身份，内核+用户时间 100ns tick 累计值）。</summary>
    private readonly Dictionary<int, (ProcessIdentity Identity, long TotalTicks)> _previous = new();

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

            long? totalTicks = TryReadTotalCpuTicks(pid);
            double? cpuPercent = null;

            if (totalTicks.HasValue
                && _hasPrevious
                && _previous.TryGetValue(pid, out (ProcessIdentity Identity, long TotalTicks) previous)
                && previous.Identity == process.Identity)
            {
                cpuPercent = CpuUsageCalculator.ComputePercent(
                    previous.TotalTicks, totalTicks.Value,
                    _previousWallTicks, currentWallTicks,
                    CoreCount);
            }

            if (totalTicks.HasValue)
            {
                _previous[pid] = (process.Identity, totalTicks.Value);
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
    /// 读取进程内核+用户 CPU 时间之和（FILETIME 100ns tick）。
    /// 句柄打不开 / 已退出 / 读取失败返回 null。
    /// </summary>
    private static long? TryReadTotalCpuTicks(int pid)
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
                    out _,
                    out _,
                    out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
                    out System.Runtime.InteropServices.ComTypes.FILETIME user))
            {
                return null;
            }

            long kernelTicks = ((long)kernel.dwHighDateTime << 32) | (uint)kernel.dwLowDateTime;
            long userTicks = ((long)user.dwHighDateTime << 32) | (uint)user.dwLowDateTime;
            long total = kernelTicks + userTicks;
            return total >= 0 ? total : null;
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
