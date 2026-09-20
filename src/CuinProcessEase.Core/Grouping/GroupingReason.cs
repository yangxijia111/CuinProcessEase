namespace CuinProcessEase.Core.Grouping;

/// <summary>
/// 分组依据：解释"为什么这些进程被认为属于同一个软件"。
/// </summary>
[Flags]
public enum GroupingReason
{
    /// <summary>无依据。</summary>
    None = 0,

    /// <summary>相同软件包身份（MSIX/AppContainer；预留）。</summary>
    SamePackage = 1 << 0,

    /// <summary>已验证的父子进程关系（双方 StartTime 已知且父不晚于子）。</summary>
    VerifiedParentChild = 1 << 1,

    /// <summary>未验证的父子进程关系（仅单次快照 PPID，作为辅助证据）。</summary>
    UnverifiedParentChild = 1 << 2,

    /// <summary>相同的可执行文件完整路径（同一软件的多实例）。</summary>
    SameExecutable = 1 << 3,

    /// <summary>相同的、足够具体的软件安装目录。</summary>
    SameInstallDirectory = 1 << 4,

    /// <summary>相同的有效产品名。</summary>
    SameProduct = 1 << 5,

    /// <summary>相同的公司名（只能作为辅助证据，绝不能单独触发合并）。</summary>
    SameCompany = 1 << 6,

    /// <summary>命令行关联（预留：当前引擎不基于命令行自动合并）。</summary>
    CommandLineAssociation = 1 << 7,

    /// <summary>用户手动覆盖规则（预留）。</summary>
    UserOverride = 1 << 8,
}
