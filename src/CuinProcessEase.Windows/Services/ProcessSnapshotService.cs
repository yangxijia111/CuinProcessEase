using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CuinProcessEase.Core.Interfaces;
using CuinProcessEase.Core.Models;
using CuinProcessEase.Windows.Native;
using CuinProcessEase.Windows.ProcessApi;

namespace CuinProcessEase.Windows.Services;

/// <summary>
/// 基于 System.Diagnostics.Process + Win32 P/Invoke 的进程快照实现。
/// </summary>
/// <remarks>
/// 数据来源分工：
/// - 进程枚举 / 名称 / StartTime / SessionId / 内存：System.Diagnostics.Process；
/// - 父进程 PID（PPID）：Win32 Tool Help API（CreateToolhelp32Snapshot / Process32First / Process32Next）；
/// - 可执行路径：QueryFullProcessImageNameW（仅要求 PROCESS_QUERY_LIMITED_INFORMATION，成功率高于 MainModule）；
/// - 用户名 / 提升状态：进程令牌 TokenUser / TokenElevation；
/// - 架构：IsWow64Process2（准确区分 X86 / X64 / ARM64 及 ARM64 仿真进程），不可用时降级 IsWow64Process。
/// 容错原则：任何单进程、单字段读取失败只置 null / Unknown，绝不使整次扫描失败。
/// </remarks>
public sealed class ProcessSnapshotService : IProcessSnapshotService
{
    /// <inheritdoc />
    public Task<ProcessSnapshotCollection> CaptureAsync(CancellationToken cancellationToken = default)
    {
        // 扫描全程在线程池执行，调用方（WPF UI 线程）不被阻塞
        return Task.Run(() => Capture(cancellationToken), cancellationToken);
    }

    private static ProcessSnapshotCollection Capture(CancellationToken cancellationToken)
    {
        DateTime capturedAtUtc = DateTime.UtcNow;

        // PPID 映射来自 Tool Help 快照；创建失败时降级为空表（PPID 全部为 null），
        // 不让这一个系统调用失败拖垮整次扫描
        Dictionary<int, ToolhelpProcessEntry> toolhelpEntries;
        try
        {
            toolhelpEntries = ToolhelpSnapshot.CaptureProcesses();
        }
        catch (Win32Exception)
        {
            toolhelpEntries = new Dictionary<int, ToolhelpProcessEntry>();
        }

        Process[] processes = Process.GetProcesses();
        var snapshots = new List<ProcessSnapshot>(processes.Length);

        try
        {
            foreach (Process process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    snapshots.Add(CreateSnapshot(process, toolhelpEntries));
                }
                catch
                {
                    // 单个进程构建快照失败（典型为枚举瞬间退出）时跳过，不影响其余进程
                }
                finally
                {
                    try { process.Dispose(); } catch { /* 忽略 Dispose 异常 */ }
                }
            }
        }
        finally
        {
            // 兜底释放，防止任何路径遗漏导致句柄泄漏
            foreach (Process process in processes)
            {
                try { process.Dispose(); } catch { /* 忽略 Dispose 异常 */ }
            }
        }

        snapshots.Sort((a, b) => a.ProcessId.CompareTo(b.ProcessId));

        return new ProcessSnapshotCollection
        {
            CapturedAtUtc = capturedAtUtc,
            Processes = snapshots,
        };
    }

    private static ProcessSnapshot CreateSnapshot(
        Process process,
        IReadOnlyDictionary<int, ToolhelpProcessEntry> toolhelpEntries)
    {
        int pid = process.Id;
        toolhelpEntries.TryGetValue(pid, out ToolhelpProcessEntry? entry);

        // 进程名：优先 Tool Help 原始 exe 名（含 .exe，如 chrome.exe），
        // 其次 Process.ProcessName，最后 "Unknown"
        string name = "Unknown";
        if (entry is not null && !string.IsNullOrWhiteSpace(entry.ExeFileName))
        {
            name = entry.ExeFileName;
        }
        else
        {
            string? processName = TryReadString(() => process.ProcessName);
            if (!string.IsNullOrWhiteSpace(processName))
            {
                name = processName;
            }
        }

        // StartTime 第一路径：System.Diagnostics.Process；失败（AccessDenied / 进程已退出）先保持 null，
        // 在拿到低权限句柄后由 GetProcessTimes 第二路径补读（见下）
        DateTime? startTimeUtc = TryRead(() => process.StartTime)?.ToUniversalTime();

        int? sessionId = TryRead(() => process.SessionId);
        long? workingSet = TryRead(() => process.WorkingSet64);
        long? privateMemory = TryRead(() => process.PrivateMemorySize64);

        string? executablePath = null;
        string? userName = null;
        bool? isElevated = null;
        ProcessArchitecture architecture = ProcessArchitecture.Unknown;

        // 用最低权限句柄统一读取路径 / 架构 / 令牌信息；
        // 系统保护进程、其他用户会话进程会在这里 OpenProcess 失败 → 相关字段保持 null
        IntPtr processHandle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, bInheritHandle: false, (uint)pid);
        if (processHandle != IntPtr.Zero && processHandle != NativeMethods.INVALID_HANDLE_VALUE)
        {
            try
            {
                executablePath = ReadFullImageName(processHandle);

                // StartTime 第二路径：在已有低权限句柄上用 GetProcessTimes 读创建时间。
                // 仅在第一路径失败且该路径成功时采用，绝不伪造时间
                if (startTimeUtc is null)
                {
                    startTimeUtc = ReadCreationTimeUtc(processHandle);
                }

                architecture = ReadArchitecture(processHandle);

                ReadTokenInformation(processHandle, out userName, out isElevated);
            }
            finally
            {
                NativeMethods.CloseHandle(processHandle);
            }
        }

        // 路径兜底：少数情况下低权限句柄拿不到，但 Process.MainModule 可用
        if (executablePath is null)
        {
            executablePath = TryReadString(() => process.MainModule?.FileName);
        }

        return new ProcessSnapshot
        {
            Identity = new ProcessIdentity(pid, startTimeUtc),
            ParentProcessId = entry?.ParentProcessId,
            Name = name,
            ExecutablePath = executablePath,
            SessionId = sessionId,
            WorkingSetBytes = workingSet,
            PrivateMemoryBytes = privateMemory,
            UserName = userName,
            Architecture = architecture,
            IsElevated = isElevated,
        };
    }

    /// <summary>
    /// 单字段安全读取：任何 Windows 权限 / 进程退出竞态异常都转为 null。
    /// </summary>
    private static T? TryRead<T>(Func<T> reader) where T : struct
    {
        try
        {
            return reader();
        }
        catch (Exception ex) when (ex is Win32Exception                 // AccessDenied 等
                                     or InvalidOperationException      // 进程已退出 / 对象无效
                                     or ExternalException
                                     or IOException
                                     or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>字符串字段版本的单字段安全读取。</summary>
    private static string? TryReadString(Func<string?> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception ex) when (ex is Win32Exception
                                     or InvalidOperationException
                                     or ExternalException
                                     or IOException
                                     or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadFullImageName(IntPtr processHandle)
    {
        try
        {
            // 从 1K 字符起步，遇 ERROR_INSUFFICIENT_BUFFER 倍增扩容，
            // 上限覆盖 Windows 长路径（32767 字符），不引入额外依赖
            int capacity = 1024;
            while (capacity <= 65536)
            {
                var buffer = new StringBuilder(capacity);
                uint size = (uint)capacity;
                if (NativeMethods.QueryFullProcessImageNameW(processHandle, 0, buffer, ref size))
                {
                    return buffer.ToString();
                }

                if (Marshal.GetLastWin32Error() != NativeMethods.ERROR_INSUFFICIENT_BUFFER)
                {
                    // 权限不足等其他错误，按 null 处理，不再扩容
                    return null;
                }

                capacity *= 2;
            }
        }
        catch
        {
            // 读取失败按 null 处理
        }

        return null;
    }

    /// <summary>
    /// 通过 GetProcessTimes 读取进程创建时间（UTC），作为 StartTime 的第二获取路径。
    /// </summary>
    /// <remarks>
    /// internal 以便测试直接验证；任何失败或非法值（FILETIME 为 0）都返回 null，不伪造时间。
    /// </remarks>
    internal static DateTime? ReadCreationTimeUtc(IntPtr processHandle)
    {
        try
        {
            if (!NativeMethods.GetProcessTimes(
                    processHandle,
                    out System.Runtime.InteropServices.ComTypes.FILETIME creation,
                    out _, out _, out _))
            {
                return null;
            }

            long raw = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
            if (raw == 0)
            {
                // 无效创建时间，宁缺毋假
                return null;
            }

            // GetProcessTimes 返回的创建时间本身就是 UTC FILETIME
            return DateTime.FromFileTimeUtc(raw);
        }
        catch
        {
            return null;
        }
    }

    private static ProcessArchitecture ReadArchitecture(IntPtr processHandle)
    {
        try
        {
            // 首选 IsWow64Process2：能准确区分 X86 / X64 / ARM64，
            // 以及 ARM64 系统上经仿真运行的 x64 / x86 进程（旧 API 会把仿真 x64 误判为 X86）
            NativeMethods.IsWow64Process2Delegate? isWow64Process2 = IsWow64Process2Proc.Value;
            if (isWow64Process2 is not null
                && isWow64Process2(processHandle, out ushort processMachine, out ushort nativeMachine))
            {
                // processMachine 为 UNKNOWN 表示进程原生执行，架构即本机架构；
                // 否则 processMachine 就是进程真实的镜像机器架构（含仿真场景）
                return MachineToArchitecture(
                    processMachine == NativeMethods.IMAGE_FILE_MACHINE_UNKNOWN ? nativeMachine : processMachine);
            }

            // API 不存在（Windows 10 1511 之前）或本次调用失败 → 安全降级到 IsWow64Process 方案
            return ReadArchitectureByWow64(processHandle);
        }
        catch
        {
            return ProcessArchitecture.Unknown;
        }
    }

    /// <summary>降级路径：IsWow64Process + 系统原生架构推断（不含 IsWow64Process2 的旧系统）。</summary>
    private static ProcessArchitecture ReadArchitectureByWow64(IntPtr processHandle)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            // 32 位系统上所有进程必然是 x86
            return ProcessArchitecture.X86;
        }

        if (!NativeMethods.IsWow64Process(processHandle, out bool isWow64))
        {
            return ProcessArchitecture.Unknown;
        }

        if (isWow64)
        {
            return ProcessArchitecture.X86;
        }

        // 非 WoW64 进程与操作系统原生架构一致
        return NativeSystemArchitecture.Value;
    }

    /// <summary>IMAGE_FILE_MACHINE 常量到架构枚举的映射。</summary>
    private static ProcessArchitecture MachineToArchitecture(ushort machine) => machine switch
    {
        NativeMethods.IMAGE_FILE_MACHINE_I386 => ProcessArchitecture.X86,
        NativeMethods.IMAGE_FILE_MACHINE_AMD64 => ProcessArchitecture.X64,
        NativeMethods.IMAGE_FILE_MACHINE_ARM64 => ProcessArchitecture.Arm64,
        _ => ProcessArchitecture.Unknown,
    };

    /// <summary>
    /// IsWow64Process2 函数指针缓存（Windows 10 1511+ 可用）。
    /// 通过 GetProcAddress 动态绑定：旧系统上解析结果为 null，调用方走降级路径，
    /// 避免直接 P/Invoke 触发 EntryPointNotFoundException。
    /// </summary>
    private static readonly Lazy<NativeMethods.IsWow64Process2Delegate?> IsWow64Process2Proc = new(() =>
    {
        IntPtr kernel32 = NativeMethods.GetModuleHandleW("kernel32.dll");
        if (kernel32 == IntPtr.Zero)
        {
            return null;
        }

        IntPtr address = NativeMethods.GetProcAddress(kernel32, "IsWow64Process2");
        if (address == IntPtr.Zero)
        {
            return null;
        }

        return Marshal.GetDelegateForFunctionPointer<NativeMethods.IsWow64Process2Delegate>(address);
    });

    /// <summary>操作系统原生架构（惰性计算一次，仅降级路径使用）。</summary>
    /// <remarks>
    /// 降级路径的已知限制：ARM64 系统上经模拟运行的 x64 进程会被 WoW64 判断标记为 X86；
    /// 首选的 IsWow64Process2 路径不存在该问题。
    /// </remarks>
    private static readonly Lazy<ProcessArchitecture> NativeSystemArchitecture = new(() =>
    {
        NativeMethods.GetNativeSystemInfo(out NativeMethods.SYSTEM_INFO systemInfo);
        return systemInfo.wProcessorArchitecture switch
        {
            NativeMethods.PROCESSOR_ARCHITECTURE_AMD64 => ProcessArchitecture.X64,
            NativeMethods.PROCESSOR_ARCHITECTURE_ARM64 => ProcessArchitecture.Arm64,
            _ => ProcessArchitecture.Unknown,
        };
    });

    private static void ReadTokenInformation(IntPtr processHandle, out string? userName, out bool? isElevated)
    {
        userName = null;
        isElevated = null;

        if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out IntPtr tokenHandle))
        {
            return;
        }

        try
        {
            userName = ReadTokenUserName(tokenHandle);
            isElevated = ReadTokenElevation(tokenHandle);
        }
        finally
        {
            NativeMethods.CloseHandle(tokenHandle);
        }
    }

    /// <summary>读取令牌的用户账户（TOKEN_USER → SID → 账户名）。</summary>
    private static string? ReadTokenUserName(IntPtr tokenHandle)
    {
        try
        {
            // 第一次调用获取所需缓冲区长度（TOKEN_USER = 一个 SID 指针 + SID）
            NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenUser, IntPtr.Zero, 0, out uint length);
            if (length == 0 || length > 64 * 1024)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenUser, buffer, length, out _))
                {
                    return null;
                }

                // TOKEN_USER 结构第一个成员就是用户 SID 指针
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero)
                {
                    return null;
                }

                var nameBuilder = new StringBuilder(256);
                var domainBuilder = new StringBuilder(256);
                uint nameLength = (uint)nameBuilder.Capacity;
                uint domainLength = (uint)domainBuilder.Capacity;

                if (!NativeMethods.LookupAccountSidW(
                        IntPtr.Zero, sid, nameBuilder, ref nameLength,
                        domainBuilder, ref domainLength, out _))
                {
                    return null;
                }

                string name = nameBuilder.ToString();
                string domain = domainBuilder.ToString();
                return domain.Length > 0 ? $"{domain}\\{name}" : name;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadTokenElevation(IntPtr tokenHandle)
    {
        try
        {
            NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenElevation, IntPtr.Zero, 0, out uint length);
            if (length == 0 || length > 64)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!NativeMethods.GetTokenInformation(tokenHandle, NativeMethods.TokenElevation, buffer, length, out _))
                {
                    return null;
                }

                // TOKEN_ELEVATION 结构第一个 DWORD 即 TokenIsElevated
                return Marshal.ReadInt32(buffer) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }
}
