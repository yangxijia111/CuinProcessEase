namespace CuinProcessEase.Core.Elevated;

/// <summary>
/// Elevated Helper 执行终止所需 Win32 原语的最小抽象（跨进程边界，public seam）。
/// </summary>
/// <remarks>
/// 与主程序终止引擎的 internal ITerminationInterop 方法签名保持一致，
/// 真实实现可同时实现两个接口；fake 实现供单元测试断言
/// "身份不匹配时 0 次 TerminateProcess"等安全不变量。
/// 访问权限常量与错误码见 <see cref="ElevatedInteropConstants"/>（Core 不依赖 Win32 层）。
/// </remarks>
public interface IElevatedKillInterop
{
    /// <summary>OpenProcess；失败返回 <see cref="IntPtr.Zero"/> 并通过 out 返回 Win32 错误码。</summary>
    IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId, out int win32Error);

    /// <summary>
    /// 读取进程 CreationTime（原始 FILETIME，100ns tick），
    /// 与请求预期值逐位比较（差 1 tick 也拒绝），绝无时间容差。
    /// </summary>
    bool TryGetCreationFileTime(IntPtr processHandle, out long creationFileTimeUtc);

    /// <summary>TerminateProcess（破坏性动作）。</summary>
    bool TerminateProcess(IntPtr processHandle, uint exitCode, out int win32Error);

    /// <summary>WaitForSingleObject；返回原生等待结果（WAIT_OBJECT_0 / WAIT_TIMEOUT / WAIT_FAILED）。</summary>
    uint WaitForSingleObject(IntPtr processHandle, uint milliseconds);

    /// <summary>CloseHandle。</summary>
    bool CloseHandle(IntPtr handle);
}

/// <summary>Core 层自带的访问权限 / 等待结果 / 错误码常量（值与 Win32 SDK 一致）。</summary>
public static class ElevatedInteropConstants
{
    /// <summary>PROCESS_TERMINATE (0x0001)。</summary>
    public const uint ProcessTerminate = 0x0001;

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION (0x1000)。</summary>
    public const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>SYNCHRONIZE (0x00100000)。</summary>
    public const uint Synchronize = 0x00100000;

    /// <summary>WAIT_OBJECT_0。</summary>
    public const uint WaitObject0 = 0x0000;

    /// <summary>WAIT_TIMEOUT。</summary>
    public const uint WaitTimeout = 0x0102;

    /// <summary>ERROR_ACCESS_DENIED。</summary>
    public const int ErrorAccessDenied = 5;

    /// <summary>TerminateProcess 后确认退出的有限等待（毫秒）。</summary>
    public const uint ForceWaitMs = 5000;
}
