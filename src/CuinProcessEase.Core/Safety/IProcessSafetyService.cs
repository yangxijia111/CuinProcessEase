using CuinProcessEase.Core.Grouping;
using CuinProcessEase.Core.Models;

namespace CuinProcessEase.Core.Safety;

/// <summary>
/// 安全引擎接口：判断"这个目标是否允许未来执行破坏性操作"。
/// </summary>
/// <remarks>
/// 与 Grouping Engine 完全分离：GroupingConfidence 与 Safety 决策无任何因果关系。
/// 本接口只做"判断"，不做任何"执行"（无 Kill / Terminate / CloseMainWindow）。
/// 实现应按需计算（用户准备执行操作前重新验证），不进入 Snapshot 每秒热路径。
/// </remarks>
public interface IProcessSafetyService
{
    /// <summary>评估单个进程的安全性。</summary>
    ProcessSafetyResult Assess(ProcessSnapshot process);

    /// <summary>评估一个应用组：先逐成员评估，再取最严格结果。</summary>
    ApplicationSafetyResult Assess(ApplicationGroup group);
}
