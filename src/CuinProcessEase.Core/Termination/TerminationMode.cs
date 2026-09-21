using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Termination;

/// <summary>终止模式。</summary>
public enum TerminationMode
{
    /// <summary>优雅关闭（仅向目标进程的顶层窗口发送 WM_CLOSE，绝不自动升级为强杀）。</summary>
    Graceful = 0,

    /// <summary>强制终止（TerminateProcess，仅限 Fresh Safety 为 Allowed 的组）。</summary>
    Force = 1,
}
