using System.Runtime.InteropServices;
using CuinProcessEase.Windows.Native;

namespace CuinProcessEase.Windows.Resources;

/// <summary>系统级资源采样（Overview 栏）。</summary>
/// <param name="CpuPercent">整机 CPU 百分比（0-100）；首次采样为 null。</param>
/// <param name="MemoryPercent">物理内存占用百分比（0-100）。</param>
/// <param name="TotalPhysicalBytes">物理内存总量（字节）。</param>
/// <param name="AvailablePhysicalBytes">可用物理内存（字节）。</param>
public readonly record struct SystemResourceSample(
    double? CpuPercent,
    double MemoryPercent,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes);

/// <summary>
/// 系统资源监控：CPU 来自 GetSystemTimes（两次采样差分，含多核总量），
/// 内存来自 GlobalMemoryStatusEx。纯官方 API，不引入第三方监控库。
/// </summary>
/// <remarks>非线程安全：由单一刷新循环顺序调用。</remarks>
public sealed class SystemResourceMonitor
{
    private long _previousIdleTicks;
    private long _previousKernelTicks;
    private long _previousUserTicks;
    private bool _hasPrevious;

    /// <summary>采样一次系统 CPU 与内存。</summary>
    public SystemResourceSample Sample()
    {
        double? cpuPercent = null;

        if (NativeMethods.GetSystemTimes(
                out System.Runtime.InteropServices.ComTypes.FILETIME idle,
                out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
                out System.Runtime.InteropServices.ComTypes.FILETIME user))
        {
            long idleTicks = ToLong(idle);
            long kernelTicks = ToLong(kernel);
            long userTicks = ToLong(user);

            if (_hasPrevious)
            {
                // kernel 时间包含 idle，总量 = kernel + user，忙碌 = 总量 − idle
                long totalDelta = (kernelTicks - _previousKernelTicks) + (userTicks - _previousUserTicks);
                long idleDelta = idleTicks - _previousIdleTicks;
                if (totalDelta > 0)
                {
                    cpuPercent = Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0.0, 100.0);
                }
            }

            _previousIdleTicks = idleTicks;
            _previousKernelTicks = kernelTicks;
            _previousUserTicks = userTicks;
            _hasPrevious = true;
        }

        var memoryStatus = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
        };

        double memoryPercent = 0;
        ulong totalPhysical = 0;
        ulong availablePhysical = 0;
        if (NativeMethods.GlobalMemoryStatusEx(ref memoryStatus))
        {
            memoryPercent = memoryStatus.dwMemoryLoad;
            totalPhysical = memoryStatus.ullTotalPhys;
            availablePhysical = memoryStatus.ullAvailPhys;
        }

        return new SystemResourceSample(cpuPercent, memoryPercent, totalPhysical, availablePhysical);
    }

    private static long ToLong(System.Runtime.InteropServices.ComTypes.FILETIME fileTime)
        => ((long)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
}
