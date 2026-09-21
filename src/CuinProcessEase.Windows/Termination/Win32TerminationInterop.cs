using System.Runtime.InteropServices;
using CuinProcessEase.Windows.Native;

namespace CuinProcessEase.Windows.Termination;

/// <summary>
/// 真实 Win32 实现：直接 P/Invoke NativeMethods。
/// Win32 错误码在 SetLastError 调用后立即读取，避免被中间代码污染。
/// </summary>
internal sealed class Win32TerminationInterop : ITerminationInterop
{
    /// <inheritdoc />
    public IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId, out int win32Error)
    {
        IntPtr handle = NativeMethods.OpenProcess(desiredAccess, inheritHandle, processId);
        win32Error = handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        return handle;
    }

    /// <inheritdoc />
    public bool TryGetCreationFileTime(IntPtr processHandle, out long creationFileTimeUtc)
    {
        if (NativeMethods.GetProcessTimes(processHandle, out var creation, out _, out _, out _))
        {
            long raw = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
            if (raw > 0)
            {
                creationFileTimeUtc = raw;
                return true;
            }
        }

        creationFileTimeUtc = 0;
        return false;
    }

    /// <inheritdoc />
    public bool TerminateProcess(IntPtr processHandle, uint exitCode, out int win32Error)
    {
        bool succeeded = NativeMethods.TerminateProcess(processHandle, exitCode);
        win32Error = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    /// <inheritdoc />
    public uint WaitForSingleObject(IntPtr processHandle, uint milliseconds)
        => NativeMethods.WaitForSingleObject(processHandle, milliseconds);

    /// <inheritdoc />
    public bool PostCloseMessage(IntPtr windowHandle)
        => NativeMethods.PostMessageW(windowHandle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <inheritdoc />
    public IReadOnlyList<IntPtr> FindTopLevelWindows(int processId)
    {
        var windows = new List<IntPtr>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (NativeMethods.GetWindowThreadProcessId(hWnd, out uint windowPid) != 0
                && windowPid == (uint)processId)
            {
                windows.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <inheritdoc />
    public bool CloseHandle(IntPtr handle) => NativeMethods.CloseHandle(handle);
}
