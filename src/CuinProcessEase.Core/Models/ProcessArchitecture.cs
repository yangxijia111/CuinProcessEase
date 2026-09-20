namespace CuinProcessEase.Core.Models;

/// <summary>
/// 进程可执行文件的 CPU 架构。
/// 无法确定时为 <see cref="Unknown"/>。
/// </summary>
public enum ProcessArchitecture
{
    /// <summary>无法读取或无法判断。</summary>
    Unknown = 0,

    /// <summary>32 位 x86 进程（含 64 位系统上的 WoW64 进程）。</summary>
    X86 = 1,

    /// <summary>64 位 x64 进程。</summary>
    X64 = 2,

    /// <summary>ARM64 原生进程。</summary>
    Arm64 = 3,
}
