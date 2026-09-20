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
                // 空快照（理论上是系统里至少有 Idle），按无进程处理
                return result;
            }

            do
            {
                int pid = unchecked((int)entry.th32ProcessID);
                result[pid] = new ToolhelpProcessEntry(
                    pid,
                    unchecked((int)entry.th32ParentProcessID),
                    entry.szExeFile);
            }
            while (NativeMethods.Process32NextW(snapshot, ref entry));
            // Process32NextW 返回 false 表示枚举完毕（或读取出错），两种情况都停止即可
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return result;
    }
}
