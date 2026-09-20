namespace CuinProcessEase.Core.Models;

/// <summary>
/// 单个进程在某一时刻的快照。
/// </summary>
/// <remarks>
/// 所有可空字段在 Windows 权限不足、进程退出竞态或系统保护进程情况下
/// 允许为 <c>null</c>（或枚举 <see cref="ProcessArchitecture.Unknown"/>），
/// 单个进程读取失败绝不代表整次扫描失败。
/// </remarks>
public sealed class ProcessSnapshot
{
    /// <summary>进程唯一身份（PID + StartTime），用于防 PID 重用。</summary>
    public required ProcessIdentity Identity { get; init; }

    /// <summary>进程 ID。便捷访问 <see cref="ProcessIdentity.ProcessId"/>。</summary>
    public int ProcessId => Identity.ProcessId;

    /// <summary>
    /// 父进程 ID（PPID），来自 Win32 Tool Help 快照。
    /// 注意：父进程可能早已退出，该 PID 可能已被复用，消费方不得假定其仍存在。
    /// 快照间隙无法读取时为 <c>null</c>。
    /// </summary>
    public int? ParentProcessId { get; init; }

    /// <summary>进程可执行文件名（如 chrome.exe）；无法读取时为 "Unknown"，绝不为 null。</summary>
    public required string Name { get; init; }

    /// <summary>可执行文件完整路径。权限不足（如受保护进程、其他用户会话进程）时为 <c>null</c>。</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>进程启动时间（UTC）。权限不足或进程已退出时为 <c>null</c>。</summary>
    public DateTime? StartTimeUtc => Identity.StartTimeUtc;

    /// <summary>会话 ID（Session 0 为服务会话）。读取失败时为 <c>null</c>。</summary>
    public int? SessionId { get; init; }

    /// <summary>工作集内存（字节）。读取失败时为 <c>null</c>。</summary>
    public long? WorkingSetBytes { get; init; }

    /// <summary>私有提交内存（字节）。读取失败时为 <c>null</c>。</summary>
    public long? PrivateMemoryBytes { get; init; }

    /// <summary>进程所属用户（DOMAIN\User 格式）。权限不足或无令牌（如 System Idle Process）时为 <c>null</c>。</summary>
    public string? UserName { get; init; }

    /// <summary>可执行文件 CPU 架构。无法读取时为 <see cref="ProcessArchitecture.Unknown"/>。</summary>
    public ProcessArchitecture Architecture { get; init; }

    /// <summary>进程是否以提升（管理员）权限运行。无法读取令牌时为 <c>null</c>。</summary>
    public bool? IsElevated { get; init; }

    /// <summary>进程完整命令行。WMI 等读取途径不可用或权限不足时为 <c>null</c>。</summary>
    public string? CommandLine { get; init; }

    /// <summary>产品名（来自 exe 版本资源 ProductName）。无版本信息或读取失败为 <c>null</c>。</summary>
    public string? ProductName { get; init; }

    /// <summary>公司名（来自 exe 版本资源 CompanyName）。无版本信息或读取失败为 <c>null</c>。</summary>
    public string? CompanyName { get; init; }

    /// <summary>文件描述（来自 exe 版本资源 FileDescription）。无版本信息或读取失败为 <c>null</c>。</summary>
    public string? FileDescription { get; init; }

    /// <summary>原始文件名（来自 exe 版本资源 OriginalFilename）。无版本信息或读取失败为 <c>null</c>。</summary>
    public string? OriginalFileName { get; init; }
}
