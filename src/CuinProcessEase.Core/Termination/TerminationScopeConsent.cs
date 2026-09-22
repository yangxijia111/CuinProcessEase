namespace CuinProcessEase.Core.Termination;

/// <summary>
/// 终止范围授权（P6.4 Weak Group Scope Consent）。
/// </summary>
/// <remarks>
/// UI 对"一行 = 一个应用组"构造请求时会把整行成员作为锚点，
/// 因此弱证据多进程组（Confidence &lt; High）若只靠默认行为，
/// 用户确认的仍是整组——弱证据 blast radius 没有真正被限制。
/// 该枚举要求弱组操作必须经过显著不同的专门确认
/// （列出全部将操作的进程）后显式携带 <see cref="ExplicitWeakGroup"/>，
/// 引擎才允许操作这些明确确认的成员；默认 <see cref="Default"/>
/// 绝不自动获得弱组授权。
/// </remarks>
public enum TerminationScopeConsent
{
    /// <summary>默认：未对弱证据分组做过任何额外确认（弱多进程组将被拒绝执行）。</summary>
    Default = 0,

    /// <summary>用户已在专门的弱组确认对话框中明确确认操作范围（仅授权确认时列出的 exact identities）。</summary>
    ExplicitWeakGroup = 1,
}
