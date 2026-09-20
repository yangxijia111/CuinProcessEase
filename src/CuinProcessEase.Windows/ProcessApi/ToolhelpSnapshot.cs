using System.ComponentModel;
using System.Runtime.InteropServices;
using CuinProcessEase.Windows.Native;

namespace CuinProcessEase.Windows.ProcessApi;

/// <summary>
/// 单条 Tool Help 进程条目（来自 PROCESSENTRY32W）。
/// </summary>
/// <param name="ProcessId">进程 PID。</param>
/// <param name="ParentProcessId">父进程 PID（PPID）。父进程可能已退出。</param>
/// <param name="ExeFileName">进程可执行文件名（含 .exe）。</param>
internal sealed record ToolhelpProcessEntry(
    int ProcessId,
    int ParentProcessId,
    string ExeFileName);

/// <summary>
/// Win32 Tool Help API（CreateToolhelp32Snapshot / Process32First / Process32Next）封装。
/// 用途：一次性获取全部进程的 PID → PPID 映射，避免用进程名猜测父进程。
/// </summary>
internal static class ToolhelpSnapshot
{
    /// <summary>
    /// 拍摄一次全系统进程快照，返回 PID → 条目 的映射。
    /// </summary>
    /// <exception cref="Win32Exception">快照句柄创建失败等系统级错误。</exception>
    public static Dictionary<int, ToolhelpProcessEntry> CaptureProcesses()
    {
        var result = new Dictionary<int, ToolhelpProcessEntry>(1024);

        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.INVALID_HANDLE_VALUE)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot 失败");
        }

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>(),
            };

            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == unchecked((int)NativeMethods.ERROR_NO_MORE_FILES))
                {
                    // 空快照：没有任何进程可枚举，属正常结束
                    return result;
                }

                throw new Win32Exception(error, $"Process32FirstW 失败，Win32 错误码 {error}");
            }

            while (true)
            {
                int pid = unchecked((int)entry.th32ProcessID);
                result[pid] = new ToolhelpProcessEntry(
                    pid,
                    unchecked((int)entry.th32ParentProcessID),
                    entry.szExeFile);

                if (NativeMethods.Process32NextW(snapshot, ref entry))
                {
                    continue;
                }

                int error = Marshal.GetLastWin32Error();
                if (error == unchecked((int)NativeMethods.ERROR_NO_MORE_FILES))
                {
                    // 枚举完成
                    break;
                }

                // 其他错误：抛出，由 SnapshotService 的降级逻辑统一处理
                throw new Win32Exception(error, $"Process32NextW 失败，Win32 错误码 {error}");
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return result;
    }
}
