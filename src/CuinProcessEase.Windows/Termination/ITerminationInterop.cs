namespace CuinProcessEase.Windows.Termination;

/// <summary>
/// 终止引擎所需 Win32 原语的抽象 seam：真实实现走 P/Invoke，
/// fake 实现供测试断言“0 destructive action”等安全不变量。
/// </summary>
/// <remarks>
/// <see cref="TerminateProcess"/> 与 <see cref="PostCloseMessage"/> 是破坏性动作，
/// 实现必须保证调用可审计（fake 实现据此断言整组取消时破坏性调用次数为 0）。
/// </remarks>
internal interface ITerminationInterop
{
    /// <summary>OpenProcess；失败返回 <see cref="IntPtr.Zero"/> 并通过 out 返回 Win32 错误码。</summary>
    IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId, out int win32Error);

    /// <summary>
    /// 读取进程 CreationTime；<paramref name="creationFileTimeUtc"/> 返回原始 FILETIME 值
    /// （100ns tick，与 Fresh Snapshot StartTimeUtc.ToFileTimeUtc() 逐位比较，绝无容差）。
    /// </summary>
    bool TryGetCreationFileTime(IntPtr processHandle, out long creationFileTimeUtc);

    /// <summary>TerminateProcess（破坏性动作）。</summary>
    bool TerminateProcess(IntPtr processHandle, uint exitCode, out int win32Error);

    /// <summary>WaitForSingleObject；返回原生等待结果（WAIT_OBJECT_0 / WAIT_TIMEOUT / WAIT_FAILED）。</summary>
    uint WaitForSingleObject(IntPtr processHandle, uint milliseconds);

    /// <summary>向指定顶层窗口投递 WM_CLOSE（破坏性动作，绝不广播）。</summary>
    bool PostCloseMessage(IntPtr windowHandle);

    /// <summary>枚举属于目标进程的全部顶层窗口句柄。</summary>
    IReadOnlyList<IntPtr> FindTopLevelWindows(int processId);

    /// <summary>CloseHandle；失败返回 false。</summary>
    bool CloseHandle(IntPtr handle);
}
