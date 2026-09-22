namespace CuinProcessEase.Core.Termination;

/// <summary>单进程终止结果状态。</summary>
public enum ProcessTerminationStatus
{
    /// <summary>已优雅关闭（WM_CLOSE 后在等待窗口内退出）。</summary>
    ClosedGracefully = 0,

    /// <summary>已强制终止（TerminateProcess 成功且进程对象已进入 signaled）。</summary>
    Terminated = 1,

    /// <summary>执行前已退出（进程对象已 signaled）。</summary>
    AlreadyExited = 2,

    /// <summary>无顶层窗口（WM_CLOSE 不适用；优雅模式下未退出即此状态）。</summary>
    NoWindow = 3,

    /// <summary>访问被拒绝（打开句柄或终止失败，ERROR_ACCESS_DENIED）。</summary>
    AccessDenied = 4,

    /// <summary>身份不匹配（句柄 CreationTime 与 Fresh Snapshot StartTimeUtc 不一致）。</summary>
    IdentityMismatch = 5,

    /// <summary>Fresh Safety 判定禁止（Blocked）。</summary>
    Blocked = 6,

    /// <summary>等待退出超时（有限等待内未进入 signaled）。</summary>
    TimedOut = 7,

    /// <summary>执行失败（其他 Win32 或内部错误）。</summary>
    Failed = 8,

    /// <summary>残留（优雅模式未响应 / 清理轮次后仍存活）。</summary>
    Residual = 9,

    /// <summary>身份不可靠（StartTime 缺失 / 句柄创建时间读取失败，无法安全校验），已整组取消。</summary>
    UnreliableIdentity = 10,
}
