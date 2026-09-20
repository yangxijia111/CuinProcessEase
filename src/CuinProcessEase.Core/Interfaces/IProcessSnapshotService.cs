using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Interfaces;

/// <summary>
/// 进程快照采集服务：读取当前系统所有可访问进程，生成一次完整快照。
/// </summary>
/// <remarks>
/// 实现要求：
/// 1. 扫描必须异步执行，不得阻塞调用方线程（尤其是 WPF UI 线程）；
/// 2. 单个进程读取失败（AccessDenied / ProcessExited / 系统保护进程等）
///    只允许对应字段为 null / Unknown，不得抛出异常导致整次扫描失败。
/// </remarks>
public interface IProcessSnapshotService
{
    /// <summary>
    /// 捕获当前时刻的系统进程快照。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>快照集合，始终包含尽可能多的进程信息。</returns>
    Task<ProcessSnapshotCollection> CaptureAsync(CancellationToken cancellationToken = default);
}
