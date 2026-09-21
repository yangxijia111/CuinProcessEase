using System.Runtime.InteropServices;

namespace CuinProcessEase.Windows.Native;

/// <summary>
/// 本文件集中存放快照引擎所需的全部原始 Win32 P/Invoke 声明、结构体与常量。
/// 仅做声明，不含业务逻辑。
/// </summary>
internal static partial class NativeMethods
{
    // ---------- 通用 ----------

    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    /// <summary>拒绝访问：Safety API 查询被权限拒绝时作为明确证据记录。</summary>
    public const int ERROR_ACCESS_DENIED = 5;

    public const uint ERROR_NO_MORE_FILES = 18;

    /// <summary>缓冲区不足，用于路径读取的动态扩容重试。</summary>
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    // ---------- Tool Help API（父进程 PID 来源） ----------

    /// <summary>快照包含系统全部进程。</summary>
    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    /// <summary>PROCESSENTRY32W 结构体大小必须先赋值再调用 API。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr thDefaultHeap;     // ULONG_PTR，占位保持结构布局
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID; // 父进程 PID（PPID）
        public int pcPriClassBase;       // Win32 LONG，4 字节
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;         // 进程可执行文件名（含 .exe）
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    // ---------- 进程查询（路径 / 架构） ----------

    /// <summary>查询进程镜像路径等基础信息所需的最低权限。</summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        uint dwFlags,
        System.Text.StringBuilder lpExeName,
        ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    /// <summary>
    /// 读取进程时间；PROCESS_QUERY_LIMITED_INFORMATION 权限句柄即可调用，
    /// lpCreationTime 即进程创建时间（UTC FILETIME），作为 StartTime 的第二获取路径。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(
        IntPtr hProcess,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpCreationTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpExitTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

    // ---------- 进程架构（IsWow64Process2，Windows 10 1511+） ----------

    /// <summary>IMAGE_FILE_MACHINE_UNKNOWN：进程原生执行（非 WoW64 / 非仿真），架构即本机架构。</summary>
    public const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0x0000;

    public const ushort IMAGE_FILE_MACHINE_I386 = 0x014C;

    public const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;

    public const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;

    /// <summary>IsWow64Process2 函数签名（经 GetProcAddress 动态绑定，旧系统上不可用）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool IsWow64Process2Delegate(
        IntPtr hProcess,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    // ---------- 令牌查询（用户名 / 提升状态） ----------

    public const uint TOKEN_QUERY = 0x0008;

    /// <summary>GetTokenInformation 的 TOKEN_USER 类别，用于取进程用户 SID。</summary>
    public const int TokenUser = 1;

    /// <summary>GetTokenInformation 的 TOKEN_ELEVATION 类别，用于判断管理员权限。</summary>
    public const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupAccountSidW(
        IntPtr lpSystemName,
        IntPtr sid,
        System.Text.StringBuilder lpName,
        ref uint cchName,
        System.Text.StringBuilder lpReferencedDomainName,
        ref uint cchReferencedDomainName,
        out int peUse);

    // ---------- 系统架构 ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_INFO
    {
        public ushort wProcessorArchitecture;
        public ushort wReserved;
        public uint dwPageSize;
        public IntPtr lpMinimumApplicationAddress;
        public IntPtr lpMaximumApplicationAddress;
        public IntPtr dwActiveProcessorMask;
        public uint dwNumberOfProcessors;
        public uint dwProcessorType;
        public uint dwAllocationGranularity;
        public ushort wProcessorLevel;
        public ushort wProcessorRevision;
    }

    public const ushort PROCESSOR_ARCHITECTURE_AMD64 = 9;
    public const ushort PROCESSOR_ARCHITECTURE_ARM64 = 12;

    [DllImport("kernel32.dll")]
    public static extern void GetNativeSystemInfo(out SYSTEM_INFO lpSystemInfo);

    // ---------- 系统资源（Overview 栏） ----------

    /// <summary>
    /// 系统全局时间：内核时间（含 Idle）与用户时间（FILETIME 100ns）。
    /// CPU% = (Δkernel + Δuser − Δidle) / (Δkernel + Δuser) × 100。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;          // 调用前必须设为 Marshal.SizeOf<MEMORYSTATUSEX>()
        public uint dwMemoryLoad;      // 物理内存占用百分比 0-100
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---------- 终止引擎（Phase 6） ----------

    /// <summary>允许终止进程（OpenProcess 访问权）。</summary>
    public const uint PROCESS_TERMINATE = 0x0001;

    /// <summary>允许 WaitForSingleObject 等待进程对象（OpenProcess 访问权）。</summary>
    public const uint SYNCHRONIZE = 0x00100000;

    /// <summary>WM_CLOSE 消息（仅向目标进程的顶层窗口投递，禁止广播）。</summary>
    public const uint WM_CLOSE = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    /// <summary>返回值：WAIT_OBJECT_0(0)=signaled；WAIT_TIMEOUT(0x102)=超时；WAIT_FAILED=失败。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>读取窗口所属进程 ID（线程 ID 经 out 参数返回，调用方不需要时忽略）。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
