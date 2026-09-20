using System.Management;

namespace CuinProcessEase.Windows.ProcessApi;

/// <summary>
/// 进程命令行读取器：通过 WMI Win32_Process 一次性读取全部进程命令行。
/// </summary>
/// <remarks>
/// 按需调用（如详情页、需要命令行辅助分组的场景），不参与默认每秒快照热路径——
/// 一次 WMI 全表查询约增加 1 秒耗时。
/// 安全边界：官方稳定只读接口；不注入、不使用 DebugPrivilege、不解析目标进程 PEB；
/// 普通权限下读不到的进程（其他用户 / 受保护进程）返回 null。
/// </remarks>
public sealed class ProcessCommandLineProvider
{
    /// <summary>
    /// 按需读取单个进程的命令行（WMI 单条查询，避免全表扫描的 1 秒开销）。
    /// 进程已退出 / 权限不足 / WMI 不可用返回 null，绝不抛出。
    /// </summary>
    public string? Capture(int processId)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");

            foreach (ManagementBaseObject managementObject in searcher.Get())
            {
                try
                {
                    return managementObject["CommandLine"] as string;
                }
                finally
                {
                    try { managementObject.Dispose(); } catch { /* 忽略 */ }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 读取当前全部进程的命令行，返回 PID → 命令行 映射。
    /// WMI 不可用等系统级失败返回空表，绝不抛出。
    /// </summary>
    public Dictionary<int, string?> CaptureAll()
    {
        var result = new Dictionary<int, string?>(256);
        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT ProcessId, CommandLine FROM Win32_Process");

            foreach (ManagementBaseObject managementObject in searcher.Get())
            {
                try
                {
                    object? pidValue = managementObject["ProcessId"];
                    if (pidValue is null)
                    {
                        continue;
                    }

                    result[Convert.ToInt32(pidValue)] =
                        managementObject["CommandLine"] as string;
                }
                catch
                {
                    // 单条记录损坏跳过，不影响其余
                }
                finally
                {
                    try { managementObject.Dispose(); } catch { /* 忽略 */ }
                }
            }
        }
        catch
        {
            // WMI 服务不可用等系统级失败：命令行整体缺失，安全降级为空表
        }

        return result;
    }
}
